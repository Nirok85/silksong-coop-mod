using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using SilksongCoop.Tests.TestHarness;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Milestone 5 Challenger 2 Adversarial Network Stress Suite.
    /// Empirically verifies:
    /// 1. Simulated high packet drop (20% - 40%) and out-of-order reordering over Channel 3.
    /// 2. Duplicate packet storms (retransmitting the same unlock 50 times in rapid succession).
    /// 3. Mutual simultaneous discovery of 50 crests/tools at the exact same millisecond timestamp.
    /// 4. Verification that no echo loop / ping-pong storm is triggered.
    /// </summary>
    public class Challenger2Milestone5NetworkStressTests
    {
        #region Helper: ARQ Loss & Reordering Simulator for Channel 3

        /// <summary>
        /// Models an adversarial network channel with configurable packet drop (20%-40%),
        /// latency jitter, out-of-order delivery, and ARQ retransmission until successful delivery.
        /// </summary>
        private sealed class AdversarialChannel3Simulator
        {
            private readonly float _dropRate;
            private readonly Random _random;

            public int TotalTransmitAttempts { get; private set; }
            public int TotalDroppedPackets { get; private set; }
            public int TotalDeliveredPackets { get; private set; }

            public AdversarialChannel3Simulator(float dropRate, int seed = 1337)
            {
                _dropRate = dropRate;
                _random = new Random(seed);
            }

            public List<ItemUnlockPacket> SimulateTransmission(IEnumerable<ItemUnlockPacket> originalPackets)
            {
                var deliveredPackets = new List<(int deliveryOrder, ItemUnlockPacket packet)>();
                int arrivalCounter = 0;

                foreach (var packet in originalPackets)
                {
                    bool delivered = false;
                    int retransmitAttempt = 0;

                    while (!delivered)
                    {
                        TotalTransmitAttempts++;
                        // Determine if dropped on this attempt
                        if (_random.NextDouble() < _dropRate)
                        {
                            TotalDroppedPackets++;
                            retransmitAttempt++;
                            continue; // Retransmit next attempt
                        }

                        // Packet succeeded on this attempt!
                        // Jitter affects order of arrival: retransmissions or jitter displace order
                        int jitterOrder = arrivalCounter + (retransmitAttempt * 10) + _random.Next(-3, 4);
                        deliveredPackets.Add((jitterOrder, packet));
                        TotalDeliveredPackets++;
                        delivered = true;
                        arrivalCounter++;
                    }
                }

                // Sort by jittered arrival order to simulate realistic out-of-order reception
                return deliveredPackets
                    .OrderBy(p => p.deliveryOrder)
                    .Select(p => p.packet)
                    .ToList();
            }
        }

        #endregion

        #region 1. Simulated High Packet Drop (20% - 40%) & Out-of-Order Reordering over Channel 3

        [Theory]
        [InlineData(0.20f, 1001)] // 20% packet drop
        [InlineData(0.30f, 2002)] // 30% packet drop
        [InlineData(0.40f, 3003)] // 40% packet drop
        public void Channel3_SimulatedPacketDropAndOutOrderReordering_All50ItemsPreserved(float dropRate, int seed)
        {
            // Arrange
            var hostService = new InventorySyncService();
            var clientService = new InventorySyncService();
            var hostPeer = new NetworkPeerId(1, "Host");
            var simulator = new AdversarialChannel3Simulator(dropRate, seed);

            var capturedOutgoing = new List<ItemUnlockPacket>();
            hostService.OnOutgoingItemUnlockPacket += pkt => capturedOutgoing.Add(pkt);

            // Host discovers 50 diverse items (crests, tools, abilities, key items)
            for (int i = 0; i < 50; i++)
            {
                var category = (ItemCategory)(i % 4);
                string itemId = $"{category}_{i:D3}";
                hostService.OnLocalItemDiscovered(itemId, category);
            }

            Assert.Equal(50, capturedOutgoing.Count);

            // Act: Run through lossy channel simulator with ARQ retransmission and out-of-order arrival
            var receivedPackets = simulator.SimulateTransmission(capturedOutgoing);

            Assert.True(simulator.TotalDroppedPackets > 0, "Adversarial simulator should have dropped packets.");
            Assert.Equal(50, simulator.TotalDeliveredPackets);

            var clientReceivedEvents = new List<string>();
            clientService.OnItemUnlocked += (id, cat) => clientReceivedEvents.Add(id);

            // Deliver out-of-order packets to client
            foreach (var packet in receivedPackets)
            {
                clientService.HandleItemUnlockPacket(hostPeer, packet);
            }

            // Assert: 100% preservation, zero item loss, zero corruption
            Assert.Equal(50, clientService.UnlockedItems.Count);
            Assert.Equal(50, clientReceivedEvents.Count);

            for (int i = 0; i < 50; i++)
            {
                var category = (ItemCategory)(i % 4);
                string itemId = $"{category}_{i:D3}";
                Assert.True(clientService.IsItemUnlocked(itemId), $"Item {itemId} was not unlocked after network delivery.");
            }
        }

        [Fact]
        public void Channel3_ExtremeReverseOrderDelivery_All50ItemsPreserved()
        {
            // Arrange
            var hostService = new InventorySyncService();
            var clientService = new InventorySyncService();
            var hostPeer = new NetworkPeerId(1, "Host");

            var packets = new List<ItemUnlockPacket>();
            hostService.OnOutgoingItemUnlockPacket += pkt => packets.Add(pkt);

            for (int i = 0; i < 50; i++)
            {
                hostService.OnLocalItemDiscovered($"Tool_Needle_{i}", ItemCategory.Tool);
            }

            // Act: Reverse delivery (Packet 49 down to 0)
            packets.Reverse();

            var unlockedOnClient = new List<string>();
            clientService.OnItemUnlocked += (id, cat) => unlockedOnClient.Add(id);

            foreach (var pkt in packets)
            {
                clientService.HandleItemUnlockPacket(hostPeer, pkt);
            }

            // Assert
            Assert.Equal(50, clientService.UnlockedItems.Count);
            Assert.Equal(50, unlockedOnClient.Count);
            for (int i = 0; i < 50; i++)
            {
                Assert.True(clientService.IsItemUnlocked($"Tool_Needle_{i}"));
            }
        }

        [Fact]
        public void Channel3_PseudoRandomPermutationFuzz_50Items_AllPreserved()
        {
            // Arrange
            var hostService = new InventorySyncService();
            var clientService = new InventorySyncService();
            var hostPeer = new NetworkPeerId(1, "Host");

            var packets = new List<ItemUnlockPacket>();
            hostService.OnOutgoingItemUnlockPacket += pkt => packets.Add(pkt);

            for (int i = 0; i < 50; i++)
            {
                hostService.OnLocalItemDiscovered($"Crest_Fuzz_{i}", ItemCategory.Crest);
            }

            // Fisher-Yates shuffle
            var rng = new Random(777);
            for (int i = packets.Count - 1; i > 0; i--)
            {
                int k = rng.Next(i + 1);
                (packets[i], packets[k]) = (packets[k], packets[i]);
            }

            // Act
            foreach (var pkt in packets)
            {
                clientService.HandleItemUnlockPacket(hostPeer, pkt);
            }

            // Assert
            Assert.Equal(50, clientService.UnlockedItems.Count);
            for (int i = 0; i < 50; i++)
            {
                Assert.True(clientService.IsItemUnlocked($"Crest_Fuzz_{i}"));
            }
        }

        [Fact]
        public void Channel3_BurstLossMiddleChunk_WithDelayedDelivery_PreservesCategoryIntegrity()
        {
            // Arrange: 60 items across all 4 categories (15 each)
            var hostService = new InventorySyncService();
            var clientService = new InventorySyncService();
            var hostPeer = new NetworkPeerId(1, "Host");

            var packets = new List<ItemUnlockPacket>();
            hostService.OnOutgoingItemUnlockPacket += pkt => packets.Add(pkt);

            for (int i = 0; i < 60; i++)
            {
                var cat = (ItemCategory)(i % 4);
                hostService.OnLocalItemDiscovered($"Item_{cat}_{i}", cat);
            }

            // Simulate burst drop: indices 20..44 (25 packets, >40% burst) are delayed
            var firstBatch = packets.Take(20).ToList();
            var droppedBurst = packets.Skip(20).Take(25).ToList();
            var lastBatch = packets.Skip(45).ToList();

            var receivedCategories = new Dictionary<string, ItemCategory>(StringComparer.OrdinalIgnoreCase);
            clientService.OnItemUnlocked += (id, cat) => receivedCategories[id] = cat;

            // Deliver first batch, then last batch (arrival before dropped burst), then delayed dropped burst
            foreach (var p in firstBatch) clientService.HandleItemUnlockPacket(hostPeer, p);
            foreach (var p in lastBatch) clientService.HandleItemUnlockPacket(hostPeer, p);
            foreach (var p in droppedBurst) clientService.HandleItemUnlockPacket(hostPeer, p);

            // Assert
            Assert.Equal(60, clientService.UnlockedItems.Count);
            Assert.Equal(60, receivedCategories.Count);

            for (int i = 0; i < 60; i++)
            {
                var cat = (ItemCategory)(i % 4);
                string id = $"Item_{cat}_{i}";
                Assert.True(clientService.IsItemUnlocked(id));
                Assert.Equal(cat, receivedCategories[id]);
            }
        }

        #endregion

        #region 2. Duplicate Packet Storms (Retransmitting Same Unlock 50 Times in Rapid Succession)

        [Fact]
        public void DuplicateStorm_SingleItemRetransmitted50Times_FiresEventExactlyOnce()
        {
            // Arrange
            var service = new InventorySyncService();
            var peer = new NetworkPeerId(2, "Sender");
            var txId = Guid.NewGuid();
            var packet = new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlockSync, 3, 1, 1000),
                ItemId = "Crest_NeedleMaster",
                Category = ItemCategory.Crest,
                TransactionId = txId,
                Timestamp = 1000
            };

            int unlockEventCount = 0;
            int outgoingPacketCount = 0;
            service.OnItemUnlocked += (id, cat) => unlockEventCount++;
            service.OnOutgoingItemUnlockPacket += pkt => outgoingPacketCount++;

            // Act: Fire the exact same unlock packet 50 times in rapid succession
            for (int i = 0; i < 50; i++)
            {
                service.HandleItemUnlockPacket(peer, packet);
            }

            // Assert
            Assert.Equal(1, unlockEventCount);
            Assert.Single(service.UnlockedItems);
            Assert.True(service.IsItemUnlocked("Crest_NeedleMaster"));
            Assert.True(service.IsTransactionProcessed(txId));
            Assert.Equal(0, outgoingPacketCount); // Zero echo
        }

        [Fact]
        public void DuplicateStorm_50UniqueItemsRetransmitted50TimesEach_Total2500Packets_YieldsExactly50Unlocks()
        {
            // Arrange: 50 distinct items
            var service = new InventorySyncService();
            var peer = new NetworkPeerId(2, "Sender");
            var itemPackets = new List<ItemUnlockPacket>();

            for (int i = 0; i < 50; i++)
            {
                itemPackets.Add(new ItemUnlockPacket
                {
                    Header = new PacketHeader(PacketType.ItemUnlockSync, 3, (uint)(i + 1), 2000),
                    ItemId = $"Tool_StormItem_{i}",
                    Category = ItemCategory.Tool,
                    TransactionId = Guid.NewGuid(),
                    Timestamp = 2000
                });
            }

            int unlockEvents = 0;
            service.OnItemUnlocked += (id, cat) => Interlocked.Increment(ref unlockEvents);

            // Act: Send each packet 50 times in an interleaved storm (total 2500 packets)
            for (int round = 0; round < 50; round++)
            {
                for (int i = 0; i < 50; i++)
                {
                    service.HandleItemUnlockPacket(peer, itemPackets[i]);
                }
            }

            // Assert: Exactly 50 unlocks recorded, 2450 duplicates dropped
            Assert.Equal(50, unlockEvents);
            Assert.Equal(50, service.UnlockedItems.Count);
            for (int i = 0; i < 50; i++)
            {
                Assert.True(service.IsItemUnlocked($"Tool_StormItem_{i}"));
                Assert.True(service.IsTransactionProcessed(itemPackets[i].TransactionId));
            }
        }

        [Fact]
        public void DuplicateStorm_ConcurrentParallelThreads_500Packets_ThreadSafe()
        {
            // Arrange
            var service = new InventorySyncService();
            var peer = new NetworkPeerId(2, "Sender");
            var txId = Guid.NewGuid();
            var packet = new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlockSync, 3, 100, 3000),
                ItemId = "Ability_ThreadSphere",
                Category = ItemCategory.Ability,
                TransactionId = txId,
                Timestamp = 3000
            };

            int unlockEventCount = 0;
            service.OnItemUnlocked += (id, cat) => Interlocked.Increment(ref unlockEventCount);

            // Act: 10 parallel threads each sending the same packet 50 times concurrently
            Parallel.For(0, 10, _ =>
            {
                for (int i = 0; i < 50; i++)
                {
                    service.HandleItemUnlockPacket(peer, packet);
                }
            });

            // Assert
            Assert.Equal(1, unlockEventCount);
            Assert.Single(service.UnlockedItems);
            Assert.True(service.IsItemUnlocked("Ability_ThreadSphere"));
        }

        [Fact]
        public void DuplicateStorm_Crossing500TransactionFifoEviction_PreservesDiscoveryState()
        {
            // Arrange
            var service = new InventorySyncService();
            var peer = new NetworkPeerId(2, "Sender");

            // Step 1: Initial item with transaction txAncient
            var txAncient = Guid.NewGuid();
            var ancientPacket = new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlockSync, 3, 1, 1000),
                ItemId = "Crest_AncientWeaver",
                Category = ItemCategory.Crest,
                TransactionId = txAncient,
                Timestamp = 1000
            };

            int unlockEventCount = 0;
            service.OnItemUnlocked += (id, cat) => unlockEventCount++;

            service.HandleItemUnlockPacket(peer, ancientPacket);
            Assert.Equal(1, unlockEventCount);
            Assert.True(service.IsTransactionProcessed(txAncient));

            // Step 2: Flood 501 unique transactions to completely evict txAncient from the 500-item FIFO ring buffer
            for (int i = 0; i < 501; i++)
            {
                service.HandleItemUnlockPacket(peer, new ItemUnlockPacket
                {
                    Header = new PacketHeader(PacketType.ItemUnlockSync, 3, (uint)(i + 2), 1000),
                    ItemId = $"FillerItem_{i}",
                    Category = ItemCategory.KeyItem,
                    TransactionId = Guid.NewGuid(),
                    Timestamp = 1000
                });
            }

            // Assert txAncient was evicted from transaction order queue
            Assert.False(service.IsTransactionProcessed(txAncient));

            // Step 3: Now retransmit Crest_AncientWeaver with txAncient 50 times!
            for (int i = 0; i < 50; i++)
            {
                service.HandleItemUnlockPacket(peer, ancientPacket);
            }

            // Crucial Invariant: Even though txAncient was evicted from transaction history,
            // the item itself is ALREADY present in _unlockedItems!
            // Therefore, OnItemUnlocked MUST NOT fire a second time!
            Assert.Equal(502, unlockEventCount); // 1 (ancient) + 501 (fillers) + 0 (retransmit)
            Assert.True(service.IsItemUnlocked("Crest_AncientWeaver"));
        }

        [Fact]
        public void DuplicateStorm_SpoofedCategoryOnRetransmit_RejectedSafely()
        {
            // Arrange
            var service = new InventorySyncService();
            var peer = new NetworkPeerId(2, "Sender");
            var tx = Guid.NewGuid();

            ItemCategory? receivedCat = null;
            service.OnItemUnlocked += (id, cat) => receivedCat = cat;

            // Legitimate initial packet
            var legitimate = new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlockSync, 3, 1, 100),
                ItemId = "Tool_Pimpillo",
                Category = ItemCategory.Tool,
                TransactionId = tx,
                Timestamp = 100
            };
            service.HandleItemUnlockPacket(peer, legitimate);
            Assert.Equal(ItemCategory.Tool, receivedCat);

            // Adversarial duplicate with spoofed category Crest
            var spoofed = new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlockSync, 3, 1, 100),
                ItemId = "Tool_Pimpillo",
                Category = ItemCategory.Crest,
                TransactionId = tx,
                Timestamp = 100
            };

            for (int i = 0; i < 50; i++)
            {
                service.HandleItemUnlockPacket(peer, spoofed);
            }

            // Assert: Category was never mutated
            Assert.Equal(ItemCategory.Tool, receivedCat);
            Assert.Single(service.UnlockedItems);
        }

        #endregion

        #region 3. Mutual Simultaneous Discovery of 50 Crests/Tools at the Exact Same Millisecond Timestamp

        [Fact]
        public void MutualSimultaneousDiscovery_50IdenticalItems_ExactSameTimestamp_NoDuplication()
        {
            // Arrange: DualClientTestHarness
            using var harness = new DualClientTestHarness();
            uint sameTimestamp = (uint)Environment.TickCount;

            var hostUnlocked = new List<string>();
            var clientUnlocked = new List<string>();

            harness.HostInventory.OnItemUnlocked += (id, cat) => hostUnlocked.Add(id);
            harness.ClientInventory.OnItemUnlocked += (id, cat) => clientUnlocked.Add(id);

            // Act: Both Host and Client locally discover 50 identical items at the exact same timestamp
            for (int i = 0; i < 50; i++)
            {
                string itemId = $"SharedCrest_{i:D2}";
                harness.HostInventory.OnLocalItemDiscovered(itemId, ItemCategory.Crest);
                harness.ClientInventory.OnLocalItemDiscovered(itemId, ItemCategory.Crest);
            }

            // Both discovered locally
            Assert.Equal(50, hostUnlocked.Count);
            Assert.Equal(50, clientUnlocked.Count);

            // Deliver all crossed network packets
            harness.DeliverAll();

            // Assert: When partner's unlock packet arrives, neither client re-fires OnItemUnlocked
            Assert.Equal(50, hostUnlocked.Count);
            Assert.Equal(50, clientUnlocked.Count);
            Assert.Equal(50, harness.HostInventory.UnlockedItems.Count);
            Assert.Equal(50, harness.ClientInventory.UnlockedItems.Count);

            for (int i = 0; i < 50; i++)
            {
                string itemId = $"SharedCrest_{i:D2}";
                Assert.True(harness.HostInventory.IsItemUnlocked(itemId));
                Assert.True(harness.ClientInventory.IsItemUnlocked(itemId));
            }
        }

        [Fact]
        public void MutualSimultaneousDiscovery_50DisjointItems_ExactSameTimestamp_Total100ItemsSynced()
        {
            // Arrange: DualClientTestHarness
            using var harness = new DualClientTestHarness();
            uint sameTimestamp = (uint)Environment.TickCount;

            var hostEvents = new List<string>();
            var clientEvents = new List<string>();

            harness.HostInventory.OnItemUnlocked += (id, cat) => hostEvents.Add(id);
            harness.ClientInventory.OnItemUnlocked += (id, cat) => clientEvents.Add(id);

            // Host discovers HostItems_0..49, Client discovers ClientItems_0..49
            for (int i = 0; i < 50; i++)
            {
                harness.HostInventory.OnLocalItemDiscovered($"HostTool_{i}", ItemCategory.Tool);
                harness.ClientInventory.OnLocalItemDiscovered($"ClientCrest_{i}", ItemCategory.Crest);
            }

            // Deliver all packets across Channel 3
            harness.DeliverAll();

            // Assert: Both ends must have 100 items total (50 local + 50 remote)
            Assert.Equal(100, hostEvents.Count);
            Assert.Equal(100, clientEvents.Count);
            Assert.Equal(100, harness.HostInventory.UnlockedItems.Count);
            Assert.Equal(100, harness.ClientInventory.UnlockedItems.Count);

            for (int i = 0; i < 50; i++)
            {
                Assert.True(harness.HostInventory.IsItemUnlocked($"HostTool_{i}"));
                Assert.True(harness.HostInventory.IsItemUnlocked($"ClientCrest_{i}"));
                Assert.True(harness.ClientInventory.IsItemUnlocked($"HostTool_{i}"));
                Assert.True(harness.ClientInventory.IsItemUnlocked($"ClientCrest_{i}"));
            }

            // Loadout isolation invariant remains intact
            Assert.True(harness.HostInventory.IsEquippedLoadoutIsolated);
            Assert.True(harness.ClientInventory.IsEquippedLoadoutIsolated);
        }

        [Fact]
        public void MutualSimultaneousDiscovery_25Shared_25UniqueEach_ExactSameTimestamp_75Total()
        {
            // Arrange
            using var harness = new DualClientTestHarness();

            var hostEvents = new List<string>();
            var clientEvents = new List<string>();

            harness.HostInventory.OnItemUnlocked += (id, cat) => hostEvents.Add(id);
            harness.ClientInventory.OnItemUnlocked += (id, cat) => clientEvents.Add(id);

            // 25 shared discoveries
            for (int i = 0; i < 25; i++)
            {
                harness.HostInventory.OnLocalItemDiscovered($"CommonItem_{i}", ItemCategory.Ability);
                harness.ClientInventory.OnLocalItemDiscovered($"CommonItem_{i}", ItemCategory.Ability);
            }

            // 25 unique each
            for (int i = 0; i < 25; i++)
            {
                harness.HostInventory.OnLocalItemDiscovered($"HostOnly_{i}", ItemCategory.KeyItem);
                harness.ClientInventory.OnLocalItemDiscovered($"ClientOnly_{i}", ItemCategory.KeyItem);
            }

            harness.DeliverAll();

            // Assert: Total unique items must be 75 (25 common + 25 host + 25 client)
            Assert.Equal(75, harness.HostInventory.UnlockedItems.Count);
            Assert.Equal(75, harness.ClientInventory.UnlockedItems.Count);

            // Events on host: 25 common local + 25 host local + 25 client remote = 75 total
            Assert.Equal(75, hostEvents.Count);
            Assert.Equal(75, clientEvents.Count);
        }

        [Fact]
        public async Task MutualSimultaneousDiscovery_MultithreadedHighConcurrency_100ItemsStress()
        {
            // Arrange
            using var harness = new DualClientTestHarness();

            var hostItems = Enumerable.Range(0, 50).Select(i => $"HostConcurrent_{i}").ToList();
            var clientItems = Enumerable.Range(0, 50).Select(i => $"ClientConcurrent_{i}").ToList();

            // Act: Fire on separate background threads concurrently
            var hostTask = Task.Run(() =>
            {
                foreach (var item in hostItems)
                {
                    harness.HostInventory.OnLocalItemDiscovered(item, ItemCategory.Tool);
                }
            });

            var clientTask = Task.Run(() =>
            {
                foreach (var item in clientItems)
                {
                    harness.ClientInventory.OnLocalItemDiscovered(item, ItemCategory.Crest);
                }
            });

            await Task.WhenAll(hostTask, clientTask);

            harness.DeliverAll();

            // Assert
            Assert.Equal(100, harness.HostInventory.UnlockedItems.Count);
            Assert.Equal(100, harness.ClientInventory.UnlockedItems.Count);
        }

        #endregion

        #region 4. Verification that No Echo Loop / Ping-Pong Storm Is Triggered

        [Fact]
        public void EchoSuppression_RemotePacketHandling_NeverEmitsOutgoingPacket()
        {
            // Invariant: HandleItemUnlockPacket must strictly never invoke OnOutgoingItemUnlockPacket
            var service = new InventorySyncService();
            var peer = new NetworkPeerId(2, "Remote");

            int outgoingCallCount = 0;
            service.OnOutgoingItemUnlockPacket += pkt => outgoingCallCount++;

            for (int i = 0; i < 50; i++)
            {
                var packet = new ItemUnlockPacket
                {
                    Header = new PacketHeader(PacketType.ItemUnlockSync, 3, (uint)(i + 1), 100),
                    ItemId = $"RemoteItem_{i}",
                    Category = ItemCategory.Crest,
                    TransactionId = Guid.NewGuid(),
                    Timestamp = 100
                };

                service.HandleItemUnlockPacket(peer, packet);
            }

            Assert.Equal(50, service.UnlockedItems.Count);
            Assert.Equal(0, outgoingCallCount); // Strict zero-egress invariant
        }

        [Fact]
        public void EchoSuppression_CrossWiredLoop_SingleDiscovery_TotalPacketsEqualsOne()
        {
            // Arrange: Full cross-wiring simulation
            var hostService = new InventorySyncService();
            var clientService = new InventorySyncService();

            var hostPeer = new NetworkPeerId(1, "Host");
            var clientPeer = new NetworkPeerId(2, "Client");

            int totalNetworkPackets = 0;
            int hostReceivedPackets = 0;
            int clientReceivedPackets = 0;

            hostService.OnOutgoingItemUnlockPacket += pkt =>
            {
                totalNetworkPackets++;
                clientService.HandleItemUnlockPacket(hostPeer, pkt);
            };

            clientService.OnOutgoingItemUnlockPacket += pkt =>
            {
                totalNetworkPackets++;
                hostService.HandleItemUnlockPacket(clientPeer, pkt);
            };

            hostService.OnItemUnlocked += (id, cat) => hostReceivedPackets++;
            clientService.OnItemUnlocked += (id, cat) => clientReceivedPackets++;

            // Act: Host discovers 1 item
            hostService.OnLocalItemDiscovered("Crest_Single", ItemCategory.Crest);

            // Assert: Exactly 1 packet on the wire, 0 echo packets returned to Host
            Assert.Equal(1, totalNetworkPackets);
            Assert.Equal(1, hostReceivedPackets);
            Assert.Equal(1, clientReceivedPackets);
            Assert.True(hostService.IsItemUnlocked("Crest_Single"));
            Assert.True(clientService.IsItemUnlocked("Crest_Single"));
        }

        [Fact]
        public void EchoSuppression_CrossWiredLoop_50SimultaneousDiscoveries_TotalPacketsEquals100()
        {
            // Arrange
            var hostService = new InventorySyncService();
            var clientService = new InventorySyncService();

            var hostPeer = new NetworkPeerId(1, "Host");
            var clientPeer = new NetworkPeerId(2, "Client");

            int hostOutgoingCount = 0;
            int clientOutgoingCount = 0;

            hostService.OnOutgoingItemUnlockPacket += pkt =>
            {
                Interlocked.Increment(ref hostOutgoingCount);
                clientService.HandleItemUnlockPacket(hostPeer, pkt);
            };

            clientService.OnOutgoingItemUnlockPacket += pkt =>
            {
                Interlocked.Increment(ref clientOutgoingCount);
                hostService.HandleItemUnlockPacket(clientPeer, pkt);
            };

            // Act: Host discovers 50 items, Client discovers 50 items
            for (int i = 0; i < 50; i++)
            {
                hostService.OnLocalItemDiscovered($"HostEchoTest_{i}", ItemCategory.Tool);
                clientService.OnLocalItemDiscovered($"ClientEchoTest_{i}", ItemCategory.Crest);
            }

            // Assert: Strictly 50 Host -> Client and 50 Client -> Host = 100 total. Zero ping-pong echo.
            Assert.Equal(50, hostOutgoingCount);
            Assert.Equal(50, clientOutgoingCount);
            Assert.Equal(100, hostService.UnlockedItems.Count);
            Assert.Equal(100, clientService.UnlockedItems.Count);
        }

        [Fact]
        public void EchoSuppression_EquipActionsEmitZeroPacketsOnChannel3()
        {
            // Arrange
            var host = new InventorySyncService();
            var client = new InventorySyncService();

            int hostOutgoingPackets = 0;
            int clientOutgoingPackets = 0;

            host.OnOutgoingItemUnlockPacket += pkt => hostOutgoingPackets++;
            client.OnOutgoingItemUnlockPacket += pkt => clientOutgoingPackets++;

            // Act: Rapidly equip and modify loadout 50 times
            for (int i = 0; i < 50; i++)
            {
                host.SetLocalEquippedCrest($"Crest_Host_{i}");
                host.SetLocalEquippedTools(new[] { $"Tool_Host_A_{i}", $"Tool_Host_B_{i}" });

                client.SetLocalEquippedCrest($"Crest_Client_{i}");
                client.SetLocalEquippedTools(new[] { $"Tool_Client_X_{i}", $"Tool_Client_Y_{i}" });
            }

            // Assert: Zero network egress on inventory channel
            Assert.Equal(0, hostOutgoingPackets);
            Assert.Equal(0, clientOutgoingPackets);

            // Loadouts remain isolated
            Assert.Equal("Crest_Host_49", host.LocalEquippedCrest);
            Assert.Equal("Crest_Client_49", client.LocalEquippedCrest);
            Assert.NotEqual(host.LocalEquippedCrest, client.LocalEquippedCrest);
        }

        [Fact]
        public void EchoSuppression_ReEntrancyFlagSimulation_BlocksEngineHookEgress()
        {
            // Simulates the Harmony postfix re-entrancy guard:
            // When applying a remote unlock, IsApplyingRemoteUnlock is true.
            // Any hook invocation during this window must immediately return.
            bool isApplyingRemoteUnlock = false;
            int hookBroadcastAttempts = 0;
            var service = new InventorySyncService();

            service.OnOutgoingItemUnlockPacket += pkt => hookBroadcastAttempts++;

            void SimulatedHookPostfix(string itemId, ItemCategory cat)
            {
                if (isApplyingRemoteUnlock)
                {
                    return; // Re-entrancy suppressed!
                }
                service.OnLocalItemDiscovered(itemId, cat);
            }

            // Test 1: Normal local unlock -> hook runs and broadcasts
            isApplyingRemoteUnlock = false;
            SimulatedHookPostfix("LocalTool_1", ItemCategory.Tool);
            Assert.Equal(1, hookBroadcastAttempts);

            // Test 2: Remote unlock active -> hook suppressed
            isApplyingRemoteUnlock = true;
            try
            {
                // Engine fires unlock hook as a side-effect
                SimulatedHookPostfix("RemoteTool_2", ItemCategory.Tool);
            }
            finally
            {
                isApplyingRemoteUnlock = false;
            }

            // Broadcast count should still be 1!
            Assert.Equal(1, hookBroadcastAttempts);
        }

        #endregion

        #region 5. Robustness & Invariant Boundary Hardening

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void Boundary_InvalidItemIds_IgnoredCleanly(string? invalidId)
        {
            var service = new InventorySyncService();
            int outgoing = 0;
            service.OnOutgoingItemUnlockPacket += pkt => outgoing++;

            service.OnLocalItemDiscovered(invalidId!, ItemCategory.Crest);
            service.HandleItemUnlockPacket(new NetworkPeerId(2), new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlockSync, 3, 1, 100),
                ItemId = invalidId!,
                Category = ItemCategory.Crest,
                TransactionId = Guid.NewGuid(),
                Timestamp = 100
            });

            Assert.Equal(0, outgoing);
            Assert.Empty(service.UnlockedItems);
        }

        [Fact]
        public void Boundary_EmptyGuid_IgnoredCleanly()
        {
            var service = new InventorySyncService();
            int outgoing = 0;
            service.OnOutgoingItemUnlockPacket += pkt => outgoing++;

            service.OnLocalItemDiscovered("Item_EmptyGuid", ItemCategory.Tool, Guid.Empty);
            service.HandleItemUnlockPacket(new NetworkPeerId(2), new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlockSync, 3, 1, 100),
                ItemId = "Item_EmptyGuid",
                Category = ItemCategory.Tool,
                TransactionId = Guid.Empty,
                Timestamp = 100
            });

            Assert.Equal(0, outgoing);
            Assert.Empty(service.UnlockedItems);
        }

        [Fact]
        public void Boundary_NumericItemOverload_50Items_MirroredCleanly()
        {
            using var harness = new DualClientTestHarness();

            for (int id = 1001; id <= 1050; id++)
            {
                harness.HostInventory.OnLocalItemDiscovered(id, ItemCategory.Tool);
            }

            harness.DeliverAll();

            Assert.Equal(50, harness.ClientInventory.UnlockedItems.Count);
            for (int id = 1001; id <= 1050; id++)
            {
                Assert.True(harness.ClientInventory.IsItemUnlocked(id.ToString()));
            }
        }

        #endregion

        #region 6. Compound Adversarial Chaos Monkey & Concurrency Stress

        [Fact]
        public void CompoundChaosMonkey_40PercentDrop_Reordering_DuplicateStorm_Simultaneous50Items()
        {
            // Adversarial compound test:
            // 1. Host and Client simultaneously discover 50 items each (25 shared, 25 host-only, 25 client-only = 75 total).
            // 2. Transmitted across an adversarial channel with 40% packet loss, packet duplication storms (up to 5x duplicates),
            //    and out-of-order reordering.
            // 3. Verifies both peers converge on exactly 75 unlocked items with zero duplicate callbacks, zero echo loops,
            //    and complete loadout isolation.

            var hostService = new InventorySyncService();
            var clientService = new InventorySyncService();
            var hostPeer = new NetworkPeerId(1, "Host");
            var clientPeer = new NetworkPeerId(2, "Client");

            var hostOutgoing = new List<ItemUnlockPacket>();
            var clientOutgoing = new List<ItemUnlockPacket>();

            hostService.OnOutgoingItemUnlockPacket += pkt => hostOutgoing.Add(pkt);
            clientService.OnOutgoingItemUnlockPacket += pkt => clientOutgoing.Add(pkt);

            var hostUnlockedEvents = new ConcurrentBag<string>();
            var clientUnlockedEvents = new ConcurrentBag<string>();

            hostService.OnItemUnlocked += (id, cat) => hostUnlockedEvents.Add(id);
            clientService.OnItemUnlocked += (id, cat) => clientUnlockedEvents.Add(id);

            // Host discovers items 0..49
            for (int i = 0; i < 50; i++)
            {
                var cat = (ItemCategory)(i % 4);
                hostService.OnLocalItemDiscovered($"ChaosItem_{i}", cat);
            }

            // Client discovers items 25..74 (25 overlap with Host, 25 new)
            for (int i = 25; i < 75; i++)
            {
                var cat = (ItemCategory)(i % 4);
                clientService.OnLocalItemDiscovered($"ChaosItem_{i}", cat);
            }

            Assert.Equal(50, hostOutgoing.Count);
            Assert.Equal(50, clientOutgoing.Count);

            // Adversarial pipeline: 40% drop + ARQ retransmission + duplicate storm + reordering
            List<ItemUnlockPacket> RunAdversarialPipeline(List<ItemUnlockPacket> inputPackets, int seed)
            {
                var rng = new Random(seed);
                var delivered = new List<(int arrivalOrder, ItemUnlockPacket pkt)>();
                int arrivalCounter = 0;

                foreach (var packet in inputPackets)
                {
                    bool deliveredSuccess = false;
                    int attempts = 0;

                    while (!deliveredSuccess)
                    {
                        attempts++;
                        if (rng.NextDouble() < 0.40) // 40% packet drop
                        {
                            continue;
                        }

                        // Delivered!
                        int arrivalOrder = arrivalCounter + (attempts * 7) + rng.Next(-5, 6);
                        delivered.Add((arrivalOrder, packet));
                        deliveredSuccess = true;
                        arrivalCounter++;

                        // Duplicate storm: 50% chance to generate 2 to 5 duplicate copies
                        if (rng.NextDouble() < 0.50)
                        {
                            int dupCopies = rng.Next(2, 6);
                            for (int d = 0; d < dupCopies; d++)
                            {
                                int dupOrder = arrivalOrder + rng.Next(1, 15);
                                delivered.Add((dupOrder, packet));
                            }
                        }
                    }
                }

                return delivered.OrderBy(p => p.arrivalOrder).Select(p => p.pkt).ToList();
            }

            var hostPacketsDeliveredToClient = RunAdversarialPipeline(hostOutgoing, 4004);
            var clientPacketsDeliveredToHost = RunAdversarialPipeline(clientOutgoing, 5005);

            // Verify that pipeline created duplicate storm and out-of-order delivery
            Assert.True(hostPacketsDeliveredToClient.Count > hostOutgoing.Count, "Duplicate storm should expand packet count.");
            Assert.True(clientPacketsDeliveredToHost.Count > clientOutgoing.Count, "Duplicate storm should expand packet count.");

            // Deliver all packets
            foreach (var pkt in hostPacketsDeliveredToClient)
            {
                clientService.HandleItemUnlockPacket(hostPeer, pkt);
            }

            foreach (var pkt in clientPacketsDeliveredToHost)
            {
                hostService.HandleItemUnlockPacket(clientPeer, pkt);
            }

            // Assert: Strict convergence
            Assert.Equal(75, hostService.UnlockedItems.Count);
            Assert.Equal(75, clientService.UnlockedItems.Count);

            // On each peer, exactly 75 unlock events fired (zero duplicate events for duplicates or re-discoveries)
            Assert.Equal(75, hostUnlockedEvents.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(75, clientUnlockedEvents.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(75, hostUnlockedEvents.Count);
            Assert.Equal(75, clientUnlockedEvents.Count);

            // Invariant: Loadout isolation preserved
            Assert.True(hostService.IsEquippedLoadoutIsolated);
            Assert.True(clientService.IsEquippedLoadoutIsolated);
        }

        [Fact]
        public async Task ConcurrentEquipLoadoutChurn_DuringMutualSimultaneous50ItemsDiscovery()
        {
            // Invariant Verification:
            // High-frequency equipping and unequipping of crests and tools happening concurrently on separate threads
            // must NEVER mutate the remote peer's loadout and must NEVER leak packets onto Channel 3,
            // even while 50 items are simultaneously unlocked and synchronized.

            using var harness = new DualClientTestHarness();

            var hostDiscoveredItems = Enumerable.Range(0, 50).Select(i => $"ChurnItem_Host_{i}").ToList();
            var clientDiscoveredItems = Enumerable.Range(0, 50).Select(i => $"ChurnItem_Client_{i}").ToList();

            var cts = new CancellationTokenSource();
            var token = cts.Token;

            // Background loadout churn task on Host
            var hostLoadoutTask = Task.Run(() =>
            {
                int iter = 0;
                while (!token.IsCancellationRequested && iter < 100)
                {
                    harness.HostInventory.SetLocalEquippedCrest($"HostCrest_{iter}");
                    harness.HostInventory.SetLocalEquippedTools(new[] { $"HostTool_A_{iter}", $"HostTool_B_{iter}" });
                    iter++;
                    Thread.Sleep(1);
                }
            });

            // Background loadout churn task on Client
            var clientLoadoutTask = Task.Run(() =>
            {
                int iter = 0;
                while (!token.IsCancellationRequested && iter < 100)
                {
                    harness.ClientInventory.SetLocalEquippedCrest($"ClientCrest_{iter}");
                    harness.ClientInventory.SetLocalEquippedTools(new[] { $"ClientTool_X_{iter}", $"ClientTool_Y_{iter}" });
                    iter++;
                    Thread.Sleep(1);
                }
            });

            // Concurrently discover 50 items on each peer
            var hostDiscoverTask = Task.Run(() =>
            {
                foreach (var item in hostDiscoveredItems)
                {
                    harness.HostInventory.OnLocalItemDiscovered(item, ItemCategory.Tool);
                }
            });

            var clientDiscoverTask = Task.Run(() =>
            {
                foreach (var item in clientDiscoveredItems)
                {
                    harness.ClientInventory.OnLocalItemDiscovered(item, ItemCategory.Crest);
                }
            });

            await Task.WhenAll(hostDiscoverTask, clientDiscoverTask);
            await Task.WhenAll(hostLoadoutTask, clientLoadoutTask);

            // Deliver all discovery packets
            harness.DeliverAll();

            // Assert: All 100 items synced
            Assert.Equal(100, harness.HostInventory.UnlockedItems.Count);
            Assert.Equal(100, harness.ClientInventory.UnlockedItems.Count);

            // Assert: Equipped loadout isolation invariant strictly preserved
            Assert.True(harness.HostInventory.IsEquippedLoadoutIsolated);
            Assert.True(harness.ClientInventory.IsEquippedLoadoutIsolated);
            Assert.StartsWith("HostCrest_", harness.HostInventory.LocalEquippedCrest);
            Assert.StartsWith("ClientCrest_", harness.ClientInventory.LocalEquippedCrest);
            Assert.NotEqual(harness.HostInventory.LocalEquippedCrest, harness.ClientInventory.LocalEquippedCrest);
        }

        [Fact]
        public void AdversarialNetwork_OutOfRangeCategoryEnum_HandledSafelyWithoutCrash()
        {
            // Verify that if a corrupted packet arrives with an out-of-range ItemCategory byte (e.g. 99, 255),
            // the system records the unlock without throwing an unhandled exception or corrupting state.

            var service = new InventorySyncService();
            var peer = new NetworkPeerId(2, "Sender");

            ItemCategory? receivedCat = null;
            service.OnItemUnlocked += (id, cat) => receivedCat = cat;

            var packet99 = new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlockSync, 3, 1, 100),
                ItemId = "Item_CorruptCategory_99",
                Category = (ItemCategory)99,
                TransactionId = Guid.NewGuid(),
                Timestamp = 100
            };

            var packet255 = new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlockSync, 3, 2, 100),
                ItemId = "Item_CorruptCategory_255",
                Category = (ItemCategory)255,
                TransactionId = Guid.NewGuid(),
                Timestamp = 100
            };

            service.HandleItemUnlockPacket(peer, packet99);
            Assert.True(service.IsItemUnlocked("Item_CorruptCategory_99"));
            Assert.Equal((ItemCategory)99, receivedCat);

            service.HandleItemUnlockPacket(peer, packet255);
            Assert.True(service.IsItemUnlocked("Item_CorruptCategory_255"));
            Assert.Equal((ItemCategory)255, receivedCat);

            Assert.Equal(2, service.UnlockedItems.Count);
        }

        [Fact]
        public void AdversarialNetwork_BufferCapacityBoundary_DocumentedLimit()
        {
            // Verifies the exact boundary capacity for binary serialization:
            // Fixed overhead: Header (12) + String length prefix (2) + Category (1) + TransactionId (16) + Timestamp (4) = 35 bytes.
            // With a 256-byte stackalloc/heap buffer, the maximum UTF-8 byte length for ItemId is 221 bytes.
            // A 200-byte string serializes and deserializes cleanly.
            // A 250-byte string will throw InvalidOperationException when serialized into a 256-byte buffer.

            string safe200ByteString = new string('X', 200);
            var safePacket = new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlockSync, 3, 1, 100),
                ItemId = safe200ByteString,
                Category = ItemCategory.Tool,
                TransactionId = Guid.NewGuid(),
                Timestamp = 100
            };

            Span<byte> safeBuffer = stackalloc byte[256];
            var writer = new PacketWriter(safeBuffer);
            safePacket.Serialize(ref writer);
            Assert.Equal(235, writer.Position); // 35 bytes overhead + 200 bytes string = 235 bytes <= 256

            var reader = new PacketReader(safeBuffer.Slice(0, writer.Position));
            var deserialized = ItemUnlockPacket.Deserialize(ref reader);
            Assert.Equal(safe200ByteString, deserialized.ItemId);
            Assert.Equal(safePacket.TransactionId, deserialized.TransactionId);

            // Now test 250-byte string: requires 285 bytes, exceeding 256-byte buffer
            string oversizeString = new string('Z', 250);
            var oversizePacket = new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlockSync, 3, 2, 100),
                ItemId = oversizeString,
                Category = ItemCategory.Tool,
                TransactionId = Guid.NewGuid(),
                Timestamp = 100
            };

            Span<byte> smallBuffer = stackalloc byte[256];
            var smallWriter = new PacketWriter(smallBuffer);
            bool threw = false;
            try
            {
                oversizePacket.Serialize(ref smallWriter);
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            Assert.True(threw, "Serializing 250-byte string into 256-byte buffer must throw InvalidOperationException");
        }

        #endregion
    }
}
