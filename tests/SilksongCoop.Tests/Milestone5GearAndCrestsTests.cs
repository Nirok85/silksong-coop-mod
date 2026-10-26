using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using SilksongCoop.Tests.TestHarness;
using Xunit;

namespace SilksongCoop.Tests
{
    public class Milestone5GearAndCrestsTests
    {
        #region Tier 1: Feature Tests (F27 Discovery Mirroring)

        [Fact]
        public void F27_CrestDiscoveryMirror_HostToClient()
        {
            using var harness = new DualClientTestHarness();
            string? clientReceivedId = null;
            ItemCategory? clientReceivedCategory = null;

            harness.ClientInventory.OnItemUnlocked += (id, cat) =>
            {
                clientReceivedId = id;
                clientReceivedCategory = cat;
            };

            harness.HostInventory.OnLocalItemDiscovered("Crest_Hunter", ItemCategory.Crest);
            harness.DeliverAll();

            Assert.True(harness.HostInventory.IsItemUnlocked("Crest_Hunter"));
            Assert.True(harness.ClientInventory.IsItemUnlocked("Crest_Hunter"));
            Assert.Equal("Crest_Hunter", clientReceivedId);
            Assert.Equal(ItemCategory.Crest, clientReceivedCategory);
            Assert.Single(harness.DeliveredItemUnlocksToClient);
            Assert.Equal(3, harness.DeliveredItemUnlocksToClient[0].Header.ChannelId);
        }

        [Fact]
        public void F27_CrestDiscoveryMirror_ClientToHost()
        {
            using var harness = new DualClientTestHarness();
            string? hostReceivedId = null;

            harness.HostInventory.OnItemUnlocked += (id, cat) => hostReceivedId = id;

            harness.ClientInventory.OnLocalItemDiscovered("Crest_Weaver", ItemCategory.Crest);
            harness.DeliverAll();

            Assert.True(harness.HostInventory.IsItemUnlocked("Crest_Weaver"));
            Assert.True(harness.ClientInventory.IsItemUnlocked("Crest_Weaver"));
            Assert.Equal("Crest_Weaver", hostReceivedId);
            Assert.Single(harness.DeliveredItemUnlocksToHost);
        }

        [Fact]
        public void F27_ToolDiscoveryMirror_HostToClient()
        {
            using var harness = new DualClientTestHarness();
            harness.HostInventory.OnLocalItemDiscovered("Tool_RosaryCannon", ItemCategory.Tool);
            harness.DeliverAll();

            Assert.True(harness.HostInventory.IsItemUnlocked("Tool_RosaryCannon"));
            Assert.True(harness.ClientInventory.IsItemUnlocked("Tool_RosaryCannon"));
        }

        [Fact]
        public void F27_ToolDiscoveryMirror_ClientToHost()
        {
            using var harness = new DualClientTestHarness();
            harness.ClientInventory.OnLocalItemDiscovered("Tool_Pimpillo", ItemCategory.Tool);
            harness.DeliverAll();

            Assert.True(harness.HostInventory.IsItemUnlocked("Tool_Pimpillo"));
            Assert.True(harness.ClientInventory.IsItemUnlocked("Tool_Pimpillo"));
        }

        [Fact]
        public void F27_AbilityDiscoveryMirror_Bidirectional()
        {
            using var harness = new DualClientTestHarness();

            harness.HostInventory.OnLocalItemDiscovered("Ability_SilkDash", ItemCategory.Ability);
            harness.ClientInventory.OnLocalItemDiscovered("Ability_HarpoonClimb", ItemCategory.Ability);
            harness.DeliverAll();

            Assert.True(harness.HostInventory.IsItemUnlocked("Ability_SilkDash"));
            Assert.True(harness.HostInventory.IsItemUnlocked("Ability_HarpoonClimb"));
            Assert.True(harness.ClientInventory.IsItemUnlocked("Ability_SilkDash"));
            Assert.True(harness.ClientInventory.IsItemUnlocked("Ability_HarpoonClimb"));
        }

        [Fact]
        public void F27_KeyItemDiscoveryMirror_Bidirectional()
        {
            using var harness = new DualClientTestHarness();
            harness.HostInventory.OnLocalItemDiscovered("KeyItem_BellTowerKey", ItemCategory.KeyItem);
            harness.DeliverAll();

            Assert.True(harness.ClientInventory.IsItemUnlocked("KeyItem_BellTowerKey"));
        }

        [Fact]
        public void F27_MultipleSequentialDiscoveries_AllCategoriesPreserved()
        {
            using var harness = new DualClientTestHarness();
            var items = new (string Id, ItemCategory Cat)[]
            {
                ("Crest_Reaper", ItemCategory.Crest),
                ("Tool_StraightPin", ItemCategory.Tool),
                ("Tool_StingShard", ItemCategory.Tool),
                ("Ability_NeedleThrow", ItemCategory.Ability),
                ("KeyItem_OldCompass", ItemCategory.KeyItem)
            };

            foreach (var (id, cat) in items)
            {
                harness.HostInventory.OnLocalItemDiscovered(id, cat);
            }
            harness.DeliverAll();

            foreach (var (id, _) in items)
            {
                Assert.True(harness.HostInventory.IsItemUnlocked(id));
                Assert.True(harness.ClientInventory.IsItemUnlocked(id));
            }
            Assert.Equal(5, harness.DeliveredItemUnlocksToClient.Count);
        }

        [Fact]
        public void F27_NumericItemIdOverload_MirrorsSuccessfully()
        {
            using var harness = new DualClientTestHarness();
            harness.HostInventory.OnLocalItemDiscovered(1042, ItemCategory.Tool);
            harness.DeliverAll();

            Assert.True(harness.HostInventory.IsItemUnlocked("1042"));
            Assert.True(harness.ClientInventory.IsItemUnlocked("1042"));
        }

        #endregion

        #region Tier 2: Invariant Tests (F26 Loadout Isolation)

        [Fact]
        public void F26_Invariant_ContractProperty_IsEquippedLoadoutIsolated_IsTrue()
        {
            var service = new InventorySyncService();
            Assert.True(service.IsEquippedLoadoutIsolated);
        }

        [Fact]
        public void F26_Invariant_EquippedCrests_RemainStrictlyIsolated()
        {
            using var harness = new DualClientTestHarness();

            harness.HostInventory.SetLocalEquippedCrest("Crest_Hunter");
            harness.ClientInventory.SetLocalEquippedCrest("Crest_Architect");
            harness.DeliverAll();

            Assert.Equal("Crest_Hunter", harness.HostInventory.LocalEquippedCrest);
            Assert.Equal("Crest_Architect", harness.ClientInventory.LocalEquippedCrest);
            Assert.Empty(harness.DeliveredItemUnlocksToClient);
            Assert.Empty(harness.DeliveredItemUnlocksToHost);
        }

        [Fact]
        public void F26_Invariant_EquippedTools_RemainStrictlyIsolated()
        {
            using var harness = new DualClientTestHarness();

            harness.HostInventory.SetLocalEquippedTools(new[] { "Tool_StingShard", "Tool_StraightPin" });
            harness.ClientInventory.SetLocalEquippedTools(new[] { "Tool_Pimpillo", "Tool_CurveClaw" });
            harness.DeliverAll();

            Assert.Equal(2, harness.HostInventory.LocalEquippedTools.Count);
            Assert.Contains("Tool_StingShard", harness.HostInventory.LocalEquippedTools);
            Assert.DoesNotContain("Tool_StingShard", harness.ClientInventory.LocalEquippedTools);

            Assert.Equal(2, harness.ClientInventory.LocalEquippedTools.Count);
            Assert.Contains("Tool_Pimpillo", harness.ClientInventory.LocalEquippedTools);
            Assert.DoesNotContain("Tool_Pimpillo", harness.HostInventory.LocalEquippedTools);

            Assert.Empty(harness.DeliveredItemUnlocksToClient);
            Assert.Empty(harness.DeliveredItemUnlocksToHost);
        }

        [Fact]
        public void F26_Invariant_UnequippingCrestOrTools_DoesNotAffectRemote()
        {
            using var harness = new DualClientTestHarness();

            harness.HostInventory.SetLocalEquippedCrest("Crest_Hunter");
            harness.ClientInventory.SetLocalEquippedCrest("Crest_Weaver");

            // Host unequips crest
            harness.HostInventory.SetLocalEquippedCrest(string.Empty);
            harness.DeliverAll();

            Assert.Equal(string.Empty, harness.HostInventory.LocalEquippedCrest);
            Assert.Equal("Crest_Weaver", harness.ClientInventory.LocalEquippedCrest);
        }

        [Fact]
        public void F26_Invariant_EquippingActions_EmitZeroPacketsOnChannel3()
        {
            using var harness = new DualClientTestHarness();
            int outgoingPackets = 0;
            harness.HostInventory.OnOutgoingItemUnlockPacket += _ => outgoingPackets++;

            harness.HostInventory.SetLocalEquippedCrest("Crest_Hunter");
            harness.HostInventory.SetLocalEquippedTools(new[] { "Tool_Pin1", "Tool_Pin2" });

            Assert.Equal(0, outgoingPackets);
        }

        [Fact]
        public void F26_Invariant_ItemUnlock_DoesNotAutoEquipOnRemotePlayer()
        {
            using var harness = new DualClientTestHarness();
            harness.ClientInventory.SetLocalEquippedCrest("Crest_Default");

            harness.HostInventory.OnLocalItemDiscovered("Crest_Reaper", ItemCategory.Crest);
            harness.DeliverAll();

            Assert.True(harness.ClientInventory.IsItemUnlocked("Crest_Reaper"));
            Assert.Equal("Crest_Default", harness.ClientInventory.LocalEquippedCrest);
        }

        #endregion

        #region Tier 3: Boundary & Deduplication Tests

        [Fact]
        public void Boundary_DuplicatePacket_SameTransactionId_FiresEventOnlyOnce()
        {
            var service = new InventorySyncService();
            int unlockEvents = 0;
            service.OnItemUnlocked += (_, _) => unlockEvents++;

            var tx = Guid.NewGuid();
            var packet = new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlockSync, 3, 1, 1000),
                ItemId = "Tool_RosaryCannon",
                Category = ItemCategory.Tool,
                TransactionId = tx,
                Timestamp = 1000
            };

            service.HandleItemUnlockPacket(new NetworkPeerId(1), packet);
            service.HandleItemUnlockPacket(new NetworkPeerId(1), packet); // Duplicate transmission

            Assert.Equal(1, unlockEvents);
            Assert.True(service.IsTransactionProcessed(tx));
        }

        [Fact]
        public void Boundary_SimultaneousDiscovery_SameItemId_DifferentGuids_Deduplicated()
        {
            using var harness = new DualClientTestHarness();
            int hostUnlockEvents = 0;
            int clientUnlockEvents = 0;

            harness.HostInventory.OnItemUnlocked += (_, _) => hostUnlockEvents++;
            harness.ClientInventory.OnItemUnlocked += (_, _) => clientUnlockEvents++;

            // Simultaneous local discoveries
            harness.HostInventory.OnLocalItemDiscovered("Tool_Compass", ItemCategory.Tool);
            harness.ClientInventory.OnLocalItemDiscovered("Tool_Compass", ItemCategory.Tool);

            // Transmit packets
            harness.DeliverAll();

            // Each client should have fired OnItemUnlocked exactly once (on their own local discovery)
            Assert.Equal(1, hostUnlockEvents);
            Assert.Equal(1, clientUnlockEvents);
            Assert.True(harness.HostInventory.IsItemUnlocked("Tool_Compass"));
            Assert.True(harness.ClientInventory.IsItemUnlocked("Tool_Compass"));
        }

        [Fact]
        public void Boundary_EmptyTransactionGuid_DiscardedCleanly()
        {
            var service = new InventorySyncService();
            int events = 0;
            service.OnItemUnlocked += (_, _) => events++;

            var packet = new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlockSync, 3, 1, 1000),
                ItemId = "Tool_StingShard",
                Category = ItemCategory.Tool,
                TransactionId = Guid.Empty,
                Timestamp = 1000
            };

            service.HandleItemUnlockPacket(new NetworkPeerId(1), packet);

            Assert.Equal(0, events);
            Assert.False(service.IsItemUnlocked("Tool_StingShard"));
        }

        [Fact]
        public void Boundary_NullOrWhitespaceItemId_DiscardedCleanly()
        {
            var service = new InventorySyncService();
            int events = 0;
            service.OnItemUnlocked += (_, _) => events++;

            service.OnLocalItemDiscovered(string.Empty, ItemCategory.Tool);
            service.OnLocalItemDiscovered("   ", ItemCategory.Tool);
            service.OnLocalItemDiscovered(null!, ItemCategory.Tool);

            Assert.Equal(0, events);
            Assert.Empty(service.UnlockedItems);
        }

        [Fact]
        public void Boundary_EchoLoopPrevention_RemoteUnlockDoesNotRebroadcast()
        {
            using var harness = new DualClientTestHarness();

            harness.HostInventory.OnLocalItemDiscovered("Tool_Harpoon", ItemCategory.Tool);
            harness.DeliverAll();

            // Host sent 1 packet to Client. Client must NOT send any packet back to Host.
            Assert.Single(harness.DeliveredItemUnlocksToClient);
            Assert.Empty(harness.DeliveredItemUnlocksToHost);
        }

        [Fact]
        public void Boundary_CaseInsensitiveItemMatching_TreatedAsDuplicate()
        {
            var service = new InventorySyncService();
            int events = 0;
            service.OnItemUnlocked += (_, _) => events++;

            service.OnLocalItemDiscovered("crest_hunter", ItemCategory.Crest);
            service.OnLocalItemDiscovered("CREST_HUNTER", ItemCategory.Crest);

            Assert.Equal(1, events);
            Assert.True(service.IsItemUnlocked("Crest_Hunter"));
        }

        [Fact]
        public void Boundary_TransactionRingBuffer_EvictsOldestAfter500()
        {
            var service = new InventorySyncService();
            var guids = new List<Guid>();

            for (int i = 0; i < 550; i++)
            {
                var g = Guid.NewGuid();
                guids.Add(g);
                var packet = new ItemUnlockPacket
                {
                    Header = new PacketHeader(PacketType.ItemUnlockSync, 3, (uint)i, 1000),
                    ItemId = $"Item_{i}",
                    Category = ItemCategory.Tool,
                    TransactionId = g,
                    Timestamp = 1000
                };
                service.HandleItemUnlockPacket(new NetworkPeerId(1), packet);
            }

            // Oldest GUID should have been evicted
            Assert.False(service.IsTransactionProcessed(guids[0]));
            // Recent GUID should still be tracked
            Assert.True(service.IsTransactionProcessed(guids[549]));
        }

        [Fact]
        public void Boundary_OutOfRangeCategoryEnum_HandledSafely()
        {
            var service = new InventorySyncService();
            ItemCategory? observedCategory = null;
            service.OnItemUnlocked += (_, cat) => observedCategory = cat;

            var packet = new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlockSync, 3, 1, 1000),
                ItemId = "CustomItem",
                Category = (ItemCategory)99,
                TransactionId = Guid.NewGuid(),
                Timestamp = 1000
            };

            service.HandleItemUnlockPacket(new NetworkPeerId(1), packet);

            Assert.True(service.IsItemUnlocked("CustomItem"));
            Assert.Equal((ItemCategory)99, observedCategory);
        }

        #endregion

        #region Tier 4: Concurrency, Network Stress & Serialization

        [Fact]
        public async Task Stress_ConcurrentLocalAndRemoteUnlocks_NoDataRace()
        {
            var service = new InventorySyncService();
            int count = 100;
            var tasks = new List<Task>();

            for (int i = 0; i < count; i++)
            {
                int index = i;
                tasks.Add(Task.Run(() =>
                {
                    service.OnLocalItemDiscovered($"Local_{index}", ItemCategory.Tool);
                }));

                tasks.Add(Task.Run(() =>
                {
                    service.HandleItemUnlockPacket(new NetworkPeerId(2), new ItemUnlockPacket
                    {
                        Header = new PacketHeader(PacketType.ItemUnlockSync, 3, (uint)index, 1000),
                        ItemId = $"Remote_{index}",
                        Category = ItemCategory.Crest,
                        TransactionId = Guid.NewGuid(),
                        Timestamp = 1000
                    });
                }));
            }

            await Task.WhenAll(tasks);

            Assert.Equal(count * 2, service.UnlockedItems.Count);
        }

        [Fact]
        public void Stress_SimulatedPacketLossAndJitter_AllReliableOrderedItemsArrive()
        {
            using var harness = new DualClientTestHarness(latencyMs: 15, jitterMs: 5, lossRate: 0.15f);

            for (int i = 0; i < 20; i++)
            {
                harness.HostInventory.OnLocalItemDiscovered($"BurstTool_{i}", ItemCategory.Tool);
            }

            // Deliver across simulated network with losses
            harness.DeliverAll(maxSteps: 200);

            for (int i = 0; i < 20; i++)
            {
                Assert.True(harness.ClientInventory.IsItemUnlocked($"BurstTool_{i}"));
            }
        }

        [Fact]
        public void Stress_ItemUnlockPacket_TruncatedPayloads_ThrowCleanUnderflow()
        {
            var packet = new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlockSync, 3, 10, 5000),
                ItemId = "Tool_RosaryCannon",
                Category = ItemCategory.Tool,
                TransactionId = Guid.NewGuid(),
                Timestamp = 5000
            };

            Span<byte> buffer = stackalloc byte[128];
            var writer = new PacketWriter(buffer);
            packet.Serialize(ref writer);
            int totalLen = writer.Position;

            // Header is 12 bytes; truncated slices from 12 to totalLen - 1 must throw InvalidOperationException
            for (int len = PacketHeader.HeaderSize; len < totalLen; len++)
            {
                bool threw = false;
                try
                {
                    var reader = new PacketReader(buffer.Slice(0, len));
                    ItemUnlockPacket.Deserialize(ref reader);
                }
                catch (InvalidOperationException)
                {
                    threw = true;
                }
                Assert.True(threw, $"Slice length {len}/{totalLen} should throw buffer underflow exception.");
            }
        }

        #endregion
    }
}
