using System;
using System.Collections.Generic;
using SilksongCoop.Core.Interpolation;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests
{
    public class DualClientIntegrationTests
    {
        [Fact]
        public void DualClient_FullCoopSimulation_ReplicatesAllChannelsAndCoordinatesLifecycle()
        {
            // Set up paired host and client
            var (hostTransport, clientTransport) = MockNetworkTransport.CreatePair(useVirtualTime: true);

            var hostSession = new CoopSessionState();
            var clientSession = new CoopSessionState();

            hostSession.SetHosting(hostTransport.LocalPeerId, "SK-TEST-ROOM");
            clientSession.SetConnecting(clientTransport.LocalPeerId, "SK-TEST-ROOM");

            hostSession.SetConnected(clientTransport.LocalPeerId);
            clientSession.SetConnected(hostTransport.LocalPeerId);

            var receivedActionsOnHost = new List<ActionTriggerPacket>();
            var receivedCurrenciesOnHost = new List<CurrencySyncPacket>();
            var receivedTransformsOnClient = new List<TransformSyncPacket>();
            var receivedDeathsOnClient = new List<DeathLifecyclePacket>();

            // Host receiver dispatcher
            hostTransport.OnDataReceived += (peer, data, offset, length) =>
            {
                var reader = new PacketReader(data.AsSpan(offset, length));
                if (reader.TryReadHeader(out var header))
                {
                    reader = new PacketReader(data.AsSpan(offset, length));
                    switch (header.PacketId)
                    {
                        case PacketType.ActionTrigger:
                            receivedActionsOnHost.Add(ActionTriggerPacket.Deserialize(ref reader));
                            break;
                        case PacketType.CurrencySync:
                            receivedCurrenciesOnHost.Add(CurrencySyncPacket.Deserialize(ref reader));
                            break;
                    }
                }
            };

            // Client receiver dispatcher
            clientTransport.OnDataReceived += (peer, data, offset, length) =>
            {
                var reader = new PacketReader(data.AsSpan(offset, length));
                if (reader.TryReadHeader(out var header))
                {
                    reader = new PacketReader(data.AsSpan(offset, length));
                    switch (header.PacketId)
                    {
                        case PacketType.TransformSync:
                            receivedTransformsOnClient.Add(TransformSyncPacket.Deserialize(ref reader));
                            break;
                        case PacketType.PlayerDeath:
                            receivedDeathsOnClient.Add(DeathLifecyclePacket.Deserialize(ref reader));
                            break;
                    }
                }
            };

            // 1. Host sends Transform update on Channel 0
            var transform = new TransformSyncPacket
            {
                Header = new PacketHeader(PacketType.TransformSync, 0, 1, 100),
                PositionX = 14.5f,
                PositionY = 3.2f,
                VelocityX = 8.0f,
                VelocityY = 0f,
                FacingDirection = 1,
                StateFlags = TransformSyncPacket.FlagGrounded
            };
            Span<byte> buf1 = stackalloc byte[128];
            var w1 = new PacketWriter(buf1);
            transform.Serialize(ref w1);
            hostTransport.Send(clientTransport.LocalPeerId, buf1.ToArray(), 0, w1.Position, DeliveryMethod.Unreliable, 0);

            // 2. Client triggers Harpoon Dash on Channel 1
            var action = new ActionTriggerPacket
            {
                Header = new PacketHeader(PacketType.ActionTrigger, 1, 1, 105),
                Action = ActionType.HarpoonDash,
                ActionSequenceId = 1,
                OriginX = 10f,
                OriginY = 3.2f,
                DirectionX = 1f,
                DirectionY = 0f
            };
            Span<byte> buf2 = stackalloc byte[128];
            var w2 = new PacketWriter(buf2);
            action.Serialize(ref w2);
            clientTransport.Send(hostTransport.LocalPeerId, buf2.ToArray(), 0, w2.Position, DeliveryMethod.ReliableOrdered, 1);

            // 3. Client picks up Rosaries on Channel 3
            var txGuid = Guid.NewGuid();
            var currency = new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 1, 110),
                Currency = CurrencyType.Rosaries,
                TotalWalletAmount = 350,
                DeltaAmount = 50,
                PickupUniqueId = 12345,
                TransactionId = txGuid
            };
            Span<byte> buf3 = stackalloc byte[128];
            var w3 = new PacketWriter(buf3);
            currency.Serialize(ref w3);
            clientTransport.Send(hostTransport.LocalPeerId, buf3.ToArray(), 0, w3.Position, DeliveryMethod.ReliableOrdered, 3);

            // 4. Host takes fatal hit and enters solo death spectator mode on Channel 4
            var death = new DeathLifecyclePacket
            {
                Header = new PacketHeader(PacketType.PlayerDeath, 4, 1, 120),
                EventType = DeathEventType.SoloDeathOccurred,
                FallenPlayerIndex = 0, // Host fallen
                CoordinateX = 14.5f,
                CoordinateY = 3.2f,
                RemainingSeconds = 20.0f,
                Epoch = 1
            };
            Span<byte> buf4 = stackalloc byte[128];
            var w4 = new PacketWriter(buf4);
            death.Serialize(ref w4);
            hostTransport.Send(clientTransport.LocalPeerId, buf4.ToArray(), 0, w4.Position, DeliveryMethod.ReliableOrdered, 4);

            // Poll both transports to deliver all frames
            hostTransport.PollEvents();
            clientTransport.PollEvents();

            // Verify assertions on Host
            Assert.Single(receivedActionsOnHost);
            Assert.Equal(ActionType.HarpoonDash, receivedActionsOnHost[0].Action);

            Assert.Single(receivedCurrenciesOnHost);
            Assert.Equal(350, receivedCurrenciesOnHost[0].TotalWalletAmount);
            Assert.Equal(txGuid, receivedCurrenciesOnHost[0].TransactionId);

            // Verify assertions on Client
            Assert.Single(receivedTransformsOnClient);
            Assert.Equal(14.5f, receivedTransformsOnClient[0].PositionX);

            Assert.Single(receivedDeathsOnClient);
            Assert.Equal(DeathEventType.SoloDeathOccurred, receivedDeathsOnClient[0].EventType);
            Assert.Equal(20.0f, receivedDeathsOnClient[0].RemainingSeconds);
        }
    }
}
