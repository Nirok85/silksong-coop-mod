using System;
using System.Collections.Generic;
using System.Linq;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using SilksongCoop.Mod;

internal static class Program
{
    private static readonly NetworkPeerId A = new(2);
    private static readonly NetworkPeerId B = new(3);
    private static readonly NetworkPeerId C = new(4);
    private static readonly NetworkPeerId D = new(5);

    private static int Main()
    {
        var checks = new List<(string Name, Action Run)>
        {
            ("Mixed delivery preserves multi-peer enumeration and retry state", MixedDelivery),
            ("Same-frame and subinterval calls are throttled", Throttle),
            ("Deadline at 30 Hz", () => Deadline(30)),
            ("Deadline at 60 Hz", () => Deadline(60)),
            ("Deadline at 240 Hz", () => Deadline(240)),
            ("Null session clears outstanding seeds", () => Inactive(p => p.RemoveSession())),
            ("Disconnected host clears outstanding seeds", () => Inactive(p => p.SessionState.SetDisconnected())),
            ("Client clears outstanding seeds", () => Inactive(p => p.SessionState.SetConnected(A, SessionRole.Client))),
            ("Missing transport clears outstanding seeds", () => Inactive(p => p.RemoveTransport())),
            ("Successful peer is removed once and bootstrap completes", SuccessfulPeer),
            ("Missing delivery manager follows retry and timeout path", MissingManager),
            ("Successful delivery at deadline does not disconnect", SuccessAtDeadline),
            ("Empty pump has no side effects", Empty)
        };
        int passed = 0;
        foreach (var check in checks)
        {
            SilksongCoopPlugin.Warnings.Clear();
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
        Console.WriteLine($"WalletPumpChecks: {passed}/{checks.Count} passed, {checks.Count - passed} failed.");
        return passed == checks.Count ? 0 : 1;
    }

    private static SilksongCoopPlugin Host()
    {
        var plugin = new SilksongCoopPlugin();
        plugin.SessionState.SetConnected(A, SessionRole.Host);
        plugin.SessionState.AddRemotePeer(B);
        plugin.SessionState.AddRemotePeer(C);
        plugin.SessionState.AddRemotePeer(D);
        return plugin;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void MixedDelivery()
    {
        var p = Host();
        p.Queue(A);
        p.Queue(B);
        p.Queue(C, nextAttempt: 1f);
        p.Queue(D);
        p.Sender.Delivery = (peer, _) => peer == B;
        p.Pump(0f);
        Require(p.Sender.Calls.Select(c => c.Peer).SequenceEqual(new[] { A, B, D }), "All due peers must be visited despite mixed outcomes.");
        Require(p.OutstandingCount == 3, "Only delivered peer must be removed.");
        Require(p.Retry(A) == (20f, 0.25f) && p.Retry(D) == (20f, 0.25f), "Failures must retain deadlines and advance next attempt.");
        Require(p.Retry(C) == (20f, 1f), "Not-yet-due peer must retain its state.");
        Require(p.BootstrapCompleted.Count == 1 && p.BootstrapCompleted[B], "Only delivered peer completes bootstrap.");
        foreach (var call in p.Sender.Calls)
        {
            Require((call.Seed.Watermarks ?? Array.Empty<WalletSeedWatermark>()).Select(m => m.Peer).SequenceEqual(new ulong[] { 1, 2, 3, 4, 5 }), "Actual Core capture must include local and all remote origins.");
        }
        p.Sender.Delivery = (_, _) => true;
        p.Pump(0.25f);
        Require(p.OutstandingCount == 1 && p.Retry(C) == (20f, 1f), "Second mixed pass must remove delivered peers without losing deferred peer.");
        p.Pump(1f);
        Require(p.OutstandingCount == 0 && p.BootstrapCompleted.Count == 4, "All peers eventually complete independently.");
        Require(p.DisconnectCount == 0, "Mixed delivery must not disconnect.");
    }

    private static void Throttle()
    {
        var p = Host();
        p.Queue(A);
        p.Pump(0f);
        for (int i = 0; i < 100; i++) p.Pump(0f);
        p.Pump(0.249f);
        Require(p.Sender.Calls.Count == 1 && p.Retry(A) == (20f, 0.25f), "Repeated frame calls must not consume attempts or deadline.");
        p.Pump(0.25f);
        p.Pump(0.25f);
        Require(p.Sender.Calls.Count == 2 && p.Retry(A) == (20f, 0.5f), "Exactly one retry is due at interval boundary.");
    }

    private static void Deadline(int hz)
    {
        var p = Host();
        p.Queue(A);
        float disconnectedAt = -1f;
        for (int frame = 0; frame <= 21 * hz; frame++)
        {
            float now = frame / (float)hz;
            p.Pump(now);
            if (now < 20f) Require(p.DisconnectCount == 0, "Frame count must not trigger an early timeout.");
            if (p.DisconnectCount != 0) { disconnectedAt = now; break; }
        }
        Require(disconnectedAt >= 20f && disconnectedAt <= 20.25f + 1f / hz, "Timeout must be wall-clock based within one retry interval plus one frame.");
        Require(p.LastDisconnect == DisconnectReason.ConnectionFailed && p.DisconnectCount == 1, "Timeout must disconnect once with ConnectionFailed.");
        Require(p.OutstandingCount == 0 && p.BootstrapCompleted.Count == 0, "Timeout clears outstanding seeds without claiming bootstrap.");
        Require(SilksongCoopPlugin.Warnings.Count == 1, "Timeout must warn once.");
        Require(p.Sender.Calls.Count >= 70 && p.Sender.Calls.Count <= 82, "Retry frequency must be bounded independently of frame rate.");
        for (int i = 1; i < p.Sender.Calls.Count; i++)
            Require(p.Sender.Calls[i].Time - p.Sender.Calls[i - 1].Time >= 0.24999f, "Retry attempts must be separated by the throttle interval.");
        p.Pump(30f);
        Require(p.DisconnectCount == 1 && SilksongCoopPlugin.Warnings.Count == 1, "Empty post-timeout pump must not disconnect or warn again.");
    }

    private static void Inactive(Action<SilksongCoopPlugin> deactivate)
    {
        var p = Host();
        p.Queue(A);
        p.Queue(B);
        deactivate(p);
        p.Pump(100f);
        Require(p.OutstandingCount == 0, "Inactive host must clear all seeds.");
        Require(p.Sender.Calls.Count == 0 && p.BootstrapCompleted.Count == 0, "Inactive pump must not send or complete bootstrap.");
        Require(p.DisconnectCount == 0 && SilksongCoopPlugin.Warnings.Count == 0, "Inactive cleanup must not trigger timeout side effects.");
    }

    private static void SuccessfulPeer()
    {
        var p = Host();
        p.Queue(A);
        p.Sender.Delivery = (_, _) => true;
        p.Pump(0f);
        Require(p.OutstandingCount == 0 && p.BootstrapCompleted.Count == 1 && p.BootstrapCompleted[A], "Successful peer must be removed and recorded complete.");
        p.Pump(0f);
        p.Pump(0.25f);
        p.Pump(21f);
        Require(p.Sender.Calls.Count == 1 && p.BootstrapCompleted.Count == 1 && p.DisconnectCount == 0, "Delivered peer must never be retried or timed out.");
    }

    private static void MissingManager()
    {
        var p = Host();
        p.Queue(A);
        p.RemoveManager();
        p.Pump(0f);
        Require(p.Retry(A) == (20f, 0.25f), "Missing manager must schedule retry rather than claim success.");
        p.Pump(20f);
        Require(p.DisconnectCount == 1 && p.LastDisconnect == DisconnectReason.ConnectionFailed && p.OutstandingCount == 0, "Missing manager must eventually fail closed.");
    }

    private static void SuccessAtDeadline()
    {
        var p = Host();
        p.Queue(A);
        p.Sender.Delivery = (_, _) => true;
        p.Pump(20f);
        Require(p.OutstandingCount == 0 && p.BootstrapCompleted[A] && p.DisconnectCount == 0, "Delivery at deadline must win over timeout.");
    }

    private static void Empty()
    {
        var p = Host();
        p.RemoveSession();
        p.Pump(100f);
        Require(p.Sender.Calls.Count == 0 && p.DisconnectCount == 0 && SilksongCoopPlugin.Warnings.Count == 0, "Empty pump must return without touching external APIs.");
    }
}
