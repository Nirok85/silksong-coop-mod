using System;
using System.Collections.Generic;
using System.Linq;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests.Reliability;

public sealed class BeamSessionScenarios
{
    public sealed record Observation(int Dropped, int Refused, int Retries, int PeakQueued, string Trace);

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void SeedReproducesDeliveryAndSharedWallet(int nodes)
    {
        var first = Exercise(1337, nodes, impaired: true);
        var second = Exercise(1337, nodes, impaired: true);
        Assert.Equal(first, second);
        Assert.True(first.Dropped > 0);
        Assert.True(first.Refused > 0);
        Assert.Equal(first.Dropped, first.Retries);
    }

    public static Observation Exercise(int seed, int nodes, bool impaired)
    {
        const int perGuest = 48;
        var trace = new List<string>();
        var guests = Enumerable.Range(0, nodes - 1).Select(i => CreateGuest(seed, i, impaired)).ToArray();
        var wallet = new VitalsSyncService();
        wallet.InitializeState(5, 5, 9, 9, 0, 10, 0);
        var session = new CoopSessionState();
        session.SetConnected(guests[0].Client.LocalPeerId, SessionRole.Host);
        foreach (var guest in guests)
        {
            session.AddRemotePeer(guest.Client.LocalPeerId);
            guest.Host.OnDataReceived += (sender, data, offset, length) =>
            {
                var reader = new PacketReader(data.AsSpan(offset, length));
                var packet = CurrencySyncPacket.Deserialize(ref reader);
                wallet.HandleCurrencyPacket(sender, packet);
                guest.Delivered++;
                trace.Add($"{sender.Value}:{packet.PickupUniqueId}@{guest.Host.CurrentVirtualTimeMs}");
            };
        }

        try
        {
            int peak = 0;
            for (int step = 0; step < 300; step++)
            {
                foreach (var guest in guests)
                {
                    if (guest.Sent < perGuest + 1)
                    {
                        // Repeat the last application event with the same transaction ID.
                        int index = Math.Min(guest.Sent + 1, perGuest);
                        if (Send(guest, index, seed)) guest.Sent++;
                    }
                    peak = Math.Max(peak, guest.Host.PendingPacketCount);
                    guest.Host.AdvanceVirtualTime(10);
                    guest.Client.AdvanceVirtualTime(10);
                    guest.Host.PollEvents();
                    Assert.InRange(guest.Host.PendingPacketCount, 0, perGuest + 1);
                }
                if (guests.All(g => g.Delivered == perGuest + 1)) break;
            }

            Assert.All(guests, g => Assert.Equal(perGuest + 1, g.Delivered));
            Assert.All(guests, g => Assert.Equal(0, g.Host.PendingPacketCount));
            Assert.Equal(10 + 2 * (1 + (nodes - 1) * (perGuest - 1)), wallet.SharedRosaries);

            var replaced = guests[0];
            replaced.Client.Disconnect();
            session.RemoveRemotePeer(replaced.Client.LocalPeerId);
            Assert.All(guests.Skip(1), g => Assert.True(g.Client.IsConnected));
            Assert.Equal(0, replaced.Host.PendingPacketCount);
            if (nodes > 2)
            {
                Assert.DoesNotContain(replaced.Client.LocalPeerId, session.RemotePeerIds);
                Assert.All(guests.Skip(1), g => Assert.Contains(g.Client.LocalPeerId, session.RemotePeerIds));
            }

            replaced.Client.Connect("host");
            session.AddRemotePeer(replaced.Client.LocalPeerId);
            var seedPacket = wallet.CaptureWalletSeed(replaced.Host.LocalPeerId, session.RemotePeerIds);
            var rejoinedWallet = new VitalsSyncService();
            rejoinedWallet.BeginWalletSeed();
            rejoinedWallet.ApplyWalletSeed(seedPacket, replaced.Client.LocalPeerId, Array.Empty<CurrencySyncPacket>());
            Assert.Equal(wallet.SharedRosaries, rejoinedWallet.SharedRosaries);
            Assert.Equal(nodes - 1, session.RemotePeerIds.Count);

            foreach (var guest in guests) guest.Host.Disconnect();
            session.SetDisconnected();
            Assert.All(guests, g => Assert.False(g.Client.IsConnected));
            Assert.Empty(session.RemotePeerIds);

            return new Observation(guests.Sum(g => g.Client.DroppedDatagrams), guests.Sum(g => g.Client.RefusedSends),
                guests.Sum(g => g.Client.ReliableRetries), peak, string.Join(";", trace));
        }
        finally
        {
            foreach (var guest in guests)
            {
                guest.Host.Dispose();
                guest.Client.Dispose();
            }
        }
    }

    private static Guest CreateGuest(int seed, int offset, bool impaired)
    {
        var host = new MockNetworkTransport(1, seed: seed + offset * 2) { UseVirtualTime = true };
        var client = new MockNetworkTransport((ulong)(offset + 2), seed: seed + offset * 2 + 1)
        {
            UseVirtualTime = true,
            LatencyMs = impaired ? 50 : 0,
            JitterMs = impaired ? 30 : 0,
            PacketLossRate = impaired ? 0.05f : 0f,
            SendRefusalRate = impaired ? 0.10f : 0f,
            SimulateReliableLoss = true
        };
        host.SetPeer(client);
        client.SetPeer(host);
        host.StartHost();
        client.Connect("host");
        return new Guest(host, client);
    }

    private static bool Send(Guest guest, int index, int seed)
    {
        var sender = guest.Client.LocalPeerId.Value;
        var packet = new CurrencySyncPacket
        {
            Header = new PacketHeader(PacketType.CurrencySync, NetworkChannels.Currency, (uint)index, 0),
            PickupUniqueId = (uint)index,
            PickupKey = index == 1 ? 1UL : sender * 1000UL + (ulong)index,
            TransactionId = new Guid(seed, (short)sender, (short)index, new byte[8]),
            DeltaAmount = 2
        };
        var buffer = new byte[64];
        var writer = new PacketWriter(buffer);
        packet.Serialize(ref writer);
        return guest.Client.Send(guest.Host.LocalPeerId, buffer, 0, writer.Position,
            DeliveryMethod.ReliableOrdered, NetworkChannels.Currency);
    }

    private sealed class Guest(MockNetworkTransport host, MockNetworkTransport client)
    {
        public MockNetworkTransport Host { get; } = host;
        public MockNetworkTransport Client { get; } = client;
        public int Sent;
        public int Delivered;
    }
}
