using System;
using System.Collections.Generic;
using SilksongCoop.Core.Localization;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using SilksongCoop.Mod.Clones;
using SilksongCoop.Mod.Services;
using SilksongCoop.Mod.State;
using SilksongCoop.Mod.UI;
using UnityEngine;

namespace SilksongCoop.Mod.Coordinators
{
    /// <summary>
    /// Ingests raw network payloads from the active transport, validates packet headers and framing,
    /// buffers incoming gameplay packets while save loading is incomplete, and dispatches validated
    /// packets to their respective coordinators and sync managers.
    /// </summary>
    public sealed class IncomingPacketRouter : IDisposable
    {
        private readonly ISessionService _sessionService;
        private readonly PeerHandshakeCoordinator _handshakeCoordinator;
        private readonly ReviveLifecycleCoordinator _reviveCoordinator;
        private readonly InventoryApplicationCoordinator _inventoryCoordinator;
        private readonly VitalsSyncService _vitalsSync;
        private readonly DeathLifecycleCoordinator _deathCoordinator;
        private readonly QuestStateSyncManager? _questStateSync;
        private readonly EnemySyncController? _enemySync;
        private readonly Func<RemoteHornetSpawner?> _cloneSpawnerProvider;
        private readonly Func<CoopMenuUI?> _menuUIProvider;
        private readonly Func<NetworkDiagnostics?> _diagnosticsProvider;
        private readonly Func<VitalsSyncManager?> _vitalsSyncManagerProvider;
        private readonly Func<bool> _isSaveReadyProvider;
        private readonly Func<bool> _canSendSceneTransformProvider;
        private readonly Func<INetworkTransport?> _activeTransportProvider;
        private readonly Action<DisconnectReason> _disconnectCallback;
        private readonly Func<DesyncDetector?> _desyncDetectorProvider;

        private readonly Dictionary<NetworkPeerId, RemoteSceneSequence> _remoteScenes = new();
        private readonly Queue<(NetworkPeerId Peer, byte[] Data)> _deferredGamePackets = new();

        public int DeferredGamePacketsCount => _deferredGamePackets.Count;

        public IncomingPacketRouter(
            ISessionService sessionService,
            PeerHandshakeCoordinator handshakeCoordinator,
            ReviveLifecycleCoordinator reviveCoordinator,
            InventoryApplicationCoordinator inventoryCoordinator,
            VitalsSyncService vitalsSync,
            DeathLifecycleCoordinator deathCoordinator,
            QuestStateSyncManager? questStateSync,
            EnemySyncController? enemySync,
            Func<RemoteHornetSpawner?> cloneSpawnerProvider,
            Func<CoopMenuUI?> menuUIProvider,
            Func<NetworkDiagnostics?> diagnosticsProvider,
            Func<VitalsSyncManager?> vitalsSyncManagerProvider,
            Func<bool> isSaveReadyProvider,
            Func<bool> canSendSceneTransformProvider,
            Func<INetworkTransport?> activeTransportProvider,
            Action<DisconnectReason> disconnectCallback,
            Func<DesyncDetector?> desyncDetectorProvider)
        {
            _sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
            _handshakeCoordinator = handshakeCoordinator ?? throw new ArgumentNullException(nameof(handshakeCoordinator));
            _reviveCoordinator = reviveCoordinator ?? throw new ArgumentNullException(nameof(reviveCoordinator));
            _inventoryCoordinator = inventoryCoordinator ?? throw new ArgumentNullException(nameof(inventoryCoordinator));
            _vitalsSync = vitalsSync ?? throw new ArgumentNullException(nameof(vitalsSync));
            _deathCoordinator = deathCoordinator ?? throw new ArgumentNullException(nameof(deathCoordinator));
            _questStateSync = questStateSync;
            _enemySync = enemySync;
            _cloneSpawnerProvider = cloneSpawnerProvider ?? throw new ArgumentNullException(nameof(cloneSpawnerProvider));
            _menuUIProvider = menuUIProvider ?? throw new ArgumentNullException(nameof(menuUIProvider));
            _diagnosticsProvider = diagnosticsProvider ?? throw new ArgumentNullException(nameof(diagnosticsProvider));
            _vitalsSyncManagerProvider = vitalsSyncManagerProvider ?? throw new ArgumentNullException(nameof(vitalsSyncManagerProvider));
            _isSaveReadyProvider = isSaveReadyProvider ?? throw new ArgumentNullException(nameof(isSaveReadyProvider));
            _canSendSceneTransformProvider = canSendSceneTransformProvider ?? throw new ArgumentNullException(nameof(canSendSceneTransformProvider));
            _activeTransportProvider = activeTransportProvider ?? throw new ArgumentNullException(nameof(activeTransportProvider));
            _disconnectCallback = disconnectCallback ?? throw new ArgumentNullException(nameof(disconnectCallback));
            _desyncDetectorProvider = desyncDetectorProvider ?? throw new ArgumentNullException(nameof(desyncDetectorProvider));
        }

        public void Clear()
        {
            _remoteScenes.Clear();
            _deferredGamePackets.Clear();
        }

        public void ClearPeer(NetworkPeerId peer)
        {
            _remoteScenes.Remove(peer);
        }

        public void Dispose()
        {
            Clear();
        }

        public void HandleDataReceived(INetworkTransport transport, INetworkTransport? activeTransport, NetworkPeerId peer, byte[] data, int offset, int length)
        {
            try
            {
                if (!ReferenceEquals(transport, activeTransport)) return;

                _sessionService.SessionState?.RecordReceived(length);
                _diagnosticsProvider()?.RecordReceived(length);

                var span = new ReadOnlySpan<byte>(data, offset, length);
                if (!_isSaveReadyProvider() && IsKnownPeer(peer) &&
                    PacketHeader.TryReadFrom(span, out var header) &&
                    header.IsValidPacketType && header.PacketId != PacketType.PeerReady)
                {
                    if (header.PacketId == PacketType.TransformSync ||
                        header.PacketId == PacketType.ActionTrigger) return;

                    if (_deferredGamePackets.Count >= 4096)
                    {
                        SilksongCoopPlugin.LogWarn("[IncomingPacketRouter] Save load packet queue exhausted; disconnecting.");
                        _disconnectCallback(DisconnectReason.ConnectionFailed);
                        return;
                    }
                    _deferredGamePackets.Enqueue((peer, span.ToArray()));
                    return;
                }

                if (_isSaveReadyProvider() && _deferredGamePackets.Count > 0)
                {
                    _handshakeCoordinator.PumpPeerReadiness();
                }

                ProcessPacket(peer, span);
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[IncomingPacketRouter] Error processing packet from {peer}: {ex.Message}");
            }
        }

        public void DrainDeferredPackets()
        {
            if (!_isSaveReadyProvider()) return;

            while (_deferredGamePackets.Count > 0)
            {
                var pending = _deferredGamePackets.Dequeue();
                ProcessPacket(pending.Peer, pending.Data);
            }
        }

        public void ProcessPacket(NetworkPeerId peer, ReadOnlySpan<byte> span)
        {
            try
            {
                if (!IsKnownPeer(peer))
                {
                    _diagnosticsProvider()?.RecordRejected();
                    return;
                }

                if (DesyncChecksumFrame.TryRead(span, out uint round, out uint checksum))
                {
                    _desyncDetectorProvider()?.OnRemoteChecksumReceived(checksum, round, Time.unscaledTime);
                    return;
                }

                if (span.Length < PacketHeader.HeaderSize)
                {
                    _diagnosticsProvider()?.RecordRejected();
                    return;
                }

                if (!PacketHeader.TryReadFrom(span, out var header))
                {
                    _diagnosticsProvider()?.RecordRejected();
                    return;
                }

                if (!header.IsValidPacketType)
                {
                    _diagnosticsProvider()?.RecordRejected();
                    return;
                }

                var reader = new PacketReader(span);
                switch (header.PacketId)
                {
                    case PacketType.PeerReady:
                        reader.ReadHeader();
                        var saveInstance = reader.ReadGuid();
                        if (reader.Remaining != 0 || saveInstance == Guid.Empty) { _diagnosticsProvider()?.RecordRejected(); return; }
                        _handshakeCoordinator.HandlePeerReady(peer, saveInstance);
                        break;

                    case PacketType.CatchUpComplete:
                        reader.ReadHeader();
                        var completedInstance = reader.ReadGuid();
                        if (reader.Remaining != 0 || completedInstance == Guid.Empty) { _diagnosticsProvider()?.RecordRejected(); return; }
                        _handshakeCoordinator.HandleCatchUpComplete(peer, completedInstance);
                        break;

                    case PacketType.TransformSync:
                        var trans = TransformSyncPacket.Deserialize(ref reader);
                        if (reader.Remaining != 0 || !PacketValidator.ValidateTransformSync(trans)) { _diagnosticsProvider()?.RecordRejected(); return; }
                        _remoteScenes.TryGetValue(peer, out var remoteScene);
                        if (!remoteScene.AcceptTransform(trans.SceneGeneration, header.TickSequence)) break;
                        _remoteScenes[peer] = remoteScene;
                        int localSceneHash = GameManager.instance != null ? GameManager.instance.sceneNameHash : 0;
                        bool isSameScene = _canSendSceneTransformProvider() && trans.SceneNameHash != 0 && trans.SceneNameHash == localSceneHash;
                        _cloneSpawnerProvider()?.UpdateRemoteTransform(
                            peer,
                            trans.PositionX, trans.PositionY,
                            trans.VelocityX, trans.VelocityY,
                            trans.FacingDirection,
                            trans.StateFlags,
                            trans.AnimationClipHash,
                            trans.NormalizedTime,
                            header.Timestamp,
                            isSameScene,
                            header.TickSequence);
                        break;

                    case PacketType.BenchCheckpointSync:
                        var bench = BenchCheckpointSyncPacket.Deserialize(ref reader);
                        if (reader.Remaining != 0 || !PacketValidator.ValidateBenchCheckpoint(bench)) { _diagnosticsProvider()?.RecordRejected(); return; }
                        _handshakeCoordinator.HandleBenchCheckpoint(peer, bench);
                        break;

                    case PacketType.ActionTrigger:
                        var action = ActionTriggerPacket.Deserialize(ref reader);
                        if (reader.Remaining != 0 || !PacketValidator.ValidateActionTrigger(action)) { _diagnosticsProvider()?.RecordRejected(); return; }
                        var cloneSpawner = _cloneSpawnerProvider();
                        if (cloneSpawner == null || !cloneSpawner.IsRemoteInSameScene(peer) ||
                            !_remoteScenes.TryGetValue(peer, out var actionScene) ||
                            !actionScene.HasTransform || actionScene.Generation != action.SceneGeneration)
                        {
                            break;
                        }
                        cloneSpawner.TriggerRemoteAction(peer, action.Action, (byte)action.Parameter, action.OriginX, action.OriginY);
                        break;

                    case PacketType.VitalsSync:
                        var vitals = VitalsSyncPacket.Deserialize(ref reader);
                        if (reader.Remaining != 0 || !PacketValidator.ValidateVitalsSync(vitals)) { _diagnosticsProvider()?.RecordRejected(); return; }
                        _vitalsSync.HandleVitalsPacket(peer, vitals);
                        break;

                    case PacketType.CurrencySync:
                        var currency = CurrencySyncPacket.Deserialize(ref reader);
                        if (reader.Remaining != 0 || !PacketValidator.ValidateCurrencySync(currency)) { _diagnosticsProvider()?.RecordRejected(); return; }
                        if (currency.DeltaAmount != 0 || currency.PickupKey != 0)
                            _vitalsSync.HandleCurrencyPacket(peer, currency);
                        var transport = _activeTransportProvider();
                        if (transport != null)
                        {
                            if (!PacketSender.SendTo(transport, peer, new CurrencyReceiptPacket
                            {
                                Header = new PacketHeader(PacketType.CurrencyReceipt, NetworkChannels.Currency, 0, NetworkClock.NowMs),
                                TransactionId = currency.TransactionId
                            }, DeliveryMethod.ReliableOrdered, NetworkChannels.Currency, PacketHeader.HeaderSize + 16, _diagnosticsProvider()))
                            {
                                SilksongCoopPlugin.LogWarn("[IncomingPacketRouter] Transport refused a currency receipt; queued for bounded retry.");
                                _inventoryCoordinator.EnqueueCurrencyReceipt(peer, currency.TransactionId);
                            }
                        }
                        break;

                    case PacketType.WalletSeed:
                        var walletSeed = WalletSeedPacket.Deserialize(ref reader);
                        if (reader.Remaining != 0) { _diagnosticsProvider()?.RecordRejected(); return; }
                        _handshakeCoordinator.HandleWalletSeed(peer, walletSeed);
                        break;

                    case PacketType.CurrencyReceipt:
                        reader.ReadHeader();
                        var receipt = reader.ReadGuid();
                        if (reader.Remaining != 0 || receipt == Guid.Empty) { _diagnosticsProvider()?.RecordRejected(); return; }
                        _vitalsSyncManagerProvider()?.ReceiveCurrencyReceipt(peer, receipt);
                        break;

                    case PacketType.PlayerDeath:
                        var death = DeathLifecyclePacket.Deserialize(ref reader);
                        if (reader.Remaining != 0 || !PacketValidator.ValidateDeathLifecycle(death)) { _diagnosticsProvider()?.RecordRejected(); return; }
                        if (_deathCoordinator != null)
                        {
                            bool wasAlive = _deathCoordinator.IsRemotePeerAlive(peer);
                            _deathCoordinator.HandleDeathPacket(peer, death);
                            bool isAlive = _deathCoordinator.IsRemotePeerAlive(peer);

                            var menuUI = _menuUIProvider();
                            if (death.EventType == DeathEventType.SoloBenchRespawn)
                            {
                                menuUI?.SetStatusMessage(Loc.T("Partner revived at the last bench."));
                            }
                            else if (wasAlive && !isAlive)
                            {
                                menuUI?.SetStatusMessage(Loc.T("{0} died", peer.DisplayName));
                            }
                            else if (!wasAlive && isAlive && death.EventType == DeathEventType.PartnerReviveTrigger)
                            {
                                menuUI?.SetStatusMessage(Loc.T("{0} revived", peer.DisplayName));
                            }

                            if (wasAlive != isAlive)
                            {
                                ApplyRemoteDeathVisibility(peer, death.EventType);
                            }
                            else if (death.EventType == DeathEventType.PartnerReviveTrigger && isAlive)
                            {
                                ApplyRemoteDeathVisibility(peer, death.EventType);
                            }
                            else if ((death.EventType == DeathEventType.SoloDeathOccurred || death.EventType == DeathEventType.TeamWipeOccurred) && !isAlive)
                            {
                                ApplyRemoteDeathVisibility(peer, death.EventType);
                            }
                        }
                        else
                        {
                            ApplyRemoteDeathVisibility(peer, death.EventType);
                        }
                        break;

                    case PacketType.SceneTransition:
                        var scene = SceneTransitionPacket.Deserialize(ref reader);
                        if (reader.Remaining != 0 || !PacketValidator.ValidateSceneTransition(scene)) { _diagnosticsProvider()?.RecordRejected(); return; }
                        _remoteScenes.TryGetValue(peer, out var departed);
                        if (scene.TransitionPhase == 0 && departed.AcceptDeparture(header.TickSequence))
                        {
                            _remoteScenes[peer] = departed;
                            _cloneSpawnerProvider()?.SetRemoteScenePresence(peer, false);
                        }
                        break;

                    case PacketType.EnemySync:
                        var enemyPacket = EnemySyncPacket.Deserialize(ref reader);
                        if (reader.Remaining != 0 || !PacketValidator.ValidateEnemySync(enemyPacket)) { _diagnosticsProvider()?.RecordRejected(); return; }
                        _enemySync?.ApplyRemoteHit(enemyPacket, peer);
                        break;

                    case PacketType.EnemySnapshot:
                        var enemySnapshot = EnemySnapshotPacket.Deserialize(ref reader);
                        if (reader.Remaining != 0 || !PacketValidator.ValidateEnemySnapshot(enemySnapshot)) { _diagnosticsProvider()?.RecordRejected(); return; }
                        _enemySync?.ApplyRemoteSnapshot(enemySnapshot, peer);
                        break;

                    case PacketType.WorldEvent:
                        var worldEvent = WorldEventPacket.Deserialize(ref reader);
                        if (reader.Remaining != 0 || !PacketValidator.ValidateWorldEvent(worldEvent)) { _diagnosticsProvider()?.RecordRejected(); return; }
                        if (IsWorldAuthority(peer, worldEvent.OriginPeerId))
                            _enemySync?.ApplyRemoteWorldEvent(worldEvent, peer);
                        break;

                    case PacketType.PersistentState:
                        var persistentState = PersistentStatePacket.Deserialize(ref reader);
                        if (reader.Remaining != 0 || !PacketValidator.ValidatePersistentState(persistentState)) { _diagnosticsProvider()?.RecordRejected(); return; }
                        if (IsWorldAuthority(peer, persistentState.OriginPeerId))
                            _enemySync?.ApplyRemotePersistentState(persistentState);
                        break;

                    case PacketType.QuestState:
                        QuestStatePacket questState;
                        try { questState = QuestStatePacket.Deserialize(ref reader); }
                        catch (FormatException) { _diagnosticsProvider()?.RecordRejected(); return; }
                        if (reader.Remaining != 0 || !PacketValidator.ValidateQuestState(questState)) { _diagnosticsProvider()?.RecordRejected(); return; }
                        _questStateSync?.ApplyRemote(questState);
                        break;

                    case PacketType.ItemUnlockSync:
                        var item = ItemUnlockPacket.Deserialize(ref reader);
                        if (reader.Remaining != 0 || !PacketValidator.ValidateItemUnlock(item)) { _diagnosticsProvider()?.RecordRejected(); return; }
                        _inventoryCoordinator.ProcessRemoteUnlock(peer, item);
                        break;
                }
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[IncomingPacketRouter] Error processing packet from {peer}: {ex.Message}");
            }
        }

        private void ApplyRemoteDeathVisibility(NetworkPeerId peer, DeathEventType eventType)
        {
            try
            {
                var cloneSpawner = _cloneSpawnerProvider();
                if (cloneSpawner == null) return;

                switch (eventType)
                {
                    case DeathEventType.LifecycleSnapshot:
                        cloneSpawner.SetRemotePartnerAlive(peer, _deathCoordinator?.IsRemotePeerAlive(peer) ?? false);
                        break;
                    case DeathEventType.SoloDeathOccurred:
                    case DeathEventType.TeamWipeOccurred:
                        cloneSpawner.SetRemotePartnerAlive(peer, false);
                        break;
                    case DeathEventType.PartnerReviveTrigger:
                    case DeathEventType.SoloBenchRespawn:
                    case DeathEventType.BenchCheckpointUpdate:
                        cloneSpawner.SetRemotePartnerAlive(peer, true);
                        break;
                }
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[IncomingPacketRouter] Failed to update remote clone visibility: {ex.Message}");
            }
        }

        private bool IsWorldAuthority(NetworkPeerId peer, ulong origin)
        {
            var session = _sessionService.SessionState;
            bool accepted = session?.IsHost == false && session.RemotePeerId == peer &&
                            peer.Value == origin;
            if (!accepted) _diagnosticsProvider()?.RecordRejected();
            return accepted;
        }

        public bool IsKnownPeer(NetworkPeerId peer)
        {
            var session = _sessionService.SessionState;
            if (session == null) return false;

            if (session.RemotePeerId.HasValue && session.RemotePeerId.Value == peer)
                return true;

            var peers = session.RemotePeerIds;
            for (int i = 0; i < peers.Count; i++)
            {
                if (peers[i] == peer) return true;
            }

            return false;
        }
    }
}
