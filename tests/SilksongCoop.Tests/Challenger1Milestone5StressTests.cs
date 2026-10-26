using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
    /// Empirical Adversarial Stress Suite for Milestone 5 (Gear, Crests & Discovery Mirroring):
    /// 1. High-frequency simultaneous item unlocks across both peers under concurrency and packet loss.
    /// 2. Invariant verification that equipping/unequipping tools or crests during active packet floods NEVER alters remote loadouts.
    /// 3. Stress testing the 500-item FIFO ring buffer eviction boundary (e.g. 1,000+ sequential and concurrent items).
    /// 4. Boundary cases: malformed packets, truncated buffers, empty GUIDs, non-ASCII/Unicode strings, and duplicate transaction edge cases.
    /// </summary>
    public class Challenger1Milestone5StressTests
    {
        #region 1. High-Frequency Simultaneous Item Unlocks Across Both Peers

        [Fact]
        public async Task Stress_SimultaneousUnlocks_SameItems_HighConcurrency_ExactDeduplication()
        {
            // Both Host and Client attempt to unlock the EXACT SAME 100 items simultaneously across 100 concurrent tasks.
            // Invariant:
            // - Every item must end up unlocked on both peers.
            // - Exactly 100 items in UnlockedItems.
            // - OnItemUnlocked callback fires at most once per item on each peer.
            // - Zero deadlocks, race conditions, or unhandled exceptions.

            using var harness = new DualClientTestHarness();
            const int itemCount = 100;
            var hostCallbackCount = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var clientCallbackCount = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            harness.HostInventory.OnItemUnlocked += (id, _) =>
                hostCallbackCount.AddOrUpdate(id, 1, (_, count) => count + 1);

            harness.ClientInventory.OnItemUnlocked += (id, _) =>
                clientCallbackCount.AddOrUpdate(id, 1, (_, count) => count + 1);

            var tasks = new List<Task>();

            for (int i = 0; i < itemCount; i++)
            {
                int index = i;
                string itemId = $"ConcurrentSharedItem_{index}";

                tasks.Add(Task.Run(() =>
                {
                    harness.HostInventory.OnLocalItemDiscovered(itemId, ItemCategory.Tool);
                }));

                tasks.Add(Task.Run(() =>
                {
                    harness.ClientInventory.OnLocalItemDiscovered(itemId, ItemCategory.Tool);
                }));
            }

            await Task.WhenAll(tasks);

            // Deliver all generated packets across the network
            harness.DeliverAll();

            // Invariant assertions
            Assert.Equal(itemCount, harness.HostInventory.UnlockedItems.Count);
            Assert.Equal(itemCount, harness.ClientInventory.UnlockedItems.Count);

            for (int i = 0; i < itemCount; i++)
            {
                string itemId = $"ConcurrentSharedItem_{i}";
                Assert.True(harness.HostInventory.IsItemUnlocked(itemId), $"Host missing item {itemId}");
                Assert.True(harness.ClientInventory.IsItemUnlocked(itemId), $"Client missing item {itemId}");

                // Each peer must have triggered OnItemUnlocked exactly once for that item
                Assert.True(hostCallbackCount.TryGetValue(itemId, out int hCount), $"Host did not fire callback for {itemId}");
                Assert.Equal(1, hCount);

                Assert.True(clientCallbackCount.TryGetValue(itemId, out int cCount), $"Client did not fire callback for {itemId}");
                Assert.Equal(1, cCount);
            }
        }

        [Fact]
        public async Task Stress_SimultaneousUnlocks_DisjointItems_CrossMirror_400Total()
        {
            // Host unlocks 200 distinct items (HostItem_0..199).
            // Client unlocks 200 distinct items (ClientItem_0..199).
            // All operations execute concurrently on background threads.
            // Invariant:
            // - Both peers must end with exactly 400 total items unlocked.
            // - Exactly 400 unlock callbacks per peer (200 local + 200 remote).

            using var harness = new DualClientTestHarness();
            const int perPeerCount = 200;
            int totalExpected = perPeerCount * 2;

            int hostUnlockedCount = 0;
            int clientUnlockedCount = 0;

            harness.HostInventory.OnItemUnlocked += (_, _) => System.Threading.Interlocked.Increment(ref hostUnlockedCount);
            harness.ClientInventory.OnItemUnlocked += (_, _) => System.Threading.Interlocked.Increment(ref clientUnlockedCount);

            var tasks = new List<Task>();

            for (int i = 0; i < perPeerCount; i++)
            {
                int index = i;
                tasks.Add(Task.Run(() =>
                {
                    harness.HostInventory.OnLocalItemDiscovered($"HostItem_{index}", ItemCategory.Crest);
                }));

                tasks.Add(Task.Run(() =>
                {
                    harness.ClientInventory.OnLocalItemDiscovered($"ClientItem_{index}", ItemCategory.Ability);
                }));
            }

            await Task.WhenAll(tasks);

            // Deliver all packets between peers
            harness.DeliverAll();

            Assert.Equal(totalExpected, harness.HostInventory.UnlockedItems.Count);
            Assert.Equal(totalExpected, harness.ClientInventory.UnlockedItems.Count);
            Assert.Equal(totalExpected, hostUnlockedCount);
            Assert.Equal(totalExpected, clientUnlockedCount);

            for (int i = 0; i < perPeerCount; i++)
            {
                Assert.True(harness.HostInventory.IsItemUnlocked($"HostItem_{i}"));
                Assert.True(harness.HostInventory.IsItemUnlocked($"ClientItem_{i}"));
                Assert.True(harness.ClientInventory.IsItemUnlocked($"HostItem_{i}"));
                Assert.True(harness.ClientInventory.IsItemUnlocked($"ClientItem_{i}"));
            }
        }

        [Fact]
        public void Stress_SimultaneousBurstUnlocks_UnderSevereNetworkLossAndJitter()
        {
            // Network impairment: 30ms latency, 15ms jitter, 25% packet drop.
            // ReliableOrdered delivery on Channel 3 must guarantee 100% item delivery without drop or out-of-order corruption.
            using var harness = new DualClientTestHarness(latencyMs: 30, jitterMs: 15, lossRate: 0.25f, seed: 9999);
            const int burstCount = 60;

            for (int i = 0; i < burstCount; i++)
            {
                harness.HostInventory.OnLocalItemDiscovered($"LossStress_Host_{i}", ItemCategory.Tool);
                harness.ClientInventory.OnLocalItemDiscovered($"LossStress_Client_{i}", ItemCategory.KeyItem);
            }

            // Deliver across simulated lossy transport
            harness.DeliverAll(maxSteps: 500);

            Assert.Equal(burstCount * 2, harness.HostInventory.UnlockedItems.Count);
            Assert.Equal(burstCount * 2, harness.ClientInventory.UnlockedItems.Count);

            for (int i = 0; i < burstCount; i++)
            {
                Assert.True(harness.HostInventory.IsItemUnlocked($"LossStress_Host_{i}"));
                Assert.True(harness.HostInventory.IsItemUnlocked($"LossStress_Client_{i}"));
                Assert.True(harness.ClientInventory.IsItemUnlocked($"LossStress_Host_{i}"));
                Assert.True(harness.ClientInventory.IsItemUnlocked($"LossStress_Client_{i}"));
            }
        }

        [Fact]
        public async Task Stress_InterleavedUnlocksAndQueries_ConcurrentContention()
        {
            // 40 concurrent tasks hammering InventorySyncService with:
            // - Local unlocks
            // - Remote packet handling
            // - UnlockedItems enumeration
            // - IsItemUnlocked queries
            // - Loadout modifications
            var service = new InventorySyncService();
            const int operationsPerThread = 50;
            var tasks = new List<Task>();

            for (int t = 0; t < 20; t++)
            {
                int threadId = t;
                tasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < operationsPerThread; i++)
                    {
                        service.OnLocalItemDiscovered($"ContentionItem_{threadId}_{i}", ItemCategory.Tool);
                        bool isUnlocked = service.IsItemUnlocked($"ContentionItem_{threadId}_{i}");
                        Assert.True(isUnlocked);
                    }
                }));

                tasks.Add(Task.Run(() =>
                {
                    for (int i = 0; i < operationsPerThread; i++)
                    {
                        var packet = new ItemUnlockPacket
                        {
                            Header = new PacketHeader(PacketType.ItemUnlockSync, 3, (uint)(threadId * 1000 + i), 12345),
                            ItemId = $"RemoteContention_{threadId}_{i}",
                            Category = ItemCategory.Crest,
                            TransactionId = Guid.NewGuid(),
                            Timestamp = 12345
                        };
                        service.HandleItemUnlockPacket(new NetworkPeerId(2), packet);
                        var count = service.UnlockedItems.Count;
                        Assert.True(count >= 0);
                    }
                }));
            }

            await Task.WhenAll(tasks);

            int totalExpected = 20 * operationsPerThread * 2;
            Assert.Equal(totalExpected, service.UnlockedItems.Count);
        }

        #endregion

        #region 2. Invariant Verification: Equipping/Unequipping During Active Packet Floods NEVER Changes Remote Loadout

        [Fact]
        public async Task Invariant_EquipDuringPacketFlood_RemoteLoadoutNeverMutates()
        {
            // Peer A and Peer B have independent loadouts.
            // A flood of 400 item unlock packets is exchanged across the network.
            // Concurrently, Peer A and Peer B rapidly mutate their local equipped crests and tools.
            // Invariants:
            // 1. Peer A's LocalEquippedCrest is NEVER altered by Peer B's loadout or incoming packets.
            // 2. Peer B's LocalEquippedCrest is NEVER altered by Peer A's loadout or incoming packets.
            // 3. Peer A's LocalEquippedTools NEVER contains Peer B's tools.
            // 4. Peer B's LocalEquippedTools NEVER contains Peer A's tools.
            // 5. IsEquippedLoadoutIsolated remains true.

            using var harness = new DualClientTestHarness();

            harness.HostInventory.SetLocalEquippedCrest("Host_Crest_Anchor");
            harness.HostInventory.SetLocalEquippedTools(new[] { "Host_Tool_A", "Host_Tool_B" });

            harness.ClientInventory.SetLocalEquippedCrest("Client_Crest_Anchor");
            harness.ClientInventory.SetLocalEquippedTools(new[] { "Client_Tool_X", "Client_Tool_Y" });

            var floodTask = Task.Run(() =>
            {
                for (int i = 0; i < 200; i++)
                {
                    harness.HostInventory.OnLocalItemDiscovered($"FloodItem_H_{i}", ItemCategory.Tool);
                    harness.ClientInventory.OnLocalItemDiscovered($"FloodItem_C_{i}", ItemCategory.Crest);
                }
            });

            var equipMutationTaskA = Task.Run(() =>
            {
                for (int i = 0; i < 100; i++)
                {
                    harness.HostInventory.SetLocalEquippedCrest($"Host_Dynamic_Crest_{i}");
                    harness.HostInventory.SetLocalEquippedTools(new[] { $"Host_Tool_{i}_1", $"Host_Tool_{i}_2" });
                }
                // Final anchor
                harness.HostInventory.SetLocalEquippedCrest("Host_Final_Crest");
                harness.HostInventory.SetLocalEquippedTools(new[] { "Host_Final_Tool1", "Host_Final_Tool2" });
            });

            var equipMutationTaskB = Task.Run(() =>
            {
                for (int i = 0; i < 100; i++)
                {
                    harness.ClientInventory.SetLocalEquippedCrest($"Client_Dynamic_Crest_{i}");
                    harness.ClientInventory.SetLocalEquippedTools(new[] { $"Client_Tool_{i}_1", $"Client_Tool_{i}_2" });
                }
                // Final anchor
                harness.ClientInventory.SetLocalEquippedCrest("Client_Final_Crest");
                harness.ClientInventory.SetLocalEquippedTools(new[] { "Client_Final_ToolA", "Client_Final_ToolB" });
            });

            await Task.WhenAll(floodTask, equipMutationTaskA, equipMutationTaskB);

            // Deliver all packets across transport
            harness.DeliverAll();

            // Assert Invariants
            Assert.True(harness.HostInventory.IsEquippedLoadoutIsolated);
            Assert.True(harness.ClientInventory.IsEquippedLoadoutIsolated);

            Assert.Equal("Host_Final_Crest", harness.HostInventory.LocalEquippedCrest);
            Assert.Equal("Client_Final_Crest", harness.ClientInventory.LocalEquippedCrest);

            var hostTools = harness.HostInventory.LocalEquippedTools;
            var clientTools = harness.ClientInventory.LocalEquippedTools;

            Assert.Equal(2, hostTools.Count);
            Assert.Contains("Host_Final_Tool1", hostTools);
            Assert.Contains("Host_Final_Tool2", hostTools);
            Assert.DoesNotContain("Client_Final_ToolA", hostTools);
            Assert.DoesNotContain("Client_Final_ToolB", hostTools);

            Assert.Equal(2, clientTools.Count);
            Assert.Contains("Client_Final_ToolA", clientTools);
            Assert.Contains("Client_Final_ToolB", clientTools);
            Assert.DoesNotContain("Host_Final_Tool1", clientTools);
            Assert.DoesNotContain("Host_Final_Tool2", clientTools);
        }

        [Fact]
        public void Invariant_UnequipToEmpty_DuringMassiveUnlockBurst_RemainsStrictlyEmpty()
        {
            using var harness = new DualClientTestHarness();

            harness.HostInventory.SetLocalEquippedCrest("Host_Crest_Active");
            harness.HostInventory.SetLocalEquippedTools(new[] { "Tool1", "Tool2" });

            harness.ClientInventory.SetLocalEquippedCrest("Client_Crest_Heavy");
            harness.ClientInventory.SetLocalEquippedTools(new[] { "ToolX", "ToolY", "ToolZ" });

            // Host completely unequips everything
            harness.HostInventory.SetLocalEquippedCrest(string.Empty);
            harness.HostInventory.SetLocalEquippedTools(Array.Empty<string>());

            // Client floods host with 150 item unlocks
            for (int i = 0; i < 150; i++)
            {
                harness.ClientInventory.OnLocalItemDiscovered($"BurstTool_{i}", ItemCategory.Tool);
            }
            harness.DeliverAll();

            // Verify Host loadout is still 100% empty
            Assert.Equal(string.Empty, harness.HostInventory.LocalEquippedCrest);
            Assert.Empty(harness.HostInventory.LocalEquippedTools);

            // Verify Client loadout is still intact and unaffected
            Assert.Equal("Client_Crest_Heavy", harness.ClientInventory.LocalEquippedCrest);
            Assert.Equal(3, harness.ClientInventory.LocalEquippedTools.Count);
            Assert.Contains("ToolX", harness.ClientInventory.LocalEquippedTools);
        }

        [Fact]
        public void Invariant_ZeroNetworkPackets_OnLocalEquipModifications()
        {
            using var harness = new DualClientTestHarness();
            int packetsSent = 0;
            harness.HostInventory.OnOutgoingItemUnlockPacket += _ => packetsSent++;

            // Perform 500 local loadout changes
            for (int i = 0; i < 500; i++)
            {
                harness.HostInventory.SetLocalEquippedCrest($"Crest_{i}");
                harness.HostInventory.SetLocalEquippedTools(new[] { $"Tool_{i}_A", $"Tool_{i}_B" });
            }

            Assert.Equal(0, packetsSent);
            Assert.Empty(harness.DeliveredItemUnlocksToClient);
        }

        #endregion

        #region 3. Stress Testing the 500-Item FIFO Ring Buffer Eviction Boundary

        [Fact]
        public void Stress_FifoRingBuffer_Sequential1000Unlocks_EvictionExactness()
        {
            // Test 1,000 sequential item unlocks to stress test the 500-item ring buffer boundary.
            // MaxTrackedTransactions = 500.
            // Invariants:
            // 1. Transactions [0..499] MUST be evicted from _processedTransactions.
            // 2. Transactions [500..999] MUST remain tracked in _processedTransactions.
            // 3. ALL 1,000 items MUST remain permanently present in UnlockedItems.
            // 4. OnItemUnlocked MUST have fired exactly 1,000 times.

            var service = new InventorySyncService();
            var guids = new List<Guid>(1000);
            int callbackCount = 0;
            service.OnItemUnlocked += (_, _) => callbackCount++;

            for (int i = 0; i < 1000; i++)
            {
                var tx = Guid.NewGuid();
                guids.Add(tx);

                var packet = new ItemUnlockPacket
                {
                    Header = new PacketHeader(PacketType.ItemUnlockSync, 3, (uint)i, (uint)(1000 + i)),
                    ItemId = $"SequentialItem_{i}",
                    Category = ItemCategory.Tool,
                    TransactionId = tx,
                    Timestamp = (uint)(1000 + i)
                };

                service.HandleItemUnlockPacket(new NetworkPeerId(1), packet);
            }

            // Assert callback and unlock counts
            Assert.Equal(1000, callbackCount);
            Assert.Equal(1000, service.UnlockedItems.Count);

            // Assert eviction boundary:
            // Items 0..499: evicted
            for (int i = 0; i < 500; i++)
            {
                Assert.False(
                    service.IsTransactionProcessed(guids[i]),
                    $"Transaction at index {i} should have been evicted by the 500-item FIFO ring buffer.");
            }

            // Items 500..999: actively retained
            for (int i = 500; i < 1000; i++)
            {
                Assert.True(
                    service.IsTransactionProcessed(guids[i]),
                    $"Transaction at index {i} should still be retained in the 500-item FIFO ring buffer.");
            }
        }

        [Fact]
        public void Stress_FifoRingBuffer_RetransmissionAfterEviction_ItemLevelDeduplicationSurvives()
        {
            // CRITICAL ADVERSARIAL ORACLE:
            // What happens when packet 0 (ItemId = "PersistentItem", Guid = tx0) is re-received
            // AFTER 500 additional transactions have passed and evicted tx0 from _processedTransactions?
            // Oracle:
            // - Since "PersistentItem" is ALREADY recorded in _unlockedItems,
            //   isNewItem = _unlockedItems.Add(...) MUST return false.
            // - OnItemUnlocked MUST NOT fire a second time!
            // - Total item unlock events for "PersistentItem" must remain strictly 1.

            var service = new InventorySyncService();
            int unlockEventCount = 0;
            service.OnItemUnlocked += (_, _) => unlockEventCount++;

            var earlyTx = Guid.NewGuid();
            var earlyPacket = new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlockSync, 3, 1, 1000),
                ItemId = "PersistentItem",
                Category = ItemCategory.Crest,
                TransactionId = earlyTx,
                Timestamp = 1000
            };

            service.HandleItemUnlockPacket(new NetworkPeerId(1), earlyPacket);
            Assert.Equal(1, unlockEventCount);
            Assert.True(service.IsTransactionProcessed(earlyTx));

            // Process 501 new distinct items to evict earlyTx
            for (int i = 0; i < 501; i++)
            {
                var fillerPacket = new ItemUnlockPacket
                {
                    Header = new PacketHeader(PacketType.ItemUnlockSync, 3, (uint)(2 + i), (uint)(2000 + i)),
                    ItemId = $"FillerItem_{i}",
                    Category = ItemCategory.Tool,
                    TransactionId = Guid.NewGuid(),
                    Timestamp = (uint)(2000 + i)
                };
                service.HandleItemUnlockPacket(new NetworkPeerId(1), fillerPacket);
            }

            Assert.Equal(502, unlockEventCount);
            // Verify earlyTx was evicted from the transaction buffer
            Assert.False(service.IsTransactionProcessed(earlyTx));

            // NOW: Delayed retransmission of earlyPacket arrives!
            service.HandleItemUnlockPacket(new NetworkPeerId(1), earlyPacket);

            // Invariant: unlockEventCount MUST NOT increment, because "PersistentItem" was already unlocked!
            Assert.Equal(502, unlockEventCount);
            Assert.True(service.IsItemUnlocked("PersistentItem"));
        }

        [Fact]
        public void Stress_FifoRingBuffer_StepByStepEvictionBoundaries()
        {
            // Detailed step-by-step verification at counts 500, 501, 502
            var service = new InventorySyncService();
            var txs = new List<Guid>();

            // Fill exactly 500 items
            for (int i = 0; i < 500; i++)
            {
                var tx = Guid.NewGuid();
                txs.Add(tx);
                service.HandleItemUnlockPacket(new NetworkPeerId(1), new ItemUnlockPacket
                {
                    Header = new PacketHeader(PacketType.ItemUnlockSync, 3, (uint)i, 100),
                    ItemId = $"StepItem_{i}",
                    Category = ItemCategory.Tool,
                    TransactionId = tx,
                    Timestamp = 100
                });
            }

            // At 500 items, ALL 500 should be present
            for (int i = 0; i < 500; i++)
            {
                Assert.True(service.IsTransactionProcessed(txs[i]), $"Item {i} should be present at size 500");
            }

            // Add 501st item
            var tx500 = Guid.NewGuid();
            txs.Add(tx500);
            service.HandleItemUnlockPacket(new NetworkPeerId(1), new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlockSync, 3, 500, 100),
                ItemId = "StepItem_500",
                Category = ItemCategory.Tool,
                TransactionId = tx500,
                Timestamp = 100
            });

            // Now item 0 should be evicted, 1..500 present
            Assert.False(service.IsTransactionProcessed(txs[0]), "Item 0 must be evicted at 501 items");
            Assert.True(service.IsTransactionProcessed(txs[1]), "Item 1 must still be present");
            Assert.True(service.IsTransactionProcessed(tx500), "Item 500 must be present");

            // Add 502nd item
            var tx501 = Guid.NewGuid();
            txs.Add(tx501);
            service.HandleItemUnlockPacket(new NetworkPeerId(1), new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlockSync, 3, 501, 100),
                ItemId = "StepItem_501",
                Category = ItemCategory.Tool,
                TransactionId = tx501,
                Timestamp = 100
            });

            // Now item 1 should also be evicted
            Assert.False(service.IsTransactionProcessed(txs[1]), "Item 1 must be evicted at 502 items");
            Assert.True(service.IsTransactionProcessed(txs[2]), "Item 2 must still be present");
            Assert.True(service.IsTransactionProcessed(tx501), "Item 501 must be present");
        }

        #endregion

        #region 4. Malformed Packets, Empty GUIDs, Boundary Item ID Strings

        [Fact]
        public void Boundary_EmptyGuid_BothLocalAndRemote_NeverProcessesOrBroadcasts()
        {
            var service = new InventorySyncService();
            int localCallbacks = 0;
            int outgoingPackets = 0;
            service.OnItemUnlocked += (_, _) => localCallbacks++;
            service.OnOutgoingItemUnlockPacket += _ => outgoingPackets++;

            // Local with empty GUID
            service.OnLocalItemDiscovered("Item_EmptyGuid", ItemCategory.Tool, Guid.Empty);

            Assert.Equal(0, localCallbacks);
            Assert.Equal(0, outgoingPackets);
            Assert.False(service.IsItemUnlocked("Item_EmptyGuid"));

            // Remote with empty GUID
            var malformedPacket = new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlockSync, 3, 1, 100),
                ItemId = "Remote_EmptyGuid",
                Category = ItemCategory.Crest,
                TransactionId = Guid.Empty,
                Timestamp = 100
            };

            service.HandleItemUnlockPacket(new NetworkPeerId(1), malformedPacket);

            Assert.Equal(0, localCallbacks);
            Assert.False(service.IsItemUnlocked("Remote_EmptyGuid"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t\r\n")]
        [InlineData(null)]
        public void Boundary_InvalidItemIdStrings_DiscardedWithoutException(string? invalidId)
        {
            var service = new InventorySyncService();
            int callbacks = 0;
            int outgoing = 0;
            service.OnItemUnlocked += (_, _) => callbacks++;
            service.OnOutgoingItemUnlockPacket += _ => outgoing++;

            service.OnLocalItemDiscovered(invalidId!, ItemCategory.Tool);

            var packet = new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlockSync, 3, 1, 100),
                ItemId = invalidId!,
                Category = ItemCategory.Tool,
                TransactionId = Guid.NewGuid(),
                Timestamp = 100
            };
            service.HandleItemUnlockPacket(new NetworkPeerId(1), packet);

            Assert.Equal(0, callbacks);
            Assert.Equal(0, outgoing);
            Assert.Empty(service.UnlockedItems);
        }

        [Fact]
        public void Boundary_Unicode_Emojis_And_SpecialCharacters_FullMirrorRoundtrip()
        {
            // Test non-ASCII item IDs:
            // - Cyrillic
            // - Chinese (CJK)
            // - Emojis
            // - Symbols, quotes, slashes, control tags
            using var harness = new DualClientTestHarness();
            var complexIds = new[]
            {
                "Шелковое_Копье_Жнеца",
                "Знак_Ткача_Древних",
                "骨钉技艺_大风车",
                "飞针_三连发",
                "🗡️✨Hornet_Needle_Apex👑",
                "Tool<Custom>&\"quoted\"/path\\escaped#123",
                "A", // single char
                new string('Z', 250) // long string
            };

            foreach (var id in complexIds)
            {
                harness.HostInventory.OnLocalItemDiscovered(id, ItemCategory.Tool);
            }

            harness.DeliverAll();

            foreach (var id in complexIds)
            {
                Assert.True(harness.HostInventory.IsItemUnlocked(id), $"Host missing {id}");
                Assert.True(harness.ClientInventory.IsItemUnlocked(id), $"Client missing {id}");
            }

            Assert.Equal(complexIds.Length, harness.DeliveredItemUnlocksToClient.Count);
        }

        [Fact]
        public void Boundary_BinaryDeserialization_TruncatedBuffers_SafeExceptions()
        {
            var packet = new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlockSync, 3, 10, 5000),
                ItemId = "ComplexItemName_ForUnderflowTest",
                Category = ItemCategory.Ability,
                TransactionId = Guid.NewGuid(),
                Timestamp = 5000
            };

            byte[] fullBuffer = new byte[256];
            var writer = new PacketWriter(fullBuffer);
            packet.Serialize(ref writer);
            int validLength = writer.Position;

            // Every truncated length from 0 to validLength - 1 must throw an underflow exception
            // without crashing the CLR
            for (int len = 0; len < validLength; len++)
            {
                bool handledOrUnderflow = false;
                try
                {
                    var reader = new PacketReader(new ReadOnlySpan<byte>(fullBuffer, 0, len));
                    ItemUnlockPacket.Deserialize(ref reader);
                }
                catch (InvalidOperationException)
                {
                    handledOrUnderflow = true;
                }
                catch (ArgumentException)
                {
                    handledOrUnderflow = true;
                }

                Assert.True(handledOrUnderflow, $"Truncated length {len}/{validLength} did not trigger safe underflow handling.");
            }
        }

        [Fact]
        public void Boundary_DuplicateTransactionId_DifferentItemIds_DropsSecondPacket()
        {
            // Security/protocol check: If a malicious or buggy peer sends 2 different item names
            // using the EXACT SAME transaction GUID, the transaction deduplication filter must
            // drop the second packet.
            var service = new InventorySyncService();
            var sharedTx = Guid.NewGuid();

            var packetA = new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlockSync, 3, 1, 100),
                ItemId = "Item_Legitimate",
                Category = ItemCategory.Tool,
                TransactionId = sharedTx,
                Timestamp = 100
            };

            var packetB = new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlockSync, 3, 2, 200),
                ItemId = "Item_SpoofedWithSameTx",
                Category = ItemCategory.Tool,
                TransactionId = sharedTx,
                Timestamp = 200
            };

            service.HandleItemUnlockPacket(new NetworkPeerId(1), packetA);
            service.HandleItemUnlockPacket(new NetworkPeerId(1), packetB);

            Assert.True(service.IsItemUnlocked("Item_Legitimate"));
            Assert.False(service.IsItemUnlocked("Item_SpoofedWithSameTx"));
            Assert.Single(service.UnlockedItems);
        }

        [Fact]
        public void Boundary_AlreadyUnlockedItem_LocalRediscovery_EmitsZeroPackets()
        {
            using var harness = new DualClientTestHarness();
            int outgoingPackets = 0;
            harness.HostInventory.OnOutgoingItemUnlockPacket += _ => outgoingPackets++;

            // First discovery: transmits packet
            harness.HostInventory.OnLocalItemDiscovered("Tool_Boomerang", ItemCategory.Tool);
            Assert.Equal(1, outgoingPackets);

            // Rediscovery with exact same ID
            harness.HostInventory.OnLocalItemDiscovered("Tool_Boomerang", ItemCategory.Tool);
            Assert.Equal(1, outgoingPackets); // Still 1

            // Rediscovery with different case (case-insensitivity invariant)
            harness.HostInventory.OnLocalItemDiscovered("TOOL_BOOMERANG", ItemCategory.Tool);
            harness.HostInventory.OnLocalItemDiscovered("tool_boomerang", ItemCategory.Tool);
            Assert.Equal(1, outgoingPackets); // Still 1
        }

        [Fact]
        public void Boundary_NegativeAndZeroNumericItemIds_HandledCleanly()
        {
            using var harness = new DualClientTestHarness();

            harness.HostInventory.OnLocalItemDiscovered(0, ItemCategory.Tool);
            harness.HostInventory.OnLocalItemDiscovered(-999, ItemCategory.Tool);
            harness.DeliverAll();

            Assert.True(harness.HostInventory.IsItemUnlocked("0"));
            Assert.True(harness.HostInventory.IsItemUnlocked("-999"));
            Assert.True(harness.ClientInventory.IsItemUnlocked("0"));
            Assert.True(harness.ClientInventory.IsItemUnlocked("-999"));
        }

        #endregion
    }
}
