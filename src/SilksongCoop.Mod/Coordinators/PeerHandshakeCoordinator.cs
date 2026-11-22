using System;
using System.Collections.Generic;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using SilksongCoop.Mod.Networking;
using SilksongCoop.Mod.Services;
using SilksongCoop.Mod.State;
using SilksongCoop.Mod.UI;
using UnityEngine;

namespace SilksongCoop.Mod.Coordinators
{
    /// <summary>
    /// Coordinates peer readiness handshake, wallet seed transmission and retry loop,
    /// and the initial catch-up packet burst sequence.
    /// </summary>
    public sealed class PeerHandshakeCoordinator : ISyncOrchestrator, IDisposable
    {
        private readonly ISessionService _sessionService;
        private readonly VitalsSyncService _vitalsSync;
        private readonly DeathLifecycleCoordinator _deathCoordinator;
        private readonly IInventorySyncService _inventorySync;
        private readonly QuestStateSyncManager? _questStateSync;
        private readonly EnemySyncController? _enemySync;
        private readonly ReviveLifecycleCoordinator _reviveCoordinator;
        private readonly InventoryApplicationCoordinator _inventoryCoordinator;
        private readonly Func<VitalsSyncManager?> _vitalsSyncManagerProvider;
        private readonly Func<INetworkTransport?> _transportProvider;
        private readonly Func<NetworkDiagnostics?> _diagnosticsProvider;
        private readonly Func<bool> _isSaveReadyProvider;
        private readonly Action<DisconnectReason> _disconnectCallback;

        private readonly HashSet<NetworkPeerId> _readySent = new();
        private readonly Dictionary<NetworkPeerId, Guid> _readyForCatchUp = new();
        private readonly Dictionary<NetworkPeerId, Guid> _walletPeerInstances = new();
        private readonly Dictionary<NetworkPeerId, bool> _walletPeerBootstrapCompleted = new();
        private readonly HashSet<NetworkPeerId> _initialStateSent = new();
        private readonly Dictionary<NetworkPeerId, BenchCheckpointSyncPacket> _pendingCheckpoints = new();
        private readonly Dictionary<NetworkPeerId, WalletSeedRetryState> _walletSeedOutstanding = new();
        private readonly WalletSeedAssembler _walletSeedAssembler = new();
        private readonly HashSet<NetworkPeerId> _appliedWalletSeeds = new();
        private readonly Dictionary<NetworkPeerId, (float Deadline, float NextAttempt)> _catchUpMarkers = new();

        private string? _walletRoom;
        private PlayerData? _walletSave;
        private Guid _walletSaveInstance;
        private bool _awaitingWalletSeed;
        private float _awaitingWalletSeedSince;
        private bool _initializedVitalsForConnection;
        private BenchCheckpointSyncPacket? _lastBenchCheckpoint;
        private PlayerData? _benchSave;

        private const float WalletSeedTimeoutSeconds = 20f;
        private const float WalletSeedRetrySeconds = 20f;
        private const float WalletSeedRetryIntervalSeconds = 0.25f;
        private const float CatchUpMarkerTimeoutSeconds = 20f;

        public enum LocalSyncStage
        {
            WaitingForSave,
            WaitingForPeer,
            SynchronizingWallet,
            QueueingInitialState,
            InitialStateQueued
        }

        public LocalSyncStage GetLocalStage(NetworkPeerId peer)
        {
            if (!_initializedVitalsForConnection) return LocalSyncStage.WaitingForSave;
            if (!_readySent.Contains(peer) || !_readyForCatchUp.ContainsKey(peer))
                return LocalSyncStage.WaitingForPeer;

            if (_walletSeedOutstanding.ContainsKey(peer) ||
                (_awaitingWalletSeed && _sessionService.SessionState?.RemotePeerId == peer))
                return LocalSyncStage.SynchronizingWallet;

            return !_initialStateSent.Contains(peer) || _catchUpMarkers.ContainsKey(peer)
                ? LocalSyncStage.QueueingInitialState
                : LocalSyncStage.InitialStateQueued;
        }

        public Guid WalletSaveInstance => _walletSaveInstance;
        public bool AwaitingWalletSeed => _awaitingWalletSeed;
        public BenchCheckpointSyncPacket? LastBenchCheckpoint => _lastBenchCheckpoint;
        public string? WalletRoom => _walletRoom;
        public PlayerData? WalletSave => _walletSave;

        private readonly struct WalletSeedRetryState
        {
            public readonly float Deadline;
            public readonly float NextAttempt;
            public readonly WalletSeedPacket Seed;
            public WalletSeedRetryState(float deadline, float nextAttempt, WalletSeedPacket seed)
            {
                Deadline = deadline;
                NextAttempt = nextAttempt;
                Seed = seed;
            }
        }

        public PeerHandshakeCoordinator(
            ISessionService sessionService,
            VitalsSyncService vitalsSync,
            DeathLifecycleCoordinator deathCoordinator,
            IInventorySyncService inventorySync,
            QuestStateSyncManager? questStateSync,
            EnemySyncController? enemySync,
            ReviveLifecycleCoordinator reviveCoordinator,
            InventoryApplicationCoordinator inventoryCoordinator,
            Func<VitalsSyncManager?> vitalsSyncManagerProvider,
            Func<INetworkTransport?> transportProvider,
            Func<NetworkDiagnostics?> diagnosticsProvider,
            Func<bool> isSaveReadyProvider,
            Action<DisconnectReason> disconnectCallback)
        {
            _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
            _vitalsSync = vitalsSync ?? throw new ArgumentNullException(nameof(vitalsSync));
            _deathCoordinator = deathCoordinator ?? throw new ArgumentNullException(nameof(deathCoordinator));
            _inventorySync = inventorySync ?? throw new ArgumentNullException(nameof(inventorySync));
            _questStateSync = questStateSync;
            _enemySync = enemySync;
            _reviveCoordinator = reviveCoordinator ?? throw new ArgumentNullException(nameof(reviveCoordinator));
            _inventoryCoordinator = inventoryCoordinator ?? throw new ArgumentNullException(nameof(inventoryCoordinator));
            _vitalsSyncManagerProvider = vitalsSyncManagerProvider ?? throw new ArgumentNullException(nameof(vitalsSyncManagerProvider));
            _transportProvider = transportProvider ?? throw new ArgumentNullException(nameof(transportProvider));
            _diagnosticsProvider = diagnosticsProvider ?? throw new ArgumentNullException(nameof(diagnosticsProvider));
            _isSaveReadyProvider = isSaveReadyProvider ?? throw new ArgumentNullException(nameof(isSaveReadyProvider));
            _disconnectCallback = disconnectCallback ?? throw new ArgumentNullException(nameof(disconnectCallback));
        }

        public void Initialize() { }

        public void OnSessionStarted(bool isHost)
        {
            ResetPeerReadiness();
        }

        public void OnSessionEnded()
        {
            ResetPeerReadiness();
            _walletSave = null;
            _walletRoom = null;
            _lastBenchCheckpoint = null;
            _benchSave = null;
            _walletPeerInstances.Clear();
        }

        public void Dispose()
        {
            ResetPeerReadiness();
        }

        public void Update()
        {
            PumpPeerReadiness();
        }

        public void ResetPeerReadiness()
        {
            _walletSeedAssembler.Reset();
            _appliedWalletSeeds.Clear();
            _catchUpMarkers.Clear();
            _readySent.Clear();
            _readyForCatchUp.Clear();
            _initialStateSent.Clear();
            _pendingCheckpoints.Clear();
            _initializedVitalsForConnection = false;
            _awaitingWalletSeed = false;
            _walletSeedOutstanding.Clear();
        }

        public void ResetPeerState(NetworkPeerId peer)
        {
            _readySent.Remove(peer);
            _readyForCatchUp.Remove(peer);
            _walletPeerInstances.Remove(peer);
            _walletPeerBootstrapCompleted.Remove(peer);
            _initialStateSent.Remove(peer);
            _pendingCheckpoints.Remove(peer);
            _walletSeedOutstanding.Remove(peer);
            _catchUpMarkers.Remove(peer);
            _appliedWalletSeeds.Remove(peer);
            if (_sessionService.SessionState?.RemotePeerId == peer)
            {
                _walletSeedAssembler.Reset();
                _awaitingWalletSeed = false;
            }
            _inventorySync.ResetPeerState(peer);
        }

        public void PumpPeerReadiness()
        {
            if (!_isSaveReadyProvider()) return;
            var transport = _transportProvider();
            var session = _sessionService.SessionState;
            if (transport == null || session?.IsConnected != true) return;

            if (!_initializedVitalsForConnection)
            {
                var pd = PlayerData.instance;
                if (pd != null)
                {
                    string? room = transport is SteamP2PTransport steam
                        ? steam.CurrentLobbyId.m_SteamID.ToString()
                        : session.CurrentRoomCode;
                    bool resume = ReferenceEquals(_walletSave, pd) && !string.IsNullOrEmpty(room) && _walletRoom == room;
                    if (!resume)
                    {
                        _walletSave = pd;
                        _walletRoom = room;
                        _walletSaveInstance = Guid.NewGuid();
                        _walletPeerInstances.Clear();
                        _walletPeerBootstrapCompleted.Clear();
                        _questStateSync?.ClearAuthoredChanges();
                        _vitalsSyncManagerProvider()?.ResetCurrencyJournal();
                    }
                    _vitalsSync.InitializeState(pd.health, pd.maxHealthBase, pd.silk,
                        Math.Max(1, pd.silkMax), pd.nailUpgrades, pd.geo, pd.ShellShards, preserveWallet: resume);
                    if (!session.IsHost && !resume) _vitalsSync.BeginWalletSeed();
                    _inventoryCoordinator.BaseMaxHealth = pd.maxHealthBase;
                    _inventoryCoordinator.BaseMaxSilkSpools = Math.Max(1, pd.silkMax);
                    _initializedVitalsForConnection = true;
                }
            }

            if (!_initializedVitalsForConnection) return;
            if (!PumpWalletSeed() || !PumpWalletSeedTimeout()) return;
            if (_vitalsSync.WalletSeedOverflow)
            {
                _disconnectCallback(DisconnectReason.ConnectionFailed);
                return;
            }

            foreach (var peer in session.RemotePeerIds)
            {
                if (!_readySent.Contains(peer))
                {
                    if (!PacketSender.SendTo(transport, peer, new PeerReadyPacket
                    {
                        Header = new PacketHeader(PacketType.PeerReady, NetworkChannels.Currency, 0, NetworkClock.NowMs),
                        SaveInstance = _walletSaveInstance
                    }, DeliveryMethod.ReliableOrdered, NetworkChannels.Currency, PacketHeader.HeaderSize + 16, _diagnosticsProvider()))
                        continue;

                    _readySent.Add(peer);
                }

                if (_readyForCatchUp.TryGetValue(peer, out var saveInstance) && _initialStateSent.Add(peer))
                {
                    bool existingPeer = _walletPeerInstances.TryGetValue(peer, out var previousSave);
                    bool resumedPeer = existingPeer && previousSave == saveInstance &&
                        _walletPeerBootstrapCompleted.TryGetValue(peer, out var bootstrapCompleted) && bootstrapCompleted;
                    if (resumedPeer && _vitalsSyncManagerProvider()?.CanResumeCurrencyPeer(peer) == false)
                    {
                        SilksongCoopPlugin.LogWarn($"[HandshakeCoordinator] Cannot resume wallet sync for {peer}: currency journal exhausted.");
                        if (transport is SteamP2PTransport steam)
                            steam.FailReliablePeer(peer);
                        else
                        {
                            _disconnectCallback(DisconnectReason.ConnectionFailed);
                            return;
                        }
                        continue;
                    }
                    _walletPeerInstances[peer] = saveInstance;
                    if (!resumedPeer)
                    {
                        if (_inventorySync is InventorySyncService concrete)
                            concrete.ResetCounterPeer(peer);
                        _vitalsSync.ResetCurrencyPeer(peer);
                        if (existingPeer || session.IsHost)
                            _vitalsSyncManagerProvider()?.ForgetCurrencyPeer(peer);
                        if (session.IsHost)
                        {
                            var seed = _vitalsSync.CaptureWalletSeed(transport.LocalPeerId, session.RemotePeerIds);
                            if (_vitalsSyncManagerProvider()?.SendWalletSeed(peer, seed) != true)
                                _walletSeedOutstanding[peer] = new WalletSeedRetryState(Time.unscaledTime + WalletSeedRetrySeconds,
                                    Time.unscaledTime + WalletSeedRetryIntervalSeconds, seed);
                            else
                                _walletPeerBootstrapCompleted[peer] = true;
                        }
                        else if (session.RemotePeerId == peer)
                        {
                            _awaitingWalletSeed = true;
                            _awaitingWalletSeedSince = Time.unscaledTime;
                        }
                    }
                    if (session.IsHost || resumedPeer || session.RemotePeerId != peer)
                        _vitalsSyncManagerProvider()?.EnableCurrencyPeer(peer);

                    var heroPosition = HeroController.instance != null ? HeroController.instance.transform.position : Vector3.zero;
                    if (_deathCoordinator != null && !PacketSender.SendTo(transport, peer,
                            _deathCoordinator.CreateLifecycleSnapshot(heroPosition.x, heroPosition.y),
                            DeliveryMethod.ReliableOrdered, NetworkChannels.Death, 64, _diagnosticsProvider()))
                    {
                        _disconnectCallback(DisconnectReason.ConnectionFailed);
                        return;
                    }
                    _enemySync?.BeginCatchUpForNewPeer();
                    _vitalsSync.BroadcastCurrentVitals();
                    _vitalsSync.BroadcastClaimedPickups();
                    _inventoryCoordinator.BroadcastInitialInventory(peer);
                    _questStateSync?.ReplayAuthoredChanges(peer);
                    if (_lastBenchCheckpoint.HasValue && ReferenceEquals(_benchSave, PlayerData.instance) &&
                        session.IsHost)
                    {
                        if (!PacketSender.SendTo(transport, peer, _lastBenchCheckpoint.Value,
                                DeliveryMethod.ReliableOrdered, NetworkChannels.Death, 1024, _diagnosticsProvider()))
                            SilksongCoopPlugin.LogWarn("[HandshakeCoordinator] Transport refused bench checkpoint catch-up.");
                    }

                    _catchUpMarkers[peer] = (Time.unscaledTime + WalletSeedRetrySeconds + CatchUpMarkerTimeoutSeconds, Time.unscaledTime);
                }

                if (!PumpCatchUpMarker(transport, peer)) return;

                if (_pendingCheckpoints.TryGetValue(peer, out var bench) &&
                    TryApplyRemoteBenchCheckpoint(bench.SceneName, bench.RespawnMarker, bench.SpawnType, bench.FacingRight))
                {
                    _pendingCheckpoints.Remove(peer);
                }
            }
        }

        private bool PumpCatchUpMarker(INetworkTransport transport, NetworkPeerId peer)
        {
            if (_walletSeedOutstanding.ContainsKey(peer) ||
                !_catchUpMarkers.TryGetValue(peer, out var pending) ||
                !_readyForCatchUp.TryGetValue(peer, out var saveInstance))
                return true;

            float now = Time.unscaledTime;
            if (now < pending.NextAttempt) return true;
            if (PacketSender.SendTo(transport, peer, new CatchUpCompletePacket
            {
                Header = new PacketHeader(PacketType.CatchUpComplete, NetworkChannels.Currency, 0, NetworkClock.NowMs),
                SaveInstance = saveInstance
            }, DeliveryMethod.ReliableOrdered, NetworkChannels.Currency, PacketHeader.HeaderSize + 16, _diagnosticsProvider()))
            {
                _catchUpMarkers.Remove(peer);
                return true;
            }

            if (now < pending.Deadline)
            {
                _catchUpMarkers[peer] = (pending.Deadline, now + WalletSeedRetryIntervalSeconds);
                return true;
            }

            _catchUpMarkers.Remove(peer);
            SilksongCoopPlugin.LogWarn("[HandshakeCoordinator] Catch-up marker could not be queued.");
            if (transport is SteamP2PTransport steam)
            {
                steam.FailReliablePeer(peer);
                return true;
            }

            _disconnectCallback(DisconnectReason.ConnectionFailed);
            return false;
        }

        public void HandlePeerReady(NetworkPeerId peer, Guid saveInstance)
        {
            var session = _sessionService.SessionState;
            if (saveInstance == Guid.Empty || session?.IsConnected != true || !session.Peers.TryGet(peer, out _))
                return;

            if (_readyForCatchUp.TryAdd(peer, saveInstance))
            {
                PumpPeerReadiness();
            }
        }

        public void HandleCatchUpComplete(NetworkPeerId peer, Guid completedInstance)
        {
            if (completedInstance == _walletSaveInstance)
            {
                SilksongCoopPlugin.LogInfo($"[HandshakeCoordinator] Initial currency-channel marker received from {peer.DisplayName}.");
            }
            else
            {
                _diagnosticsProvider()?.RecordRejected();
            }
        }

        public void HandleWalletSeed(NetworkPeerId peer, WalletSeedPacket seed)
        {
            var session = _sessionService.SessionState;
            if (session?.IsConnected == true && !session.IsHost && session.RemotePeerId == peer &&
                _transportProvider() != null && !_appliedWalletSeeds.Contains(peer))
            {
                // A resumed local connection may not have armed seed collection; arm it
                // lazily so an incoming seed is never silently dropped.
                if (!_awaitingWalletSeed)
                {
                    _vitalsSync.BeginWalletSeed();
                    _awaitingWalletSeed = true;
                    _awaitingWalletSeedSince = Time.unscaledTime;
                }
                if (!_walletSeedAssembler.TryAdd(seed, out var completedSeed)) return;
                ApplyWalletSeed(peer, completedSeed);
                _appliedWalletSeeds.Add(peer);
                _walletPeerBootstrapCompleted[peer] = true;
                _awaitingWalletSeed = false;
                _vitalsSyncManagerProvider()?.EnableCurrencyPeer(peer);
            }
        }

        public void ApplyWalletSeed(NetworkPeerId peer, WalletSeedPacket seed)
        {
            var transport = _transportProvider();
            if (transport == null) return;
            _vitalsSync.ApplyWalletSeed(seed, transport.LocalPeerId,
                _vitalsSyncManagerProvider()?.GetPendingCurrencyPackets(peer) ?? Array.Empty<CurrencySyncPacket>());
        }

        public void HandleBenchCheckpoint(NetworkPeerId peer, BenchCheckpointSyncPacket bench)
        {
            if (TryApplyRemoteBenchCheckpoint(bench.SceneName, bench.RespawnMarker, bench.SpawnType, bench.FacingRight))
                _pendingCheckpoints.Remove(peer);
            else
                _pendingCheckpoints[peer] = bench;
        }

        public void BroadcastBenchCheckpoint(BenchCheckpointSyncPacket checkpoint)
        {
            _lastBenchCheckpoint = checkpoint;
            _benchSave = PlayerData.instance;
        }

        public bool TryApplyRemoteBenchCheckpoint(string sceneName, string respawnMarker, int spawnType, bool facingRight)
        {
            try
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                if (!scene.isLoaded || scene.name != sceneName || GameManager.instance?.IsGameplayScene() != true)
                    return false;

                bool foundMarker = false;
                foreach (var marker in RespawnMarker.Markers)
                {
                    if (marker == null || marker.gameObject.scene != scene || marker.name != respawnMarker) continue;
                    foundMarker = true;
                    break;
                }
                if (!foundMarker) return false;

                using (RemoteApplyScope.Begin())
                {
                    var pd = PlayerData.instance;
                    if (pd != null)
                    {
                        pd.SetBenchRespawn(respawnMarker, sceneName, spawnType, facingRight);
                        SilksongCoopPlugin.LogInfo($"[HandshakeCoordinator] Applied remote bench checkpoint: {sceneName} / {respawnMarker}");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[HandshakeCoordinator] Failed applying remote bench checkpoint: {ex.Message}");
            }
            return false;
        }

        private bool PumpWalletSeed()
        {
            if (_walletSeedOutstanding.Count == 0) return true;
            var session = _sessionService.SessionState;
            var transport = _transportProvider();
            if (session?.IsConnected != true || session.IsHost != true || transport == null)
            {
                _walletSeedOutstanding.Clear();
                return true;
            }

            var now = Time.unscaledTime;
            List<NetworkPeerId>? delivered = null;
            List<KeyValuePair<NetworkPeerId, WalletSeedRetryState>>? retries = null;
            foreach (var pair in _walletSeedOutstanding)
            {
                if (now < pair.Value.NextAttempt)
                {
                    continue;
                }

                if (_vitalsSyncManagerProvider()?.SendWalletSeed(pair.Key, pair.Value.Seed) == true)
                {
                    _walletPeerBootstrapCompleted[pair.Key] = true;
                    delivered ??= new List<NetworkPeerId>();
                    delivered.Add(pair.Key);
                    continue;
                }

                if (now < pair.Value.Deadline)
                {
                    retries ??= new List<KeyValuePair<NetworkPeerId, WalletSeedRetryState>>();
                    retries.Add(new KeyValuePair<NetworkPeerId, WalletSeedRetryState>(
                        pair.Key, new WalletSeedRetryState(pair.Value.Deadline, now + WalletSeedRetryIntervalSeconds, pair.Value.Seed)));
                    continue;
                }

                SilksongCoopPlugin.LogWarn("[HandshakeCoordinator] Wallet seed could not be queued.");
                if (transport is SteamP2PTransport steam)
                {
                    steam.FailReliablePeer(pair.Key);
                    _catchUpMarkers.Remove(pair.Key);
                    delivered ??= new List<NetworkPeerId>();
                    delivered.Add(pair.Key);
                    continue;
                }

                _walletSeedOutstanding.Clear();
                _disconnectCallback(DisconnectReason.ConnectionFailed);
                return false;
            }

            if (delivered != null)
            {
                foreach (var peer in delivered) _walletSeedOutstanding.Remove(peer);
            }
            if (retries != null)
            {
                foreach (var retry in retries) _walletSeedOutstanding[retry.Key] = retry.Value;
            }
            return true;
        }

        private bool PumpWalletSeedTimeout()
        {
            if (!_awaitingWalletSeed || Time.unscaledTime - _awaitingWalletSeedSince < WalletSeedTimeoutSeconds)
                return true;

            _awaitingWalletSeed = false;
            SilksongCoopPlugin.LogWarn("[HandshakeCoordinator] Wallet seed was not received in time; disconnecting.");
            _disconnectCallback(DisconnectReason.ConnectionFailed);
            return false;
        }
    }
}
