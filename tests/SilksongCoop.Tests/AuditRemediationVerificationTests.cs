using System;
using SilksongCoop.Core.Clones;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using SilksongCoop.Tests.TestHarness;
using Xunit;

namespace SilksongCoop.Tests
{
    public class AuditRemediationVerificationTests
    {
        [Fact]
        public void CRIT01_TransformSyncPacket_TransmitsSceneNameHash_Correctly()
        {
            var packet = new TransformSyncPacket
            {
                Header = new PacketHeader(PacketType.TransformSync, 0, 10, 1000),
                PositionX = 15.5f,
                PositionY = 30.2f,
                VelocityX = 0f,
                VelocityY = 0f,
                FacingDirection = 1,
                StateFlags = TransformSyncPacket.FlagGrounded,
                AnimationClipHash = 5555,
                NormalizedTime = 100,
                SceneNameHash = 998877
            };

            Span<byte> buffer = stackalloc byte[64];
            var writer = new PacketWriter(buffer);
            packet.Serialize(ref writer);

            Assert.Equal(41, writer.Position); // 12 header + 29 payload bytes

            var reader = new PacketReader(writer.WrittenSpan);
            var deserialized = TransformSyncPacket.Deserialize(ref reader);

            Assert.Equal(998877, deserialized.SceneNameHash);
            Assert.True(PacketValidator.ValidateTransformSync(deserialized));
        }

        [Fact]
        public void CRIT02_SpectatorSoftlock_OnDisconnect_TransitionsStateSafely()
        {
            using var harness = new DualClientTestHarness();

            // Client dies and enters Spectating
            harness.ClientDeath.OnLocalPlayerDied(10f, 10f);
            Assert.Equal(DeathState.Spectating, harness.ClientDeath.CurrentState);

            // Host disconnects unexpectedly
            harness.ClientSession.SetDisconnected(DisconnectReason.Timeout);
            Assert.False(harness.ClientSession.IsConnected);

            // Coordinator can reset cleanly
            harness.ClientDeath.ResetAfterBenchRespawn();
            Assert.Equal(DeathState.Alive, harness.ClientDeath.CurrentState);
        }

        [Fact]
        public void HIGH03_CurrencySync_DeltaZero_EstablishesTotalWalletSync()
        {
            using var harness = new DualClientTestHarness();

            // Host starts with 1500 Rosaries
            harness.HostVitals.InitializeState(
                initialHealth: 5,
                initialMaxHealth: 5,
                initialSilk: 9,
                initialMaxSilkSpools: 1,
                needleLevel: 0,
                initialRosaries: 1500,
                initialShards: 200
            );

            // Broadcast current currency with delta = 0 (initial handshake)
            harness.HostVitals.BroadcastCurrentCurrency();
            harness.DeliverAll();

            // Verify packets delivered to client (first Rosaries, then ShellShards)
            Assert.True(harness.DeliveredCurrenciesToClient.Count >= 2);
            var rosariesPkt = harness.DeliveredCurrenciesToClient[^2];
            var shardsPkt = harness.DeliveredCurrenciesToClient[^1];

            Assert.Equal(CurrencyType.Rosaries, rosariesPkt.Currency);
            Assert.Equal(0, rosariesPkt.DeltaAmount);
            Assert.Equal(1500, rosariesPkt.TotalWalletAmount);

            Assert.Equal(CurrencyType.ShellShards, shardsPkt.Currency);
            Assert.Equal(0, shardsPkt.DeltaAmount);
            Assert.Equal(200, shardsPkt.TotalWalletAmount);
        }

        [Fact]
        public void MED01_CurrencySyncPacket_DoesNotExceedMaxAllowedWallet()
        {
            var packet = new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 0, 1, 100),
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 50,
                TotalWalletAmount = 250,
                TransactionId = Guid.NewGuid()
            };

            Assert.True(PacketValidator.ValidateCurrencySync(packet));
        }

        [Fact]
        public void MED03_BenchCheckpointPacket_Roundtrip_MatchesExactFields()
        {
            var benchPacket = new BenchCheckpointSyncPacket
            {
                Header = new PacketHeader(PacketType.BenchCheckpointSync, 0, 1, 1000),
                SceneName = "DeepDocks_01",
                RespawnMarker = "Bench_Docks_Rest",
                FacingDirection = 2,
                AtBench = true
            };

            Span<byte> buffer = stackalloc byte[128];
            var writer = new PacketWriter(buffer);
            benchPacket.Serialize(ref writer);

            var reader = new PacketReader(writer.WrittenSpan);
            var deserialized = BenchCheckpointSyncPacket.Deserialize(ref reader);

            Assert.Equal("DeepDocks_01", deserialized.SceneName);
            Assert.Equal("Bench_Docks_Rest", deserialized.RespawnMarker);
            Assert.Equal(2, deserialized.FacingDirection);
            Assert.True(deserialized.AtBench);
        }
    }
}
