using System;
using System.Collections.Generic;
using SilksongCoop.Core.Clones;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests.TestHarness
{
    /// <summary>
    /// Headless dual-client simulation harness for testing peer-to-peer co-op replication,
    /// channel segregation, network impairments, and state synchronization without Unity dependencies.
    /// </summary>
    public sealed class DualClientTestHarness : IDisposable
    {
        public MockNetworkTransport HostTransport { get; }
        public MockNetworkTransport ClientTransport { get; }

        public CoopSessionState HostSession { get; }
        public CoopSessionState ClientSession { get; }

        public VitalsSyncService HostVitals { get; }
        public VitalsSyncService ClientVitals { get; }

        public DeathLifecycleCoordinator HostDeath { get; }
        public DeathLifecycleCoordinator ClientDeath { get; }

        public InventorySyncService HostInventory { get; }
        public InventorySyncService ClientInventory { get; }

        public HeadlessPlayerCloneManager HostClones { get; }
        public HeadlessPlayerCloneManager ClientClones { get; }

        // Packet inspection logs
        public List<VitalsSyncPacket> DeliveredVitalsToHost { get; } = new();
        public List<VitalsSyncPacket> DeliveredVitalsToClient { get; } = new();
        public List<CurrencySyncPacket> DeliveredCurrenciesToHost { get; } = new();
        public List<CurrencySyncPacket> DeliveredCurrenciesToClient { get; } = new();
        public List<ActionTriggerPacket> DeliveredActionsToHost { get; } = new();
        public List<ActionTriggerPacket> DeliveredActionsToClient { get; } = new();
        public List<TransformSyncPacket> DeliveredTransformsToHost { get; } = new();
        public List<TransformSyncPacket> DeliveredTransformsToClient { get; } = new();
        public List<DeathLifecyclePacket> DeliveredDeathsToHost { get; } = new();
        public List<DeathLifecyclePacket> DeliveredDeathsToClient { get; } = new();
        public List<ItemUnlockPacket> DeliveredItemUnlocksToHost { get; } = new();
        public List<ItemUnlockPacket> DeliveredItemUnlocksToClient { get; } = new();

        public DualClientTestHarness(int latencyMs = 0, int jitterMs = 0, float lossRate = 0f, int seed = 42)
        {
            var (host, client) = MockNetworkTransport.CreatePair(
                latencyMs: latencyMs,
                jitterMs: jitterMs,
                lossRate: lossRate,
                useVirtualTime: true,
                seed: seed);

            HostTransport = host;
            ClientTransport = client;

            HostSession = new CoopSessionState();
            ClientSession = new CoopSessionState();

            HostSession.SetHosting(HostTransport.LocalPeerId, "SK-TEST-HARNESS");
            ClientSession.SetConnecting(ClientTransport.LocalPeerId, "SK-TEST-HARNESS");

            HostSession.SetConnected(ClientTransport.LocalPeerId);
            ClientSession.SetConnected(HostTransport.LocalPeerId);

            HostVitals = new VitalsSyncService();
            ClientVitals = new VitalsSyncService();

            HostDeath = new DeathLifecycleCoordinator();
            ClientDeath = new DeathLifecycleCoordinator();

            HostInventory = new InventorySyncService();
            ClientInventory = new InventorySyncService();

            HostClones = new HeadlessPlayerCloneManager(targetDelayMs: 50.0);
            ClientClones = new HeadlessPlayerCloneManager(targetDelayMs: 50.0);

            HostClones.SpawnRemotePlayer(ClientTransport.LocalPeerId, isPlayer2: true);
            ClientClones.SpawnRemotePlayer(HostTransport.LocalPeerId, isPlayer2: false);

            WireServices();
        }

        private void WireServices()
        {
            // Host -> Client outgoing wire
            HostVitals.OnOutgoingVitalsBroadcast += packet =>
            {
                Span<byte> buffer = stackalloc byte[64];
                var writer = new PacketWriter(buffer);
                packet.Serialize(ref writer);
                HostTransport.Send(ClientTransport.LocalPeerId, buffer.ToArray(), 0, writer.Position, DeliveryMethod.ReliableOrdered, 2);
            };

            HostVitals.OnOutgoingCurrencyBroadcast += packet =>
            {
                Span<byte> buffer = stackalloc byte[64];
                var writer = new PacketWriter(buffer);
                packet.Serialize(ref writer);
                HostTransport.Send(ClientTransport.LocalPeerId, buffer.ToArray(), 0, writer.Position, DeliveryMethod.ReliableOrdered, 3);
            };

            HostDeath.OnOutgoingDeathPacket += packet =>
            {
                Span<byte> buffer = stackalloc byte[64];
                var writer = new PacketWriter(buffer);
                packet.Serialize(ref writer);
                HostTransport.Send(ClientTransport.LocalPeerId, buffer.ToArray(), 0, writer.Position, DeliveryMethod.ReliableOrdered, 4);
            };

            HostInventory.OnOutgoingItemUnlockPacket += packet =>
            {
                Span<byte> buffer = stackalloc byte[512];
                var writer = new PacketWriter(buffer);
                packet.Serialize(ref writer);
                HostTransport.Send(ClientTransport.LocalPeerId, buffer.ToArray(), 0, writer.Position, DeliveryMethod.ReliableOrdered, 3);
            };

            // Client -> Host outgoing wire
            ClientVitals.OnOutgoingVitalsBroadcast += packet =>
            {
                Span<byte> buffer = stackalloc byte[64];
                var writer = new PacketWriter(buffer);
                packet.Serialize(ref writer);
                ClientTransport.Send(HostTransport.LocalPeerId, buffer.ToArray(), 0, writer.Position, DeliveryMethod.ReliableOrdered, 2);
            };

            ClientVitals.OnOutgoingCurrencyBroadcast += packet =>
            {
                Span<byte> buffer = stackalloc byte[64];
                var writer = new PacketWriter(buffer);
                packet.Serialize(ref writer);
                ClientTransport.Send(HostTransport.LocalPeerId, buffer.ToArray(), 0, writer.Position, DeliveryMethod.ReliableOrdered, 3);
            };

            ClientDeath.OnOutgoingDeathPacket += packet =>
            {
                Span<byte> buffer = stackalloc byte[64];
                var writer = new PacketWriter(buffer);
                packet.Serialize(ref writer);
                ClientTransport.Send(HostTransport.LocalPeerId, buffer.ToArray(), 0, writer.Position, DeliveryMethod.ReliableOrdered, 4);
            };

            ClientInventory.OnOutgoingItemUnlockPacket += packet =>
            {
                Span<byte> buffer = stackalloc byte[512];
                var writer = new PacketWriter(buffer);
                packet.Serialize(ref writer);
                ClientTransport.Send(HostTransport.LocalPeerId, buffer.ToArray(), 0, writer.Position, DeliveryMethod.ReliableOrdered, 3);
            };

            // Incoming dispatch on Host
            HostTransport.OnDataReceived += (peer, data, offset, length) =>
            {
                var span = new ReadOnlySpan<byte>(data, offset, length);
                if (PacketHeader.TryReadFrom(span, out var header))
                {
                    var reader = new PacketReader(span);
                    switch (header.PacketId)
                    {
                        case PacketType.VitalsSync:
                            var vitals = VitalsSyncPacket.Deserialize(ref reader);
                            DeliveredVitalsToHost.Add(vitals);
                            HostVitals.HandleVitalsPacket(peer, vitals);
                            break;

                        case PacketType.CurrencySync:
                            var currency = CurrencySyncPacket.Deserialize(ref reader);
                            DeliveredCurrenciesToHost.Add(currency);
                            HostVitals.HandleCurrencyPacket(peer, currency);
                            break;

                        case PacketType.ActionTrigger:
                            var action = ActionTriggerPacket.Deserialize(ref reader);
                            DeliveredActionsToHost.Add(action);
                            HostClones.TriggerRemoteAction(peer, action.Action, (byte)action.Parameter);
                            break;

                        case PacketType.TransformSync:
                            var trans = TransformSyncPacket.Deserialize(ref reader);
                            DeliveredTransformsToHost.Add(trans);
                            HostClones.UpdateRemoteTransform(peer, trans.PositionX, trans.PositionY, trans.VelocityX, trans.VelocityY, trans.FacingDirection, trans.StateFlags, trans.AnimationClipHash, trans.NormalizedTime, header.Timestamp);
                            break;

                        case PacketType.PlayerDeath:
                            var death = DeathLifecyclePacket.Deserialize(ref reader);
                            DeliveredDeathsToHost.Add(death);
                            HostDeath.HandleDeathPacket(peer, death);
                            break;

                        case PacketType.ItemUnlockSync:
                            var item = ItemUnlockPacket.Deserialize(ref reader);
                            DeliveredItemUnlocksToHost.Add(item);
                            HostInventory.HandleItemUnlockPacket(peer, item);
                            break;
                    }
                }
            };

            // Incoming dispatch on Client
            ClientTransport.OnDataReceived += (peer, data, offset, length) =>
            {
                var span = new ReadOnlySpan<byte>(data, offset, length);
                if (PacketHeader.TryReadFrom(span, out var header))
                {
                    var reader = new PacketReader(span);
                    switch (header.PacketId)
                    {
                        case PacketType.VitalsSync:
                            var vitals = VitalsSyncPacket.Deserialize(ref reader);
                            DeliveredVitalsToClient.Add(vitals);
                            ClientVitals.HandleVitalsPacket(peer, vitals);
                            break;

                        case PacketType.CurrencySync:
                            var currency = CurrencySyncPacket.Deserialize(ref reader);
                            DeliveredCurrenciesToClient.Add(currency);
                            ClientVitals.HandleCurrencyPacket(peer, currency);
                            break;

                        case PacketType.ActionTrigger:
                            var action = ActionTriggerPacket.Deserialize(ref reader);
                            DeliveredActionsToClient.Add(action);
                            ClientClones.TriggerRemoteAction(peer, action.Action, (byte)action.Parameter);
                            break;

                        case PacketType.TransformSync:
                            var trans = TransformSyncPacket.Deserialize(ref reader);
                            DeliveredTransformsToClient.Add(trans);
                            ClientClones.UpdateRemoteTransform(peer, trans.PositionX, trans.PositionY, trans.VelocityX, trans.VelocityY, trans.FacingDirection, trans.StateFlags, trans.AnimationClipHash, trans.NormalizedTime, header.Timestamp);
                            break;

                        case PacketType.PlayerDeath:
                            var death = DeathLifecyclePacket.Deserialize(ref reader);
                            DeliveredDeathsToClient.Add(death);
                            ClientDeath.HandleDeathPacket(peer, death);
                            break;

                        case PacketType.ItemUnlockSync:
                            var item = ItemUnlockPacket.Deserialize(ref reader);
                            DeliveredItemUnlocksToClient.Add(item);
                            ClientInventory.HandleItemUnlockPacket(peer, item);
                            break;
                    }
                }
            };
        }

        public void Step(int elapsedMs = 16)
        {
            HostTransport.AdvanceVirtualTime(elapsedMs);
            ClientTransport.AdvanceVirtualTime(elapsedMs);

            HostTransport.PollEvents();
            ClientTransport.PollEvents();
        }

        public void DeliverAll(int maxSteps = 100)
        {
            for (int i = 0; i < maxSteps; i++)
            {
                Step(16);
            }
        }

        public void AdvanceTime(int milliseconds)
        {
            int step = 16;
            int elapsed = 0;
            while (elapsed < milliseconds)
            {
                int current = Math.Min(step, milliseconds - elapsed);
                Step(current);
                elapsed += current;
            }
        }

        public void AssertVitalsDecoupled(int expectedHostHp, int expectedClientHp, int expectedHostSilk = 9, int expectedClientSilk = 9)
        {
            Assert.Equal(expectedHostHp, HostVitals.LocalHealth);
            Assert.Equal(expectedClientHp, ClientVitals.LocalHealth);
            Assert.Equal(expectedHostSilk, HostVitals.LocalSilk);
            Assert.Equal(expectedClientSilk, ClientVitals.LocalSilk);
        }

        public void AssertSharedProgression(int expectedMaxHp, int expectedMaxSilk, int expectedNeedleLevel)
        {
            Assert.Equal(expectedMaxHp, HostVitals.SharedMaxHealth);
            Assert.Equal(expectedMaxHp, ClientVitals.SharedMaxHealth);

            Assert.Equal(expectedMaxSilk, HostVitals.SharedMaxSilkSpools);
            Assert.Equal(expectedMaxSilk, ClientVitals.SharedMaxSilkSpools);

            Assert.Equal(expectedNeedleLevel, HostVitals.SharedNeedleUpgradeLevel);
            Assert.Equal(expectedNeedleLevel, ClientVitals.SharedNeedleUpgradeLevel);
        }

        public void AssertSharedWallet(int expectedRosaries, int expectedShards)
        {
            Assert.Equal(expectedRosaries, HostVitals.SharedRosaries);
            Assert.Equal(expectedRosaries, ClientVitals.SharedRosaries);

            Assert.Equal(expectedShards, HostVitals.SharedShellShards);
            Assert.Equal(expectedShards, ClientVitals.SharedShellShards);
        }

        public void Dispose()
        {
            HostTransport.Dispose();
            ClientTransport.Dispose();
        }
    }
}
