using System;
using System.Collections.Generic;
using System.Linq;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using SilksongCoop.Mod;
using SilksongCoop.Mod.Coordinators;
using SilksongCoop.Mod.Networking;
using SilksongCoop.Mod.Services;
using UnityEngine;

internal static class Program
{
    private static readonly NetworkPeerId A = new(2);
    private static readonly NetworkPeerId B = new(3);

    private static int Main()
    {
        (string Name, Action Run)[] checks =
        {
            ("Ordinary join queues a marker with the peer's save token", OrdinaryJoin),
            ("Readiness waits for initialized PlayerData", ReadinessWaitsForPlayerData),
            ("Refused seed delays marker; retry retains wallet and is throttled", SeedRetry),
            ("Marker-only retry does not restart seed or inventory", MarkerRetry),
            ("A slow guest does not stall a second guest", IndependentGuests),
            ("Refused seed expires once", SeedTimeout),
            ("Steam seed timeout isolates the expired guest", SteamGuestSeedTimeout),
            ("Peer reset cancels pending output", ResetCancelsOutput),
            ("Repeated received seed cannot replace committed wallet", DuplicateSeed),
            ("Interrupted assembly cannot mutate wallet", InterruptedAssembly)
        };

        int passed = 0;
        foreach (var check in checks)
        {
            try
            {
                check.Run();
                passed++;
                Console.WriteLine($"PASS {check.Name}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"FAIL {check.Name}: {ex}");
            }
        }
        Console.WriteLine($"WalletPumpChecks: {passed}/{checks.Length} passed, {checks.Length - passed} failed.");
        return passed == checks.Length ? 0 : 1;
    }

    private static void OrdinaryJoin()
    {
        using var fixture = new Fixture();
        Guid save = fixture.Ready(A);
        var marker = fixture.Markers(A).Single();
        var reader = new PacketReader(marker.Data);
        Require(reader.ReadHeader().ChannelId == NetworkChannels.Currency && reader.ReadGuid() == save,
            "Marker must acknowledge the recipient's save instance on the currency channel.");
        Require(marker.Accepted && fixture.Sender.Calls.Count == 1, "Join must queue one seed and one marker.");
        fixture.Step(3f);
        Require(fixture.Markers(A).Length == 1 && fixture.Inventory.InitialBroadcasts.Count == 1,
            "Subsequent steps must not restart successful catch-up.");
    }

    private static void ReadinessWaitsForPlayerData()
    {
        using var fixture = new Fixture();
        var save = PlayerData.instance;
        try
        {
            PlayerData.instance = null;
            fixture.Ready(A);
            fixture.Step(1f);
            Require(fixture.Transport.Attempts.All(a => a.Type != PacketType.PeerReady),
                "No readiness message should be sent before PlayerData exists.");
            Require(fixture.Coordinator.GetLocalStage(A) == PeerHandshakeCoordinator.LocalSyncStage.WaitingForSave,
                "A connected client still needs a local save.");

            PlayerData.instance = save;
            fixture.Step(2f);
            var sent = fixture.Transport.Attempts.Single(a => a.Type == PacketType.PeerReady);
            var reader = new PacketReader(sent.Data);
            reader.ReadHeader();
            Guid token = reader.ReadGuid();
            Require(sent.Accepted && token != Guid.Empty && token == fixture.Coordinator.WalletSaveInstance,
                "The queued readiness must identify the loaded save.");
        }
        finally
        {
            PlayerData.instance = save;
        }
    }

    private static void SeedRetry()
    {
        using var fixture = new Fixture();
        fixture.Sender.Delivery = (_, _) => false;
        fixture.Ready(A);
        fixture.Vitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, 10, 50, Guid.NewGuid());
        Require(fixture.Vitals.SharedRosaries == 50, "Fixture must change the live wallet after capture.");

        fixture.Step(0f);
        fixture.Step(0.249f);
        Require(fixture.Sender.Calls.Count == 1 && fixture.Markers(A).Length == 0,
            "Refused seed must block the marker and same-frame retries.");
        Require(fixture.Coordinator.GetLocalStage(A) == PeerHandshakeCoordinator.LocalSyncStage.SynchronizingWallet,
            "Local stage must report the outstanding wallet handoff.");

        fixture.Sender.Delivery = (_, _) => true;
        fixture.Step(0.25f);
        Require(fixture.Sender.Calls.Count == 2 && fixture.Sender.Calls.All(c => c.Seed.Rosaries == 40),
            "Retry must use the captured balance even when the live wallet changes.");
        Require(fixture.Markers(A).Single().Accepted && fixture.Inventory.InitialBroadcasts.Count == 1,
            "Successful retry must release the marker without repeating inventory.");
        Require(fixture.Coordinator.GetLocalStage(A) == PeerHandshakeCoordinator.LocalSyncStage.InitialStateQueued,
            "Local stage must report initial state queued after seed and marker handoff.");
    }

    private static void MarkerRetry()
    {
        using var fixture = new Fixture();
        fixture.Transport.Accept = (_, kind) => kind != PacketType.CatchUpComplete;
        fixture.Ready(A);
        fixture.Step(0.249f);
        Require(!fixture.Markers(A).Single().Accepted, "Refused marker must wait for its retry interval.");
        fixture.Transport.Accept = (_, _) => true;
        fixture.Step(0.25f);
        Require(fixture.Markers(A).Select(m => m.Accepted).SequenceEqual(new[] { false, true }),
            "Refused marker must be retried once, then clear.");
        Require(fixture.Sender.Calls.Count == 1 && fixture.Inventory.InitialBroadcasts.Count == 1,
            "Marker backpressure must not replay the initial seed or inventory.");
    }

    private static void IndependentGuests()
    {
        using var fixture = new Fixture();
        fixture.Session.SessionState.AddRemotePeer(B);
        fixture.Sender.Delivery = (peer, _) => peer == B;
        fixture.Ready(A);
        fixture.Ready(B);
        Require(fixture.Markers(A).Length == 0 && fixture.Markers(B).Single().Accepted,
            "One refusal must not delay another guest's progress.");
        fixture.Step(0.25f);
        Require(fixture.Sender.Calls.Count(c => c.Peer == A) == 2 &&
                fixture.Sender.Calls.Count(c => c.Peer == B) == 1,
            "Only the refused guest is retried.");
    }

    private static void SeedTimeout()
    {
        using var fixture = new Fixture();
        fixture.Sender.Delivery = (_, _) => false;
        fixture.Ready(A);
        fixture.Step(19.75f);
        Require(fixture.Disconnects.Count == 0, "Seed must not time out before the deadline.");
        fixture.Step(20f);
        fixture.Step(21f);
        Require(fixture.Disconnects.SequenceEqual(new[] { DisconnectReason.ConnectionFailed }) &&
                fixture.Markers(A).Length == 0, "Timeout must disconnect once without sending a marker.");
    }

    private static void SteamGuestSeedTimeout()
    {
        var steam = new SteamP2PTransport { IsHost = true };
        using var fixture = new Fixture(transport: steam);
        fixture.Session.SessionState.AddRemotePeer(B);
        fixture.Sender.Delivery = (peer, _) => peer == A;
        fixture.Ready(A);
        fixture.Ready(B);
        fixture.Step(19.75f);
        Require(steam.FailedPeers.Count == 0, "No guest must time out before the deadline.");
        fixture.Step(20f);
        fixture.Step(21f);
        Require(steam.FailedPeers.SequenceEqual(new[] { B }),
            "Steam timeout must fail only the secondary guest and only once.");
        Require(fixture.Disconnects.Count == 0 && fixture.Session.SessionState.IsConnected &&
                fixture.Markers(A).Single().Accepted && fixture.Markers(B).Length == 0,
            "An expired guest must not disconnect the session or disturb a healthy guest's catch-up.");
    }

    private static void ResetCancelsOutput()
    {
        using var fixture = new Fixture();
        fixture.Sender.Delivery = (_, _) => false;
        fixture.Ready(A);
        fixture.Coordinator.ResetPeerState(A);
        fixture.Session.SessionState.RemoveRemotePeer(A);
        fixture.Step(21f);
        Require(fixture.Sender.Calls.Count == 1 && fixture.Markers(A).Length == 0 && fixture.Disconnects.Count == 0,
            "Reset must cancel retries, marker and timeout.");
    }

    private static void DuplicateSeed()
    {
        using var fixture = new Fixture(host: false);
        fixture.Ready(A);
        fixture.Coordinator.HandleWalletSeed(A, Seed(70).CreateChunks().Single());
        fixture.Coordinator.HandleWalletSeed(A, Seed(99).CreateChunks().Single());
        Require(fixture.Vitals.SharedRosaries == 70 && !fixture.Coordinator.AwaitingWalletSeed,
            "A repeated complete seed must not overwrite an already-applied wallet.");
        Require(fixture.Sender.Enabled.SequenceEqual(new[] { A }), "Currency must be enabled once.");
    }

    private static void InterruptedAssembly()
    {
        using var fixture = new Fixture(host: false);
        fixture.Ready(A);
        var seed = Seed(70);
        seed.CreditedPickups = Enumerable.Range(1, WalletSeedPacket.MaxPickupsPerChunk + 1)
            .Select(i => new WalletSeedPickup { Currency = CurrencyType.Rosaries, Key = (ulong)i }).ToArray();
        var chunks = seed.CreateChunks();
        fixture.Coordinator.HandleWalletSeed(A, chunks[0]);
        Require(fixture.Vitals.SharedRosaries == 40 && fixture.Coordinator.AwaitingWalletSeed,
            "The first chunk alone must not mutate the wallet.");

        fixture.Coordinator.ResetPeerReadiness();
        fixture.Step(0.5f);
        fixture.Ready(A);
        fixture.Coordinator.HandleWalletSeed(A, Seed(90).CreateChunks().Single());
        Require(fixture.Vitals.SharedRosaries == 90 && !fixture.Coordinator.AwaitingWalletSeed,
            "Reconnect must start with an empty assembler.");
    }

    private static WalletSeedPacket Seed(int rosaries) => new()
    {
        Header = new PacketHeader(PacketType.WalletSeed, NetworkChannels.Currency, 0, 0),
        Rosaries = rosaries,
        Shards = 9
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        public TestSession Session { get; } = new();
        public VitalsSyncService Vitals { get; } = new();
        public VitalsSyncManager Sender { get; } = new();
        public InventoryApplicationCoordinator Inventory { get; } = new();
        public TestTransport Transport { get; }
        public PeerHandshakeCoordinator Coordinator { get; }
        public List<DisconnectReason> Disconnects { get; } = new();

        public Fixture(bool host = true, TestTransport? transport = null)
        {
            Time.unscaledTime = 0;
            PlayerData.instance = new();
            SilksongCoopPlugin.Warnings.Clear();
            Transport = transport ?? new TestTransport { IsHost = host };
            if (host)
                Session.SessionState.SetHosting(Transport.LocalPeerId, "room");
            else
                Session.SessionState.SetConnecting(Transport.LocalPeerId, "room");
            Session.SessionState.SetConnected(A);
            Coordinator = new PeerHandshakeCoordinator(Session, Vitals, new DeathLifecycleCoordinator(),
                new InventorySyncService(), null, null, new ReviveLifecycleCoordinator(), Inventory,
                () => Sender, () => Transport, () => null, () => true, reason =>
                {
                    Disconnects.Add(reason);
                    Session.Disconnect(reason);
                });
        }

        public Guid Ready(NetworkPeerId peer)
        {
            Guid saveInstance = Guid.NewGuid();
            Coordinator.HandlePeerReady(peer, saveInstance);
            return saveInstance;
        }

        public void Step(float now)
        {
            Time.unscaledTime = now;
            Coordinator.Update();
        }

        public TestTransport.Attempt[] Markers(NetworkPeerId target) =>
            Transport.Attempts.Where(a => a.Target == target && a.Type == PacketType.CatchUpComplete).ToArray();

        public void Dispose()
        {
            Coordinator.Dispose();
            PlayerData.instance = null;
        }
    }
}
