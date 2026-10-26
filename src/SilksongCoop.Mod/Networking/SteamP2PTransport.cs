using System;
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
        private CSteamID _lobbyId = CSteamID.Nil;
        private CSteamID _remoteSteamId = CSteamID.Nil;
        private long _connectionAttemptStartMs;
        private const long SteamConnectionTimeoutMs = 15000;
        private readonly Callback<LobbyCreated_t> _lobbyCreatedCallback;
        private readonly Callback<LobbyEnter_t> _lobbyEnteredCallback;
        private readonly Callback<P2PSessionRequest_t> _p2pSessionRequestCallback;
        private readonly Callback<GameLobbyJoinRequested_t> _gameLobbyJoinRequestedCallback;
        private readonly Callback<LobbyChatUpdate_t> _lobbyChatUpdateCallback;
        
        // Fix: Reuse buffer to avoid allocation in hot path
        private byte[] _readBuffer = new byte[4096];
        
        // Fix: Track channels actually used by the protocol (0=transform, 2=vitals, 3=currency/item, 4=death)
        private const int MinChannel = 0;
        private const int MaxChannel = 4;

        // Guards against firing OnPeerConnected more than once when both the lobby
        // membership callback and the P2P session/packet paths observe the same peer.
        private bool _peerConnectNotified;

        public event Action<NetworkPeerId>? OnPeerConnected;
        public event Action<NetworkPeerId, DisconnectReason>? OnPeerDisconnected;
        public event Action<NetworkPeerId, byte[], int, int>? OnDataReceived;

        public bool IsHost { get; private set; }
        public bool IsConnected { get; private set; }
        public NetworkPeerId LocalPeerId { get; private set; }
        public CSteamID CurrentLobbyId => _lobbyId;

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

        public SteamP2PTransport()
        {
            ulong localId = 0;
            string localName = "SteamPlayer";

            try
            {
                if (IsSteamAvailable)
                {
                    localId = SteamUser.GetSteamID().m_SteamID;
                    localName = SteamFriends.GetPersonaName();
                    SteamNetworking.AllowP2PPacketRelay(true);
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

        public void StartHost(int port = 7777)
        {
            if (!IsSteamAvailable)
            {
                throw new InvalidOperationException("Steam is not initialized or offline.");
            }

            IsHost = true;
            IsConnected = false;
            SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, 2);
        }

        public void Connect(string addressOrCode)
        {
            if (!IsSteamAvailable)
            {
                throw new InvalidOperationException("Steam is not initialized or offline.");
            }

            if (ulong.TryParse(addressOrCode, out ulong lobbyIdVal))
            {
                CSteamID lobbySteamId = new CSteamID(lobbyIdVal);
                _connectionAttemptStartMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                SteamMatchmaking.JoinLobby(lobbySteamId);
            }
            else
            {
                throw new ArgumentException("Steam transport requires a valid 64-bit Steam Lobby ID.", nameof(addressOrCode));
            }
        }

        public void OpenInviteDialog()
        {
            if (IsSteamAvailable && _lobbyId != CSteamID.Nil)
            {
                SteamFriends.ActivateGameOverlayInviteDialog(_lobbyId);
            }
        }

        public void Disconnect()
        {
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
            _connectionAttemptStartMs = 0;
            }

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

            if (wasNotified)
            {
                OnPeerDisconnected?.Invoke(new NetworkPeerId(peer.m_SteamID), reason);
            }
        }

        public void Send(NetworkPeerId target, byte[] data, int offset, int length, DeliveryMethod method, byte channel)
        {
            if (!IsSteamAvailable || _remoteSteamId == CSteamID.Nil) return;
            if (data == null || offset < 0 || length < 0 || offset + length > data.Length) return;

            EP2PSend sendType = (method == DeliveryMethod.Unreliable || method == DeliveryMethod.Sequenced)
                ? EP2PSend.k_EP2PSendUnreliableNoDelay
                : EP2PSend.k_EP2PSendReliable;

            byte[] bufferToSend;
            if (offset == 0 && length == data.Length)
            {
                bufferToSend = data;
            }
            else
            {
                bufferToSend = new byte[length];
                Buffer.BlockCopy(data, offset, bufferToSend, 0, length);
            }

            try
            {
                bool sent = SteamNetworking.SendP2PPacket(_remoteSteamId, bufferToSend, (uint)length, sendType, channel);
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
                long elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _connectionAttemptStartMs;
                if (elapsed > SteamConnectionTimeoutMs)
                {
                    _connectionAttemptStartMs = 0;
                    Logger?.LogWarning("[Steam] Connection timeout");
                    NotifyPeerDisconnected(DisconnectReason.Timeout);
                }
            }

            // 2. Fix: Poll only the channels used by the protocol (2-4) and reuse buffer
            for (int channel = MinChannel; channel <= MaxChannel; channel++)
            {
                while (SteamNetworking.IsP2PPacketAvailable(out uint msgSize, channel))
                {
                    // Fix: Reuse buffer, expand only if necessary
                    if (msgSize > _readBuffer.Length)
                    {
                        _readBuffer = new byte[msgSize];
                    }

                    if (SteamNetworking.ReadP2PPacket(_readBuffer, msgSize, out uint bytesRead, out CSteamID remoteId, channel))
                    {
                        try
                        {
                            // Fix: Validate that packets are from the expected remote peer
                            if (_remoteSteamId == CSteamID.Nil)
                            {
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

                            // Capture buffer reference to avoid aliasing if buffer is resized
                            byte[] dataBuffer = _readBuffer;
                            int dataLength = (int)bytesRead;
                            OnDataReceived?.Invoke(new NetworkPeerId(remoteId.m_SteamID), dataBuffer, 0, dataLength);
                        }
                        catch (Exception ex)
                        {
                            Logger?.LogError($"[Steam] Error processing packet: {ex.Message}");
                        }
                    }
                }
            }
        }

        private void OnLobbyCreated(LobbyCreated_t callback)
        {
            if (callback.m_eResult == EResult.k_EResultOK)
            {
                _lobbyId = new CSteamID(callback.m_ulSteamIDLobby);
                SteamMatchmaking.SetLobbyData(_lobbyId, "mod_name", "SilksongCoop");
                SteamMatchmaking.SetLobbyData(_lobbyId, "mod_version", "1.0.0");
            }
            else
            {
                IsHost = false;
                OnPeerDisconnected?.Invoke(new NetworkPeerId(0), DisconnectReason.ConnectionFailed);
            }
        }

        private void OnLobbyEntered(LobbyEnter_t callback)
        {
            Logger?.LogInfo($"[Steam] OnLobbyEntered: response={callback.m_EChatRoomEnterResponse}, lobby={callback.m_ulSteamIDLobby}");
            if (callback.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                Logger?.LogWarning($"Failed to enter lobby: {callback.m_EChatRoomEnterResponse}");
                OnPeerDisconnected?.Invoke(new NetworkPeerId(0), DisconnectReason.ConnectionFailed);
                return;
            }

            _lobbyId = new CSteamID(callback.m_ulSteamIDLobby);
            if (!IsHost)
            {
                CSteamID hostId = SteamMatchmaking.GetLobbyOwner(_lobbyId);
                if (hostId == CSteamID.Nil)
                {
                    Logger?.LogWarning("[Steam] Lobby owner is nil - cannot connect");
                    OnPeerDisconnected?.Invoke(new NetworkPeerId(0), DisconnectReason.ConnectionFailed);
                    return;
                }

                _remoteSteamId = hostId;

                // Send an initial handshake packet to trigger P2P session acceptance on the host.
                // Do NOT call NotifyPeerConnected yet - wait for P2P session to be established
                // and for actual data to flow before marking as connected.
                try
                {
                    byte[] handshake = new byte[] { 0x01 };
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
            // Only accept P2P session from the expected remote peer
            if (_remoteSteamId != CSteamID.Nil && _remoteSteamId == callback.m_steamIDRemote)
            {
                SteamNetworking.AcceptP2PSessionWithUser(callback.m_steamIDRemote);
            }
            else if (_remoteSteamId == CSteamID.Nil && IsHost)
            {
                // Host hasn't seen the client yet but is expecting a connection
                // Accept only if we're in hosting state
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
                    _remoteSteamId = joinedUser;

                    // Accept the P2P session up front and send a handshake. Without this
                    // the initial vitals broadcast can race ahead of session acceptance
                    // and the client sees "connected" but no state.
                    try
                    {
                        SteamNetworking.AcceptP2PSessionWithUser(joinedUser);
                        byte[] handshake = new byte[] { 0x01 };
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

                    NotifyPeerConnected(joinedUser);
                }
            }
            else if ((callback.m_rgfChatMemberStateChange & (uint)EChatMemberStateChange.k_EChatMemberStateChangeLeft) != 0 ||
                     (callback.m_rgfChatMemberStateChange & (uint)EChatMemberStateChange.k_EChatMemberStateChangeDisconnected) != 0)
            {
                CSteamID leftUser = new CSteamID(callback.m_ulSteamIDUserChanged);

                // Role-agnostic: the client must also notice when the host leaves the
                // lobby, otherwise it stays "connected" and streams into the void.
                if (_remoteSteamId != CSteamID.Nil && _remoteSteamId == leftUser)
                {
                    Logger?.LogInfo($"[Steam] Remote peer left lobby: {leftUser.m_SteamID}");
                    NotifyPeerDisconnected(DisconnectReason.GracefulDisconnect);
                }
            }
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
