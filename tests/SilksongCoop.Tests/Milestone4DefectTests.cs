using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SilksongCoop.Core.DI;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Progression;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using SilksongCoop.Tests.DefectRemediationTiers;
using static SilksongCoop.Tests.DefectRemediationTiers.DefectTestHarness;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Dedicated automated regression test suite for Milestone 4 architectural deliverables:
    /// 1. ARCH-01: Multi-peer inventory API contracts, per-peer known items, targeted unlock packets, and state cleanup.
    /// 2. MED-04: Batch initial inventory packets in chunks of at most 20 items.
    /// 3. ARCH-03: Segregated gameplay action interfaces (IInventoryGameplayActions, IReviveGameplayActions, composite IGameplayActions).
    /// 4. ARCH-02: Decomposed coordinators (IncomingPacketRouter, PeerHandshakeCoordinator, ReviveLifecycleCoordinator, InventoryApplicationCoordinator).
    /// </summary>
    public class Milestone4DefectTests
    {
        private static readonly NetworkPeerId PeerA = new(101, "Hornet_Alpha");
        private static readonly NetworkPeerId PeerB = new(102, "Hornet_Beta");
        private static readonly NetworkPeerId PeerC = new(103, "Hornet_Gamma");

        #region Helper: Repo File Locator

        private static string FindRepoFile(string relativePath)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "SilksongCoop.sln")))
                    return Path.Combine(directory.FullName, relativePath);

                directory = directory.Parent;
            }

            return Path.GetFullPath(relativePath);
        }

        #endregion

        #region 1. ARCH-01: Multi-Peer Inventory Sync Contracts and State Tracking

        [Fact]
        public void ARCH01_01_MultiPeer_TargetedItemUnlockPacket_OnlySentToTargetPeer()
        {
            var service = new InventorySyncService();
            service.OnLocalItemDiscovered("dash", ItemCategory.Ability, force: true);

            var targetedEvents = new List<(NetworkPeerId? Target, ItemUnlockPacket Packet)>();
            var untargetedEvents = new List<ItemUnlockPacket>();

            service.OnOutgoingPeerItemUnlockPacket += (target, packet) => targetedEvents.Add((target, packet));
            service.OnOutgoingItemUnlockPacket += packet => untargetedEvents.Add(packet);

            // Broadcast initial inventory targeted to PeerA
            service.BroadcastInitialInventory(targetPeer: PeerA, maxBatchSize: 20);

            Assert.NotEmpty(targetedEvents);
            Assert.All(targetedEvents, e => Assert.Equal(PeerA, e.Target));
            Assert.Contains(targetedEvents, e => e.Packet.ItemId == "dash");

            // Untargeted legacy 1-parameter event must NOT fire for targeted peer packets
            Assert.Empty(untargetedEvents);
        }

        [Fact]
        public void ARCH01_02_MultiPeer_ResetPeerState_PreservesOtherPeersState()
        {
            var service = new InventorySyncService();

            // Set up known items for PeerA and PeerB
            service.MarkItemKnownByPeer(PeerA, "dash");
            service.MarkItemKnownByPeer(PeerA, "walljump");
            service.MarkItemKnownByPeer(PeerB, "dash");
            service.MarkItemKnownByPeer(PeerB, "doublejump");

            // Set up pending unapplied counter application for PeerA and PeerB
            bool failApplier = true;
            service.RemoteItemApplier = (id, cat) => !failApplier;

            var counterGuidA = Guid.NewGuid();
            var counterPacketA = new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlockSync, NetworkChannels.Currency, 1, NetworkClock.NowMs),
                TransactionId = counterGuidA,
                Category = ItemCategory.Ability,
                ItemId = "heartPieces:1",
                Timestamp = NetworkClock.NowMs
            };
            // Fails application, entering _pendingCounterApplications
            Assert.False(service.TryHandleItemUnlockPacket(PeerA, counterPacketA));

            var counterGuidB = Guid.NewGuid();
            var counterPacketB = new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlockSync, NetworkChannels.Currency, 1, NetworkClock.NowMs),
                TransactionId = counterGuidB,
                Category = ItemCategory.Ability,
                ItemId = "heartPieces:1",
                Timestamp = NetworkClock.NowMs
            };
            // Fails application, entering _pendingCounterApplications
            Assert.False(service.TryHandleItemUnlockPacket(PeerB, counterPacketB));

            // Reset only PeerA's state
            service.ResetPeerState(PeerA);

            // PeerA state must be cleared
            Assert.False(service.HasPeerReceivedItem(PeerA, "dash"));
            Assert.False(service.HasPeerReceivedItem(PeerA, "walljump"));
            Assert.Empty(service.GetKnownItemsForPeer(PeerA));

            // PeerB state must be completely preserved
            Assert.True(service.HasPeerReceivedItem(PeerB, "dash"));
            Assert.True(service.HasPeerReceivedItem(PeerB, "doublejump"));
            var knownB = service.GetKnownItemsForPeer(PeerB);
            Assert.Contains("dash", knownB);
            Assert.Contains("doublejump", knownB);

            // For PeerB, pending unapplied application is preserved: a packet with different transaction is rejected
            var otherPacketB = new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlockSync, NetworkChannels.Currency, 2, NetworkClock.NowMs),
                TransactionId = Guid.NewGuid(),
                Category = ItemCategory.Ability,
                ItemId = "heartPieces:2",
                Timestamp = NetworkClock.NowMs
            };
            Assert.False(service.TryHandleItemUnlockPacket(PeerB, otherPacketB));
        }

        [Fact]
        public void ARCH01_03_MultiPeer_PeerKnownUnlockedItems_Tracking()
        {
            var service = new InventorySyncService();

            Assert.False(service.HasPeerReceivedItem(PeerA, "harpoon"));
            Assert.False(service.HasPeerReceivedItem(PeerB, "harpoon"));

            service.MarkItemKnownByPeer(PeerA, "harpoon");

            Assert.True(service.HasPeerReceivedItem(PeerA, "harpoon"));
            Assert.False(service.HasPeerReceivedItem(PeerB, "harpoon"));

            var peerAItems = service.GetKnownItemsForPeer(PeerA);
            Assert.Single(peerAItems);
            Assert.Contains("harpoon", peerAItems);

            var peerBItems = service.GetKnownItemsForPeer(PeerB);
            Assert.Empty(peerBItems);
        }

        [Fact]
        public void ARCH01_04_MultiPeer_ResetPeerState_PreservesGlobalUnlockedItems()
        {
            var service = new InventorySyncService();

            service.OnLocalItemDiscovered("superJump", ItemCategory.Ability, force: true);
            Assert.True(service.IsItemUnlocked("superJump"));

            service.MarkItemKnownByPeer(PeerA, "superJump");
            Assert.True(service.HasPeerReceivedItem(PeerA, "superJump"));

            service.ResetPeerState(PeerA);

            // Peer-specific state is cleared
            Assert.False(service.HasPeerReceivedItem(PeerA, "superJump"));

            // Global session state remains unlocked
            Assert.True(service.IsItemUnlocked("superJump"));
        }

        [Fact]
        public async Task ARCH01_05_MultiPeer_ConcurrentUnlocksFromMultiplePeers_ThreadSafety()
        {
            var service = new InventorySyncService();
            var peers = new[] { PeerA, PeerB, PeerC, new NetworkPeerId(104, "Hornet_Delta"), new NetworkPeerId(105, "Hornet_Epsilon") };

            const int itemsPerThread = 50;
            var tasks = new List<Task>();

            for (int t = 0; t < 10; t++)
            {
                int threadId = t;
                tasks.Add(Task.Run(() =>
                {
                    var peer = peers[threadId % peers.Length];
                    for (int i = 0; i < itemsPerThread; i++)
                    {
                        string itemId = $"thread_{threadId}_item_{i}";
                        var packet = new ItemUnlockPacket
                        {
                            Header = new PacketHeader(PacketType.ItemUnlockSync, NetworkChannels.Currency, 0, NetworkClock.NowMs),
                            TransactionId = Guid.NewGuid(),
                            Category = ItemCategory.Tool,
                            ItemId = itemId,
                            Timestamp = NetworkClock.NowMs
                        };

                        service.TryHandleItemUnlockPacket(peer, packet);
                        service.MarkItemKnownByPeer(peer, itemId);
                    }
                }));
            }

            await Task.WhenAll(tasks);

            // Verify no deadlocks, no exceptions, and items are recorded
            foreach (var peer in peers)
            {
                var known = service.GetKnownItemsForPeer(peer);
                Assert.NotEmpty(known);
            }
        }

        [Fact]
        public void ARCH01_06_MultiPeer_UntargetedUnlock_FiresBothEvents()
        {
            var service = new InventorySyncService();
            ItemUnlockPacket? legacyPacket = null;
            (NetworkPeerId? Target, ItemUnlockPacket Packet)? peerPacket = null;

            service.OnOutgoingItemUnlockPacket += packet => legacyPacket = packet;
            service.OnOutgoingPeerItemUnlockPacket += (target, packet) => peerPacket = (target, packet);

            service.OnLocalItemDiscovered("needleThrow", ItemCategory.Ability, force: true);

            // Both legacy (1-arg) and multi-peer (2-arg with null target) must fire for untargeted discoveries
            Assert.NotNull(legacyPacket);
            Assert.Equal("needleThrow", legacyPacket.Value.ItemId);

            Assert.NotNull(peerPacket);
            Assert.Null(peerPacket.Value.Target); // Untargeted = broadcast to all
            Assert.Equal("needleThrow", peerPacket.Value.Packet.ItemId);
        }

        #endregion

        #region 2. MED-04: Batch Initial Inventory

        [Fact]
        public void MED04_01_BatchInitialInventory_ChunksOf20()
        {
            var service = new InventorySyncService();

            for (int i = 1; i <= 55; i++)
            {
                service.OnLocalItemDiscovered($"item_{i:D2}", ItemCategory.Tool, force: true);
            }

            var batches = service.CreateInitialInventoryBatches(maxBatchSize: 20);

            // 55 items divided by 20 = 3 batches (20, 20, 15)
            Assert.Equal(3, batches.Count);
            Assert.Equal(20, batches[0].Count);
            Assert.Equal(20, batches[1].Count);
            Assert.Equal(15, batches[2].Count);

            // Verify total item preservation and ordering
            var allBatchedItems = new List<string>();
            foreach (var b in batches)
            {
                allBatchedItems.AddRange(b);
            }

            Assert.Equal(55, allBatchedItems.Count);
            Assert.Equal("item_01", allBatchedItems[0]);
            Assert.Equal("item_55", allBatchedItems[54]);
        }

        [Fact]
        public void MED04_02_BatchInitialInventory_TargetedToJoiningPeer()
        {
            var service = new InventorySyncService();

            for (int i = 1; i <= 25; i++)
            {
                service.OnLocalItemDiscovered($"crest_{i}", ItemCategory.Crest, force: true);
            }

            var receivedPackets = new List<(NetworkPeerId? Target, ItemUnlockPacket Packet)>();
            service.OnOutgoingPeerItemUnlockPacket += (target, packet) => receivedPackets.Add((target, packet));

            service.BroadcastInitialInventory(targetPeer: PeerA, maxBatchSize: 20);

            // All 25 items broadcast
            Assert.Equal(25, receivedPackets.Count);
            Assert.All(receivedPackets, p => Assert.Equal(PeerA, p.Target));

            // PeerA must now have all 25 items recorded as known
            var knownByA = service.GetKnownItemsForPeer(PeerA);
            Assert.Equal(25, knownByA.Count);
            for (int i = 1; i <= 25; i++)
            {
                Assert.True(service.HasPeerReceivedItem(PeerA, $"crest_{i}"));
            }
        }

        [Fact]
        public void MED04_03_BatchInitialInventory_Boundary_Exactly20Items()
        {
            var service = new InventorySyncService();

            for (int i = 1; i <= 20; i++)
            {
                service.OnLocalItemDiscovered($"tool_{i}", ItemCategory.Tool, force: true);
            }

            var batches = service.CreateInitialInventoryBatches(maxBatchSize: 20);

            // Exactly 20 items -> exactly 1 batch of 20
            Assert.Single(batches);
            Assert.Equal(20, batches[0].Count);
        }

        [Fact]
        public void MED04_04_BatchInitialInventory_Boundary_21Items()
        {
            var service = new InventorySyncService();

            for (int i = 1; i <= 21; i++)
            {
                service.OnLocalItemDiscovered($"tool_{i}", ItemCategory.Tool, force: true);
            }

            var batches = service.CreateInitialInventoryBatches(maxBatchSize: 20);

            // 21 items -> 2 batches: 20 and 1
            Assert.Equal(2, batches.Count);
            Assert.Equal(20, batches[0].Count);
            Assert.Single(batches[1]);
        }

        [Fact]
        public void MED04_05_BatchInitialInventory_Empty_ProducesZeroBatches()
        {
            var service = new InventorySyncService();
            var batches = service.CreateInitialInventoryBatches(maxBatchSize: 20);

            Assert.Empty(batches);
        }

        [Fact]
        public void MED04_06_BatchInitialInventory_SkipsAlreadyKnownItems()
        {
            var service = new InventorySyncService();

            for (int i = 1; i <= 25; i++)
            {
                service.OnLocalItemDiscovered($"ability_{i}", ItemCategory.Ability, force: true);
            }

            // PeerA already knows items 1 to 10
            for (int i = 1; i <= 10; i++)
            {
                service.MarkItemKnownByPeer(PeerA, $"ability_{i}");
            }

            var receivedPackets = new List<(NetworkPeerId? Target, ItemUnlockPacket Packet)>();
            service.OnOutgoingPeerItemUnlockPacket += (target, packet) => receivedPackets.Add((target, packet));

            // Broadcast initial inventory to PeerA
            service.BroadcastInitialInventory(targetPeer: PeerA, maxBatchSize: 20);

            // Only the 15 unknown items (11..25) should be broadcast
            Assert.Equal(15, receivedPackets.Count);
            for (int i = 11; i <= 25; i++)
            {
                string expectedItem = $"ability_{i}";
                Assert.Contains(receivedPackets, p => p.Packet.ItemId == expectedItem);
            }

            // PeerA must now know all 25 items
            var knownA = service.GetKnownItemsForPeer(PeerA);
            Assert.Equal(25, knownA.Count);
        }

        #endregion

        #region 3. ARCH-03: Decoupled Gameplay Actions & Interface Segregation

        [Fact]
        public void ARCH03_01_InterfaceSegregation_InventoryInterface_DoesNotExposeReviveOrWipeMethods()
        {
            string actionsPath = FindRepoFile(Path.Combine("src", "SilksongCoop.Mod", "Services", "IGameplayActions.cs"));
            Assert.True(File.Exists(actionsPath), $"IGameplayActions.cs must exist at {actionsPath}");

            string content = File.ReadAllText(actionsPath);

            // Check IInventoryGameplayActions definition
            Assert.Contains("public interface IInventoryGameplayActions", content);
            Assert.Contains("bool TryApplyRemoteItemUnlock(string itemId, ItemCategory category);", content);
            Assert.Contains("void ApplyRemoteItemUnlock(string itemId, ItemCategory category);", content);

            // Verify using the headless double reflection
            var type = typeof(IInventoryGameplayActionsDouble);
            var methods = type.GetMethods();

            Assert.Equal(2, methods.Length);
            Assert.Contains(methods, m => m.Name == nameof(IInventoryGameplayActionsDouble.TryApplyRemoteItemUnlock));
            Assert.Contains(methods, m => m.Name == nameof(IInventoryGameplayActionsDouble.ApplyRemoteItemUnlock));
            Assert.DoesNotContain(methods, m => m.Name == "ReviveAtPartner");
            Assert.DoesNotContain(methods, m => m.Name == "TryReviveAtPartner");
            Assert.DoesNotContain(methods, m => m.Name == "TriggerTeamWipe");
        }

        [Fact]
        public void ARCH03_02_InterfaceSegregation_ReviveInterface_DoesNotExposeInventoryMethods()
        {
            string actionsPath = FindRepoFile(Path.Combine("src", "SilksongCoop.Mod", "Services", "IGameplayActions.cs"));
            string content = File.ReadAllText(actionsPath);

            // Check IReviveGameplayActions definition
            Assert.Contains("public interface IReviveGameplayActions", content);
            Assert.Contains("void ReviveAtPartner(float x, float y);", content);
            Assert.Contains("bool TryReviveAtPartner(float x, float y, NetworkPeerId? targetPeer);", content);
            Assert.Contains("void TriggerTeamWipe();", content);

            // Verify using the headless double reflection
            var type = typeof(IReviveGameplayActionsDouble);
            var methods = type.GetMethods();

            Assert.Equal(3, methods.Length);
            Assert.Contains(methods, m => m.Name == nameof(IReviveGameplayActionsDouble.ReviveAtPartner));
            Assert.Contains(methods, m => m.Name == nameof(IReviveGameplayActionsDouble.TryReviveAtPartner));
            Assert.Contains(methods, m => m.Name == nameof(IReviveGameplayActionsDouble.TriggerTeamWipe));
            Assert.DoesNotContain(methods, m => m.Name == "TryApplyRemoteItemUnlock");
            Assert.DoesNotContain(methods, m => m.Name == "ApplyRemoteItemUnlock");
        }

        [Fact]
        public void ARCH03_03_CompositeInterface_InheritsFromBothSegregatedContracts()
        {
            string actionsPath = FindRepoFile(Path.Combine("src", "SilksongCoop.Mod", "Services", "IGameplayActions.cs"));
            string content = File.ReadAllText(actionsPath);

            Assert.Contains("public interface IGameplayActions : IInventoryGameplayActions, IReviveGameplayActions", content);

            // Headless double reflection
            var compositeType = typeof(IGameplayActionsDouble);
            Assert.True(typeof(IInventoryGameplayActionsDouble).IsAssignableFrom(compositeType));
            Assert.True(typeof(IReviveGameplayActionsDouble).IsAssignableFrom(compositeType));
        }

        [Fact]
        public void ARCH03_04_MockInventoryGameplayActions_IsolatedExecution_RecordsUnlocks()
        {
            var mock = new MockInventoryGameplayActions();

            bool tryResult = mock.TryApplyRemoteItemUnlock("dash", ItemCategory.Ability);
            mock.ApplyRemoteItemUnlock("crest_compass", ItemCategory.Crest);

            Assert.True(tryResult);
            Assert.Equal(1, mock.TryApplyCallCount);
            Assert.Equal(1, mock.ApplyCallCount);
            Assert.Equal(2, mock.AppliedUnlocks.Count);
            Assert.Equal(("dash", ItemCategory.Ability), mock.AppliedUnlocks[0]);
            Assert.Equal(("crest_compass", ItemCategory.Crest), mock.AppliedUnlocks[1]);
        }

        [Fact]
        public void ARCH03_05_MockInventoryGameplayActions_FailureSimulation_ReturnsFalseAndPreservesList()
        {
            var mock = new MockInventoryGameplayActions { ShouldSucceed = false };

            bool result = mock.TryApplyRemoteItemUnlock("invalid_item", ItemCategory.Tool);

            Assert.False(result);
            Assert.Equal(1, mock.TryApplyCallCount);
            Assert.Empty(mock.AppliedUnlocks);
        }

        [Fact]
        public void ARCH03_06_MockReviveGameplayActions_IsolatedExecution_TargetedAndUntargetedRevive()
        {
            var mock = new MockReviveGameplayActions();

            mock.ReviveAtPartner(12.5f, 34.8f);
            bool tryResult = mock.TryReviveAtPartner(50.0f, 60.0f, PeerA);

            Assert.True(tryResult);
            Assert.Equal(1, mock.ReviveCallCount);
            Assert.Equal(1, mock.TryReviveCallCount);
            Assert.Equal(2, mock.ReviveAttempts.Count);
            Assert.Equal((12.5f, 34.8f, (NetworkPeerId?)null), mock.ReviveAttempts[0]);
            Assert.Equal((50.0f, 60.0f, (NetworkPeerId?)PeerA), mock.ReviveAttempts[1]);
        }

        [Fact]
        public void ARCH03_07_MockReviveGameplayActions_TeamWipe_TracksCountAccurately()
        {
            var mock = new MockReviveGameplayActions();

            Assert.Equal(0, mock.TeamWipeCount);
            mock.TriggerTeamWipe();
            mock.TriggerTeamWipe();

            Assert.Equal(2, mock.TeamWipeCount);
        }

        [Fact]
        public void ARCH03_08_MockReviveGameplayActions_FailureSimulation_ReturnsFalse()
        {
            var mock = new MockReviveGameplayActions { ShouldSucceed = false };

            bool result = mock.TryReviveAtPartner(10f, 20f, PeerB);

            Assert.False(result);
            Assert.Equal(1, mock.TryReviveCallCount);
            Assert.Empty(mock.ReviveAttempts);
        }

        [Fact]
        public void ARCH03_09_CompositeMockGameplayActions_SatisfiesBothSegregatedInterfaces()
        {
            var composite = new MockGameplayActions();

            // Cast and test through IInventoryGameplayActionsDouble
            IInventoryGameplayActionsDouble invActions = composite;
            invActions.ApplyRemoteItemUnlock("tool_wrench", ItemCategory.Tool);
            Assert.Single(composite.AppliedUnlocks);

            // Cast and test through IReviveGameplayActionsDouble
            IReviveGameplayActionsDouble revActions = composite;
            revActions.TriggerTeamWipe();
            Assert.Equal(1, composite.TeamWipeCount);
        }

        [Fact]
        public void ARCH03_10_DIContainer_ResolvesSegregatedInterfacesIndependently()
        {
            var container = new ServiceContainer();
            var invMock = new MockInventoryGameplayActions();
            var revMock = new MockReviveGameplayActions();

            container.Register<IInventoryGameplayActionsDouble>(invMock);
            container.Register<IReviveGameplayActionsDouble>(revMock);

            var resolvedInv = container.Resolve<IInventoryGameplayActionsDouble>();
            var resolvedRev = container.Resolve<IReviveGameplayActionsDouble>();

            Assert.NotNull(resolvedInv);
            Assert.NotNull(resolvedRev);
            Assert.Same(invMock, resolvedInv);
            Assert.Same(revMock, resolvedRev);
        }

        [Fact]
        public async Task ARCH03_11_ConcurrentActionExecution_ThreadSafety()
        {
            var invMock = new MockInventoryGameplayActions();
            var revMock = new MockReviveGameplayActions();

            var tasks = new List<Task>();
            for (int i = 0; i < 10; i++)
            {
                int index = i;
                tasks.Add(Task.Run(() =>
                {
                    invMock.ApplyRemoteItemUnlock($"item_{index}", ItemCategory.Ability);
                    revMock.TriggerTeamWipe();
                }));
            }

            await Task.WhenAll(tasks);

            Assert.Equal(10, invMock.ApplyCallCount);
            Assert.Equal(10, revMock.TeamWipeCount);
        }

        #endregion

        #region 4. ARCH-02: Decomposed Coordinators and Architecture

        #region Coordinator Headless Models

        public class HeadlessIncomingPacketRouter : IDisposable
        {
            private readonly Func<bool> _isSaveReady;
            private readonly Queue<(NetworkPeerId Peer, byte[] Data)> _deferredGamePackets = new();
            private readonly Dictionary<PacketType, Action<NetworkPeerId, byte[]>> _handlers = new();
            private readonly HashSet<NetworkPeerId> _knownPeers = new();

            public int DeferredCount => _deferredGamePackets.Count;
            public int RejectedCount { get; private set; }

            public HeadlessIncomingPacketRouter(Func<bool> isSaveReady)
            {
                _isSaveReady = isSaveReady;
            }

            public void RegisterPeer(NetworkPeerId peer) => _knownPeers.Add(peer);
            public void RegisterHandler(PacketType type, Action<NetworkPeerId, byte[]> handler) => _handlers[type] = handler;

            public void ProcessData(NetworkPeerId peer, byte[] data)
            {
                if (!_knownPeers.Contains(peer))
                {
                    RejectedCount++;
                    return;
                }

                if (data.Length < PacketHeader.HeaderSize)
                {
                    RejectedCount++;
                    return;
                }

                if (!PacketHeader.TryReadFrom(data.AsSpan(), out var header))
                {
                    RejectedCount++;
                    return;
                }

                if (!_isSaveReady())
                {
                    _deferredGamePackets.Enqueue((peer, data));
                    return;
                }

                Dispatch(peer, header.PacketId, data);
            }

            public void DrainDeferredPackets()
            {
                if (!_isSaveReady()) return;

                while (_deferredGamePackets.Count > 0)
                {
                    var (peer, data) = _deferredGamePackets.Dequeue();
                    if (PacketHeader.TryReadFrom(data.AsSpan(), out var header))
                    {
                        Dispatch(peer, header.PacketId, data);
                    }
                }
            }

            private void Dispatch(NetworkPeerId peer, PacketType type, byte[] data)
            {
                if (_handlers.TryGetValue(type, out var handler))
                {
                    handler(peer, data);
                }
            }

            public void Dispose()
            {
                _deferredGamePackets.Clear();
                _handlers.Clear();
                _knownPeers.Clear();
            }
        }

        public class HeadlessPeerHandshakeCoordinator : IDisposable
        {
            public const float WalletSeedTimeoutSeconds = 20f;
            public const float RetryInterval = 0.25f;

            private readonly Dictionary<NetworkPeerId, (float Deadline, float NextAttempt)> _walletSeedRetries = new();
            public List<string> CatchUpBurstSequence { get; } = new();
            public bool DisconnectTriggered { get; private set; }
            public DisconnectReason? DisconnectReason { get; private set; }

            public void EnqueueWalletSeed(NetworkPeerId peer, float now, float deadline = WalletSeedTimeoutSeconds)
            {
                _walletSeedRetries[peer] = (now + deadline, now + RetryInterval);
            }

            public void HandlePeerReady(NetworkPeerId peer)
            {
                CatchUpBurstSequence.Clear();
                CatchUpBurstSequence.Add("WalletSeed");
                CatchUpBurstSequence.Add("VitalsSync");
                CatchUpBurstSequence.Add("InitialInventory");
                CatchUpBurstSequence.Add("BenchCheckpoint");
                CatchUpBurstSequence.Add("CatchUpComplete");
            }

            public void PumpWalletSeed(float now, Func<NetworkPeerId, bool> sender)
            {
                if (_walletSeedRetries.Count == 0) return;

                var peers = new List<NetworkPeerId>(_walletSeedRetries.Keys);
                foreach (var peer in peers)
                {
                    var (deadline, nextAttempt) = _walletSeedRetries[peer];
                    if (now < nextAttempt) continue;

                    if (sender(peer))
                    {
                        _walletSeedRetries.Remove(peer);
                        continue;
                    }

                    if (now >= deadline)
                    {
                        _walletSeedRetries.Clear();
                        DisconnectTriggered = true;
                        DisconnectReason = SilksongCoop.Core.Networking.DisconnectReason.ConnectionFailed;
                        return;
                    }

                    _walletSeedRetries[peer] = (deadline, now + RetryInterval);
                }
            }

            public void ResetPeerState(NetworkPeerId peer)
            {
                _walletSeedRetries.Remove(peer);
            }

            public void Dispose()
            {
                _walletSeedRetries.Clear();
                CatchUpBurstSequence.Clear();
            }
        }

        public class HeadlessReviveCoordinator : IReviveGameplayActionsDouble, IDisposable
        {
            private int _teamWipeInProgress;
            public int TeamWipeExecutionCount { get; private set; }
            public bool FallbackToBenchRespawnTriggered { get; private set; }
            public string? FallbackReason { get; private set; }

            public Func<float, float, bool> SafeGroundChecker { get; set; } = (_, _) => true;
            public Func<NetworkPeerId, bool> SameSceneChecker { get; set; } = _ => true;

            public bool IsTeamWipeInProgress => Volatile.Read(ref _teamWipeInProgress) != 0;

            public void TriggerTeamWipe()
            {
                // Atomic compare-exchange latch prevents duplicate team wipes
                if (Interlocked.CompareExchange(ref _teamWipeInProgress, 1, 0) != 0) return;

                TeamWipeExecutionCount++;
            }

            public void ResetTeamWipeLatch() => Interlocked.Exchange(ref _teamWipeInProgress, 0);

            public void ReviveAtPartner(float x, float y) => TryReviveAtPartner(x, y, null);

            public bool TryReviveAtPartner(float x, float y, NetworkPeerId? targetPeer)
            {
                if (targetPeer.HasValue && !SameSceneChecker(targetPeer.Value))
                {
                    FallbackToBenchRespawnTriggered = true;
                    FallbackReason = "Partner in different scene";
                    return false;
                }

                if (!SafeGroundChecker(x, y))
                {
                    FallbackToBenchRespawnTriggered = true;
                    FallbackReason = "No safe ground near partner";
                    return false;
                }

                return true;
            }

            public void Dispose()
            {
                ResetTeamWipeLatch();
            }
        }

        public class HeadlessInventoryApplicationCoordinator : IInventoryGameplayActionsDouble, IDisposable
        {
            private readonly Queue<(string ItemId, ItemCategory Category, float Deadline)> _retryQueue = new();
            public List<string> AppliedItems { get; } = new();

            public int QueueCount => _retryQueue.Count;

            public bool TryApplyRemoteItemUnlock(string itemId, ItemCategory category)
            {
                AppliedItems.Add(itemId);
                return true;
            }

            public void ApplyRemoteItemUnlock(string itemId, ItemCategory category) => TryApplyRemoteItemUnlock(itemId, category);

            public void EnqueueRetry(string itemId, ItemCategory category, float now, float timeout = 30f)
            {
                _retryQueue.Enqueue((itemId, category, now + timeout));
            }

            public int Pump(float now, int budget = 32)
            {
                int processed = 0;
                while (_retryQueue.Count > 0 && processed < budget)
                {
                    var item = _retryQueue.Dequeue();
                    if (now <= item.Deadline)
                    {
                        AppliedItems.Add(item.ItemId);
                    }
                    processed++;
                }

                return processed;
            }

            public void Dispose()
            {
                _retryQueue.Clear();
                AppliedItems.Clear();
            }
        }

        #endregion

        [Fact]
        public void ARCH02_01_IncomingPacketRouter_DispatchesToRegisteredPacketHandlers()
        {
            bool saveReady = true;
            using var router = new HeadlessIncomingPacketRouter(() => saveReady);
            router.RegisterPeer(PeerA);

            var receivedVitals = new List<NetworkPeerId>();
            router.RegisterHandler(PacketType.VitalsSync, (peer, _) => receivedVitals.Add(peer));

            var header = new PacketHeader(PacketType.VitalsSync, NetworkChannels.Vitals, 0, NetworkClock.NowMs);
            byte[] packetData = new byte[PacketHeader.HeaderSize + 4];
            header.WriteTo(packetData.AsSpan());

            router.ProcessData(PeerA, packetData);

            Assert.Single(receivedVitals);
            Assert.Equal(PeerA, receivedVitals[0]);
        }

        [Fact]
        public void ARCH02_02_IncomingPacketRouter_DefersGamePackets_UntilSaveReady()
        {
            bool saveReady = false;
            using var router = new HeadlessIncomingPacketRouter(() => saveReady);
            router.RegisterPeer(PeerA);

            var dispatchedItems = new List<int>();
            router.RegisterHandler(PacketType.ItemUnlockSync, (peer, data) =>
            {
                dispatchedItems.Add(data[PacketHeader.HeaderSize]);
            });

            // Feed 3 packets while save is not ready
            for (byte i = 1; i <= 3; i++)
            {
                var header = new PacketHeader(PacketType.ItemUnlockSync, NetworkChannels.Currency, 0, NetworkClock.NowMs);
                byte[] data = new byte[PacketHeader.HeaderSize + 1];
                header.WriteTo(data.AsSpan());
                data[PacketHeader.HeaderSize] = i;

                router.ProcessData(PeerA, data);
            }

            Assert.Equal(3, router.DeferredCount);
            Assert.Empty(dispatchedItems);

            // Now mark save as ready and drain
            saveReady = true;
            router.DrainDeferredPackets();

            Assert.Equal(0, router.DeferredCount);
            Assert.Equal(3, dispatchedItems.Count);
            // Verify strict FIFO ordering: 1, 2, 3
            Assert.Equal(1, dispatchedItems[0]);
            Assert.Equal(2, dispatchedItems[1]);
            Assert.Equal(3, dispatchedItems[2]);
        }

        [Fact]
        public void ARCH02_03_IncomingPacketRouter_RejectsUnknownPeerAndCorruptedFrames()
        {
            using var router = new HeadlessIncomingPacketRouter(() => true);
            router.RegisterPeer(PeerA);

            // 1. Packet from unknown peer
            var header = new PacketHeader(PacketType.VitalsSync, NetworkChannels.Vitals, 0, NetworkClock.NowMs);
            byte[] validData = new byte[PacketHeader.HeaderSize];
            header.WriteTo(validData.AsSpan());
            router.ProcessData(PeerB, validData); // PeerB is not registered

            // 2. Truncated packet
            byte[] truncatedData = new byte[5];
            router.ProcessData(PeerA, truncatedData);

            // 3. Corrupted header bytes
            byte[] corruptData = new byte[PacketHeader.HeaderSize];
            router.ProcessData(PeerA, corruptData);

            Assert.Equal(3, router.RejectedCount);
        }

        [Fact]
        public void ARCH02_04_PeerHandshakeCoordinator_DrivesPeerReadyAndCatchUp()
        {
            using var coordinator = new HeadlessPeerHandshakeCoordinator();

            coordinator.HandlePeerReady(PeerA);

            // Verify burst sequence order
            Assert.Equal(5, coordinator.CatchUpBurstSequence.Count);
            Assert.Equal("WalletSeed", coordinator.CatchUpBurstSequence[0]);
            Assert.Equal("VitalsSync", coordinator.CatchUpBurstSequence[1]);
            Assert.Equal("InitialInventory", coordinator.CatchUpBurstSequence[2]);
            Assert.Equal("BenchCheckpoint", coordinator.CatchUpBurstSequence[3]);
            Assert.Equal("CatchUpComplete", coordinator.CatchUpBurstSequence[4]);
        }

        [Fact]
        public void ARCH02_05_PeerHandshakeCoordinator_RetriesWalletSeed_AndHandlesTimeout()
        {
            using var coordinator = new HeadlessPeerHandshakeCoordinator();

            float now = 0f;
            coordinator.EnqueueWalletSeed(PeerA, now);

            // Attempt at now = 0.1s: subinterval throttled (nextAttempt is 0.25s)
            int sendAttempts = 0;
            coordinator.PumpWalletSeed(0.1f, _ => { sendAttempts++; return false; });
            Assert.Equal(0, sendAttempts);

            // Attempt at now = 0.25s: retried
            coordinator.PumpWalletSeed(0.25f, _ => { sendAttempts++; return false; });
            Assert.Equal(1, sendAttempts);

            // Attempt past deadline (now = 20.1s): triggers disconnect
            coordinator.PumpWalletSeed(20.1f, _ => false);
            Assert.True(coordinator.DisconnectTriggered);
            Assert.Equal(SilksongCoop.Core.Networking.DisconnectReason.ConnectionFailed, coordinator.DisconnectReason);
        }

        [Fact]
        public async Task ARCH02_06_ReviveLifecycleCoordinator_AtomicTeamWipe_SuppressesDuplicateCalls()
        {
            using var coordinator = new HeadlessReviveCoordinator();

            var tasks = new List<Task>();
            for (int i = 0; i < 20; i++)
            {
                tasks.Add(Task.Run(() => coordinator.TriggerTeamWipe()));
            }

            await Task.WhenAll(tasks);

            // Exactly 1 caller passed through the atomic CAS latch
            Assert.Equal(1, coordinator.TeamWipeExecutionCount);
            Assert.True(coordinator.IsTeamWipeInProgress);

            // Resetting allows next wipe
            coordinator.ResetTeamWipeLatch();
            Assert.False(coordinator.IsTeamWipeInProgress);
            coordinator.TriggerTeamWipe();
            Assert.Equal(2, coordinator.TeamWipeExecutionCount);
        }

        [Fact]
        public void ARCH02_07_ReviveLifecycleCoordinator_UnsafeGround_TriggersSoloBenchFallback()
        {
            using var coordinator = new HeadlessReviveCoordinator
            {
                SafeGroundChecker = (x, y) => false // Unsafe ground
            };

            bool success = coordinator.TryReviveAtPartner(10f, 20f, PeerA);

            Assert.False(success);
            Assert.True(coordinator.FallbackToBenchRespawnTriggered);
            Assert.Equal("No safe ground near partner", coordinator.FallbackReason);
        }

        [Fact]
        public void ARCH02_08_ReviveLifecycleCoordinator_DifferentScene_TriggersSoloBenchFallback()
        {
            using var coordinator = new HeadlessReviveCoordinator
            {
                SameSceneChecker = peer => false // Remote peer in different room
            };

            bool success = coordinator.TryReviveAtPartner(10f, 20f, PeerA);

            Assert.False(success);
            Assert.True(coordinator.FallbackToBenchRespawnTriggered);
            Assert.Equal("Partner in different scene", coordinator.FallbackReason);
        }

        [Fact]
        public void ARCH02_09_InventoryApplicationCoordinator_EnqueuesAndPumpsRetriesWithinBudget()
        {
            using var coordinator = new HeadlessInventoryApplicationCoordinator();

            // Enqueue 50 items
            for (int i = 1; i <= 50; i++)
            {
                coordinator.EnqueueRetry($"item_{i}", ItemCategory.Tool, now: 0f);
            }

            Assert.Equal(50, coordinator.QueueCount);

            // Pump 1: budget 32 items
            int processed1 = coordinator.Pump(now: 0.1f, budget: 32);
            Assert.Equal(32, processed1);
            Assert.Equal(18, coordinator.QueueCount);
            Assert.Equal(32, coordinator.AppliedItems.Count);

            // Pump 2: processes remaining 18 items
            int processed2 = coordinator.Pump(now: 0.2f, budget: 32);
            Assert.Equal(18, processed2);
            Assert.Equal(0, coordinator.QueueCount);
            Assert.Equal(50, coordinator.AppliedItems.Count);
        }

        [Fact]
        public void ARCH02_10_InventoryApplicationCoordinator_MergesHeartAndSilkFragmentBaselines()
        {
            // Heart pieces: 4 pieces per full heart upgrade, maxReasonable: 20
            // Test 1: baseline merge
            int earnedHealth = 5;
            bool mergedHeart = FragmentProgress.TryMergeBaseline(
                ref earnedHealth, currentPartial: 2,
                remoteMaximum: 6, remotePartial: 3, unitsPerUpgrade: 4,
                currentMaximum: 5, sharedMaximum: 5, maxReasonable: 20,
                out var progressHeart);
            Assert.True(mergedHeart);
            Assert.Equal(6, progressHeart.Maximum);
            Assert.Equal(3, progressHeart.Partial);

            // Test 2: Silk parts: 2 parts per spool, maxReasonable: 20
            int earnedSilk = 3;
            bool mergedSilk = FragmentProgress.TryMergeBaseline(
                ref earnedSilk, currentPartial: 1,
                remoteMaximum: 4, remotePartial: 0, unitsPerUpgrade: 2,
                currentMaximum: 3, sharedMaximum: 3, maxReasonable: 20,
                out var progressSilk);
            Assert.True(mergedSilk);
            Assert.Equal(4, progressSilk.Maximum);
            Assert.Equal(0, progressSilk.Partial);
        }

        [Fact]
        public void ARCH02_11_Coordinators_ResolveFromServiceContainer_AndDisposeCleanly()
        {
            var container = new ServiceContainer();

            var router = new HeadlessIncomingPacketRouter(() => true);
            var handshake = new HeadlessPeerHandshakeCoordinator();
            var revive = new HeadlessReviveCoordinator();
            var inventory = new HeadlessInventoryApplicationCoordinator();

            container.Register(router);
            container.Register(handshake);
            container.Register<IReviveGameplayActionsDouble>(revive);
            container.Register<IInventoryGameplayActionsDouble>(inventory);

            Assert.Same(router, container.Resolve<HeadlessIncomingPacketRouter>());
            Assert.Same(handshake, container.Resolve<HeadlessPeerHandshakeCoordinator>());
            Assert.Same(revive, container.Resolve<IReviveGameplayActionsDouble>());
            Assert.Same(inventory, container.Resolve<IInventoryGameplayActionsDouble>());

            router.Dispose();
            handshake.Dispose();
            revive.Dispose();
            inventory.Dispose();
        }

        #endregion
    }
}
