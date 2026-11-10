using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace SilksongCoop.Core.Networking
{
    /// <summary>
    /// High-performance UDP socket network transport supporting unreliable, sequenced,
    /// reliable ordered and reliable unordered delivery with room-code and direct IP:port
    /// connection.
    /// </summary>
    public class LiteNetLibTransport : INetworkTransport, IMultiPeerTransport, IReliableDropDiagnostics
    {
        /// <summary>
        /// Wire protocol revision. Bumped when packet layouts change incompatibly; both
        /// peers must match or the handshake is refused.
        /// </summary>
        public const ushort ProtocolVersion = NetworkProtocol.Version;

        private const byte FlagMacPresent = 0x01;
        private const int MacSizeBytes = 16;

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
            public DeliveryMethod Method;
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
            public readonly int DataLength;
            public readonly bool DataPooled;
            public readonly long Generation;

            // Legacy five-parameter shape is used by existing reflection-based tests and
            // by peer lifecycle events. Data-payload events use the overload below.
            public DeferredEvent(DeferredEventKind kind, NetworkPeerId peer,
                DisconnectReason reason = DisconnectReason.None, byte[]? data = null,
                long generation = 0)
                : this(kind, peer, reason, data, data?.Length ?? 0, dataPooled: false, generation)
            {
            }

            public DeferredEvent(DeferredEventKind kind, NetworkPeerId peer,
                DisconnectReason reason, byte[]? data, int dataLength, bool dataPooled,
                long generation)
            {
                Kind = kind;
                Peer = peer;
                Reason = reason;
                Data = data;
                DataLength = dataLength >= 0 ? dataLength : data?.Length ?? 0;
                DataPooled = dataPooled;
                Generation = generation;
            }
        }

        #endregion

        #region Fields and Constants

        private Socket? _socket;
        private EndPoint? _remoteEndPoint;
        private NetworkPeerId? _remotePeerId;

        // Identity announced by the last unauthenticated handshake. The endpoint is NOT
        // committed until the HMAC challenge succeeds: otherwise the first datagram from an
        // unauthenticated sender permanently owned the single slot and blocked every other
        // client (issue #1.1). Each endpoint gets its own nonce, so a spoofed handshake
        // cannot replace the real client's pending challenge.
        private readonly byte[] _challengeKey = new byte[32];
        private byte[]? _clientChallenge;
        private long _clientChallengeReceivedAt;
        private const int ChallengeBytes = 16;
        private const int ChallengeResponseBytes = ChallengeBytes + sizeof(ulong) + 32;
        private const int ChallengeBucketMs = 5000;
        private readonly object _lock = new();
        // Serializes deferred-event drains across concurrent PollEvents callers. The same
        // object is reentrant on one thread, which preserves the handler re-entrancy guard.
        private readonly object _drainLock = new();
        private bool _drainRequested;
        private HMACSHA256? _hmac;
        private HMACSHA256? _receiveHmac;
        private uint _controlSequence;
        private readonly Dictionary<UdpFrameType, uint> _latestControl = new();
        private readonly Dictionary<byte, (uint Latest, ulong Seen)> _unreliableReplay = new();
        private readonly Queue<(NetworkPeerId Peer, byte[] Data, DeliveryMethod Method, byte Channel)> _reliableQueue = new();
        private const int ReliableQueueCapacity = 4096;

        /// <summary>Queues reliable application messages while the retransmit window is full.</summary>
        public bool QueueReliableWhenBusy { get; set; }
        private const int HmacHashBytes = 32;
        // Reused drain batch: ToArray() allocated once per PollEvents frame with events.
        private readonly List<DeferredEvent> _drainBatch = new List<DeferredEvent>(64);
        private bool _drainingEvents;
        private readonly RateLimiter _rateLimiter = new RateLimiter(maxTokens: 4096, refillRatePerSecond: 256);
        // Pre-authentication traffic cannot be keyed by a peer id yet. Buckets are keyed
        // by source endpoint so one flooding sender cannot starve a legitimate client's
        // handshake. The table is cleared if spoofed sources grow it past the cap.
        private readonly RateLimiter _preAuthLimiter = new RateLimiter(maxTokens: 32, refillRatePerSecond: 16);
        private const int MaxPreAuthBuckets = 1024;
        private const int MaxDatagramsPerPoll = 256;
        private readonly ConnectionAuthenticator _authenticator = new ConnectionAuthenticator();
        private readonly ulong _gameVersionTag;
        // Derived after the HMAC challenge; authenticates every post-handshake datagram
        // (data, ACK, ping/pong, disconnect). Null for legacy direct-IP no-code sessions.
        private byte[]? _sessionKey;
        private string? _roomCode;
        private bool _requireRoomCode = true;

        /// <summary>
        /// Defaults to true. Set false explicitly to allow unauthenticated direct-IP
        /// clients on a trusted private network.
        /// </summary>
        public bool RequireRoomCode
        {
            get => _requireRoomCode;
            set => _requireRoomCode = value;
        }

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
        private readonly Dictionary<(byte channel, DeliveryMethod method, uint sequence), LinkedListNode<SentReliablePacket>> _unacknowledgedIndex = new();
        private const int MaxUnacknowledgedPackets = NetworkLimits.ReliableBacklogCap;

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
        private long _sessionGeneration;

        // ReliableUnordered has its own sequence space but no ordering window. Duplicates
        // are possible because retransmissions are allowed, so remember recently delivered
        // sequences (bounded) and still ACK duplicates.
        private readonly Dictionary<byte, UnorderedReplayWindow> _receivedUnordered = new();

        private sealed class UnorderedReplayWindow
        {
            private const uint Capacity = 4096;
            private readonly uint[] _sequences = new uint[Capacity];
            private readonly bool[] _seen = new bool[Capacity];
            private uint _latest;
            private bool _initialized;

            public bool IsNew(uint sequence)
            {
                if (!_initialized || IsSequenceNewer(sequence, _latest)) return true;
                if (unchecked(_latest - sequence) >= Capacity) return false;
                uint slot = sequence % Capacity;
                return !_seen[slot] || _sequences[slot] != sequence;
            }

            public bool Accept(uint sequence)
            {
                if (!IsNew(sequence)) return false;
                if (!_initialized || IsSequenceNewer(sequence, _latest))
                {
                    _latest = sequence;
                    _initialized = true;
                }
                uint slot = sequence % Capacity;
                _sequences[slot] = sequence;
                _seen[slot] = true;
                return true;
            }
        }

        // BUG-A3: last time the client sent its Handshake frame. The initial datagram and
        // the host's HandshakeAck both travel unreliably, so the client must retransmit
        // while the handshake is still pending.
        private long _lastHandshakeSentMs;

        // Pre-allocated receive buffer to avoid per-poll GC pressure
        private const int MaxDatagramSize = 2048;
        // Leave one extra byte to identify truncation on platforms that return a prefix.
        private readonly byte[] _recvBuffer = new byte[MaxDatagramSize + 1];
        // Reused only while connected: handshake frames may store the endpoint as a
        // dictionary key, so those paths keep a fresh IPEndPoint.
        private readonly IPEndPoint _connectedSenderEndPoint = new IPEndPoint(IPAddress.Any, 0);

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

        // Monotonic clock: DateTime.UtcNow jumps when the system clock is adjusted
        // (NTP/VPN/manual change) and broke RTT, heartbeat and retry timers. Stopwatch
        // is unaffected by wall-clock changes.
        private static readonly Stopwatch _clock = Stopwatch.StartNew();
        private static long GetTimestampMs() => _clock.ElapsedMilliseconds;

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

        // Cached single-peer snapshot for ConnectedPeers. The UDP transport holds one
        // remote peer at a time, so the read-only view is reused until the peer id changes.
        private IReadOnlyList<NetworkPeerId> _connectedPeersSnapshot = System.Array.Empty<NetworkPeerId>();
        private NetworkPeerId? _connectedPeersSnapshotPeer;

        public System.Collections.Generic.IReadOnlyList<NetworkPeerId> ConnectedPeers
        {
            get
            {
                // Connection state is mutated under _lock; take the same lock so a peer
                // change cannot pair one peer id with another peer's cached snapshot.
                lock (_lock)
                {
                    NetworkPeerId? peer = _remotePeerId;
                    if (!peer.HasValue) return System.Array.Empty<NetworkPeerId>();

                    NetworkPeerId? cachedPeer = _connectedPeersSnapshotPeer;
                    if (cachedPeer.HasValue &&
                        cachedPeer.Value.Value == peer.Value.Value &&
                        string.Equals(cachedPeer.Value.DisplayName, peer.Value.DisplayName, StringComparison.Ordinal))
                    {
                        return _connectedPeersSnapshot;
                    }

                    var snapshot = System.Array.AsReadOnly(new[] { peer.Value });
                    _connectedPeersSnapshot = snapshot;
                    _connectedPeersSnapshotPeer = peer;
                    return snapshot;
                }
            }
        }
        public int RoundTripTimeMs => _rttMs;
        /// <summary>
        /// Unreliable packet loss estimate (0-100) over the last few seconds, based on
        /// sequence gaps on the unreliable channel.
        /// </summary>
        public int PacketLossPercent => _packetLossPercent;
        public int BoundPort => _boundPort;

        /// <inheritdoc />
        public int ReliableBacklogCount
        {
            get { lock (_lock) { return _unacknowledged.Count; } }
        }

        private long _droppedReliablePackets;
        private long _lastBacklogWarningMs;

        /// <inheritdoc />
        public long DroppedReliablePackets => System.Threading.Interlocked.Read(ref _droppedReliablePackets);

        #endregion

        #region Connection Management

        /// <summary>
        /// Initializes a new instance of the LiteNetLibTransport class.
        /// </summary>
        /// <param name="peerId">The unique identifier for this peer.</param>
        /// <param name="displayName">Optional display name for this peer.</param>
        /// <param name="gameVersion">Exact game build expected by this transport, when used in-game.</param>
        public LiteNetLibTransport(ulong peerId = 1, string displayName = "Player", string? gameVersion = null)
        {
            _localPeerId = new NetworkPeerId(peerId, displayName);
            _gameVersionTag = NetworkProtocol.GameVersionTag(gameVersion);
        }

        /// <summary>
        /// Assigns the local peer identity used in handshakes and in the
        /// <c>OriginPeerId</c> field of world-state packets. The menu calls this before
        /// hosting (id 1) and before joining (id 2): without it both sides used id 1 and
        /// revision tracking could not distinguish the origin.
        /// </summary>
        public void SetLocalPeerId(ulong peerId, string displayName = "Player")
        {
            if (peerId == 0)
                throw new ArgumentOutOfRangeException(nameof(peerId), "Peer id 0 is reserved");

            _localPeerId = new NetworkPeerId(peerId, displayName);
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
            // Any event already queued belongs to the session being torn down. It must not
            // be delivered after a new StartHost/Connect (for example a stale disconnect
            // event tearing down a freshly started session).
            ClearPendingEvents();
            _sessionGeneration++;

            foreach (var node in _unacknowledged)
            {
                ReturnPooledBuffer(node.Envelope);
            }
            _reliableQueue.Clear();
            _latestControl.Clear();
            _unreliableReplay.Clear();
            _controlSequence = 0;
            _sendSequences.Clear();
            _expectedReceiveSequences.Clear();
            _latestSequencedReceived.Clear();
            _sequencedInitialized.Clear();
            _pendingReliable.Clear();
            _unacknowledged.Clear();
            _unacknowledgedIndex.Clear();
            _receivedUnordered.Clear();
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
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(_challengeKey);
            _clientChallenge = null;
            _clientChallengeReceivedAt = 0;
            ClearSessionKey();
            _authenticator.Reset();
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

                Socket? socket = null;
                try
                {
                    socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                    {
                        Blocking = false
                    };

                    // Enable socket reuse and avoid ICMP unreachable errors on Windows
                    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    const int SIO_UDP_CONNRESET = -1744830452;
                    try
                    {
                        socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
                    }
                    catch (Exception ex)
                    {
                        // Log but don't fail - this is Windows-specific optimization
                        CoopLog.Warn($"Failed to set SIO_UDP_CONNRESET: {ex.Message}");
                    }

                    socket.Bind(new IPEndPoint(IPAddress.Any, port));
                    _boundPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
                    _socket = socket;
                    _remoteEndPoint = null;
                    _remotePeerId = null;
                    _isHost = true;
                    _isConnected = false;
                }
                catch
                {
                    if (socket != null)
                    {
                        try { socket.Close(); } catch { /* best effort */ }
                    }
                    _socket = null;
                    _boundPort = 0;
                    _remoteEndPoint = null;
                    _remotePeerId = null;
                    _isHost = false;
                    _isConnected = false;
                    throw;
                }
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

            if (RoomCodeCodec.TryCanonicalize(addressOrCode, out string canonicalCode))
            {
                RoomCodeCodec.TryDecode(canonicalCode, out IPAddress decodedIp, out int decodedPort);
                targetIp = decodedIp;
                targetPort = decodedPort;
                // BUG-B2: store the CANONICAL code form — the host HMACs against its own
                // Encode() output, so both sides must key the challenge-response on the
                // exact same string (the raw input may differ in case/hyphenation).
                // TryCanonicalize preserves the random secret suffix (issue #1.1): the
                // legacy 12-character code is derivable from IP:port and is not a secret.
                _roomCode = canonicalCode;
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
                    throw new ArgumentException("Invalid address or room code.", nameof(addressOrCode));
                }
            }

            lock (_lock)
            {
                CloseSocket();
                ResetSessionState();

                Socket? socket = null;
                try
                {
                    socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                    {
                        Blocking = false
                    };

                    // Enable socket reuse and avoid ICMP unreachable errors on Windows
                    const int SIO_UDP_CONNRESET = -1744830452;
                    try
                    {
                        socket.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
                    }
                    catch (Exception ex)
                    {
                        // Log but don't fail - this is Windows-specific optimization
                        CoopLog.Warn($"Failed to set SIO_UDP_CONNRESET: {ex.Message}");
                    }

                    socket.Bind(new IPEndPoint(IPAddress.Any, 0));
                    _boundPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
                    _socket = socket;

                    _remoteEndPoint = new IPEndPoint(targetIp, targetPort);
                    _remotePeerId = null;
                    _isHost = false;
                    _isConnected = false;
                    _connectionAttemptStartMs = GetTimestampMs();
                    _lastHandshakeSentMs = 0;

                    // Send initial handshake
                    SendEnvelope(UdpFrameType.Handshake, channel: 0, sequence: 0, method: DeliveryMethod.Unreliable, payload: BuildHandshakePayload());
                }
                catch
                {
                    if (socket != null)
                    {
                        try { socket.Close(); } catch { /* best effort */ }
                    }
                    _socket = null;
                    _boundPort = 0;
                    _remoteEndPoint = null;
                    _remotePeerId = null;
                    _isHost = false;
                    _isConnected = false;
                    _connectionAttemptStartMs = 0;
                    throw;
                }
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
            byte[] payload = new byte[_gameVersionTag == 0 ? 11 : 19];
            Buffer.BlockCopy(BitConverter.GetBytes(LocalPeerId.Value), 0, payload, 0, 8);
            payload[8] = (byte)(_roomCode != null ? 1 : 0);
            Buffer.BlockCopy(BitConverter.GetBytes(ProtocolVersion), 0, payload, 9, 2);
            if (_gameVersionTag != 0)
                BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(11), _gameVersionTag);
            return payload;
        }

        private byte[] BuildHandshakeAckPayload()
        {
            byte[] payload = new byte[_gameVersionTag == 0 ? 10 : 18];
            Buffer.BlockCopy(BitConverter.GetBytes(LocalPeerId.Value), 0, payload, 0, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(ProtocolVersion), 0, payload, 8, 2);
            if (_gameVersionTag != 0)
                BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(10), _gameVersionTag);
            return payload;
        }

        private bool MatchesGameVersion(ReadOnlySpan<byte> payload, int offset) => _gameVersionTag == 0
            ? payload.Length == offset
            : payload.Length == offset + sizeof(ulong) &&
              BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(offset)) == _gameVersionTag;

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
                _rateLimiter.RemovePeer(peer);
                ResetSessionState();

                if (wasConnected)
                {
                    _pendingEvents.Enqueue(new DeferredEvent(DeferredEventKind.PeerDisconnected, peer, reason, generation: _sessionGeneration));
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
                        CoopLog.Warn($"[LiteNetLibTransport] Failed to send disconnect packet: {ex.Message}");
                    }
                }

                bool wasConnected = IsConnected;
                var peer = _remotePeerId ?? new NetworkPeerId(0);
                _isConnected = false;
                _remoteEndPoint = null;
                _remotePeerId = null;

                _rateLimiter.RemovePeer(peer);
                ResetSessionState();
                CloseSocket();

                if (wasConnected)
                {
                    // BUG-02: defer the event - handlers re-enter the transport (Send /
                    // Disconnect / session teardown) and must not run while _lock is held.
                    _pendingEvents.Enqueue(new DeferredEvent(DeferredEventKind.PeerDisconnected, peer, reason, generation: _sessionGeneration));
                }
            }
        }

        #endregion

        #region Sending and Event Polling

        /// <summary>
        /// Sends data to all connected peers. Single-peer transports iterate their one peer.
        /// </summary>
        public bool Broadcast(byte[] data, int offset, int length, DeliveryMethod method, byte channel)
        {
            // This transport holds a single remote peer; send directly instead of
            // materializing a ConnectedPeers snapshot on every broadcast.
            NetworkPeerId? peer = _remotePeerId;
            return peer.HasValue && Send(peer.Value, data, offset, length, method, channel);
        }

        /// <summary>Legacy UDP transport holds one remote peer at a time.</summary>
        public int MaxRemotePeers => 1;

        /// <summary>Client-to-client UDP relay is not implemented yet.</summary>
        public bool SupportsHostRelay => false;

        /// <summary>
        /// Sends data to the specified target peer.
        /// </summary>
        /// <param name="target">The target peer to send to.</param>
        /// <param name="data">The data buffer to send.</param>
        /// <param name="offset">The offset within the buffer to start from.</param>
        /// <param name="length">The number of bytes to send.</param>
        /// <param name="method">The delivery method to use.</param>
        /// <param name="channel">The channel to send on.</param>
        public bool Send(NetworkPeerId target, byte[] data, int offset, int length, DeliveryMethod method, byte channel)
        {
            return SendInternal(target, data, offset, length, method, channel, allowQueue: true);
        }

        private void SendImmediate(NetworkPeerId target, byte[] data, int offset, int length, DeliveryMethod method, byte channel)
        {
            SendInternal(target, data, offset, length, method, channel, allowQueue: false);
        }

        private bool SendInternal(NetworkPeerId target, byte[] data, int offset, int length, DeliveryMethod method, byte channel, bool allowQueue)
        {
            lock (_lock)
            {
                if (!IsConnected || _remoteEndPoint == null || _socket == null) return false;
                if (data == null) return false;
                if (offset < 0 || length < 0 || offset > data.Length - length) return false;
                if (_remotePeerId.HasValue && target != _remotePeerId.Value) return false;

                bool includeMac = _sessionKey != null;
                if (length > MaxDatagramSize - 8 - (includeMac ? MacSizeBytes : 0))
                {
                    CoopLog.Warn($"[UDP] Payload too large for a single datagram ({length} bytes).");
                    return false;
                }

                // Backpressure instead of evicting the oldest unacknowledged packet: the
                // receiver is still waiting for that sequence, and dropping it from the
                // retransmit list leaves a permanent hole that blocks every later packet
                // on the channel. Dropping the NEW packet (and not consuming a sequence
                // number) keeps the reliable stream contiguous; the heartbeat will tear
                // the session down if the backlog never drains.
                bool isReliable = method == DeliveryMethod.ReliableOrdered ||
                                  method == DeliveryMethod.ReliableUnordered;
                if (isReliable && allowQueue && QueueReliableWhenBusy &&
                    (_reliableQueue.Count > 0 || _unacknowledged.Count >= MaxUnacknowledgedPackets))
                {
                    if (_reliableQueue.Count >= ReliableQueueCapacity)
                    {
                        CoopLog.Warn("[UDP] Reliable queue exhausted; disconnecting to avoid silent state loss.");
                        if (IsHost) DropPeerForTimeout(DisconnectReason.Timeout);
                        else Disconnect(DisconnectReason.ConnectionFailed);
                        return false;
                    }

                    _reliableQueue.Enqueue((target, data.AsSpan(offset, length).ToArray(), method, channel));
                    return true;
                }

                if (isReliable && _unacknowledged.Count >= MaxUnacknowledgedPackets)
                {
                    System.Threading.Interlocked.Increment(ref _droppedReliablePackets);

                    // Throttle: a saturated link would otherwise write one line per packet
                    // per frame and drown the log.
                    long now = Environment.TickCount;
                    if (unchecked(now - _lastBacklogWarningMs) >= 1000)
                    {
                        _lastBacklogWarningMs = now;
                        CoopLog.Warn(
                            $"[LiteNetLibTransport] Reliable backlog full ({MaxUnacknowledgedPackets}); " +
                            $"dropping new packet on channel {channel} " +
                            $"(total dropped: {DroppedReliablePackets})");
                    }
                    return false;
                }

                uint seq;
                var sequenceKey = (channel, method);
                if (!_sendSequences.TryGetValue(sequenceKey, out seq))
                    seq = 0;

                // Build envelope into a pooled buffer to avoid per-packet heap allocations
                // (BUG-P16). The rented buffer is returned immediately for unreliable
                // traffic; reliable traffic keeps it until the packet is acked/dropped.
                int envelopeLength = 8 + length + (includeMac ? MacSizeBytes : 0);
                byte[] envelope = ArrayPool<byte>.Shared.Rent(envelopeLength);
                try
                {
                    envelope[0] = (byte)UdpFrameType.Data;
                    envelope[1] = channel;
                    envelope[2] = (byte)method;
                    BinaryPrimitives.WriteUInt32LittleEndian(envelope.AsSpan(3, 4), seq);
                    envelope[7] = includeMac ? FlagMacPresent : (byte)0;
                    data.AsSpan(offset, length).CopyTo(envelope.AsSpan(8));
                    if (includeMac)
                    {
                        AppendMac(envelope, 8 + length);
                    }
                }
                catch (Exception ex)
                {
                    // A reliable ordered stream must not contain a sequence hole: consume
                    // the sequence only after the envelope was built successfully.
                    CoopLog.Warn($"[LiteNetLibTransport] Failed to build envelope: {ex.Message}");
                    ReturnPooledBuffer(envelope);
                    return false;
                }

                _sendSequences[sequenceKey] = seq + 1;

                bool sent = false;
                try
                {
                    sent = _socket.SendTo(envelope, 0, envelopeLength, SocketFlags.None, _remoteEndPoint) == envelopeLength;
                }
                catch (SocketException ex) { CoopLog.Warn($"Socket error: {ex.Message}"); }
                catch (ObjectDisposedException ex) { CoopLog.Warn($"Socket disposed: {ex.Message}"); }
                catch (Exception ex) { CoopLog.Warn($"Send error: {ex.Message}"); }

                if (isReliable)
                {
                    var node = _unacknowledged.AddLast(new SentReliablePacket
                    {
                        Sequence = seq,
                        Channel = channel,
                        Method = method,
                        Envelope = envelope,
                        EnvelopeLength = envelopeLength,
                        LastSentTimeMs = GetTimestampMs(),
                        Retries = 0
                    });
                    _unacknowledgedIndex[(channel, method, seq)] = node;
                }
                else
                {
                    // Unreliable/sequenced: buffer is no longer needed after SendTo.
                    ReturnPooledBuffer(envelope);
                }
                return isReliable || sent;
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

                    // 1. Receive incoming datagrams - reuse pre-allocated buffer.
                    // Bound the work per frame: a flood must not stall the Unity main thread.
                    int processedDatagrams = 0;

                    while (processedDatagrams < MaxDatagramsPerPoll)
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

                        // Connected data traffic only compares the endpoint, so one instance
                        // can be reused. Handshake/disconnected paths keep a fresh object
                        // because a successful proof can retain it as the session endpoint.
                        EndPoint senderEp = IsConnected
                            ? _connectedSenderEndPoint
                            : new IPEndPoint(IPAddress.Any, 0);

                        try
                        {
                            int received = socket.ReceiveFrom(_recvBuffer, ref senderEp);
                            processedDatagrams++;
                            if (received < 0) break;
                            if (received > MaxDatagramSize) continue;
                            if (received >= 8) // Minimum envelope header
                            {
                                // Basic sanity check on frame type range
                                byte frameTypeByte = _recvBuffer[0];
                                if (frameTypeByte > 20)
                                {
                                    continue;
                                }
                                ProcessIncomingDatagram(_recvBuffer.AsSpan(0, received), senderEp);
                            }
                        }
                        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.MessageSize)
                        {
                            processedDatagrams++;
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
                            _remoteEndPoint = null;
                            _remotePeerId = null;
                            ResetSessionState();
                            CloseSocket();
                            _pendingEvents.Enqueue(new DeferredEvent(DeferredEventKind.PeerDisconnected, peer, DisconnectReason.Timeout, generation: _sessionGeneration));
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
                                catch (SocketException ex) { CoopLog.Warn($"Socket error: {ex.Message}"); }
                                catch (ObjectDisposedException ex) { CoopLog.Warn($"Socket disposed: {ex.Message}"); }
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

                    while (_reliableQueue.Count > 0 && _unacknowledged.Count < MaxUnacknowledgedPackets)
                    {
                        var item = _reliableQueue.Dequeue();
                        SendImmediate(item.Peer, item.Data, 0, item.Data.Length, item.Method, item.Channel);
                    }

                    // 3. Heartbeat ping
                    if (IsConnected && _remoteEndPoint != null && _socket != null)
                    {
                        if (now - _lastPingSentMs > 1000)
                        {
                            _lastPingSentMs = now;
                            Span<byte> timestampPayload = stackalloc byte[8];
                            BinaryPrimitives.WriteInt64LittleEndian(timestampPayload, now);
                            SendEnvelope(UdpFrameType.Ping, 0, 0, DeliveryMethod.Unreliable, timestampPayload);
                        }
                    }
                }
            }
        }

        #endregion

        #region Incoming Packet Processing

        /// <summary>
        /// Applies the pre-authentication rate limit per source endpoint. Returns false for
        /// a malformed sender, or when that endpoint has spent its handshake budget.
        /// </summary>
        private bool IsPreAuthAllowed(EndPoint sender)
        {
            if (!(sender is IPEndPoint endpoint)) return false;

            if (_preAuthLimiter.BucketCount > MaxPreAuthBuckets)
                _preAuthLimiter.Clear();

            ulong key = ComputeEndpointBucketKey(endpoint);
            return _preAuthLimiter.TryConsume(new NetworkPeerId(key, "pre-auth"));
        }

        /// <summary>
        /// FNV-1a hash over the address bytes and port, used only as a rate-limit bucket id.
        /// A hash collision merely makes two endpoints share one handshake budget.
        /// </summary>
        private static ulong ComputeEndpointBucketKey(IPEndPoint endpoint)
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                byte[] address = endpoint.Address.GetAddressBytes();
                for (int i = 0; i < address.Length; i++)
                {
                    hash ^= address[i];
                    hash *= 1099511628211UL;
                }

                hash ^= (uint)endpoint.Port;
                hash *= 1099511628211UL;
                return hash == 0 ? 1UL : hash;
            }
        }

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
                byte flags = datagram[7];
                if ((flags & ~FlagMacPresent) != 0) return;
                bool hasMac = (flags & FlagMacPresent) != 0;
                int macOffset = datagram.Length - (hasMac ? MacSizeBytes : 0);
                if (macOffset < 8) return;
                ReadOnlySpan<byte> payload = datagram.Slice(8, macOffset - 8);

                // Validate enum ranges without reflection (per-datagram hot path).
                if ((uint)frameType < (uint)UdpFrameType.Handshake ||
                    (uint)frameType > (uint)UdpFrameType.ChallengeResponse) return;
                if ((uint)method > (uint)DeliveryMethod.ReliableUnordered) return;

                // Nothing is authenticated yet: rate-limit handshakes/challenges so an
                // attacker cannot brute-force the room-code HMAC or reflect challenges.
                // The bucket is per source endpoint so a single sender cannot exhaust the
                // budget that a real client needs to complete its handshake.
                if (!IsConnected)
                {
                    if (IsHost && frameType != UdpFrameType.Handshake && frameType != UdpFrameType.ChallengeResponse)
                        return;
                    if (!IsHost && frameType != UdpFrameType.Challenge && frameType != UdpFrameType.HandshakeAck)
                        return;
                    if (!IsPreAuthAllowed(sender)) return;
                }

                // Once a session key exists, every frame except the still-relevant
                // pre-connection handshake frames must be authenticated. Challenge and
                // ChallengeResponse are only pre-auth while the session is not connected:
                // accepting them later would let spoofed datagrams spend peer tokens, and
                // the switch ignores them anyway. Handshake stays unsigned for the lost-ack
                // retry path, but it never refreshes the heartbeat below.
                bool preAuthFrame = frameType == UdpFrameType.Handshake ||
                                    ((frameType == UdpFrameType.Challenge ||
                                      frameType == UdpFrameType.ChallengeResponse) &&
                                     !IsConnected);
                bool macRequired = _sessionKey != null && !preAuthFrame;

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

                // Authenticate before spending the peer's token bucket. Consuming tokens
                // first let a spoofed invalid-MAC flood drain the real peer's budget and
                // block its legitimate traffic. Handshake frames without a key (legacy
                // direct-IP sessions) still pass through the rate limiter below.
                if (hasMac)
                {
                    if (!ValidateMac(datagram.Slice(0, macOffset), datagram.Slice(macOffset, MacSizeBytes)))
                        return;
                }
                else if (macRequired)
                {
                    return;
                }

                // An unsigned duplicate handshake is unauthenticated even in a room-code
                // session, so it must not spend the live peer's tokens. Charge it to the
                // per-endpoint pre-auth budget instead.
                bool useEndpointBudget = !hasMac &&
                                         frameType == UdpFrameType.Handshake &&
                                         _remotePeerId.HasValue;
                if (useEndpointBudget)
                {
                    if (!IsPreAuthAllowed(sender))
                        return;
                }
                else if (_remotePeerId.HasValue && !_rateLimiter.TryConsume(_remotePeerId.Value))
                {
                    CoopLog.Warn($"[LiteNetLibTransport] Rate limit exceeded for peer {_remotePeerId.Value}");
                    return;
                }

                // An unsigned duplicate handshake is allowed for the lost-ack retry path,
                // but it must not refresh the heartbeat: only authenticated post-handshake
                // traffic proves the peer is still alive.
                if (IsConnected && hasMac)
                {
                    if (frameType == UdpFrameType.Data)
                    {
                        if (method == DeliveryMethod.Unreliable && !AcceptUnreliable(channel, sequence)) return;
                        if (method == DeliveryMethod.Unreliable ||
                            (method == DeliveryMethod.Sequenced &&
                             (!_latestSequencedReceived.TryGetValue(channel, out uint latest) ||
                              IsSequenceNewer(sequence, latest))) ||
                            (method == DeliveryMethod.ReliableOrdered && IsNewOrderedData(channel, sequence)) ||
                            (method == DeliveryMethod.ReliableUnordered &&
                             (!_receivedUnordered.TryGetValue(channel, out var unordered) || unordered.IsNew(sequence))))
                            _lastReceivedMs = GetTimestampMs();
                    }
                    else if (frameType == UdpFrameType.Ping || frameType == UdpFrameType.Pong ||
                             frameType == UdpFrameType.Disconnect)
                    {
                        if (_latestControl.TryGetValue(frameType, out uint latest) &&
                            !IsSequenceNewer(sequence, latest)) return;
                        _latestControl[frameType] = sequence;
                        _lastReceivedMs = GetTimestampMs();
                    }
                }
                else if (frameType != UdpFrameType.Handshake)
                {
                    _lastReceivedMs = GetTimestampMs();
                }

                switch (frameType)
                {
                    case UdpFrameType.Handshake:
                        if (IsHost && IsConnected)
                        {
                            // BUG-A3: our HandshakeAck travels unreliably and may have been
                            // lost — the client retried its handshake because it never
                            // connected. Re-send the ack so the session can complete.
                            SendEnvelope(UdpFrameType.HandshakeAck, 0, 0, DeliveryMethod.Unreliable, BuildHandshakeAckPayload());
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

                            // Protocol compatibility: reject pre-2.1 clients (9-byte
                            // handshake) and any future incompatible layout before the
                            // challenge is issued.
                            if (payload.Length < 11)
                                break;

                            ushort clientProtocol = (ushort)(payload[9] | (payload[10] << 8));
                            if (clientProtocol != ProtocolVersion)
                            {
                                CoopLog.Warn(
                                    $"[LiteNetLibTransport] Rejected client protocol {clientProtocol} (expected {ProtocolVersion})");
                                break;
                            }

                            if (!MatchesGameVersion(payload, 11))
                            {
                                CoopLog.Warn("[LiteNetLibTransport] Rejected client with a different game version");
                                break;
                            }
                            ulong clientPeerValue = BitConverter.ToUInt64(payload);
                            var candidatePeer = new NetworkPeerId(clientPeerValue, "Client");

                            // BUG-B2: only challenge clients that announced a room code (flag
                            // byte in the handshake payload). A direct-IP client has no shared
                            // secret and could never answer.
                            bool clientHasCode = payload.Length >= 9 && payload[8] != 0;
                            if (_requireRoomCode && (_roomCode == null || !clientHasCode))
                            {
                                break;
                            }
                            if (_roomCode != null && clientHasCode && sender is IPEndPoint handshakeEndpoint)
                            {
                                Span<byte> cookie = stackalloc byte[ChallengeBytes];
                                uint bucket = (uint)(GetTimestampMs() / ChallengeBucketMs);
                                WriteChallenge(cookie, handshakeEndpoint, clientPeerValue, bucket);
                                SendEnvelopeTo(sender, UdpFrameType.Challenge, 0, 0, DeliveryMethod.Unreliable, cookie);
                            }
                            else
                            {
                                // No room code on either side, fall back to old behavior (no authentication)
                                _remoteEndPoint = sender;
                                _remotePeerId = candidatePeer;
                                _lastReceivedMs = Math.Max(1, GetTimestampMs());
                                _isConnected = true;
                                SendEnvelope(UdpFrameType.HandshakeAck, 0, 0, DeliveryMethod.Unreliable, BuildHandshakeAckPayload());
                                _pendingEvents.Enqueue(new DeferredEvent(DeferredEventKind.PeerConnected, candidatePeer, generation: _sessionGeneration));
                            }
                        }
                        break;

                    case UdpFrameType.Challenge:
                        if (!IsHost && !IsConnected && _roomCode != null)
                        {
                            // Only the host we are dialling may challenge us. A challenge
                            // from any other endpoint would replace the session key with a
                            // nonce the real host never issued and break the MAC stream.
                            if (_remoteEndPoint is IPEndPoint expected &&
                                sender is IPEndPoint actual &&
                                (!expected.Address.Equals(actual.Address) || expected.Port != actual.Port))
                            {
                                break;
                            }

                            if (payload.Length != ChallengeBytes) break;
                            long now = GetTimestampMs();
                            if (_clientChallenge != null &&
                                now - _clientChallengeReceivedAt < ChallengeBucketMs * 2 &&
                                !payload.SequenceEqual(_clientChallenge)) break;

                            if (_clientChallenge == null || !payload.SequenceEqual(_clientChallenge))
                            {
                                _clientChallenge = payload.ToArray();
                                _clientChallengeReceivedAt = now;
                                SetSessionKey(_authenticator.ComputeSessionKey(_clientChallenge, _roomCode));
                            }

                            Span<byte> response = stackalloc byte[ChallengeResponseBytes];
                            payload.CopyTo(response);
                            BinaryPrimitives.WriteUInt64LittleEndian(response.Slice(ChallengeBytes), LocalPeerId.Value);
                            _authenticator.ComputeSignature(_clientChallenge, _roomCode)
                                .CopyTo(response.Slice(ChallengeBytes + sizeof(ulong)));
                            SendEnvelope(UdpFrameType.ChallengeResponse, 0, 0, DeliveryMethod.Unreliable, response);
                        }
                        break;

                    case UdpFrameType.ChallengeResponse:
                        if (IsHost && !IsConnected && _roomCode != null &&
                            sender is IPEndPoint responseEndpoint && payload.Length == ChallengeResponseBytes)
                        {
                            var cookie = payload.Slice(0, ChallengeBytes);
                            uint bucket = BinaryPrimitives.ReadUInt32LittleEndian(cookie);
                            uint current = (uint)(GetTimestampMs() / ChallengeBucketMs);
                            if (unchecked(current - bucket) > 1) break;

                            ulong clientId = BinaryPrimitives.ReadUInt64LittleEndian(payload.Slice(ChallengeBytes));
                            Span<byte> expected = stackalloc byte[ChallengeBytes];
                            WriteChallenge(expected, responseEndpoint, clientId, bucket);
                            if (!CryptographicOperations.FixedTimeEquals(cookie, expected)) break;

                            byte[] nonce = cookie.ToArray();
                            byte[] proof = _authenticator.ComputeSignature(nonce, _roomCode);
                            if (!CryptographicOperations.FixedTimeEquals(
                                    proof, payload.Slice(ChallengeBytes + sizeof(ulong)))) break;

                            SetSessionKey(_authenticator.ComputeSessionKey(nonce, _roomCode));
                            _remoteEndPoint = responseEndpoint;
                            _remotePeerId = new NetworkPeerId(clientId, "Client");
                            _lastReceivedMs = Math.Max(1, GetTimestampMs());
                            _isConnected = true;
                            SendEnvelopeTo(sender, UdpFrameType.HandshakeAck, 0, 0, DeliveryMethod.Unreliable, BuildHandshakeAckPayload());
                            _pendingEvents.Enqueue(new DeferredEvent(DeferredEventKind.PeerConnected, _remotePeerId.Value, generation: _sessionGeneration));
                        }
                        break;

                    case UdpFrameType.HandshakeAck:
                        if (!IsHost && !IsConnected)
                        {
                            // A room-code session is authenticated only by the HMAC challenge.
                            // Accepting an ack before the client derived _sessionKey would let
                            // a spoofed ack pin a false peer id and report "connected" while
                            // the real host never received a challenge response.
                            if (_roomCode != null && _sessionKey == null)
                            {
                                CoopLog.Warn("[LiteNetLibTransport] Ignored HandshakeAck before the challenge completed");
                                break;
                            }

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

                            // 8-byte peer id + 2-byte protocol version. Old hosts send only
                            // the peer id and must not be accepted (their parser cannot read
                            // 2.1 packets).
                            if (payload.Length < 10)
                                break;

                            ushort hostProtocol = (ushort)(payload[8] | (payload[9] << 8));
                            if (hostProtocol != ProtocolVersion)
                            {
                                CoopLog.Warn(
                                    $"[LiteNetLibTransport] Rejected host protocol {hostProtocol} (expected {ProtocolVersion})");
                                break;
                            }

                            if (!MatchesGameVersion(payload, 10))
                            {
                                CoopLog.Warn("[LiteNetLibTransport] Rejected host with a different game version");
                                break;
                            }
                            ulong hostPeerValue = BitConverter.ToUInt64(payload);
                            _remotePeerId = new NetworkPeerId(hostPeerValue, "Host");
                            _isConnected = true;

                            _pendingEvents.Enqueue(new DeferredEvent(DeferredEventKind.PeerConnected, _remotePeerId.Value, generation: _sessionGeneration));
                        }
                        break;

                    case UdpFrameType.Data:
                        // Never accept game data before the peer is authenticated (or the
                        // legacy no-code handshake completed). Otherwise an unauthenticated
                        // datagram could advance the reliable sequence window and cause the
                        // real client's first packets to be dropped as duplicates.
                        if (!IsConnected || _remotePeerId == null || _remoteEndPoint == null)
                        {
                            break;
                        }

                        // Handle incoming data first
                        bool accepted = HandleIncomingData(channel, method, sequence, payload);

                        // ACK only after the packet was actually delivered or buffered.
                        // A reliable packet dropped because the reorder buffer was full must
                        // NOT be acknowledged: the sender still owns it and will retransmit
                        // once the missing sequence arrives.
                        if ((method == DeliveryMethod.ReliableOrdered ||
                             method == DeliveryMethod.ReliableUnordered) && IsConnected && accepted)
                        {
                            Span<byte> ackPayload = stackalloc byte[5];
                            ackPayload[0] = channel;
                            BinaryPrimitives.WriteUInt32LittleEndian(ackPayload.Slice(1, 4), sequence);
                            SendEnvelope(UdpFrameType.Ack, channel, sequence, method, ackPayload);
                        }
                        break;

                    case UdpFrameType.Ack:
                        if (payload.Length >= 5)
                        {
                            byte ackChannel = payload[0];
                            uint ackSeq = BitConverter.ToUInt32(payload.Slice(1, 4));

                            // O(1) removal using index. The ACK envelope carries the
                            // delivery method that owns the acknowledged sequence space.
                            if (TryRemoveAcknowledged(ackChannel, method, ackSeq))
                                _lastReceivedMs = GetTimestampMs();
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
                            // (TriggerTeamWipe -> ExitSpectatorMode -> Disconnect) and must
                            // not run while _lock is held.
                            _pendingEvents.Enqueue(new DeferredEvent(DeferredEventKind.PeerDisconnected, peer, DisconnectReason.PeerDisconnected, generation: _sessionGeneration));
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                CoopLog.Warn($"[LiteNetLibTransport] ProcessIncomingDatagram error: {ex.Message}");
            }
        }

        /// <summary>
        /// Wrap-aware sequence comparison: true when <paramref name="candidate"/> is ahead
        /// of <paramref name="reference"/> even if the 32-bit counter wrapped.
        /// </summary>
        private static bool IsSequenceNewer(uint candidate, uint reference) =>
            unchecked((int)(candidate - reference)) > 0;

        /// <summary>
        /// Wrap-aware counterpart of <see cref="IsSequenceNewer"/> for stale packets.
        /// </summary>
        private static bool IsSequenceOlder(uint candidate, uint reference) =>
            unchecked((int)(candidate - reference)) < 0;

        /// <summary>
        /// Buffers or dispatches an incoming data frame.
        /// </summary>
        /// <returns>
        /// True when the frame was delivered, buffered for ordered delivery, or was an
        /// already-delivered duplicate (safe to ACK). False when a future reliable frame
        /// had to be rejected because the reorder buffer is full — the caller must not ACK
        /// it so the sender retransmits later.
        /// </returns>
        private bool IsNewOrderedData(byte channel, uint sequence)
        {
            _expectedReceiveSequences.TryGetValue(channel, out uint expected);
            return sequence == expected ||
                   (IsSequenceNewer(sequence, expected) &&
                    (!_pendingReliable.TryGetValue(channel, out var pending) ||
                     (pending.Count < 256 && !pending.ContainsKey(sequence))));
        }

        private bool AcceptUnreliable(byte channel, uint sequence)
        {
            if (!_unreliableReplay.TryGetValue(channel, out var window))
            {
                _unreliableReplay[channel] = (sequence, 1);
                return true;
            }

            int distance = unchecked((int)(sequence - window.Latest));
            if (distance > 0)
            {
                _unreliableReplay[channel] = (sequence, distance >= 64 ? 1 : (window.Seen << distance) | 1);
                return true;
            }
            if (distance <= -64) return false;
            ulong bit = 1UL << -distance;
            if ((window.Seen & bit) != 0) return false;
            _unreliableReplay[channel] = (window.Latest, window.Seen | bit);
            return true;
        }

        private bool HandleIncomingData(byte channel, DeliveryMethod method, uint sequence, ReadOnlySpan<byte> payload)
        {
            try
            {
                if (method == DeliveryMethod.ReliableOrdered)
                {
                    if (!_pendingReliable.TryGetValue(channel, out var pending))
                    {
                        pending = new SortedDictionary<uint, byte[]>();
                        _pendingReliable[channel] = pending;
                    }

                    _expectedReceiveSequences.TryGetValue(channel, out uint expected);

                    // Cap pending buffer size to prevent unbounded growth under packet loss.
                    // Never evict a buffered future packet: the sender would have to
                    // retransmit it, but it already received our ACK (or worse, it was
                    // dropped from its own retransmit list). Instead, reject NEW future
                    // packets without ACKing them once the buffer is full.
                    const int MaxPendingPerChannel = 256;
                    if (pending.Count >= MaxPendingPerChannel && IsSequenceNewer(sequence, expected))
                    {
                        // Reclaim entries that can no longer be needed (stale duplicates).
                        var staleKeys = new List<uint>();
                        foreach (var key in pending.Keys)
                        {
                            if (IsSequenceOlder(key, expected))
                                staleKeys.Add(key);
                        }
                        foreach (var key in staleKeys)
                        {
                            pending.Remove(key);
                        }

                        if (pending.Count >= MaxPendingPerChannel)
                        {
                            CoopLog.Warn($"[LiteNetLibTransport] Reorder buffer full on channel {channel}; deferring sequence {sequence}");
                            return false;
                        }
                    }

                    if (sequence == expected)
                    {
                        DispatchData(payload.ToArray());
                        if (!IsConnected) return true;

                        expected++;
                        _expectedReceiveSequences[channel] = expected;

                        while (pending.TryGetValue(expected, out var nextData))
                        {
                            pending.Remove(expected);
                            DispatchData(nextData);
                            if (!IsConnected) return true;

                            expected++;
                            _expectedReceiveSequences[channel] = expected;
                        }
                    }
                    else if (IsSequenceNewer(sequence, expected))
                    {
                        pending[sequence] = payload.ToArray();
                    }
                    // If sequence < expected, it's a duplicate/old packet - drop silently
                    // but still ACK it so a sender that lost the original ACK can retire it.
                    return true;
                }

                if (method == DeliveryMethod.ReliableUnordered)
                {
                    if (!_receivedUnordered.TryGetValue(channel, out var window))
                        _receivedUnordered[channel] = window = new UnorderedReplayWindow();
                    if (!window.Accept(sequence)) return true;
                    DispatchData(payload.ToArray());
                    return true;
                }

                if (method == DeliveryMethod.Sequenced)
                {
                    _latestSequencedReceived.TryGetValue(channel, out uint latest);
                    // BUG-N4: `latest == 0` made every sequence-0 packet look acceptable, so
                    // duplicates of the first packet were delivered repeatedly. Track whether
                    // the channel has seen any packet instead of using 0 as a sentinel.
                    if (!_sequencedInitialized.Contains(channel))
                    {
                        _sequencedInitialized.Add(channel);
                        _latestSequencedReceived[channel] = sequence;
                    }
                    else if (IsSequenceNewer(sequence, latest))
                    {
                        _latestSequencedReceived[channel] = sequence;
                    }
                    else
                    {
                        return true;
                    }

                    byte[] pooled = RentAndCopy(payload);
                    DispatchData(pooled, payload.Length, pooled: true);
                    return true;
                }

                TrackUnreliableSequences(channel, sequence);
                byte[] unreliable = RentAndCopy(payload);
                DispatchData(unreliable, payload.Length, pooled: true);
                return true;
            }
            catch (Exception ex)
            {
                CoopLog.Warn($"[LiteNetLibTransport] HandleIncomingData error: {ex.Message}");
                return false;
            }
        }

        private static byte[] RentAndCopy(ReadOnlySpan<byte> payload)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(payload.Length);
            payload.CopyTo(buffer);
            return buffer;
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

        private void DispatchData(byte[]? data, int length = -1, bool pooled = false)
        {
            int actualLength = length >= 0 ? length : data?.Length ?? 0;
            try
            {
                // Ensure we have a valid peer and connection before dispatching.
                if (!_remotePeerId.HasValue || !IsConnected || data == null || actualLength <= 0)
                {
                    if (pooled) ReturnPooledBuffer(data);
                    return;
                }

                var peer = _remotePeerId.Value;
                // BUG-03: defer dispatch - handlers call transport.Send() and must not
                // execute while _lock is held.
                _pendingEvents.Enqueue(new DeferredEvent(
                    DeferredEventKind.DataReceived,
                    peer,
                    reason: DisconnectReason.None,
                    data: data,
                    dataLength: actualLength,
                    dataPooled: pooled,
                    generation: _sessionGeneration));
            }
            catch (Exception ex)
            {
                if (pooled) ReturnPooledBuffer(data);
                CoopLog.Warn($"[LiteNetLibTransport] DispatchData error: {ex.Message}");
            }
        }

        /// <summary>
        /// Removes an acknowledged reliable packet. Older peers sent ACKs with the
        /// Unreliable method byte, so only that legacy form falls back to the reliable
        /// sequence spaces. A modern reliable ACK must retire exactly its own space;
        /// otherwise a delayed duplicate could remove an unrelated packet.
        /// </summary>
        private bool TryRemoveAcknowledged(byte channel, DeliveryMethod method, uint sequence)
        {
            if (_unacknowledgedIndex.TryGetValue((channel, method, sequence), out var node))
            {
                ReturnPooledBuffer(node.Value.Envelope);
                _unacknowledged.Remove(node);
                _unacknowledgedIndex.Remove((node.Value.Channel, node.Value.Method, node.Value.Sequence));
                return true;
            }

            if (method != DeliveryMethod.Unreliable) return false;

            if (!_unacknowledgedIndex.TryGetValue((channel, DeliveryMethod.ReliableOrdered, sequence), out node) &&
                !_unacknowledgedIndex.TryGetValue((channel, DeliveryMethod.ReliableUnordered, sequence), out node))
            {
                return false;
            }

            ReturnPooledBuffer(node.Value.Envelope);
            _unacknowledged.Remove(node);
            _unacknowledgedIndex.Remove((node.Value.Channel, node.Value.Method, node.Value.Sequence));
            return true;
        }

        /// <summary>
        /// Delivers queued transport events on the caller's thread, outside <see cref="_lock"/>.
        /// Handlers may re-enter the transport (Send / Disconnect / StartHost); any events they
        /// enqueue are picked up by the following drain iteration. Bounded to avoid pathological
        /// loops caused by handler feedback.
        /// </summary>
        private void DrainPendingEvents()
        {
            // Claim the drain under a short lock, but never hold it while user handlers
            // run: a handler may synchronously wait for another thread that calls
            // PollEvents(), and that thread must be able to return instead of blocking.
            lock (_drainLock)
            {
                if (_drainingEvents)
                {
                    // Do not wait: a handler may be waiting for this thread to return.
                    // Ask the active drain to run one more pass after its current batch.
                    _drainRequested = true;
                    return;
                }
                _drainingEvents = true;
            }

            const int MaxDrainPasses = 4;
            try
            {
                for (int pass = 0; pass < MaxDrainPasses; pass++)
                {
                    DrainPendingEventsCore();

                    // Clear the active flag and inspect the request atomically. Releasing
                    // _drainLock before clearing the flag would let a concurrent PollEvents
                    // set _drainRequested in the gap; that request would then be dropped.
                    lock (_drainLock)
                    {
                        if (!_drainRequested)
                        {
                            _drainingEvents = false;
                            return;
                        }
                        _drainRequested = false;
                    }
                }
            }
            catch
            {
                lock (_drainLock)
                {
                    _drainingEvents = false;
                    _drainRequested = false;
                }
                throw;
            }

            // Bounded feedback loop: leave any remaining queued events for the next poll.
            lock (_drainLock)
            {
                _drainingEvents = false;
                _drainRequested = false;
            }
        }

        private void DrainPendingEventsCore()
        {
            for (int guard = 0; guard < 1024; guard++)
            {
                    _drainBatch.Clear();
                    lock (_lock)
                    {
                        if (_pendingEvents.Count == 0) return;
                        while (_pendingEvents.Count > 0)
                        {
                            _drainBatch.Add(_pendingEvents.Dequeue());
                        }
                    }

                    int processedThrough = 0;
                    try
                    {
                        for (int i = 0; i < _drainBatch.Count; i++)
                        {
                            var e = _drainBatch[i];
                            try
                            {
                                ProcessDeferredEvent(e);
                            }
                            catch (Exception ex)
                            {
                                // A faulty game/plugin handler must not abort the transport
                                // drain and strand every later event in the batch.
                                CoopLog.Warn(
                                    $"[LiteNetLibTransport] Deferred event handler failed: {ex.Message}");
                            }
                            finally
                            {
                                // Pooled payload buffers are owned by this event until the
                                // handler has returned (or the event was discarded).
                                if (e.DataPooled) ReturnPooledBuffer(e.Data);
                                processedThrough = i + 1;
                            }
                        }
                    }
                    finally
                    {
                        // If a handler threw, return buffers from the unprocessed tail too.
                        for (int i = processedThrough; i < _drainBatch.Count; i++)
                        {
                            if (_drainBatch[i].DataPooled)
                                ReturnPooledBuffer(_drainBatch[i].Data);
                        }
                        _drainBatch.Clear();
                    }
                }
        }

        private void ProcessDeferredEvent(in DeferredEvent e)
        {
            // A handler in this batch may have started/stopped a session. Events
            // captured before that reset must never leak into the new session.
            if (e.Generation != _sessionGeneration) return;

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
                    OnDataReceived?.Invoke(e.Peer, e.Data, 0, e.DataLength);
                    break;
            }
        }

        #endregion

        #region Helper Methods

        private void ClearPendingEvents()
        {
            while (_pendingEvents.Count > 0)
            {
                var e = _pendingEvents.Dequeue();
                if (e.DataPooled) ReturnPooledBuffer(e.Data);
            }
        }

        private static void ReturnPooledBuffer(byte[]? buffer)
        {
            if (buffer != null)
            {
                // Clear on return: pooled arrays may be handed to another subsystem that
                // reads past the current envelope length. Wiping prevents stale packet data
                // (including room-code-authenticated frames) from leaking into later traffic.
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }

        private void WriteChallenge(Span<byte> cookie, IPEndPoint endpoint, ulong peerId, uint bucket)
        {
            Span<byte> input = stackalloc byte[18];
            if (!endpoint.Address.TryWriteBytes(input.Slice(0, 4), out int written) || written != 4)
                throw new ArgumentException("An IPv4 endpoint is required.", nameof(endpoint));
            BinaryPrimitives.WriteUInt16LittleEndian(input.Slice(4), (ushort)endpoint.Port);
            BinaryPrimitives.WriteUInt64LittleEndian(input.Slice(6), peerId);
            BinaryPrimitives.WriteUInt32LittleEndian(input.Slice(14), bucket);

            Span<byte> hash = stackalloc byte[32];
            using var hmac = new HMACSHA256(_challengeKey);
            if (!hmac.TryComputeHash(input, hash, out _))
                throw new InvalidOperationException("Challenge generation failed.");

            BinaryPrimitives.WriteUInt32LittleEndian(cookie, bucket);
            hash.Slice(0, ChallengeBytes - sizeof(uint)).CopyTo(cookie.Slice(sizeof(uint)));
        }

        private void SendEnvelope(UdpFrameType type, byte channel, uint sequence, DeliveryMethod method, ReadOnlySpan<byte> payload)
        {
            SendEnvelopeTo(_remoteEndPoint, type, channel, sequence, method, payload);
        }

        private void SendEnvelopeTo(EndPoint? target, UdpFrameType type, byte channel, uint sequence, DeliveryMethod method, ReadOnlySpan<byte> payload)
        {
            if (_socket == null || target == null) return;

            bool includeMac = _sessionKey != null && NeedsMac(type);
            if (includeMac && (type == UdpFrameType.Ping || type == UdpFrameType.Pong || type == UdpFrameType.Disconnect))
                sequence = ++_controlSequence;
            int envelopeLength = 8 + payload.Length + (includeMac ? MacSizeBytes : 0);
            byte[] envelope = ArrayPool<byte>.Shared.Rent(envelopeLength);
            try
            {
                WriteEnvelope(envelope, type, channel, sequence, method, payload, includeMac);
                _socket.SendTo(envelope, 0, envelopeLength, SocketFlags.None, target);
            }
            catch (SocketException ex) { CoopLog.Warn($"Socket error: {ex.Message}"); }
            catch (ObjectDisposedException ex) { CoopLog.Warn($"Socket disposed: {ex.Message}"); }
            catch (Exception ex) { CoopLog.Warn($"SendEnvelope error: {ex.Message}"); }
            finally
            {
                ReturnPooledBuffer(envelope);
            }
        }

        private void WriteEnvelope(
            byte[] buffer,
            UdpFrameType type,
            byte channel,
            uint sequence,
            DeliveryMethod method,
            ReadOnlySpan<byte> payload,
            bool includeMac)
        {
            buffer[0] = (byte)type;
            buffer[1] = channel;
            buffer[2] = (byte)method;
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(3, 4), sequence);
            buffer[7] = includeMac ? FlagMacPresent : (byte)0;
            payload.CopyTo(buffer.AsSpan(8));
            if (includeMac)
            {
                AppendMac(buffer, 8 + payload.Length);
            }
        }

        /// <summary>
        /// Frames exchanged before the challenge completes have no session key yet.
        /// Everything else is authenticated with a truncated HMAC.
        /// </summary>
        private static bool RequiresMac(UdpFrameType type) =>
            type == UdpFrameType.Data ||
            type == UdpFrameType.Ack ||
            type == UdpFrameType.Ping ||
            type == UdpFrameType.Pong ||
            type == UdpFrameType.Disconnect;

        /// <summary>
        /// The handshake ack is normally a pre-auth frame, but once the challenge has
        /// derived a session key it must be authenticated as well.
        /// </summary>
        private bool NeedsMac(UdpFrameType type) =>
            RequiresMac(type) ||
            (type == UdpFrameType.HandshakeAck && _sessionKey != null);

        private void SetSessionKey(byte[]? key)
        {
            _hmac?.Dispose();
            _receiveHmac?.Dispose();
            _sessionKey = key;
            _hmac = null;
            _receiveHmac = null;
            if (key == null || key.Length == 0) return;

            using var derivation = new HMACSHA256(key);
            var clientToHost = derivation.ComputeHash(new byte[] { 0 });
            var hostToClient = derivation.ComputeHash(new byte[] { 1 });
            _hmac = new HMACSHA256(IsHost ? hostToClient : clientToHost);
            _receiveHmac = new HMACSHA256(IsHost ? clientToHost : hostToClient);
        }

        private void ClearSessionKey() => SetSessionKey(null);

        private void AppendMac(byte[] buffer, int headerAndPayloadLength)
        {
            if (_hmac == null)
            {
                throw new InvalidOperationException("Cannot append a MAC without a session key");
            }

            Span<byte> hash = stackalloc byte[HmacHashBytes];
            if (!_hmac.TryComputeHash(
                    buffer.AsSpan(0, headerAndPayloadLength),
                    hash,
                    out int written) ||
                written < MacSizeBytes)
            {
                throw new InvalidOperationException("HMAC computation failed");
            }

            hash.Slice(0, MacSizeBytes).CopyTo(buffer.AsSpan(headerAndPayloadLength, MacSizeBytes));
        }

        private bool ValidateMac(ReadOnlySpan<byte> headerAndPayload, ReadOnlySpan<byte> receivedMac)
        {
            if (_receiveHmac == null || receivedMac.Length != MacSizeBytes) return false;

            Span<byte> hash = stackalloc byte[HmacHashBytes];
            if (!_receiveHmac.TryComputeHash(headerAndPayload, hash, out int written) ||
                written < MacSizeBytes)
            {
                return false;
            }

            int diff = 0;
            for (int i = 0; i < MacSizeBytes; i++)
            {
                diff |= hash[i] ^ receivedMac[i];
            }
            return diff == 0;
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
                catch (Exception ex) { CoopLog.Warn($"Socket close error: {ex.Message}"); }
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
