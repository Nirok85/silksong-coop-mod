using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace SilksongCoop.Core.Networking
{
    /// <summary>
    /// High-performance UDP socket network transport supporting unreliable, sequenced,
    /// and reliable ordered delivery channels with room-code and direct IP:port connection.
    /// </summary>
    public class LiteNetLibTransport : INetworkTransport
    {
        #region Nested Types

        private enum UdpFrameType : byte
        {
            Handshake = 0x01,
            HandshakeAck = 0x02,
            Data = 0x03,
            Ack = 0x04,
            Ping = 0x05,
            Pong = 0x06,
            Disconnect = 0x07,
            Challenge = 0x08,
            ChallengeResponse = 0x09
        }

        private struct SentReliablePacket
        {
            public uint Sequence;
            public byte Channel;
            public byte[] Envelope;
            public int EnvelopeLength;
            public long LastSentTimeMs;
            public int Retries;
        }

        /// <summary>
        /// Kind of deferred transport event queued for delivery outside the transport lock.
        /// </summary>
        private enum DeferredEventKind
        {
            PeerConnected,
            PeerDisconnected,
            DataReceived
        }

        /// <summary>
        /// A transport event queued under <see cref="_lock"/> and delivered after the lock
        /// is released (see <see cref="DrainPendingEvents"/>). Handler callbacks re-enter
        /// the transport (Send / Disconnect / session orchestration), so they must never
        /// execute while the lock is held.
        /// </summary>
        private readonly struct DeferredEvent
        {
            public readonly DeferredEventKind Kind;
            public readonly NetworkPeerId Peer;
            public readonly DisconnectReason Reason;
            public readonly byte[]? Data;

            public DeferredEvent(DeferredEventKind kind, NetworkPeerId peer,
                DisconnectReason reason = DisconnectReason.None, byte[]? data = null)
            {
                Kind = kind;
                Peer = peer;
                Reason = reason;
                Data = data;
            }
        }

        #endregion

        #region Fields and Constants

        private Socket? _socket;
        private EndPoint? _remoteEndPoint;
        private NetworkPeerId? _remotePeerId;
        private readonly object _lock = new();
        private readonly RateLimiter _rateLimiter = new RateLimiter(maxTokens: 1000, refillRatePerSecond: 100);
        private readonly ConnectionAuthenticator _authenticator = new ConnectionAuthenticator();
        private string? _roomCode;

        // BUG-N1: sequence spaces are per (channel, delivery method). Unreliable and
        // ReliableOrdered traffic share a channel in this protocol (channel 0 carries the
        // 30 Hz unreliable transform stream and the reliable bench-checkpoint packet).
        // With a single per-channel counter the first reliable packet on channel 0 got a
        // sequence in the hundreds/thousands while the receiver still expected 0, so it
        // was buffered as "future" forever and the bench checkpoint was never applied.
        private readonly Dictionary<(byte Channel, DeliveryMethod Method), uint> _sendSequences = new();
        private readonly Dictionary<byte, uint> _expectedReceiveSequences = new();
        private readonly Dictionary<byte, uint> _latestSequencedReceived = new();
        private readonly HashSet<byte> _sequencedInitialized = new();
        private readonly Dictionary<byte, SortedDictionary<uint, byte[]>> _pendingReliable = new();
        private readonly LinkedList<SentReliablePacket> _unacknowledged = new();
        private readonly Dictionary<(byte channel, uint sequence), LinkedListNode<SentReliablePacket>> _unacknowledgedIndex = new();
        private const int MaxUnacknowledgedPackets = 512;

        private long _lastPingSentMs;
        private long _lastReceivedMs;
        // Connection attempt tracking for client-side timeout (UX: avoid stuck "Connecting" state)
        private long _connectionAttemptStartMs;
        private const long ConnectionTimeoutMs = 10000;
        private const long HeartbeatTimeoutMs = 15000;

        /// <summary>
        /// BUG-P6: timeouts scale with the measured RTT so a WAN peer with a 300 ms ping
        /// is not falsely declared dead (fixed 15 s was too short) and a LAN peer is not
        /// kept waiting the full 15 s after a real outage (fixed was too long). Base values
        /// are floors; the adaptive value is <c>max(base, rtt * 4 + margin)</c>.
        /// </summary>
        private long AdaptiveConnectionTimeoutMs => Math.Max(ConnectionTimeoutMs, (long)_rttMs * 4 + 5000);
        private long AdaptiveHeartbeatTimeoutMs => Math.Max(HeartbeatTimeoutMs, (long)_rttMs * 4 + 5000);

        // BUG-02/BUG-03: transport events are enqueued under _lock and drained after the
        // lock is released, because handler callbacks re-enter the transport
        // (Send / Disconnect / death-state chains) and must not run while it is held.
        private readonly Queue<DeferredEvent> _pendingEvents = new();

        // BUG-A3: last time the client sent its Handshake frame. The initial datagram and
        // the host's HandshakeAck both travel unreliably, so the client must retransmit
        // while the handshake is still pending.
        private long _lastHandshakeSentMs;

        // Pre-allocated receive buffer to avoid per-poll GC pressure
        private readonly byte[] _recvBuffer = new byte[2048];

        // BUG-N2: heartbeat spoof protection — a packet from an unknown endpoint used to
        // refresh _lastReceivedMs before sender validation, letting an off-path attacker
        // keep a dead session "alive" forever.

        // Diagnostics: last unreliable sequence per channel is used to estimate packet loss.
        // Unreliable frames are never retransmitted, so sequence gaps are a genuine loss
        // signal (unlike reliable frames, which are repaired by retries).
        private readonly Dictionary<byte, uint> _lastUnreliableSequences = new();
        private long _lossWindowStartMs;
        private long _lossPacketsExpected;
        private long _lossPacketsLost;
        private volatile int _packetLossPercent;

        private static long GetTimestampMs() => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMillisecond;

        #endregion

        #region Events and Properties

        public event Action<NetworkPeerId>? OnPeerConnected;
        public event Action<NetworkPeerId, DisconnectReason>? OnPeerDisconnected;
        public event Action<NetworkPeerId, byte[], int, int>? OnDataReceived;

        private volatile bool _isHost;
        private volatile bool _isConnected;
        private NetworkPeerId _localPeerId;
        private volatile int _rttMs = 30;
        private volatile int _boundPort;

        public bool IsHost => _isHost;
        public bool IsConnected => _isConnected;
        public NetworkPeerId LocalPeerId => _localPeerId;
        public NetworkPeerId? RemotePeerId => _remotePeerId;
        public int RoundTripTimeMs => _rttMs;
        /// <summary>
        /// Unreliable packet loss estimate (0-100) over the last few seconds, based on
        /// sequence gaps on the unreliable channel.
        /// </summary>
        public int PacketLossPercent => _packetLossPercent;
        public int BoundPort => _boundPort;

        #endregion

        #region Connection Management

        /// <summary>
        /// Initializes a new instance of the LiteNetLibTransport class.
        /// </summary>
        /// <param name="peerId">The unique identifier for this peer.</param>
        /// <param name="displayName">Optional display name for this peer.</param>
        public LiteNetLibTransport(ulong peerId = 1, string displayName = "Player")
        {
            _localPeerId = new NetworkPeerId(peerId, displayName);
        }

        private bool ValidateRoomCode(string roomCode)
        {
            // Reject deprecated 8-character compact codes (weak 2-bit CRC)
            if (RoomCodeCodec.IsDeprecatedCode(roomCode))
                return false;

            // Validate using secure decoder (12-character with CRC-8)
            return RoomCodeCodec.TryDecodeSecure(roomCode, out _, out _);
        }

        /// <summary>
        /// Sets the room code for connection authentication.
        /// Must be called before StartHost or Connect.
        /// </summary>
        public void SetRoomCode(string roomCode)
        {
            _roomCode = roomCode;
        }

        private void ResetSessionState()
        {
            foreach (var node in _unacknowledged)
            {
                ReturnPooledBuffer(node.Envelope);
            }
            _sendSequences.Clear();
            _expectedReceiveSequences.Clear();
            _latestSequencedReceived.Clear();
            _sequencedInitialized.Clear();
            _pendingReliable.Clear();
            _unacknowledged.Clear();
            _unacknowledgedIndex.Clear();
            _lastUnreliableSequences.Clear();
            _lossWindowStartMs = 0;
            _lossPacketsExpected = 0;
            _lossPacketsLost = 0;
            _packetLossPercent = 0;
            _lastPingSentMs = 0;
            _lastReceivedMs = 0;
            _lastHandshakeSentMs = 0;
            _rttMs = 30;
            _connectionAttemptStartMs = 0;
        }

        /// <summary>
        /// Starts hosting a game session on the specified port.
        /// </summary>
        /// <param name="port">The UDP port to bind to.</param>
        public void StartHost(int port = 7777)
        {
            lock (_lock)
            {
                CloseSocket();
                ResetSessionState();
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                {
                    Blocking = false
                };

                // Enable socket reuse and avoid ICMP unreachable errors on Windows
                _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                const int SIO_UDP_CONNRESET = -1744830452;
                try
                {
                    _socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
                }
                catch (Exception ex)
                {
                    // Log but don't fail - this is Windows-specific optimization
                    System.Diagnostics.Debug.WriteLine($"Failed to set SIO_UDP_CONNRESET: {ex.Message}");
                }

                _socket.Bind(new IPEndPoint(IPAddress.Any, port));
                _boundPort = ((IPEndPoint)_socket.LocalEndPoint!).Port;
                _remoteEndPoint = null;
                _remotePeerId = null;
                _isHost = true;
                _isConnected = false;
            }
        }

        /// <summary>
        /// Connects to a remote host using the specified address or room code.
        /// </summary>
        /// <param name="addressOrCode">The IP address:port or room code to connect to.</param>
        public void Connect(string addressOrCode)
        {
            IPAddress targetIp;
            int targetPort;

            // Reject deprecated 8-character compact codes (weak 2-bit CRC)
            if (RoomCodeCodec.IsDeprecatedCode(addressOrCode))
            {
                throw new ArgumentException(
                    "8-character room codes are deprecated due to weak security. " +
                    "Please use the 12-character code format (SK-XXXX-XXXX-XXXX).",
                    nameof(addressOrCode));
            }

            if (RoomCodeCodec.TryDecode(addressOrCode, out IPAddress decodedIp, out int decodedPort))
            {
                targetIp = decodedIp;
                targetPort = decodedPort;
                // BUG-B2: store the CANONICAL code form — the host HMACs against its own
                // Encode() output, so both sides must key the challenge-response on the
                // exact same string (the raw input may differ in case/hyphenation).
                _roomCode = RoomCodeCodec.Encode(decodedIp, decodedPort);
            }
            else
            {
                // BUG-N3: direct IP:port connection — drop any room code left over from a
                // previous hosting/join attempt. Otherwise the client advertises "I have a
                // code" in the handshake and the host challenges it with its own code, so
                // the HMAC never matches and the connection is refused forever.
                _roomCode = null;

                // Parse standard host:port or IP:port
                string[] parts = addressOrCode.Split(':');
                if (parts.Length == 2 && int.TryParse(parts[1], out int p))
                {
                    targetPort = p;
                    if (!IPAddress.TryParse(parts[0], out targetIp!))
                    {
                        try
                        {
                            var addresses = Dns.GetHostAddresses(parts[0]);
                            if (addresses.Length == 0)
                                throw new ArgumentException($"DNS resolution returned no addresses for: {parts[0]}");
                            targetIp = addresses[0];
                        }
                        catch (Exception ex) when (ex is SocketException || ex is ArgumentException)
                        {
                            throw new ArgumentException($"Failed to resolve hostname '{parts[0]}': {ex.Message}", nameof(addressOrCode), ex);
                        }
                    }
                }
                else if (IPAddress.TryParse(addressOrCode, out targetIp!))
                {
                    targetPort = 7777;
                }
                else
                {
                    throw new ArgumentException($"Invalid address or room code: {addressOrCode}", nameof(addressOrCode));
                }
            }

            lock (_lock)
            {
                CloseSocket();
                ResetSessionState();
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                {
                    Blocking = false
                };

                // Enable socket reuse and avoid ICMP unreachable errors on Windows
                const int SIO_UDP_CONNRESET = -1744830452;
                try
                {
                    _socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
                }
                catch (Exception ex)
                {
                    // Log but don't fail - this is Windows-specific optimization
                    System.Diagnostics.Debug.WriteLine($"Failed to set SIO_UDP_CONNRESET: {ex.Message}");
                }

                _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                _boundPort = ((IPEndPoint)_socket.LocalEndPoint!).Port;

                _remoteEndPoint = new IPEndPoint(targetIp, targetPort);
                _remotePeerId = null;
                _isHost = false;
                _isConnected = false;
                _connectionAttemptStartMs = GetTimestampMs();
                _lastHandshakeSentMs = 0;

                // Send initial handshake
                SendEnvelope(UdpFrameType.Handshake, channel: 0, sequence: 0, method: DeliveryMethod.Unreliable, payload: BuildHandshakePayload());
            }
        }

        /// <summary>
        /// BUG-B2: builds the Handshake payload — peer id (8 bytes) plus a flag byte telling
        /// the host whether this client carries a room code for HMAC challenge-response.
        /// Direct-IP clients (no code) keep the legacy unauthenticated path; the host must
        /// not challenge a client that cannot answer.
        /// </summary>
        private byte[] BuildHandshakePayload()
        {
            byte[] payload = new byte[9];
            Buffer.BlockCopy(BitConverter.GetBytes(LocalPeerId.Value), 0, payload, 0, 8);
            payload[8] = (byte)(_roomCode != null ? 1 : 0);
            return payload;
        }

        /// <summary>
        /// Drops the current peer but keeps the listening socket open (host only), so the
        /// partner can reconnect without the host having to re-open the session.
        /// BUG-N13: timeouts used to call <see cref="Disconnect(DisconnectReason)"/>, which
        /// closed the socket — the host stopped listening, so the client's advertised
        /// auto-reconnect could never complete after a transient outage.
        /// </summary>
        private void DropPeerForTimeout(DisconnectReason reason)
        {
            lock (_lock)
            {
                if (_socket == null) return;

                bool wasConnected = IsConnected;
                var peer = _remotePeerId ?? new NetworkPeerId(0);
                _isConnected = false;
                _remoteEndPoint = null;
                _remotePeerId = null;
                _authenticator.Reset();
                ResetSessionState();

                if (wasConnected)
                {
                    _pendingEvents.Enqueue(new DeferredEvent(DeferredEventKind.PeerDisconnected, peer, reason));
                }
            }
        }

        /// <summary>
        /// Disconnects gracefully from the current session.
        /// </summary>
        public void Disconnect() => Disconnect(DisconnectReason.GracefulDisconnect);

        /// <summary>
        /// Disconnects from the current session with the specified reason.
        /// </summary>
        /// <param name="reason">The reason for disconnection.</param>
        public void Disconnect(DisconnectReason reason)
        {
            lock (_lock)
            {
                // If socket is null, we're already fully disconnected
                if (_socket == null)
                {
                    return;
                }

                // Send disconnect packet if we have a remote endpoint
                if (_remoteEndPoint != null)
                {
                    try
                    {
                        SendEnvelope(UdpFrameType.Disconnect, 0, 0, DeliveryMethod.Unreliable, ReadOnlySpan<byte>.Empty);
                    }
                    catch (Exception ex)
                    {
                        // Log but don't fail - we're disconnecting anyway
                        System.Diagnostics.Debug.WriteLine($"[LiteNetLibTransport] Failed to send disconnect packet: {ex.Message}");
                    }
                }

                bool wasConnected = IsConnected;
                var peer = _remotePeerId ?? new NetworkPeerId(0);
                _isConnected = false;
                _remoteEndPoint = null;
                _remotePeerId = null;

                ResetSessionState();
                CloseSocket();

                if (wasConnected)
                {
                    // BUG-02: defer the event - handlers re-enter the transport (Send /
                    // Disconnect / session teardown) and must not run while _lock is held.
                    _pendingEvents.Enqueue(new DeferredEvent(DeferredEventKind.PeerDisconnected, peer, reason));
                }
            }
        }

        #endregion

        #region Sending and Event Polling

        /// <summary>
        /// Sends data to the specified target peer.
        /// </summary>
        /// <param name="target">The target peer to send to.</param>
        /// <param name="data">The data buffer to send.</param>
        /// <param name="offset">The offset within the buffer to start from.</param>
        /// <param name="length">The number of bytes to send.</param>
        /// <param name="method">The delivery method to use.</param>
        /// <param name="channel">The channel to send on.</param>
        public void Send(NetworkPeerId target, byte[] data, int offset, int length, DeliveryMethod method, byte channel)
        {
            lock (_lock)
            {
                if (!IsConnected || _remoteEndPoint == null || _socket == null) return;
                if (data == null) return;
                if (offset < 0 || length < 0 || offset + length > data.Length) return;
                if (_remotePeerId.HasValue && target != _remotePeerId.Value) return;

                uint seq;
                var sequenceKey = (channel, method);
                if (!_sendSequences.TryGetValue(sequenceKey, out seq))
                    seq = 0;
                _sendSequences[sequenceKey] = seq + 1;

                // Build envelope into a pooled buffer to avoid per-packet heap allocations
                // (BUG-P16). The rented buffer is returned immediately for unreliable
                // traffic; reliable traffic keeps it until the packet is acked/dropped.
                int envelopeLength = 8 + length;
                byte[] envelope = ArrayPool<byte>.Shared.Rent(envelopeLength);
                envelope[0] = (byte)UdpFrameType.Data;
                envelope[1] = channel;
                envelope[2] = (byte)method;
                Buffer.BlockCopy(BitConverter.GetBytes(seq), 0, envelope, 3, 4);
                envelope[7] = 0; // Flags/Reserved
                data.AsSpan(offset, length).CopyTo(envelope.AsSpan(8));

                bool sendOk = true;
                try
                {
                    _socket.SendTo(envelope, 0, envelopeLength, SocketFlags.None, _remoteEndPoint);
                }
                catch (SocketException ex) { System.Diagnostics.Debug.WriteLine($"Socket error: {ex.Message}"); sendOk = false; }
                catch (ObjectDisposedException ex) { System.Diagnostics.Debug.WriteLine($"Socket disposed: {ex.Message}"); sendOk = false; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Send error: {ex.Message}"); sendOk = false; }

                if (method == DeliveryMethod.ReliableOrdered)
                {
                    // Fix: Cap unacknowledged packets to prevent unbounded memory growth
                    if (_unacknowledged.Count >= MaxUnacknowledgedPackets)
                    {
                        // Drop oldest packet to make room (O(1) with LinkedList)
                        var oldest = _unacknowledged.First;
                        if (oldest != null)
                        {
                            ReturnPooledBuffer(oldest.Value.Envelope);
                            _unacknowledgedIndex.Remove((oldest.Value.Channel, oldest.Value.Sequence));
                            _unacknowledged.RemoveFirst();
                        }
                    }
                    var node = _unacknowledged.AddLast(new SentReliablePacket
                    {
                        Sequence = seq,
                        Channel = channel,
                        Envelope = envelope,
                        EnvelopeLength = envelopeLength,
                        LastSentTimeMs = GetTimestampMs(),
                        Retries = 0
                    });
                    _unacknowledgedIndex[(channel, seq)] = node;
                }
                else
                {
                    // Unreliable/sequenced: buffer is no longer needed after SendTo.
                    ReturnPooledBuffer(envelope);
                }
            }
        }

        /// <summary>
        /// Polls for incoming network events and processes them.
        /// </summary>
        public void PollEvents()
        {
            // BUG-02/BUG-03: socket I/O runs under _lock, but transport events are
            // delivered OUTSIDE the lock (see DrainPendingEvents) so handler callbacks
            // can re-enter the transport (Send / Disconnect / orchestration) without
            // reentrant locking.
            PollEventsLocked();
            DrainPendingEvents();
        }

        /// <summary>
        /// Polls socket I/O and connection state under <see cref="_lock"/>.
        /// Transport events raised here are queued for deferred delivery.
        /// </summary>
        private void PollEventsLocked()
        {
            lock (_lock)
            {
                if (_socket != null)
                {
                    long now = GetTimestampMs();

                    // 1. Receive incoming datagrams - reuse pre-allocated buffer
                    EndPoint senderEp = new IPEndPoint(IPAddress.Any, 0);

                    while (true)
                    {
                        Socket? socket = _socket;
                        if (socket == null) break;

                        int available;
                        try
                        {
                            available = socket.Available;
                        }
                        catch (SocketException)
                        {
                            break;
                        }
                        catch (ObjectDisposedException)
                        {
                            break;
                        }

                        if (available <= 0) break;

                        // Fresh sender endpoint per iteration to avoid stale data
                        senderEp = new IPEndPoint(IPAddress.Any, 0);

                        try
                        {
                            int received = socket.ReceiveFrom(_recvBuffer, ref senderEp);
                            if (received == 0) break;
                            if (received >= 8) // Minimum envelope header
                            {
                                ProcessIncomingDatagram(_recvBuffer.AsSpan(0, received), senderEp);
                            }
                        }
                        catch (SocketException)
                        {
                            break;
                        }
                        catch (ObjectDisposedException)
                        {
                            break;
                        }
                    }

                    // Client-side connection timeout: avoid stuck "Connecting" state (UX)
                    if (!IsHost && !IsConnected && _remoteEndPoint != null && _socket != null && _connectionAttemptStartMs > 0)
                    {
                        if (GetTimestampMs() - _connectionAttemptStartMs > AdaptiveConnectionTimeoutMs)
                        {
                            var peer = _remotePeerId ?? new NetworkPeerId(0);
                            _connectionAttemptStartMs = 0;
                            ResetSessionState();
                            CloseSocket();
                            _pendingEvents.Enqueue(new DeferredEvent(DeferredEventKind.PeerDisconnected, peer, DisconnectReason.Timeout));
                            return;
                        }
                    }

                    // BUG-A3: retransmit the handshake every 500 ms while connecting — both
                    // the client's Handshake and the host's HandshakeAck travel unreliably,
                    // so losing either one used to stall the handshake until the timeout.
                    if (!IsHost && !IsConnected && _connectionAttemptStartMs > 0 && _socket != null)
                    {
                        if (now - _lastHandshakeSentMs >= 500)
                        {
                            _lastHandshakeSentMs = now;
                            SendEnvelope(UdpFrameType.Handshake, 0, 0, DeliveryMethod.Unreliable, BuildHandshakePayload());
                        }
                    }

                    // Heartbeat timeout: detect peer disappearance (host AND client —
                    // BUG-B1: a client whose host crashed or dropped previously never
                    // timed out and stayed "Connected" forever)
                    if (IsConnected && _remoteEndPoint != null && _socket != null && _lastReceivedMs > 0)
                    {
                        if (GetTimestampMs() - _lastReceivedMs > AdaptiveHeartbeatTimeoutMs)
                        {
                            // BUG-N13: a host must keep listening so the partner can reconnect.
                            if (IsHost) DropPeerForTimeout(DisconnectReason.Timeout);
                            else Disconnect(DisconnectReason.Timeout);
                            return;
                        }
                    }

                    // Roll the unreliable packet-loss window (diagnostics overlay).
                    UpdatePacketLossWindow(now);

                    // Early exit if disconnected or socket closed during datagram processing
                    if (_socket == null || !IsConnected || _remoteEndPoint == null)
                    {
                        return;
                    }

                    // 2. Retransmit unacknowledged reliable packets
                    bool connectionLost = false;
                    var node = _unacknowledged.First;
                    while (node != null)
                    {
                        var next = node.Next;
                        var item = node.Value;
                        // Exponential backoff: base = max(100, RTT), doubled per retry, capped at 2000ms.
                        // BUG-N16: the fixed 100 ms base starved the first retry on WAN links whose
                        // RTT alone exceeds 100 ms, so a single lost packet took 100 ms + 200 ms + ...
                        // before the first retransmission even had a chance to land.
                        int baseBackoffMs = Math.Max(100, _rttMs);
                        int backoffMs = Math.Min(baseBackoffMs * (1 << Math.Min(item.Retries, 4)), 2000);
                        if (now - item.LastSentTimeMs > backoffMs)
                        {
                            if (item.Retries >= 10)
                            {
                                // Connection lost - mark for disconnect after loop
                                connectionLost = true;
                                break;
                            }

                            item.Retries++;
                            item.LastSentTimeMs = now;
                            node.Value = item;

                            if (_remoteEndPoint != null && _socket != null)
                            {
                                try
                                {
                                    _socket.SendTo(item.Envelope, 0, item.EnvelopeLength, SocketFlags.None, _remoteEndPoint);
                                }
                                catch (SocketException ex) { System.Diagnostics.Debug.WriteLine($"Socket error: {ex.Message}"); }
                                catch (ObjectDisposedException ex) { System.Diagnostics.Debug.WriteLine($"Socket disposed: {ex.Message}"); }
                            }
                        }
                        node = next;
                    }

                    if (connectionLost)
                    {
                        // BUG-N13: keep the host listening for a reconnect.
                        if (IsHost) DropPeerForTimeout(DisconnectReason.Timeout);
                        else Disconnect(DisconnectReason.Timeout);
                        return;
                    }

                    // 3. Heartbeat ping
                    if (IsConnected && _remoteEndPoint != null && _socket != null)
                    {
                        if (now - _lastPingSentMs > 1000)
                        {
                            _lastPingSentMs = now;
                            byte[] timestampPayload = BitConverter.GetBytes(now);
                            SendEnvelope(UdpFrameType.Ping, 0, 0, DeliveryMethod.Unreliable, timestampPayload);
                        }
                    }
                }
            }
        }

        #endregion

        #region Incoming Packet Processing

        private void ProcessIncomingDatagram(ReadOnlySpan<byte> datagram, EndPoint sender)
        {
            // Guard against malformed/tiny packets
            if (datagram.Length < 8) return;

            try
            {
                UdpFrameType frameType = (UdpFrameType)datagram[0];
                byte channel = datagram[1];
                DeliveryMethod method = (DeliveryMethod)datagram[2];
                uint sequence = BitConverter.ToUInt32(datagram.Slice(3, 4));
                ReadOnlySpan<byte> payload = datagram.Slice(8);

                // Validate enum ranges
                if (!Enum.IsDefined(typeof(UdpFrameType), frameType)) return;
                if (!Enum.IsDefined(typeof(DeliveryMethod), method)) return;

                // Sender validation FIRST: when connected, only accept from the known remote
                // endpoint. BUG-N2: this must run before the rate limiter and before
                // _lastReceivedMs is refreshed — otherwise an off-path/spoofed datagram
                // could drain the real peer's token bucket and keep a dead session alive.
                if (IsConnected && _remoteEndPoint != null)
                {
                    if (sender is IPEndPoint remoteEp && _remoteEndPoint is IPEndPoint knownEp)
                    {
                        if (!remoteEp.Address.Equals(knownEp.Address) || remoteEp.Port != knownEp.Port)
                            return; // Drop packet from unknown sender
                    }
                }

                // Track last received time for heartbeat timeout (validated sender only)
                _lastReceivedMs = GetTimestampMs();

                // Rate limiting: check if peer is allowed to send
                if (_remotePeerId.HasValue && !_rateLimiter.TryConsume(_remotePeerId.Value))
                {
                    System.Diagnostics.Debug.WriteLine($"[LiteNetLibTransport] Rate limit exceeded for peer {_remotePeerId.Value}");
                    return;
                }

                switch (frameType)
                {
                    case UdpFrameType.Handshake:
                        if (IsHost && IsConnected)
                        {
                            // BUG-A3: our HandshakeAck travels unreliably and may have been
                            // lost — the client retried its handshake because it never
                            // connected. Re-send the ack so the session can complete.
                            SendEnvelope(UdpFrameType.HandshakeAck, 0, 0, DeliveryMethod.Unreliable, BitConverter.GetBytes(LocalPeerId.Value));
                            break;
                        }

                        if (IsHost && !IsConnected)
                        {
                            // Validate sender is not ourselves
                            if (sender is IPEndPoint ep && ep.Port == BoundPort &&
                                (ep.Address.Equals(IPAddress.Loopback) || ep.Address.Equals(IPAddress.Any)))
                            {
                                // Only ignore if it's truly from ourselves
                                if (ep.Address.Equals(IPAddress.Loopback) && ep.Port == BoundPort)
                                    break; // Ignore self-handshake
                            }

                            // If we already have a remote endpoint, only accept from same sender
                            if (_remoteEndPoint != null)
                            {
                                if (_remoteEndPoint is IPEndPoint knownEp && sender is IPEndPoint remoteEp)
                                {
                                    if (!remoteEp.Address.Equals(knownEp.Address) || remoteEp.Port != knownEp.Port)
                                        break; // Drop handshake from different sender
                                }
                            }
                            else
                            {
                                _remoteEndPoint = sender;
                            }

                            ulong clientPeerValue = payload.Length >= 8 ? BitConverter.ToUInt64(payload) : 2;
                            if (payload.Length > 9) clientPeerValue = 2; // Cap oversized payloads
                            _remotePeerId = new NetworkPeerId(clientPeerValue, "Client");

                            // BUG-B2: only challenge clients that announced a room code (flag
                            // byte in the handshake payload). A direct-IP client has no shared
                            // secret and could never answer — it keeps the legacy open path.
                            bool clientHasCode = payload.Length >= 9 && payload[8] != 0;
                            if (_roomCode != null && clientHasCode)
                            {
                                if (_authenticator.TryGetPendingNonce(out byte[]? pendingNonce))
                                {
                                    // BUG-A3: duplicate handshake while auth is pending —
                                    // resend the SAME nonce so the client's retransmitted
                                    // response stays valid (a fresh nonce would invalidate
                                    // any response already in flight).
                                    SendEnvelope(UdpFrameType.Challenge, 0, 0, DeliveryMethod.Unreliable, pendingNonce);
                                }
                                else
                                {
                                    byte[] nonce = _authenticator.GenerateNonce();
                                    SendEnvelope(UdpFrameType.Challenge, 0, 0, DeliveryMethod.Unreliable, nonce);
                                }
                            }
                            else
                            {
                                // No room code on either side, fall back to old behavior (no authentication)
                                _isConnected = true;
                                SendEnvelope(UdpFrameType.HandshakeAck, 0, 0, DeliveryMethod.Unreliable, BitConverter.GetBytes(LocalPeerId.Value));
                                _pendingEvents.Enqueue(new DeferredEvent(DeferredEventKind.PeerConnected, _remotePeerId.Value));
                            }
                        }
                        break;

                    case UdpFrameType.Challenge:
                        if (!IsHost && !IsConnected && _roomCode != null)
                        {
                            // Received challenge from host, compute HMAC and send response
                            if (payload.Length >= 16)
                            {
                                byte[] nonce = payload.Slice(0, 16).ToArray();
                                byte[] signature = _authenticator.ComputeSignature(nonce, _roomCode);
                                SendEnvelope(UdpFrameType.ChallengeResponse, 0, 0, DeliveryMethod.Unreliable, signature);
                            }
                        }
                        break;

                    case UdpFrameType.ChallengeResponse:
                        if (IsHost && !IsConnected && _roomCode != null)
                        {
                            // Validate sender matches expected remote endpoint
                            if (_remoteEndPoint != null && sender is IPEndPoint remoteEp && _remoteEndPoint is IPEndPoint knownEp)
                            {
                                if (!remoteEp.Address.Equals(knownEp.Address) || remoteEp.Port != knownEp.Port)
                                    break; // Drop response from different sender
                            }

                            // Validate signature
                            if (payload.Length >= 32 && _authenticator.ValidateSignature(payload.Slice(0, 32).ToArray(), _roomCode))
                            {
                                _isConnected = true;
                                SendEnvelope(UdpFrameType.HandshakeAck, 0, 0, DeliveryMethod.Unreliable, BitConverter.GetBytes(LocalPeerId.Value));
                                _pendingEvents.Enqueue(new DeferredEvent(DeferredEventKind.PeerConnected, _remotePeerId!.Value));
                            }
                            else
                            {
                                // Invalid signature, reset and ignore
                                _remoteEndPoint = null;
                                _remotePeerId = null;
                                _authenticator.Reset();
                            }
                        }
                        break;

                    case UdpFrameType.HandshakeAck:
                        if (!IsHost && !IsConnected)
                        {
                            // Validate sender matches expected remote endpoint
                            if (_remoteEndPoint != null)
                            {
                                if (_remoteEndPoint is IPEndPoint knownEp && sender is IPEndPoint remoteEp)
                                {
                                    if (!remoteEp.Address.Equals(knownEp.Address) || remoteEp.Port != knownEp.Port)
                                        break; // Drop ack from unknown sender
                                }
                            }
                            else
                            {
                                _remoteEndPoint = sender;
                            }

                            ulong hostPeerValue = payload.Length >= 8 ? BitConverter.ToUInt64(payload) : 1;
                            if (payload.Length > 8) hostPeerValue = 1; // Cap oversized payloads
                            _remotePeerId = new NetworkPeerId(hostPeerValue, "Host");
                            _isConnected = true;

                            _pendingEvents.Enqueue(new DeferredEvent(DeferredEventKind.PeerConnected, _remotePeerId.Value));
                        }
                        break;

                    case UdpFrameType.Data:
                        // Handle incoming data first
                        HandleIncomingData(channel, method, sequence, payload);

                        // If reliable, send ACK after successful dispatch
                        if (method == DeliveryMethod.ReliableOrdered && IsConnected)
                        {
                            byte[] ackPayload = new byte[5];
                            ackPayload[0] = channel;
                            Buffer.BlockCopy(BitConverter.GetBytes(sequence), 0, ackPayload, 1, 4);
                            SendEnvelope(UdpFrameType.Ack, channel, sequence, DeliveryMethod.Unreliable, ackPayload);
                        }
                        break;

                    case UdpFrameType.Ack:
                        if (payload.Length >= 5)
                        {
                            byte ackChannel = payload[0];
                            uint ackSeq = BitConverter.ToUInt32(payload.Slice(1, 4));

                            // O(1) removal using index
                            if (_unacknowledgedIndex.TryGetValue((ackChannel, ackSeq), out var node))
                            {
                                ReturnPooledBuffer(node.Value.Envelope);
                                _unacknowledged.Remove(node);
                                _unacknowledgedIndex.Remove((ackChannel, ackSeq));
                            }
                        }
                        break;

                    case UdpFrameType.Ping:
                        // Echo only the 8-byte timestamp to prevent amplification
                        if (payload.Length >= 8)
                        {
                            SendEnvelope(UdpFrameType.Pong, 0, 0, DeliveryMethod.Unreliable, payload.Slice(0, 8));
                        }
                        break;

                    case UdpFrameType.Pong:
                        if (payload.Length >= 8)
                        {
                            long sentTime = BitConverter.ToInt64(payload);
                            long rtt = GetTimestampMs() - sentTime;
                            if (rtt >= 0 && rtt < 2000)
                            {
                                _rttMs = (int)rtt;
                            }
                        }
                        break;

                    case UdpFrameType.Disconnect:
                        if (IsConnected)
                        {
                            var peer = _remotePeerId ?? new NetworkPeerId(0);
                            _isConnected = false;
                            _remoteEndPoint = null;
                            _remotePeerId = null;
                            ResetSessionState();
                            // BUG-02: defer the event - the handler re-enters the transport
                            // (TriggerTeamWipe в†’ ExitSpectatorMode в†’ Disconnect) and must
                            // not run while _lock is held.
                            _pendingEvents.Enqueue(new DeferredEvent(DeferredEventKind.PeerDisconnected, peer, DisconnectReason.GracefulDisconnect));
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LiteNetLibTransport] ProcessIncomingDatagram error: {ex.Message}");
            }
        }

        private void HandleIncomingData(byte channel, DeliveryMethod method, uint sequence, ReadOnlySpan<byte> payload)
        {
            try
            {
                byte[] data = payload.ToArray();

                if (method == DeliveryMethod.ReliableOrdered)
                {
                    if (!_pendingReliable.TryGetValue(channel, out var pending))
                    {
                        pending = new SortedDictionary<uint, byte[]>();
                        _pendingReliable[channel] = pending;
                    }

                    _expectedReceiveSequences.TryGetValue(channel, out uint expected);

                    // Cap pending buffer size to prevent unbounded growth under packet loss
                    const int MaxPendingPerChannel = 256;
                    if (pending.Count >= MaxPendingPerChannel)
                    {
                        // First remove stale entries (below expected)
                        var staleKeys = new List<uint>();
                        foreach (var key in pending.Keys)
                        {
                            if (key < expected)
                                staleKeys.Add(key);
                        }
                        foreach (var key in staleKeys)
                        {
                            pending.Remove(key);
                        }

                        // If still over cap, remove oldest entries. On a SortedDictionary the
                        // enumerator walks directly to the minimum key (O(log n) per removal).
                        while (pending.Count >= MaxPendingPerChannel)
                        {
                            var first = pending.Keys.GetEnumerator();
                            if (!first.MoveNext()) break;
                            pending.Remove(first.Current);
                        }
                    }

                    if (sequence == expected)
                    {
                        DispatchData(data);
                        if (!IsConnected) return;

                        expected++;
                        _expectedReceiveSequences[channel] = expected;

                        while (pending.TryGetValue(expected, out var nextData))
                        {
                            pending.Remove(expected);
                            DispatchData(nextData);
                            if (!IsConnected) return;

                            expected++;
                            _expectedReceiveSequences[channel] = expected;
                        }
                    }
                    else if (sequence > expected)
                    {
                        pending[sequence] = data;
                    }
                    // If sequence < expected, it's a duplicate/old packet - drop silently
                }
                else if (method == DeliveryMethod.Sequenced)
                {
                    _latestSequencedReceived.TryGetValue(channel, out uint latest);
                    // BUG-N4: `latest == 0` made every sequence-0 packet look acceptable, so
                    // duplicates of the first packet were delivered repeatedly. Track whether
                    // the channel has seen any packet instead of using 0 as a sentinel.
                    if (!_sequencedInitialized.Contains(channel))
                    {
                        _sequencedInitialized.Add(channel);
                        _latestSequencedReceived[channel] = sequence;
                        DispatchData(data);
                    }
                    else if (sequence > latest)
                    {
                        _latestSequencedReceived[channel] = sequence;
                        DispatchData(data);
                    }
                }
                else
                {
                    TrackUnreliableSequences(channel, sequence);
                    DispatchData(data);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LiteNetLibTransport] HandleIncomingData error: {ex.Message}");
            }
        }

        /// <summary>
        /// Tracks unreliable sequence gaps as a packet-loss estimate. Reliability is not
        /// repaired for this delivery method, so every missing sequence was genuinely lost.
        /// </summary>
        /// <param name="channel">Channel the packet arrived on.</param>
        /// <param name="sequence">Sequence number carried by the packet.</param>
        private void TrackUnreliableSequences(byte channel, uint sequence)
        {
            if (_lastUnreliableSequences.TryGetValue(channel, out uint last))
            {
                uint delta = unchecked(sequence - last);
                if (delta == 0)
                {
                    // Duplicate or reordered packet — not a loss sample.
                    return;
                }

                if (delta < 256)
                {
                    _lossPacketsExpected += delta;
                    if (delta > 1)
                    {
                        _lossPacketsLost += delta - 1;
                    }
                }
                // else: sequence jump after a reset/wrap — ignore the sample.
            }
            else
            {
                _lossPacketsExpected++;
            }

            _lastUnreliableSequences[channel] = sequence;
        }

        /// <summary>
        /// Rolls the packet-loss window forward (once every 5 seconds) and publishes the
        /// estimate used by the diagnostics overlay.
        /// </summary>
        private void UpdatePacketLossWindow(long now)
        {
            if (_lossWindowStartMs == 0)
            {
                _lossWindowStartMs = now;
                return;
            }

            if (now - _lossWindowStartMs < 5000) return;

            if (_lossPacketsExpected > 0)
            {
                int percent = (int)Math.Round(_lossPacketsLost * 100.0 / _lossPacketsExpected);
                _packetLossPercent = Math.Clamp(percent, 0, 100);
            }
            else
            {
                _packetLossPercent = 0;
            }

            _lossPacketsExpected = 0;
            _lossPacketsLost = 0;
            _lossWindowStartMs = now;
        }

        private void DispatchData(byte[] data)
        {
            try
            {
                // Fix: Ensure we have a valid peer and connection before dispatching
                if (!_remotePeerId.HasValue || !IsConnected)
                {
                    // No remote peer established or not connected, drop the data
                    return;
                }
                if (data == null || data.Length == 0) return;
                var peer = _remotePeerId.Value;
                // BUG-03: defer dispatch - handlers call transport.Send() and must not
                // execute while _lock is held.
                _pendingEvents.Enqueue(new DeferredEvent(DeferredEventKind.DataReceived, peer, data: data));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LiteNetLibTransport] DispatchData error: {ex.Message}");
            }
        }

        /// <summary>
        /// Delivers queued transport events on the caller's thread, outside <see cref="_lock"/>.
        /// Handlers may re-enter the transport (Send / Disconnect / StartHost); any events they
        /// enqueue are picked up by the following drain iteration. Bounded to avoid pathological
        /// loops caused by handler feedback.
        /// </summary>
        private void DrainPendingEvents()
        {
            for (int guard = 0; guard < 1024; guard++)
            {
                DeferredEvent[] batch;
                lock (_lock)
                {
                    if (_pendingEvents.Count == 0) return;
                    batch = _pendingEvents.ToArray();
                    _pendingEvents.Clear();
                }

                foreach (var e in batch)
                {
                    switch (e.Kind)
                    {
                        case DeferredEventKind.PeerConnected:
                            OnPeerConnected?.Invoke(e.Peer);
                            break;

                        case DeferredEventKind.PeerDisconnected:
                            OnPeerDisconnected?.Invoke(e.Peer, e.Reason);
                            break;

                        case DeferredEventKind.DataReceived:
                            // The connection may have been dropped by an earlier handler in this
                            // batch (e.g. Disconnect inside a data callback) - drop stale data.
                            if (!_isConnected || e.Data == null) break;
                            OnDataReceived?.Invoke(e.Peer, e.Data, 0, e.Data.Length);
                            break;
                    }
                }
            }
        }

        #endregion

        #region Helper Methods

        private static void ReturnPooledBuffer(byte[]? buffer)
        {
            if (buffer != null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private void SendEnvelope(UdpFrameType type, byte channel, uint sequence, DeliveryMethod method, ReadOnlySpan<byte> payload)
        {
            if (_socket == null || _remoteEndPoint == null) return;

            byte[] envelope = BuildEnvelope(type, channel, sequence, method, payload);
            try
            {
                _socket.SendTo(envelope, _remoteEndPoint);
            }
            catch (SocketException ex) { System.Diagnostics.Debug.WriteLine($"Socket error: {ex.Message}"); }
            catch (ObjectDisposedException ex) { System.Diagnostics.Debug.WriteLine($"Socket disposed: {ex.Message}"); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"SendEnvelope error: {ex.Message}"); }
        }

        private byte[] BuildEnvelope(UdpFrameType type, byte channel, uint sequence, DeliveryMethod method, ReadOnlySpan<byte> payload)
        {
            byte[] buffer = new byte[8 + payload.Length];
            buffer[0] = (byte)type;
            buffer[1] = channel;
            buffer[2] = (byte)method;
            Buffer.BlockCopy(BitConverter.GetBytes(sequence), 0, buffer, 3, 4);
            buffer[7] = 0; // Flags/Reserved
            payload.CopyTo(buffer.AsSpan(8));
            return buffer;
        }

        private void CloseSocket()
        {
            var socket = _socket;
            _socket = null; // Clear reference first to prevent use-after-close
            if (socket != null)
            {
                try
                {
                    socket.Shutdown(SocketShutdown.Both);
                }
                catch (Exception) { /* Socket may not be connected */ }
                try
                {
                    socket.Close();
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Socket close error: {ex.Message}"); }
            }
            _boundPort = 0;
        }

        /// <summary>
        /// Disposes the transport and releases all resources.
        /// </summary>
        public void Dispose()
        {
            Disconnect();
        }

        #endregion
    }
}
