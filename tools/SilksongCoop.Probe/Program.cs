using System;
using System.Collections.Generic;
using System.Threading;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;

// Headless protocol client for a two-client smoke test without a second player.
//
// It performs the same UDP handshake as the mod, reports every packet type received
// from the game, and sends a valid VitalsSync once per second. That is enough to
// verify the host UI, room-code authentication, the real socket path and the partner
// HUD. It does not simulate movement or combat.
//
// Usage:
//   dotnet run --project tools/SilksongCoop.Probe -c Release -- "SK-XXXX-..." [seconds]
//
// Start a UDP host in the game (F10 -> HOST -> create UDP), copy the room code, then
// run this probe. Use a direct IP:port only if the host did not require a room code.

string target = args.Length > 0 ? args[0] : "127.0.0.1:7777";
int seconds = args.Length > 1 && int.TryParse(args[1], out int parsedSeconds)
    ? Math.Clamp(parsedSeconds, 5, 300)
    : 20;

using var transport = new LiteNetLibTransport(2, "Probe");
var counts = new Dictionary<PacketType, int>();
bool connected = false;

transport.OnPeerConnected += peer =>
{
    connected = true;
    Console.WriteLine($"CONNECTED peer={peer.Value} name={peer.DisplayName}");
};
transport.OnPeerDisconnected += (peer, reason) =>
{
    connected = false;
    Console.WriteLine($"DISCONNECTED peer={peer.Value} reason={reason}");
};
transport.OnDataReceived += (peer, data, offset, length) =>
{
    try
    {
        var reader = new PacketReader(new ReadOnlySpan<byte>(data, offset, length));
        if (!reader.TryReadHeader(out var header)) return;
        counts.TryGetValue(header.PacketId, out int current);
        counts[header.PacketId] = current + 1;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"PARSE_ERROR {ex.GetType().Name}: {ex.Message}");
    }
};

Console.WriteLine($"Connecting to {target} for {seconds}s...");
transport.Connect(target);

uint tick = 0;
long lastVitals = 0;
var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
while (DateTime.UtcNow < deadline)
{
    transport.PollEvents();

    if (connected && Environment.TickCount64 - lastVitals >= 1000)
    {
        lastVitals = Environment.TickCount64;
        var vitals = new VitalsSyncPacket
        {
            Header = new PacketHeader(PacketType.VitalsSync, NetworkChannels.Vitals, ++tick, NetworkClock.NowMs),
            CurrentHealth = 7,
            CurrentSilk = 9,
            MaxHealth = 9,
            MaxSilkSpools = 1,
            NeedleUpgradeLevel = 1
        };
        byte[] buffer = new byte[64];
        var writer = new PacketWriter(buffer);
        vitals.Serialize(ref writer);
        transport.Send(new NetworkPeerId(1), buffer, 0, writer.Position,
            DeliveryMethod.ReliableOrdered, NetworkChannels.Vitals);
        Console.WriteLine($"SENT_VITALS hp=7/9 silk=9/9 tick={tick}");
    }

    Thread.Sleep(10);
}

Console.WriteLine($"FINAL connected={connected} rtt={transport.RoundTripTimeMs} loss={transport.PacketLossPercent}");
foreach (var pair in counts)
    Console.WriteLine($"RX {pair.Key} = {pair.Value}");
