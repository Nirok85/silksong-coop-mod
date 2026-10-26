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
        private readonly Callback<LobbyCreated_t> _lobbyCreatedCallback;
        private readonly Callback<LobbyEnter_t> _lobbyEnteredCallback;
        private readonly Callback<P2PSessionRequest_t> _p2pSessionRequestCallback;
        private readonly Callback<GameLobbyJoinRequested_t> _gameLobbyJoinRequestedCallback;
        
        // Fix: Reuse buffer to avoid allocation in hot path
        private byte[] _readBuffer = new byte[4096];
        
        // Fix: Track channels actually used by the protocol (2=vitals, 3=currency/item, 4=death)
        private const int MinChannel = 2;
        private const int MaxChannel = 4;

        public event Action<NetworkPeerId>? OnPeerConnected;
        public event Action<NetworkPeerId, DisconnectReason>? OnPeerDisconnected;
        public event Action<NetworkPeerId, byte[], int, int>? OnDataReceived;

        public bool IsHost { get; private set; }
        public bool IsConnected { get; private set; }
        public NetworkPeerId LocalPeerId { get; private set; }
        public CSteamID CurrentLobbyId => _lobbyId;

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
                System.Diagnostics.Debug.WriteLine($"Steam initialization error: {ex.Message}");
            }

            LocalPeerId = new NetworkPeerId(localId, localName);

            _lobbyCreatedCallback = Callback<LobbyCreated_t>.Create(OnLobbyCreated);
            _lobbyEnteredCallback = Callback<LobbyEnter_t>.Create(OnLobbyEntered);
            _p2pSessionRequestCallback = Callback<P2PSessionRequest_t>.Create(OnP2PSessionRequest);
            _gameLobbyJoinRequestedCallback = Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);
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
                    System.Diagnostics.Debug.WriteLine($"Error leaving lobby: {ex.Message}");
                }
                _lobbyId = CSteamID.Nil;
            }

            if (_remoteSteamId != CSteamID.Nil)
            {
                try
                {
                    SteamNetworking.CloseP2PSessionWithUser(_remoteSteamId);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error closing P2P session: {ex.Message}");
                }

                var remotePeer = new NetworkPeerId(_remoteSteamId.m_SteamID);
                _remoteSteamId = CSteamID.Nil;
                IsConnected = false;
                OnPeerDisconnected?.Invoke(remotePeer, DisconnectReason.GracefulDisconnect);
            }
            else
            {
                IsConnected = false;
            }
        }

        public void Send(NetworkPeerId target, byte[] data, int offset, int length, DeliveryMethod method, byte channel)
        {
            if (!IsSteamAvailable || _remoteSteamId == CSteamID.Nil) return;

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

            SteamNetworking.SendP2PPacket(_remoteSteamId, bufferToSend, (uint)length, sendType, channel);
        }

        public void PollEvents()
        {
            if (!IsSteamAvailable) return;

            // 1. Process Steam callbacks
            SteamAPI.RunCallbacks();

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
                        // Fix: Validate that packets are from the expected remote peer
                        if (_remoteSteamId == CSteamID.Nil)
                        {
                            _remoteSteamId = remoteId;
                            IsConnected = true;
                            OnPeerConnected?.Invoke(new NetworkPeerId(remoteId.m_SteamID));
                        }
                        else if (_remoteSteamId != remoteId)
                        {
                            // Fix: Reject packets from unexpected peers (security fix)
                            System.Diagnostics.Debug.WriteLine($"Rejected packet from unexpected peer: {remoteId.m_SteamID}");
                            continue;
                        }

                        OnDataReceived?.Invoke(new NetworkPeerId(remoteId.m_SteamID), _readBuffer, 0, (int)bytesRead);
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
                OnPeerDisconnected?.Invoke(new NetworkPeerId(0), DisconnectReason.ConnectionFailed);
            }
        }

        private void OnLobbyEntered(LobbyEnter_t callback)
        {
            if (callback.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to enter lobby: {callback.m_EChatRoomEnterResponse}");
                OnPeerDisconnected?.Invoke(new NetworkPeerId(0), DisconnectReason.ConnectionFailed);
                return;
            }

            _lobbyId = new CSteamID(callback.m_ulSteamIDLobby);
            if (!IsHost)
            {
                CSteamID hostId = SteamMatchmaking.GetLobbyOwner(_lobbyId);
                _remoteSteamId = hostId;
                IsConnected = true;
                OnPeerConnected?.Invoke(new NetworkPeerId(hostId.m_SteamID, SteamFriends.GetFriendPersonaName(hostId)));
            }
        }

        private void OnP2PSessionRequest(P2PSessionRequest_t callback)
        {
            // Fix: Only accept P2P session from lobby owner or expected peer (security fix)
            if (_remoteSteamId == CSteamID.Nil || _remoteSteamId == callback.m_steamIDRemote)
            {
                SteamNetworking.AcceptP2PSessionWithUser(callback.m_steamIDRemote);
                if (_remoteSteamId == CSteamID.Nil)
                {
                    _remoteSteamId = callback.m_steamIDRemote;
                    IsConnected = true;
                    OnPeerConnected?.Invoke(new NetworkPeerId(callback.m_steamIDRemote.m_SteamID));
                }
            }
            else
            {
                // Fix: Reject session requests from unexpected peers
                SteamNetworking.CloseP2PSessionWithUser(callback.m_steamIDRemote);
                System.Diagnostics.Debug.WriteLine($"Rejected P2P session request from unexpected peer: {callback.m_steamIDRemote.m_SteamID}");
            }
        }

        private void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t callback)
        {
            SteamMatchmaking.JoinLobby(callback.m_steamIDLobby);
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}
