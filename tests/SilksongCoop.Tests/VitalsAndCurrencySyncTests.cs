using System;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests
{
    public class VitalsAndCurrencySyncTests
    {
        [Fact]
        public void LocalDamage_ModifiesLocalHealth_DoesNotAffectRemoteService()
        {
            var service1 = new VitalsSyncService();
            var service2 = new VitalsSyncService();
            service1.InitializeState(5, 5, 9, 1, 0, 100, 20);
            service2.InitializeState(5, 5, 9, 1, 0, 100, 20);

            // Player 1 takes 2 damage
            service1.OnLocalDamageTaken(2, 3);

            Assert.Equal(3, service1.LocalHealth);
            Assert.Equal(5, service2.LocalHealth); // Player 2 HP is unchanged!
        }

        [Fact]
        public void LocalSilkUsed_ModifiesLocalSilk_DoesNotAffectRemoteService()
        {
            var service1 = new VitalsSyncService();
            var service2 = new VitalsSyncService();
            service1.InitializeState(5, 5, 9, 1, 0, 100, 20);
            service2.InitializeState(5, 5, 9, 1, 0, 100, 20);

            // Player 1 casts a skill using 4 silk
            service1.OnLocalSilkUsed(4, 5);

            Assert.Equal(5, service1.LocalSilk);
            Assert.Equal(9, service2.LocalSilk); // Player 2 Silk is untouched!
        }

        [Fact]
        public void MaxHealthUpgrade_ReplicatesAcrossClients()
        {
            var hostService = new VitalsSyncService();
            var clientService = new VitalsSyncService();
            hostService.InitializeState(5, 5, 9, 1, 0, 0, 0);
            clientService.InitializeState(5, 5, 9, 1, 0, 0, 0);

            VitalsSyncPacket? sentPacket = null;
            hostService.OnOutgoingVitalsBroadcast += packet => sentPacket = packet;

            // Host finds 4th mask shard -> Max HP increases to 6
            hostService.OnLocalMaxHealthUpgrade(6);

            Assert.Equal(6, hostService.SharedMaxHealth);
            Assert.NotNull(sentPacket);
            Assert.Equal(6, sentPacket.Value.MaxHealth);

            // Client receives host's packet
            clientService.HandleVitalsPacket(new NetworkPeerId(1), sentPacket.Value);
            Assert.Equal(6, clientService.SharedMaxHealth); // Client's max health upgraded!
        }

        [Fact]
        public void NeedleUpgrade_ReplicatesToPartner()
        {
            var host = new VitalsSyncService();
            var client = new VitalsSyncService();
            host.InitializeState(5, 5, 9, 1, 0, 0, 0);
            client.InitializeState(5, 5, 9, 1, 0, 0, 0);

            VitalsSyncPacket? captured = null;
            client.OnOutgoingVitalsBroadcast += p => captured = p;

            // Client upgrades needle at the forge (level 0 -> 1)
            client.OnLocalNeedleUpgrade(1);

            Assert.Equal(1, client.SharedNeedleUpgradeLevel);
            Assert.NotNull(captured);

            // Host receives upgrade
            host.HandleVitalsPacket(new NetworkPeerId(2), captured.Value);
            Assert.Equal(1, host.SharedNeedleUpgradeLevel);
        }

        [Fact]
        public void CurrencyGain_IncrementsSharedWallet_WithDeduplication()
        {
            var p1 = new VitalsSyncService();
            var p2 = new VitalsSyncService();
            p1.InitializeState(5, 5, 9, 1, 0, 50, 10);
            p2.InitializeState(5, 5, 9, 1, 0, 50, 10);

            CurrencySyncPacket? broadcast = null;
            p1.OnOutgoingCurrencyBroadcast += p => broadcast = p;

            var txId = Guid.NewGuid();
            p1.OnLocalCurrencyChanged(CurrencyType.Rosaries, 25, 75, txId);

            Assert.Equal(75, p1.SharedRosaries);
            Assert.NotNull(broadcast);
            Assert.Equal(25, broadcast.Value.DeltaAmount);
            Assert.Equal(txId, broadcast.Value.TransactionId);

            // P2 receives currency packet
            p2.HandleCurrencyPacket(new NetworkPeerId(1), broadcast.Value);
            Assert.Equal(75, p2.SharedRosaries);

            // Adversarial check: duplicate packet sent over network (replay / network jitter)
            p2.HandleCurrencyPacket(new NetworkPeerId(1), broadcast.Value);
            Assert.Equal(75, p2.SharedRosaries); // Still 75, NOT 100! Deduplication succeeded!
        }

        [Fact]
        public void ShellShards_SynchronizeCorrectly()
        {
            var p1 = new VitalsSyncService();
            var p2 = new VitalsSyncService();
            p1.InitializeState(5, 5, 9, 1, 0, 0, 15);
            p2.InitializeState(5, 5, 9, 1, 0, 0, 15);

            CurrencySyncPacket? broadcast = null;
            p2.OnOutgoingCurrencyBroadcast += p => broadcast = p;

            var tx = Guid.NewGuid();
            p2.OnLocalCurrencyChanged(CurrencyType.ShellShards, 10, 25, tx);

            Assert.Equal(25, p2.SharedShellShards);
            Assert.NotNull(broadcast);

            p1.HandleCurrencyPacket(new NetworkPeerId(2), broadcast.Value);
            Assert.Equal(25, p1.SharedShellShards);
        }

        [Fact]
        public void MaxSilkUpgrade_ReplicatesAcrossClients()
        {
            var hostService = new VitalsSyncService();
            var clientService = new VitalsSyncService();
            hostService.InitializeState(5, 5, 9, 1, 0, 0, 0);
            clientService.InitializeState(5, 5, 9, 1, 0, 0, 0);

            VitalsSyncPacket? sentPacket = null;
            hostService.OnOutgoingVitalsBroadcast += packet => sentPacket = packet;

            // Host completes 2nd silk spool -> MaxSilkSpools increases to 2
            hostService.OnLocalMaxSilkUpgrade(2);

            Assert.Equal(2, hostService.SharedMaxSilkSpools);
            Assert.NotNull(sentPacket);
            Assert.Equal(2, sentPacket.Value.MaxSilkSpools);

            // Client receives host's packet
            clientService.HandleVitalsPacket(new NetworkPeerId(1), sentPacket.Value);
            Assert.Equal(2, clientService.SharedMaxSilkSpools);
        }
    }
}
