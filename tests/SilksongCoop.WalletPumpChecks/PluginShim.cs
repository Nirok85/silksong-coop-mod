using System;
using System.Collections.Generic;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using SilksongCoop.Mod.Services;

namespace UnityEngine
{
    public static class Time
    {
        public static float unscaledTime;
    }
}

namespace SilksongCoop.Mod.Services
{
    // The external delivery boundary is scripted; retry decisions stay in the linked production partial.
    public sealed class VitalsSyncManager
    {
        public Func<NetworkPeerId, WalletSeedPacket, bool> Delivery { get; set; } = (_, _) => false;
        public List<(NetworkPeerId Peer, WalletSeedPacket Seed, float Time)> Calls { get; } = new();

        internal bool SendWalletSeed(NetworkPeerId peer, WalletSeedPacket seed)
        {
            Calls.Add((peer, seed, UnityEngine.Time.unscaledTime));
            return Delivery(peer, seed);
        }
    }
}

namespace SilksongCoop.Mod
{
    public partial class SilksongCoopPlugin
    {
        public CoopSessionState SessionState { get; private set; } = new();
        public VitalsSyncService VitalsSync { get; private set; } = new();
        public INetworkTransport? ActiveTransport { get; private set; } = new TestTransport();
        private VitalsSyncManager? _vitalsSyncManager = new();
        private readonly Dictionary<NetworkPeerId, bool> _walletPeerBootstrapCompleted = new();

        public VitalsSyncManager Sender => _vitalsSyncManager!;
        public int OutstandingCount => _walletSeedOutstanding.Count;
        public int DisconnectCount { get; private set; }
        public DisconnectReason? LastDisconnect { get; private set; }
        public static List<string> Warnings { get; } = new();
        public IReadOnlyDictionary<NetworkPeerId, bool> BootstrapCompleted => _walletPeerBootstrapCompleted;

        public void Queue(NetworkPeerId peer, float deadline = 20f, float nextAttempt = 0f) =>
            _walletSeedOutstanding.Add(peer, new WalletSeedRetryState(deadline, nextAttempt));
        public (float Deadline, float NextAttempt) Retry(NetworkPeerId peer) =>
            (_walletSeedOutstanding[peer].Deadline, _walletSeedOutstanding[peer].NextAttempt);
        public void Pump(float now)
        {
            UnityEngine.Time.unscaledTime = now;
            PumpWalletSeed();
        }
        public void RemoveSession() => SessionState = null!;
        public void RemoveTransport() => ActiveTransport = null;
        public void RemoveManager() => _vitalsSyncManager = null;

        public void DisconnectFromSession(DisconnectReason reason)
        {
            DisconnectCount++;
            LastDisconnect = reason;
        }
        public static void LogWarn(string message) => Warnings.Add(message);
    }

    internal sealed class TestTransport : INetworkTransport
    {
        public event Action<NetworkPeerId> OnPeerConnected { add { } remove { } }
        public event Action<NetworkPeerId, DisconnectReason> OnPeerDisconnected { add { } remove { } }
        public event Action<NetworkPeerId, byte[], int, int> OnDataReceived { add { } remove { } }
        public bool IsHost => true;
        public bool IsConnected => true;
        public NetworkPeerId LocalPeerId => new(1);
        public IReadOnlyList<NetworkPeerId> ConnectedPeers => Array.Empty<NetworkPeerId>();
        public int RoundTripTimeMs => 0;
        public int PacketLossPercent => 0;
        public int ReliableBacklogCount => 0;
        public void StartHost(int port = 7777) { }
        public void Connect(string addressOrCode) { }
        public void Disconnect() { }
        public void PollEvents() { }
        public void Dispose() { }
        public bool Send(NetworkPeerId target, byte[] data, int offset, int length, DeliveryMethod method, byte channel) =>
            throw new NotSupportedException("Delivery is intercepted at SendWalletSeed.");
    }
}
