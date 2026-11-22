using System;
using System.Collections.Generic;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using SilksongCoop.Mod.Services;

internal sealed class TestSession : ISessionService
{
    public CoopSessionState SessionState { get; } = new();
    public void StartHost(int port) => throw new NotSupportedException();
    public void StartHostSteam() => throw new NotSupportedException();
    public void Connect(string address) => throw new NotSupportedException();
    public void ConnectSteam(string lobbyId) => throw new NotSupportedException();
    public void Disconnect(DisconnectReason reason) => SessionState.SetDisconnected();
    public void PrepareNewSession() => throw new NotSupportedException();
}

internal class TestTransport : INetworkTransport
{
    internal readonly record struct Attempt(NetworkPeerId Target, PacketType Type, byte[] Data, float Time, bool Accepted);
    public List<Attempt> Attempts { get; } = new();
    public Func<NetworkPeerId, PacketType, bool> Accept { get; set; } = (_, _) => true;
    public event Action<NetworkPeerId> OnPeerConnected { add { } remove { } }
    public event Action<NetworkPeerId, DisconnectReason> OnPeerDisconnected { add { } remove { } }
    public event Action<NetworkPeerId, byte[], int, int> OnDataReceived { add { } remove { } }
    public bool IsHost { get; init; }
    public bool IsConnected => true;
    public NetworkPeerId LocalPeerId => new(1);
    public IReadOnlyList<NetworkPeerId> ConnectedPeers => Array.Empty<NetworkPeerId>();
    public int RoundTripTimeMs => 0;
    public int PacketLossPercent => 0;
    public int ReliableBacklogCount => 0;
    public void StartHost(int port = 7777) => throw new NotSupportedException();
    public void Connect(string addressOrCode) => throw new NotSupportedException();
    public void Disconnect() { }
    public void PollEvents() { }
    public void Dispose() { }

    public bool Send(NetworkPeerId target, byte[] data, int offset, int length, DeliveryMethod method, byte channel)
    {
        byte[] bytes = data.AsSpan(offset, length).ToArray();
        var header = PacketHeader.ReadFrom(bytes);
        bool accepted = Accept(target, header.PacketId);
        Attempts.Add(new Attempt(target, header.PacketId, bytes, UnityEngine.Time.unscaledTime, accepted));
        return accepted;
    }
}

namespace UnityEngine
{
    public static class Time
    {
        public static float unscaledTime;
    }

    public struct Vector3
    {
        public readonly float x => 0;
        public readonly float y => 0;
        public static Vector3 zero => default;
    }

    public sealed class Transform
    {
        public Vector3 position => Vector3.zero;
    }
}

namespace UnityEngine.SceneManagement
{
    public readonly record struct Scene(bool isLoaded, string name);

    public static class SceneManager
    {
        public static Scene GetActiveScene() => new(false, string.Empty);
    }
}

public sealed class PlayerData
{
    public static PlayerData? instance;
    public int health = 5;
    public int maxHealthBase = 5;
    public int silk = 9;
    public int silkMax = 9;
    public int nailUpgrades;
    public int geo = 40;
    public int ShellShards = 9;
    public void SetBenchRespawn(string marker, string scene, int type, bool facingRight) => throw new NotSupportedException();
}

public sealed class HeroController
{
    public static HeroController? instance => null;
    public UnityEngine.Transform transform { get; } = new();
}

public sealed class GameManager
{
    public static GameManager? instance => null;
    public bool IsGameplayScene() => false;
}

public sealed class RespawnMarker
{
    public sealed class MarkerObject
    {
        public UnityEngine.SceneManagement.Scene scene => default;
    }
    public static IReadOnlyList<RespawnMarker> Markers { get; } = Array.Empty<RespawnMarker>();
    public MarkerObject gameObject { get; } = new();
    public string name => string.Empty;
}

namespace SilksongCoop.Mod
{
    public sealed class SilksongCoopPlugin
    {
        public static List<string> Warnings { get; } = new();
        public static void LogWarn(string message) => Warnings.Add(message);
        public static void LogInfo(string message) { }
    }
}

namespace SilksongCoop.Mod.UI
{
    public sealed class NetworkDiagnostics : INetworkDiagnostics
    {
        public void RecordPing(float pingMs) { }
        public void RecordLossPercent(float percent) { }
        public void RecordSent(int byteCount) { }
        public void RecordRejected() { }
    }
}

namespace SilksongCoop.Mod.Networking
{
    internal sealed class SteamP2PTransport : TestTransport
    {
        public readonly record struct LobbyId(ulong m_SteamID);
        public LobbyId CurrentLobbyId { get; } = new(1);
        public List<NetworkPeerId> FailedPeers { get; } = new();
        public void FailReliablePeer(NetworkPeerId peer) => FailedPeers.Add(peer);
    }
}

namespace SilksongCoop.Mod.State
{
    public sealed class EnemySyncController
    {
        public void BeginCatchUpForNewPeer() { }
    }
}

namespace SilksongCoop.Mod.Coordinators
{
    public sealed class ReviveLifecycleCoordinator { }

    public sealed class InventoryApplicationCoordinator
    {
        public int BaseMaxHealth { get; set; }
        public int BaseMaxSilkSpools { get; set; }
        public List<NetworkPeerId> InitialBroadcasts { get; } = new();
        public void BroadcastInitialInventory(NetworkPeerId peer) => InitialBroadcasts.Add(peer);
    }
}

namespace SilksongCoop.Mod.Services
{
    public sealed class QuestStateSyncManager
    {
        public void ClearAuthoredChanges() { }
        public void ReplayAuthoredChanges(NetworkPeerId peer) { }
    }

    // Only the outbound handoff is scripted. Seed capture, retry and receive assembly are real.
    public sealed class VitalsSyncManager
    {
        public Func<NetworkPeerId, WalletSeedPacket, bool> Delivery { get; set; } = (_, _) => true;
        public List<(NetworkPeerId Peer, WalletSeedPacket Seed, float Time)> Calls { get; } = new();
        public List<NetworkPeerId> Enabled { get; } = new();

        public bool SendWalletSeed(NetworkPeerId peer, WalletSeedPacket seed)
        {
            Calls.Add((peer, seed, UnityEngine.Time.unscaledTime));
            return Delivery(peer, seed);
        }

        public void EnableCurrencyPeer(NetworkPeerId peer) => Enabled.Add(peer);
        public void ResetCurrencyJournal() { }
        public bool CanResumeCurrencyPeer(NetworkPeerId peer) => true;
        public void ForgetCurrencyPeer(NetworkPeerId peer) { }
        public IReadOnlyList<CurrencySyncPacket> GetPendingCurrencyPackets(NetworkPeerId peer) => Array.Empty<CurrencySyncPacket>();
    }
}
