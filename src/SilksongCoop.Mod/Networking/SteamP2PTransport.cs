// SteamP2PTransport.cs — Steamworks.NET P2P network transport using lobbies and SteamNetworking.
// Handles lobby creation/joining, friend invites, P2P packet streaming, and connection lifecycle.
using System;
using System.Diagnostics;
using SilksongCoop.Core.Networking;
using Steamworks;

namespace SilksongCoop.Mod.Networking
{
    /// <summary>
    /// Steamworks.NET P2P network transport utilizing SteamMatchmaking lobbies,
    /// Steam Friends overlay invites, and SteamNetworking P2P packet streaming.
    /// </summary>
    public class SteamP2PTransport : INetworkTransport
    {
        #region Fields and State

        private CSteamID _lobbyId = CSteamID.Nil;
        private CSteamID _remoteSteamId = CSteamID.Nil;

        // A peer that failed protocol validation is kept out until it leaves the lobby.
        // Otherwise the next datagram would re-adopt it and restart the reject/disconnect
        // cycle several times per second.
        private CSteamID _protocolRejectedPeer = CSteamID.Nil;
        private long _connectionAttemptStartMs;

        // Async CreateLobby/JoinLobby callbacks may arrive after the user has switched
        // transports. Only the callback belonging to the latest operation may mutate state.
        private int _sessionGeneration;
        private int _lobbyRequestGeneration = -1;
        private ulong _requestedLobbyId;

        // Monotonic clock: system-clock adjustments must not stretch or collapse the
        // Steam connection timeout.
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static long NowMs() => (long)(Clock.ElapsedTicks * 1000.0 / Stopwatch.Frequency);
        private const long SteamConnectionTimeoutMs = 15000;
        private readonly Callback<LobbyCreated_t> _lobbyCreatedCallback;
        private readonly Callback<LobbyEnter_t> _lobbyEnteredCallback;
        private readonly Callback<P2PSessionRequest_t> _p2pSessionRequestCallback;
        private readonly Callback<GameLobbyJoinRequested_t> _gameLobbyJoinRequestedCallback;
        private readonly Callback<LobbyChatUpdate_t> _lobbyChatUpdateCallback;
        
        // Fix: Reuse buffer to avoid allocation in hot path
        private byte[] _readBuffer = new byte[4096];

        // Steam has no wire-level protocol negotiation, so every game datagram is prefixed
        // with the protocol revision. A peer that does not speak the same revision is
        // disconnected instead of appearing "connected" while all packets are unusable.
        private byte[] _sendBuffer = new byte[4096];
        private const int ProtocolPrefixSize = 2;
        private const string LobbyProtocolKey = "protocol_version";
        
        // Channels actually used by the protocol. The RTT probes live on the dedicated
        // control channel; channel 1 carries ActionTrigger/SceneTransition game data and
        // must be dispatched, not swallowed as transport control.
        private const int MinChannel = NetworkChannels.Min;
        private const int MaxChannel = NetworkChannels.Max;
        private const byte RttChannel = NetworkChannels.Control;

        // Guards against firing OnPeerConnected more than once when both the lobby
        // membership callback and the P2P session/packet paths observe the same peer.
        private bool _peerConnectNotified;

        // Application-level ping/pong on the dedicated control channel. The legacy Steam
        // P2P API exposes no RTT/loss counters, so this is the only portable way to feed the
        // diagnostics overlay and adaptive interpolation.
        private const byte RttPing = 0x50;
        private const byte RttPong = 0x51;
        private const int RttPingIntervalMs = 1000;
        private const int RttLossWindowMs = 5000;
        private const int SteamHeartbeatTimeoutMs = 15000;
        private const int MaxSteamDatagramsPerPoll = 256;
        private int _rttMs;
        private int _packetLossPercent;
        private long _lastPingSentMs;
        private long _lastPongReceivedMs;
        private long _lossWindowStartMs;
        private int _pingsSentInWindow;
        private int _pongsReceivedInWindow;

        #endregion // Fields and State

        #region Events and Properties

        public event Action<NetworkPeerId>? OnPeerConnected;
        public event Action<NetworkPeerId, DisconnectReason>? OnPeerDisconnected;
        public event Action<NetworkPeerId, byte[], int, int>? OnDataReceived;

        public bool IsHost { get; private set; }
        public bool IsConnected { get; private set; }
        public NetworkPeerId LocalPeerId { get; private set; }
        public CSteamID CurrentLobbyId => _lobbyId;

        // Steamworks.NET's P2P API exposes no RTT/loss counters for lobby networking, so
        // these stay 0 (the UI treats 0 as "unknown" and hides the quality indicator).
        /// <inheritdoc />
        public int RoundTripTimeMs => _rttMs;
        /// <inheritdoc />
        public int PacketLossPercent => _packetLossPercent;

        /// <summary>
        /// Steam's reliable send is handled by the Steam client; there is no local backlog
        /// that the mod can inspect.
        /// </summary>
        public int ReliableBacklogCount => 0;

        public static BepInEx.Logging.ManualLogSource? Logger { get; set; }

        public static bool IsSteamAvailable
        {
            get
            {
                try
                {
                    return SteamAPI.IsSteamRunning();
                }
                catch
                {
                    return false;
                }
            }
        }

        #endregion // Events and Properties

        #region Construction

        /// <summary>
        /// Initializes a new Steam P2P transport instance.
        /// </summary>
        public SteamP2PTransport()
        {
            // BUG-14: never fall back to peer id 0 — the clone dictionaries key on
            // NetworkPeerId and a zero id could collide with the transport-default
            // sentinel. The Steam transport cannot connect without Steam anyway, but the
            // identity should stay well-formed.
            ulong localId = 1;
            string localName = "SteamPlayer";

            try
            {
                if (IsSteamAvailable)
                {
                    localId = SteamUser.GetSteamID().m_SteamID;
                    localName = SteamFriends.GetPersonaName();
                    SteamNetworking.AllowP2PPacketRelay(true);
                }
                else
                {
                    Logger?.LogWarning("Steam is not running; transport created with fallback peer identity (connection will not succeed until Steam is available)");
                }
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"Steam initialization error: {ex.Message}");
            }

            LocalPeerId = new NetworkPeerId(localId, localName);

            _lobbyCreatedCallback = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
            _lobbyEnteredCallback = Callback<LobbyEnter_t>.Create(OnLobbyEntered);
            _p2pSessionRequestCallback = Callback<P2PSessionRequest_t>.Create(OnP2PSessionRequest);
            _gameLobbyJoinRequestedCallback = Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);
            _lobbyChatUpdateCallback = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
        }

        #endregion // Construction

        #region Connection Management

        /// <summary>
        /// Starts hosting a Steam lobby for other players to join.
        /// </summary>
        /// <param name="port">Unused for Steam transport (lobbies are identified by Steam ID).</param>
        public void StartHost(int port = 7777)
        {
            if (!IsSteamAvailable)
            {
                throw new InvalidOperationException("Steam is not initialized or offline.");
            }

            IsHost = true;
            IsConnected = false;
            _connectionAttemptStartMs = 0;

            // Re-hosting must not leak the previous lobby/P2P session: the fields are
            // about to be overwritten, so leave/close them explicitly first.
            if (_lobbyId != CSteamID.Nil)
            {
                try { SteamMatchmaking.LeaveLobby(_lobbyId); } catch { /* best-effort */ }
            }
            if (_remoteSteamId != CSteamID.Nil)
            {
                try { SteamNetworking.CloseP2PSessionWithUser(_remoteSteamId); } catch { /* best-effort */ }
            }

            _remoteSteamId = CSteamID.Nil;
            _peerConnectNotified = false;
            _protocolRejectedPeer = CSteamID.Nil;
            _lobbyId = CSteamID.Nil;
            _requestedLobbyId = 0;
            _lobbyRequestGeneration = ++_sessionGeneration;
            SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 2);
        }

        /// <summary>
        /// Connects to a Steam lobby using the provided lobby ID.
        /// </summary>
        /// <param name="addressOrCode">64-bit Steam Lobby ID as a string.</param>
        public void Connect(string addressOrCode)
        {
            if (!IsSteamAvailable)
            {
                throw new InvalidOperationException("Steam is not initialized or offline.");
            }

            // Parse BEFORE mutating transport/session state: an invalid lobby id must not
            // leave the previous session pointed at a half-switched Steam transport.
            if (!ulong.TryParse(addressOrCode, out ulong lobbyIdVal))
            {
                throw new ArgumentException("Steam transport requires a valid 64-bit Steam Lobby ID.", nameof(addressOrCode));
            }

            IsHost = false;
            SilksongCoopPlugin.Instance?.SetActiveTransport(this);

            // Drop the previous lobby/P2P session before joining a new one.
            if (_lobbyId != CSteamID.Nil)
            {
                try { SteamMatchmaking.LeaveLobby(_lobbyId); } catch { /* best-effort */ }
            }
            if (_remoteSteamId != CSteamID.Nil)
            {
                try { SteamNetworking.CloseP2PSessionWithUser(_remoteSteamId); } catch { /* best-effort */ }
            }

            _remoteSteamId = CSteamID.Nil;
            _peerConnectNotified = false;
            _protocolRejectedPeer = CSteamID.Nil;
            _lobbyId = CSteamID.Nil;
            IsConnected = false;
            _connectionAttemptStartMs = NowMs();
            _requestedLobbyId = lobbyIdVal;
            _lobbyRequestGeneration = ++_sessionGeneration;
            SteamMatchmaking.JoinLobby(new CSteamID(lobbyIdVal));
        }

        /// <summary>
        /// Opens the Steam overlay invite dialog for the current lobby.
        /// </summary>
        public void OpenInviteDialog()
        {
            if (IsSteamAvailable && _lobbyId != CSteamID.Nil)
            {
                SteamFriends.ActivateGameOverlayInviteDialog(_lobbyId);
            }
        }

        /// <summary>
        /// Disconnects from the current Steam lobby and closes all P2P sessions.
        /// </summary>
        public void Disconnect()
        {
            _sessionGeneration++;
            _lobbyRequestGeneration = -1;
            _requestedLobbyId = 0;
            _protocolRejectedPeer = CSteamID.Nil;

            if (_lobbyId != CSteamID.Nil)
            {
                try
                {
                    SteamMatchmaking.LeaveLobby(_lobbyId);
                }
                catch (Exception ex)
                {
                    Logger?.LogWarning($"Error leaving lobby: {ex.Message}");
                }
                _lobbyId = CSteamID.Nil;
            }
            _connectionAttemptStartMs = 0;

            if (_remoteSteamId != CSteamID.Nil)
            {
                try
                {
                    SteamNetworking.CloseP2PSessionWithUser(_remoteSteamId);
                }
                catch (Exception ex)
                {
                    Logger?.LogWarning($"Error closing P2P session: {ex.Message}");
                }

                NotifyPeerDisconnected(DisconnectReason.GracefulDisconnect);
            }
            else
            {
                IsConnected = false;
                _peerConnectNotified = false;
            }
        }

        #endregion // Connection Management

        #region Connection Notification Helpers

        /// <summary>
        /// Raises OnPeerConnected exactly once per session. Both the lobby-membership
        /// callback and the P2P packet path can discover the same peer, so this keeps
        /// the UI/clone spawner from being initialized twice.
        /// </summary>
        private void NotifyPeerConnected(CSteamID peer)
        {
            if (_peerConnectNotified) return;
            _peerConnectNotified = true;
            IsConnected = true;
            _connectionAttemptStartMs = 0;

            _rttMs = 0;
            _packetLossPercent = 0;
            _lastPingSentMs = 0;
            _lastPongReceivedMs = NowMs();
            _lossWindowStartMs = 0;
            _pingsSentInWindow = 0;
            _pongsReceivedInWindow = 0;

            string displayName = peer.m_SteamID.ToString();
            try
            {
                string persona = SteamFriends.GetFriendPersonaName(peer);
                if (!string.IsNullOrEmpty(persona)) displayName = persona;
            }
            catch
            {
                // Persona lookup is cosmetic; never let it break the connection.
            }

            try
            {
                OnPeerConnected?.Invoke(new NetworkPeerId(peer.m_SteamID, displayName));
            }
            catch (Exception ex)
            {
                Logger?.LogError($"[Steam] OnPeerConnected handler threw: {ex.Message}");
            }
        }

        private void NotifyPeerDisconnected(DisconnectReason reason)
        {
            var peer = _remoteSteamId;
            bool wasNotified = _peerConnectNotified;
            _remoteSteamId = CSteamID.Nil;
            IsConnected = false;
            _peerConnectNotified = false;
            _rttMs = 0;
            _packetLossPercent = 0;
            _lastPongReceivedMs = 0;

            if (wasNotified)
            {
                OnPeerDisconnected?.Invoke(new NetworkPeerId(peer.m_SteamID), reason);
            }
        }

        /// <summary>
        /// True when the given Steam user is a member of our current lobby. P2P session
        /// requests are only accepted from such users; "first packet wins" let any Steam
        /// user who knew the host's SteamID occupy the single co-op slot.
        /// </summary>
        private bool IsLobbyMember(CSteamID user)
        {
            if (user == CSteamID.Nil || _lobbyId == CSteamID.Nil) return false;

            try
            {
                int count = SteamMatchmaking.GetNumLobbyMembers(_lobbyId);
                for (int i = 0; i < count; i++)
                {
                    if (SteamMatchmaking.GetLobbyMemberByIndex(_lobbyId, i) == user)
                        return true;
                }
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"[Steam] Lobby membership check failed: {ex.Message}");
            }

            return false;
        }

        #endregion // Connection Notification Helpers

        #region Data Transmission

        /// <summary>
        /// Sends data to a remote peer via Steam P2P.
        /// </summary>
        /// <param name="target">Target peer (ignored for Steam; sends to current remote peer).</param>
        /// <param name="data">Data buffer to send.</param>
        /// <param name="offset">Offset within the buffer.</param>
        /// <param name="length">Number of bytes to send.</param>
        /// <param name="method">Delivery method (reliable or unreliable).</param>
        /// <param name="channel">Channel index (0–255).</param>
        public void Send(NetworkPeerId target, byte[] data, int offset, int length, DeliveryMethod method, byte channel)
        {
            if (!IsSteamAvailable || _remoteSteamId == CSteamID.Nil) return;
            if (data == null || offset < 0 || length < 0 || offset + length > data.Length) return;

            EP2PSend sendType = (method == DeliveryMethod.Unreliable || method == DeliveryMethod.Sequenced)
                ? EP2PSend.k_EP2PSendUnreliableNoDelay
                : EP2PSend.k_EP2PSendReliable;

            int framedLength = ProtocolPrefixSize + length;
            if (framedLength > _sendBuffer.Length)
                _sendBuffer = new byte[framedLength];

            // Protocol revision first; older builds sent an unprefixed packet, which the
            // receiver now rejects before dispatching it to the game layer.
            _sendBuffer[0] = (byte)(NetworkProtocol.Version & 0xFF);
            _sendBuffer[1] = (byte)(NetworkProtocol.Version >> 8);
            Buffer.BlockCopy(data, offset, _sendBuffer, ProtocolPrefixSize, length);

            try
            {
                bool sent = SteamNetworking.SendP2PPacket(_remoteSteamId, _sendBuffer, (uint)framedLength, sendType, channel);
                if (!sent && method == DeliveryMethod.ReliableOrdered)
                {
                    Logger?.LogWarning($"Failed to send P2P packet to {_remoteSteamId.m_SteamID} on channel {channel}");
                }
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"[Steam] SendP2P exception: {ex.Message}");
            }
        }

        #endregion // Data Transmission

        #region Event Processing

        /// <summary>
        /// Processes Steam callbacks and polls for incoming P2P packets.
        /// </summary>
        public void PollEvents()
        {
            if (!IsSteamAvailable) return;

            // 1. Process Steam callbacks
            try
            {
                SteamAPI.RunCallbacks();
            }
            catch (Exception ex)
            {
                Logger?.LogError($"[Steam] RunCallbacks threw: {ex.Message}");
            }

            // Connection timeout check
            if (!IsConnected && _connectionAttemptStartMs > 0 && !IsHost)
            {
                long elapsed = NowMs() - _connectionAttemptStartMs;
                if (elapsed > SteamConnectionTimeoutMs)
                {
                    _connectionAttemptStartMs = 0;
                    Logger?.LogWarning("[Steam] Connection timeout");

                    // NotifyPeerDisconnected() suppresses the event when no peer was ever
                    // accepted, which left the UI stuck on "Connecting" forever. A failed
                    // attempt is a real disconnect for the session.
                    if (_lobbyId != CSteamID.Nil)
                    {
                        try { SteamMatchmaking.LeaveLobby(_lobbyId); } catch { /* best-effort */ }
                        _lobbyId = CSteamID.Nil;
                    }
                    IsConnected = false;
                    _peerConnectNotified = false;
                    _protocolRejectedPeer = CSteamID.Nil;
                    OnPeerDisconnected?.Invoke(new NetworkPeerId(0), DisconnectReason.Timeout);
                }
            }

            // Application-level RTT/loss probes (legacy Steam P2P has no counters).
            if (IsConnected && _remoteSteamId != CSteamID.Nil)
            {
                long now = NowMs();
                if (_lossWindowStartMs == 0) _lossWindowStartMs = now;

                if (now - _lastPingSentMs >= RttPingIntervalMs)
                {
                    _lastPingSentMs = now;
                    SendRttPing();
                }

                if (now - _lossWindowStartMs >= RttLossWindowMs)
                {
                    int sent = _pingsSentInWindow;
                    int received = _pongsReceivedInWindow;
                    _packetLossPercent = sent > 0
                        ? Math.Clamp((int)Math.Round((sent - received) * 100.0 / sent), 0, 100)
                        : 0;
                    _pingsSentInWindow = 0;
                    _pongsReceivedInWindow = 0;
                    _lossWindowStartMs = now;
                }

                if (_lastPongReceivedMs == 0) _lastPongReceivedMs = now;
                if (now - _lastPongReceivedMs > SteamHeartbeatTimeoutMs)
                {
                    Logger?.LogWarning("[Steam] Peer heartbeat timed out");

                    // A host keeps its lobby: the plugin reports "still hosting" and the
                    // partner can rejoin the same lobby after fixing the connection. Only a
                    // client leaves, because its lobby membership belongs to the host.
                    if (!IsHost && _lobbyId != CSteamID.Nil)
                    {
                        try { SteamMatchmaking.LeaveLobby(_lobbyId); } catch { /* best-effort */ }
                        _lobbyId = CSteamID.Nil;
                    }
                    if (_remoteSteamId != CSteamID.Nil)
                    {
                        try { SteamNetworking.CloseP2PSessionWithUser(_remoteSteamId); } catch { /* best-effort */ }
                    }
                    NotifyPeerDisconnected(DisconnectReason.Timeout);
                    return;
                }
            }

            // 2. Fix: Poll only the channels used by the protocol (2-4) and reuse buffer
            int processedDatagrams = 0;
            for (int channel = MinChannel; channel <= MaxChannel; channel++)
            {
                while (processedDatagrams < MaxSteamDatagramsPerPoll &&
                       SteamNetworking.IsP2PPacketAvailable(out uint msgSize, channel))
                {
                    // Fix: Reuse buffer, expand only if necessary
                    if (msgSize > _readBuffer.Length)
                    {
                        _readBuffer = new byte[msgSize];
                    }

                    if (SteamNetworking.ReadP2PPacket(_readBuffer, msgSize, out uint bytesRead, out CSteamID remoteId, channel))
                    {
                        processedDatagrams++;
                        try
                        {
                            // A peer rejected for an incompatible protocol stays rejected for
                            // the rest of its lobby membership. Check before the RTT branch:
                            // otherwise its control pings would keep the heartbeat alive and
                            // mask a session whose game data is never accepted.
                            if (_protocolRejectedPeer != CSteamID.Nil && remoteId == _protocolRejectedPeer)
                            {
                                try { SteamNetworking.CloseP2PSessionWithUser(remoteId); } catch { /* best-effort */ }
                                if (_remoteSteamId == remoteId)
                                    NotifyPeerDisconnected(DisconnectReason.VersionMismatch);
                                continue;
                            }

                            // The dedicated control channel is reserved for RTT probes.
                            if (channel == RttChannel)
                            {
                                HandleRttControl(remoteId, bytesRead);
                                continue;
                            }

                            // Legacy builds opened the P2P session with a one-byte ping on
                            // a game channel. It carries no usable payload; ignore it instead
                            // of treating it as a malformed game datagram.
                            if (bytesRead == 1 && _readBuffer[0] == 0x01)
                                continue;

                            if (bytesRead < ProtocolPrefixSize)
                            {
                                Logger?.LogWarning($"[Steam] Dropped undersized packet from {remoteId.m_SteamID} ({bytesRead} bytes)");
                                continue;
                            }

                            ushort remoteProtocol = (ushort)(_readBuffer[0] | (_readBuffer[1] << 8));
                            if (remoteProtocol != NetworkProtocol.Version)
                            {
                                Logger?.LogWarning(
                                    $"[Steam] Peer protocol {remoteProtocol} is incompatible with local {NetworkProtocol.Version}; disconnecting {remoteId.m_SteamID}");
                                _protocolRejectedPeer = remoteId;
                                try { SteamNetworking.CloseP2PSessionWithUser(remoteId); } catch { /* best-effort */ }
                                NotifyPeerDisconnected(DisconnectReason.VersionMismatch);
                                continue;
                            }

                            // Fix: Validate that packets are from the expected remote peer
                            if (_remoteSteamId == CSteamID.Nil)
                            {
                                // BUG-N5: only a host may adopt the first peer it hears from.
                                // A client must never accept data before its lobby join has
                                // resolved to the lobby owner, otherwise any Steam user who
                                // learned the lobby id could hijack the session.
                                if (!IsHost)
                                {
                                    Logger?.LogWarning($"[Steam] Dropped packet from {remoteId.m_SteamID} before the host was known");
                                    continue;
                                }

                                // The host must only adopt a member of its own lobby.
                                if (!IsLobbyMember(remoteId))
                                {
                                    Logger?.LogWarning($"[Steam] Dropped packet from non-lobby user: {remoteId.m_SteamID}");
                                    SteamNetworking.CloseP2PSessionWithUser(remoteId);
                                    continue;
                                }

                                _remoteSteamId = remoteId;
                            }

                            if (!_peerConnectNotified && _remoteSteamId == remoteId)
                            {
                                NotifyPeerConnected(remoteId);
                            }
                            else if (_remoteSteamId != remoteId)
                            {
                                Logger?.LogWarning($"[Steam] Rejected packet from unexpected peer: {remoteId.m_SteamID}");
                                continue;
                            }

                            // Any authenticated traffic proves the peer is alive; do not
                            // require the RTT probe specifically to succeed.
                            _lastPongReceivedMs = NowMs();

                            // Capture buffer reference to avoid aliasing if buffer is resized.
                            // Skip the protocol prefix: the game layer sees a bare packet.
                            byte[] dataBuffer = _readBuffer;
                            int dataLength = (int)bytesRead - ProtocolPrefixSize;
                            OnDataReceived?.Invoke(new NetworkPeerId(remoteId.m_SteamID), dataBuffer, ProtocolPrefixSize, dataLength);
                        }
                        catch (Exception ex)
                        {
                            Logger?.LogError($"[Steam] Error processing packet: {ex.Message}");
                        }
                    }
                }
            }
        }

        private void SendRttPing()
        {
            byte[] ping = new byte[9];
            ping[0] = RttPing;
            Buffer.BlockCopy(BitConverter.GetBytes(NowMs()), 0, ping, 1, 8);

            try
            {
                if (SteamNetworking.SendP2PPacket(
                        _remoteSteamId, ping, (uint)ping.Length,
                        EP2PSend.k_EP2PSendUnreliableNoDelay, RttChannel))
                {
                    _pingsSentInWindow++;
                }
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"[Steam] Failed to send RTT probe: {ex.Message}");
            }
        }

        private void HandleRttControl(CSteamID remoteId, uint bytesRead)
        {
            if (_remoteSteamId == CSteamID.Nil || remoteId != _remoteSteamId) return;
            if (bytesRead < 9) return;

            _lastPongReceivedMs = NowMs();
            byte type = _readBuffer[0];
            if (type == RttPing)
            {
                // Echo the sender's timestamp back unchanged (both clocks are on the sender).
                _readBuffer[0] = RttPong;
                try
                {
                    SteamNetworking.SendP2PPacket(
                        remoteId, _readBuffer, 9,
                        EP2PSend.k_EP2PSendUnreliableNoDelay, RttChannel);
                }
                catch (Exception ex)
                {
                    Logger?.LogWarning($"[Steam] Failed to reply to RTT probe: {ex.Message}");
                }
            }
            else if (type == RttPong)
            {
                _lastPongReceivedMs = NowMs();
                long sentMs = BitConverter.ToInt64(_readBuffer, 1);
                long rtt = NowMs() - sentMs;
                if (rtt >= 0 && rtt < 5000)
                {
                    _rttMs = (int)rtt;
                }
                _pongsReceivedInWindow++;
            }
        }

        #endregion // Event Processing

        #region Steam Callback Handlers

        private void OnLobbyCreated(LobbyCreated_t callback)
        {
            var createdLobby = new CSteamID(callback.m_ulSteamIDLobby);

            // Some Steam builds deliver LobbyEntered before LobbyCreated. If that already
            // accepted this exact lobby, only publish its metadata.
            if (callback.m_eResult == EResult.k_EResultOK &&
                IsHost && _lobbyId != CSteamID.Nil && _lobbyId == createdLobby)
            {
                _lobbyRequestGeneration = -1;
                _requestedLobbyId = createdLobby.m_SteamID;
                SteamMatchmaking.SetLobbyData(_lobbyId, "mod_name", "SilksongCoop");
                SteamMatchmaking.SetLobbyData(_lobbyId, "mod_version", SilksongCoopPlugin.PluginVersion);
                SteamMatchmaking.SetLobbyData(_lobbyId, LobbyProtocolKey, NetworkProtocol.Version.ToString());
                return;
            }

            // A stale CreateLobby result after Disconnect/transport switch must not leave
            // an orphan lobby open.
            if (!IsHost || _lobbyRequestGeneration != _sessionGeneration)
            {
                try { SteamMatchmaking.LeaveLobby(createdLobby); } catch { /* best-effort */ }
                return;
            }

            _lobbyRequestGeneration = -1;

            if (callback.m_eResult == EResult.k_EResultOK)
            {
                _lobbyId = createdLobby;
                _requestedLobbyId = createdLobby.m_SteamID;
                SteamMatchmaking.SetLobbyData(_lobbyId, "mod_name", "SilksongCoop");
                // Real build version, not a hard-coded placeholder — the joining client
                // compares it and refuses to mix incompatible protocol layouts.
                SteamMatchmaking.SetLobbyData(_lobbyId, "mod_version", SilksongCoopPlugin.PluginVersion);
                SteamMatchmaking.SetLobbyData(_lobbyId, LobbyProtocolKey, NetworkProtocol.Version.ToString());
            }
            else
            {
                IsHost = false;
                _connectionAttemptStartMs = 0;
                OnPeerDisconnected?.Invoke(new NetworkPeerId(0), DisconnectReason.ConnectionFailed);
            }
        }

        private void OnLobbyEntered(LobbyEnter_t callback)
        {
            Logger?.LogInfo($"[Steam] OnLobbyEntered: response={callback.m_EChatRoomEnterResponse}, lobby={callback.m_ulSteamIDLobby}");

            var enteredLobby = new CSteamID(callback.m_ulSteamIDLobby);
            bool isOwnHostLobby = IsHost && _lobbyId != CSteamID.Nil && _lobbyId == enteredLobby;
            if (!isOwnHostLobby)
            {
                // The user may have switched transports or started another join while
                // JoinLobby was in flight; never let an old callback switch transports
                // back or enter a lobby other than the one currently requested.
                if (_lobbyRequestGeneration != _sessionGeneration ||
                    (_requestedLobbyId != 0 && enteredLobby.m_SteamID != _requestedLobbyId))
                {
                    try { SteamMatchmaking.LeaveLobby(enteredLobby); } catch { /* best-effort */ }
                    return;
                }
                _lobbyRequestGeneration = -1;
            }

            if (callback.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                Logger?.LogWarning($"Failed to enter lobby: {callback.m_EChatRoomEnterResponse}");
                _requestedLobbyId = 0;
                _connectionAttemptStartMs = 0;
                try { SteamMatchmaking.LeaveLobby(enteredLobby); } catch { /* best-effort */ }
                OnPeerDisconnected?.Invoke(new NetworkPeerId(0), DisconnectReason.ConnectionFailed);
                return;
            }

            _lobbyId = enteredLobby;
            if (!IsHost)
            {
                // Refuse lobbies that were not created by this mod or that run a different
                // build. Previously `mod_version` was written as "1.0.0" and never read,
                // so 2.0.x and future incompatible builds joined each other blindly.
                string modName = SteamMatchmaking.GetLobbyData(_lobbyId, "mod_name");
                string modVersion = SteamMatchmaking.GetLobbyData(_lobbyId, "mod_version");
                string protocolText = SteamMatchmaking.GetLobbyData(_lobbyId, LobbyProtocolKey);
                bool protocolMatches = ushort.TryParse(protocolText, out ushort hostProtocol) &&
                                       hostProtocol == NetworkProtocol.Version;

                if (!string.Equals(modName, "SilksongCoop", StringComparison.Ordinal) ||
                    !string.Equals(modVersion, SilksongCoopPlugin.PluginVersion, StringComparison.Ordinal) ||
                    !protocolMatches)
                {
                    Logger?.LogWarning(
                        $"[Steam] Refusing incompatible lobby: mod='{modName}', version='{modVersion}', protocol='{protocolText}' (local {NetworkProtocol.Version})");
                    try { SteamMatchmaking.LeaveLobby(_lobbyId); } catch { /* best-effort */ }
                    _lobbyId = CSteamID.Nil;
                    _requestedLobbyId = 0;
                    _connectionAttemptStartMs = 0;
                    OnPeerDisconnected?.Invoke(new NetworkPeerId(0), DisconnectReason.VersionMismatch);
                    return;
                }

                SilksongCoopPlugin.Instance?.SetActiveTransport(this);
                CSteamID hostId = SteamMatchmaking.GetLobbyOwner(_lobbyId);
                if (hostId == CSteamID.Nil)
                {
                    Logger?.LogWarning("[Steam] Lobby owner is nil - cannot connect");
                    _connectionAttemptStartMs = 0;
                    try { SteamMatchmaking.LeaveLobby(_lobbyId); } catch { /* best-effort */ }
                    _lobbyId = CSteamID.Nil;
                    _requestedLobbyId = 0;
                    OnPeerDisconnected?.Invoke(new NetworkPeerId(0), DisconnectReason.ConnectionFailed);
                    return;
                }

                _remoteSteamId = hostId;

                // Send the protocol revision to trigger P2P session acceptance on the host
                // and let it verify compatibility before marking the session connected.
                try
                {
                    byte[] handshake = new byte[]
                    {
                        (byte)(NetworkProtocol.Version & 0xFF),
                        (byte)(NetworkProtocol.Version >> 8)
                    };
                    bool sent = SteamNetworking.SendP2PPacket(hostId, handshake, (uint)handshake.Length, EP2PSend.k_EP2PSendReliable, 2);
                    if (!sent)
                        Logger?.LogWarning("[Steam] Failed to send handshake packet to host");
                }
                catch (Exception ex)
                {
                    Logger?.LogWarning($"[Steam] Failed to send Steam handshake: {ex.Message}");
                }
            }
        }

        private void OnP2PSessionRequest(P2PSessionRequest_t callback)
        {
            // A peer rejected for an incompatible protocol must not be re-adopted by a
            // session request while it is still in the lobby.
            if (_protocolRejectedPeer != CSteamID.Nil && _protocolRejectedPeer == callback.m_steamIDRemote)
            {
                SteamNetworking.CloseP2PSessionWithUser(callback.m_steamIDRemote);
                return;
            }

            // Only accept P2P traffic from the expected peer, or (on the host) from a
            // verified member of the current lobby. Being able to send a Steam packet to a
            // SteamID must not be enough to claim the single co-op slot.
            if (_remoteSteamId != CSteamID.Nil && _remoteSteamId == callback.m_steamIDRemote)
            {
                SteamNetworking.AcceptP2PSessionWithUser(callback.m_steamIDRemote);
            }
            else if (_remoteSteamId == CSteamID.Nil && IsHost && IsLobbyMember(callback.m_steamIDRemote))
            {
                SteamNetworking.AcceptP2PSessionWithUser(callback.m_steamIDRemote);
                _remoteSteamId = callback.m_steamIDRemote;
                NotifyPeerConnected(callback.m_steamIDRemote);
            }
            else
            {
                // Reject session requests from unexpected peers
                SteamNetworking.CloseP2PSessionWithUser(callback.m_steamIDRemote);
                Logger?.LogWarning($"Rejected P2P session request from unexpected peer: {callback.m_steamIDRemote.m_SteamID}");
            }
        }

        private void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t callback)
        {
            Logger?.LogInfo($"[Steam] OnGameLobbyJoinRequested: lobby={callback.m_steamIDLobby.m_SteamID}");
            // An invite to the lobby we are already in must not drop host state or re-join.
            if (_lobbyId != CSteamID.Nil && _lobbyId == callback.m_steamIDLobby)
                return;

            IsHost = false;
            _protocolRejectedPeer = CSteamID.Nil;
            _requestedLobbyId = callback.m_steamIDLobby.m_SteamID;
            _lobbyRequestGeneration = ++_sessionGeneration;
            SilksongCoopPlugin.Instance?.SetActiveTransport(this);
            SilksongCoopPlugin.Instance?.PrepareForNewSession();

            // Accepting an invite while hosting/connected must not leak the old lobby.
            if (_lobbyId != CSteamID.Nil && _lobbyId != callback.m_steamIDLobby)
            {
                try { SteamMatchmaking.LeaveLobby(_lobbyId); } catch { /* best-effort */ }
                _lobbyId = CSteamID.Nil;
            }
            if (_remoteSteamId != CSteamID.Nil)
            {
                try { SteamNetworking.CloseP2PSessionWithUser(_remoteSteamId); } catch { /* best-effort */ }
                _remoteSteamId = CSteamID.Nil;
            }
            _peerConnectNotified = false;
            IsConnected = false;

            SilksongCoopPlugin.Instance?.SessionState?.SetConnecting(LocalPeerId, callback.m_steamIDLobby.m_SteamID.ToString());
            _connectionAttemptStartMs = NowMs();
            SteamMatchmaking.JoinLobby(callback.m_steamIDLobby);
        }

        private void OnLobbyChatUpdate(LobbyChatUpdate_t callback)
        {
            // Only handle updates for our lobby
            if (_lobbyId == CSteamID.Nil || callback.m_ulSteamIDLobby != _lobbyId.m_SteamID)
                return;

            // Check if a user entered the lobby
            if ((callback.m_rgfChatMemberStateChange & (uint)EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0)
            {
                CSteamID joinedUser = new CSteamID(callback.m_ulSteamIDUserChanged);

                // Ignore our own join event
                if (joinedUser == SteamUser.GetSteamID())
                    return;

                // Host: detect client joining the lobby
                if (IsHost)
                {
                    // This callback can arrive after the first game datagram, so the peer
                    // may already have been rejected for an incompatible protocol. Do not
                    // re-adopt it.
                    if (_protocolRejectedPeer != CSteamID.Nil && _protocolRejectedPeer == joinedUser)
                    {
                        try { SteamNetworking.CloseP2PSessionWithUser(joinedUser); } catch { /* best-effort */ }
                        return;
                    }

                    if (_remoteSteamId != CSteamID.Nil && _remoteSteamId != joinedUser)
                    {
                        try { SteamNetworking.CloseP2PSessionWithUser(joinedUser); } catch { /* best-effort */ }
                        Logger?.LogWarning($"[Steam] Ignoring additional lobby member {joinedUser.m_SteamID} - slot already taken");
                        return;
                    }

                    _remoteSteamId = joinedUser;

                    // Accept the P2P session up front and send a handshake.
                    try
                    {
                        SteamNetworking.AcceptP2PSessionWithUser(joinedUser);
                        // Send the protocol revision so the peer can verify compatibility
                        // before marking the session connected. A bare 0x01 ping carried
                        // no version and could not gate the connection.
                        byte[] handshake = new byte[]
                        {
                            (byte)(NetworkProtocol.Version & 0xFF),
                            (byte)(NetworkProtocol.Version >> 8)
                        };
                        SteamNetworking.SendP2PPacket(
                            joinedUser,
                            handshake,
                            (uint)handshake.Length,
                            EP2PSend.k_EP2PSendReliable,
                            2);
                    }
                    catch (Exception ex)
                    {
                        Logger?.LogWarning($"[Steam] Failed to prepare P2P session for {joinedUser.m_SteamID}: {ex.Message}");
                    }

                    // Don't notify connected until protocol verified on first valid packet
                }
            }
            else if ((callback.m_rgfChatMemberStateChange & (uint)EChatMemberStateChange.k_EChatMemberStateChangeLeft) != 0 ||
                     (callback.m_rgfChatMemberStateChange & (uint)EChatMemberStateChange.k_EChatMemberStateChangeDisconnected) != 0)
            {
                CSteamID leftUser = new CSteamID(callback.m_ulSteamIDUserChanged);

                // Allow a peer that failed protocol validation to retry after it updates
                // and rejoins; the rejection is scoped to one lobby membership.
                if (_protocolRejectedPeer != CSteamID.Nil && _protocolRejectedPeer == leftUser)
                    _protocolRejectedPeer = CSteamID.Nil;

                // Role-agnostic: the client must also notice when the host leaves the
                // lobby, otherwise it stays "connected" and streams into the void.
                if (_remoteSteamId != CSteamID.Nil && _remoteSteamId == leftUser)
                {
                    Logger?.LogInfo($"[Steam] Remote peer left lobby: {leftUser.m_SteamID}");
                    try { SteamNetworking.CloseP2PSessionWithUser(leftUser); } catch { /* best-effort */ }
                    NotifyPeerDisconnected(DisconnectReason.PeerDisconnected);
                }
            }
        }

        /// <summary>
        /// Disposes the transport, disconnecting and cleaning up resources.
        /// </summary>
        public void Dispose()
        {
            Disconnect();
        }

        #endregion // Steam Callback Handlers
    }
}
