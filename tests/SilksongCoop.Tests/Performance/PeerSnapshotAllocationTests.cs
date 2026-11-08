using System;
using System.Reflection;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests.Performance
{
    /// <summary>
    /// Allocation regression tests for the cached peer/session snapshots. Every getter
    /// must return its cached snapshot in steady state, so after a warm-up (JIT, first
    /// snapshot build, static generic caches) 1000 repeated reads must not allocate any
    /// managed bytes on the current thread.
    /// </summary>
    public class PeerSnapshotAllocationTests
    {
        private const int WarmupIterations = 1000;
        private const int MeasuredIterations = 1000;

        private static NetworkPeerId Peer(ulong value) => new NetworkPeerId(value, $"Peer{value}");

        private static VitalsSyncPacket Vitals(byte health) => new VitalsSyncPacket
        {
            CurrentHealth = health,
            CurrentSilk = 5,
            MaxHealth = 7,
            MaxSilkSpools = 2,
            NeedleUpgradeLevel = 1
        };

        [Fact]
        public void CoopSessionState_RemotePeerIds_ReadsAllocateNothing()
        {
            var session = new CoopSessionState();
            session.AddRemotePeer(Peer(2));
            session.AddRemotePeer(Peer(3));
            session.AddRemotePeer(Peer(4));

            _ = GC.GetAllocatedBytesForCurrentThread();

            long sink = 0;
            for (int i = 0; i < WarmupIterations; i++)
            {
                sink += session.RemotePeerIds.Count;
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < MeasuredIterations; i++)
            {
                sink += session.RemotePeerIds.Count;
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
            Assert.Equal(3L * (WarmupIterations + MeasuredIterations), sink);
        }

        [Fact]
        public void VitalsSyncService_RemotePeers_ReadsAllocateNothing()
        {
            var service = new VitalsSyncService();
            service.HandleVitalsPacket(Peer(2), Vitals(3));
            service.HandleVitalsPacket(Peer(3), Vitals(4));
            service.HandleVitalsPacket(Peer(4), Vitals(5));

            _ = GC.GetAllocatedBytesForCurrentThread();

            long sink = 0;
            for (int i = 0; i < WarmupIterations; i++)
            {
                sink += service.RemotePeers.Count;
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < MeasuredIterations; i++)
            {
                sink += service.RemotePeers.Count;
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
            Assert.Equal(3L * (WarmupIterations + MeasuredIterations), sink);
        }

        [Fact]
        public void PeerRegistry_PeersAndReadyPeers_ReadsAllocateNothing()
        {
            var registry = new PeerRegistry();
            registry.Upsert(Peer(2), PeerState.Ready, "Client");
            registry.Upsert(Peer(3), PeerState.Ready, "Client");
            registry.Upsert(Peer(4), PeerState.Ready, "Client");

            _ = GC.GetAllocatedBytesForCurrentThread();

            long sink = 0;
            for (int i = 0; i < WarmupIterations; i++)
            {
                sink += registry.Peers.Count;
                sink += registry.ReadyPeers.Count;
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < MeasuredIterations; i++)
            {
                sink += registry.Peers.Count;
                sink += registry.ReadyPeers.Count;
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
            Assert.Equal(6L * (WarmupIterations + MeasuredIterations), sink);
        }

        [Fact]
        public void LiteNetLibTransport_ConnectedPeers_DisconnectedReadsAllocateNothing()
        {
            var transport = new LiteNetLibTransport();

            _ = GC.GetAllocatedBytesForCurrentThread();

            long sink = 0;
            for (int i = 0; i < WarmupIterations; i++)
            {
                sink += transport.ConnectedPeers.Count;
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < MeasuredIterations; i++)
            {
                sink += transport.ConnectedPeers.Count;
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
            // Disconnected: the sink is intentionally zero, proving the reads happened.
            Assert.Equal(0L, sink);
        }

        [Fact]
        public void LiteNetLibTransport_ConnectedPeers_ConnectedReadsAllocateNothing()
        {
            var transport = new LiteNetLibTransport();
            try
            {
                var peerField = typeof(LiteNetLibTransport).GetField(
                    "_remotePeerId",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(peerField);
                peerField!.SetValue(transport, new NetworkPeerId(2, "Peer2"));

                // Build the cached view before measuring.
                Assert.Single(transport.ConnectedPeers);
                _ = GC.GetAllocatedBytesForCurrentThread();

                long sink = 0;
                for (int i = 0; i < WarmupIterations; i++)
                {
                    sink += transport.ConnectedPeers.Count;
                }

                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < MeasuredIterations; i++)
                {
                    sink += transport.ConnectedPeers.Count;
                }
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

                Assert.Equal(0, allocated);
                Assert.Equal(WarmupIterations + MeasuredIterations, sink);
            }
            finally
            {
                transport.Dispose();
            }
        }
    }
}
