using System;
using System.Collections.Generic;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests
{
    public class InventorySyncTests
    {
        [Fact]
        public void EquippedLoadout_RemainsStrictlyIndependent()
        {
            var p1 = new InventorySyncService();
            var p2 = new InventorySyncService();

            p1.SetLocalEquippedCrest("Crest_Hunter");
            p1.SetLocalEquippedTools(new[] { "Tool_StingShard", "Tool_StraightPin" });

            p2.SetLocalEquippedCrest("Crest_Architect");
            p2.SetLocalEquippedTools(new[] { "Tool_Pimpillo", "Tool_CurveClaw" });

            // Equipped crests remain independent
            Assert.Equal("Crest_Hunter", p1.LocalEquippedCrest);
            Assert.Equal("Crest_Architect", p2.LocalEquippedCrest);

            // Equipped tool slots remain independent
            Assert.Contains("Tool_StingShard", p1.LocalEquippedTools);
            Assert.DoesNotContain("Tool_StingShard", p2.LocalEquippedTools);
            Assert.Contains("Tool_Pimpillo", p2.LocalEquippedTools);
        }

        [Fact]
        public void LocalItemDiscovery_BroadcastsPacketAndUnlocksLocally()
        {
            var p1 = new InventorySyncService();
            var p2 = new InventorySyncService();

            ItemUnlockPacket? sentPacket = null;
            p1.OnOutgoingItemUnlockPacket += p => sentPacket = p;

            p1.OnLocalItemDiscovered("Tool_RosaryCannon", ItemCategory.Tool);

            Assert.True(p1.IsItemUnlocked("Tool_RosaryCannon"));
            Assert.NotNull(sentPacket);
            Assert.Equal("Tool_RosaryCannon", sentPacket.Value.ItemId);
            Assert.Equal(ItemCategory.Tool, sentPacket.Value.Category);

            // Remote partner receives packet
            string? unlockedOnP2 = null;
            p2.OnItemUnlocked += (id, cat) => unlockedOnP2 = id;

            p2.HandleItemUnlockPacket(new NetworkPeerId(1), sentPacket.Value);

            Assert.True(p2.IsItemUnlocked("Tool_RosaryCannon"));
            Assert.Equal("Tool_RosaryCannon", unlockedOnP2);
        }

        [Fact]
        public void CrestDiscovery_UnlocksForBothPlayers()
        {
            var host = new InventorySyncService();
            var client = new InventorySyncService();

            ItemUnlockPacket? packet = null;
            host.OnOutgoingItemUnlockPacket += p => packet = p;

            host.OnLocalItemDiscovered("Crest_Weaver", ItemCategory.Crest);

            Assert.True(host.IsItemUnlocked("Crest_Weaver"));
            Assert.NotNull(packet);

            client.HandleItemUnlockPacket(new NetworkPeerId(1), packet.Value);
            Assert.True(client.IsItemUnlocked("Crest_Weaver"));
        }

        [Fact]
        public void DuplicateItemUnlock_FiresEventOnlyOnce()
        {
            var service = new InventorySyncService();
            int unlockEvents = 0;
            service.OnItemUnlocked += (id, cat) => unlockEvents++;

            service.OnLocalItemDiscovered("Tool_Compass", ItemCategory.Tool);
            service.OnLocalItemDiscovered("Tool_Compass", ItemCategory.Tool); // duplicate

            Assert.Equal(1, unlockEvents);
        }
    }
}
