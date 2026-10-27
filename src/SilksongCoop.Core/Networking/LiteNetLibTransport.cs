using System;
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
            public long LastSentTimeMs;
            public int Retries;
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

        private readonly Dictionary<byte, uint> _sendSequences = new();
        private readonly Dictionary<byte, uint> _expectedReceiveSequences = new();
        private readonly Dictionary<byte, uint> _latestSequencedReceived = new();
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

        // Pre-allocated receive buffer to avoid per-poll GC pressure
        private readonly byte[] _recvBuffer = new byte[2048];

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
            _sendSequences.Clear();
            _expectedReceiveSequences.Clear();
            _latestSequencedReceived.Clear();
            _pendingReliable.Clear();
            _unacknowledged.Clear();
            _unacknowledgedIndex.Clear();
            _lastPingSentMs = 0;
            _lastReceivedMs = 0;
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
                _roomCode = addressOrCode; // Store for challenge-response authentication
            }
            else
            {
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

                // Send initial handshake
                SendEnvelope(UdpFrameType.Handshake, channel: 0, sequence: 0, method: DeliveryMethod.Unreliable, payload: BitConverter.GetBytes(LocalPeerId.Value));
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
                    OnPeerDisconnected?.Invoke(peer, reason);
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
                if (!_sendSequences.TryGetValue(channel, out seq))
                    seq = 0;
                _sendSequences[channel] = seq + 1;

                // Build envelope directly to avoid extra allocation
                byte[] envelope = BuildEnvelope(UdpFrameType.Data, channel, seq, method, data.AsSpan(offset, length));
                try
                {
                    _socket.SendTo(envelope, _remoteEndPoint);
                }
                catch (SocketException ex) { System.Diagnostics.Debug.WriteLine($"Socket error: {ex.Message}"); }
                catch (ObjectDisposedException ex) { System.Diagnostics.Debug.WriteLine($"Socket disposed: {ex.Message}"); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Send error: {ex.Message}"); }

                if (method == DeliveryMethod.ReliableOrdered)
                {
                    // Fix: Cap unacknowledged packets to prevent unbounded memory growth
                    if (_unacknowledged.Count >= MaxUnacknowledgedPackets)
                    {
                        // Drop oldest packet to make room (O(1) with LinkedList)
                        var oldest = _unacknowledged.First;
                        if (oldest != null)
                        {
                            _unacknowledgedIndex.Remove((oldest.Value.Channel, oldest.Value.Sequence));
                            _unacknowledged.RemoveFirst();
                        }
                    }
                    var node = _unacknowledged.AddLast(new SentReliablePacket
                    {
                        Sequence = seq,
                        Channel = channel,
                        Envelope = envelope,
                        LastSentTimeMs = GetTimestampMs(),
                        Retries = 0
                    });
                    _unacknowledgedIndex[(channel, seq)] = node;
                }
            }
        }

        /// <summary>
        /// Polls for incoming network events and processes them.
        /// </summary>
        public void PollEvents()
        {
            lock (_lock)
            {
                if (_socket == null) return;

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
                    if (GetTimestampMs() - _connectionAttemptStartMs > ConnectionTimeoutMs)
                    {
                        var peer = _remotePeerId ?? new NetworkPeerId(0);
                        _connectionAttemptStartMs = 0;
                        ResetSessionState();
                        CloseSocket();
                        OnPeerDisconnected?.Invoke(peer, DisconnectReason.Timeout);
                        return;
                    }
                }

                // Host-side heartbeat timeout: detect client disappearance
                if (IsHost && IsConnected && _remoteEndPoint != null && _socket != null && _lastReceivedMs > 0)
                {
                    if (GetTimestampMs() - _lastReceivedMs > HeartbeatTimeoutMs)
                    {
                        Disconnect(DisconnectReason.Timeout);
                        return;
                    }
                }

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
                    // Exponential backoff: 100ms, 200ms, 400ms, 800ms, capped at 2000ms
                    int backoffMs = Math.Min(100 * (1 << Math.Min(item.Retries, 4)), 2000);
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
                                _socket.SendTo(item.Envelope, _remoteEndPoint);
                            }
                            catch (SocketException ex) { System.Diagnostics.Debug.WriteLine($"Socket error: {ex.Message}"); }
                            catch (ObjectDisposedException ex) { System.Diagnostics.Debug.WriteLine($"Socket disposed: {ex.Message}"); }
                        }
                    }
                    node = next;
                }

                if (connectionLost)
                {
                    Disconnect(DisconnectReason.Timeout);
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

        #endregion

        #region Incoming Packet Processing

        private void ProcessIncomingDatagram(ReadOnlySpan<byte> datagram, EndPoint sender)
        {
            // Guard against malformed/tiny packets
            if (datagram.Length < 8) return;

            // Track last received time for heartbeat timeout
            _lastReceivedMs = GetTimestampMs();

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

                // Rate limiting: check if peer is allowed to send
                if (_remotePeerId.HasValue && !_rateLimiter.TryConsume(_remotePeerId.Value))
                {
                    System.Diagnostics.Debug.WriteLine($"[LiteNetLibTransport] Rate limit exceeded for peer {_remotePeerId.Value}");
                    return;
                }

                // Sender validation: when connected, only accept from known remote endpoint
                if (IsConnected && _remoteEndPoint != null)
                {
                    if (sender is IPEndPoint remoteEp && _remoteEndPoint is IPEndPoint knownEp)
                    {
                        if (!remoteEp.Address.Equals(knownEp.Address) || remoteEp.Port != knownEp.Port)
                            return; // Drop packet from unknown sender
                    }
                }

                switch (frameType)
                {
                    case UdpFrameType.Handshake:
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
                            if (payload.Length > 8) clientPeerValue = 2; // Cap oversized payloads
                            _remotePeerId = new NetworkPeerId(clientPeerValue, "Client");

                            // Send challenge instead of immediately connecting
                            if (_roomCode != null)
                            {
                                byte[] nonce = _authenticator.GenerateNonce();
                                SendEnvelope(UdpFrameType.Challenge, 0, 0, DeliveryMethod.Unreliable, nonce);
                            }
                            else
                            {
                                // No room code set, fall back to old behavior (no authentication)
                                _isConnected = true;
                                SendEnvelope(UdpFrameType.HandshakeAck, 0, 0, DeliveryMethod.Unreliable, BitConverter.GetBytes(LocalPeerId.Value));
                                OnPeerConnected?.Invoke(_remotePeerId.Value);
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
                                OnPeerConnected?.Invoke(_remotePeerId!.Value);
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

                            OnPeerConnected?.Invoke(_remotePeerId.Value);
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
                            // Defer event invocation to avoid reentrancy under lock
                            // Note: This still fires under lock in current design; documented as reentrant-safe
                            OnPeerDisconnected?.Invoke(peer, DisconnectReason.GracefulDisconnect);
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

                        // If still over cap, remove oldest entries
                        while (pending.Count >= MaxPendingPerChannel && pending.Count > 0)
                        {
                            uint oldestKey = 0;
                            bool found = false;
                            foreach (var key in pending.Keys)
                            {
                                oldestKey = key;
                                found = true;
                                break;
                            }
                            if (found)
                                pending.Remove(oldestKey);
                            else
                                break;
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
                    if (sequence > latest || latest == 0)
                    {
                        _latestSequencedReceived[channel] = sequence;
                        DispatchData(data);
                    }
                }
                else
                {
                    DispatchData(data);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LiteNetLibTransport] HandleIncomingData error: {ex.Message}");
            }
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
                OnDataReceived?.Invoke(peer, data, 0, data.Length);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LiteNetLibTransport] DispatchData error: {ex.Message}");
            }
        }

        #endregion

        #region Helper Methods

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
