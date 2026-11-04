using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using SilksongCoop.Core.Networking;
using Xunit;

namespace SilksongCoop.Tests.Networking
{
    public class PeerRegistryTests
    {
        private static NetworkPeerId Peer(ulong value) => new NetworkPeerId(value, $"Peer{value}");

        [Fact]
        public void Upsert_NewPeer_StoresStateAndRaisesAdded()
        {
            var registry = new PeerRegistry();
            var added = new List<PeerRecord>();
            registry.OnPeerAdded += added.Add;

            var record = registry.Upsert(Peer(1), PeerState.Authenticated);

            Assert.Equal(1, registry.Count);
            Assert.Single(added);
            Assert.Equal(PeerState.Authenticated, record.State);
            Assert.Equal(record, added[0]);
            Assert.True(registry.TryGet(Peer(1), out var stored));
            Assert.Equal(record, stored);
        }

        [Fact]
        public void Upsert_ExistingPeer_UpdatesStateAndPreservesConnectedAt()
        {
            var registry = new PeerRegistry();
            var updates = new List<(PeerRecord Old, PeerRecord New)>();
            registry.OnPeerUpdated += (oldRecord, newRecord) => updates.Add((oldRecord, newRecord));

            var first = registry.Upsert(Peer(1), PeerState.Connecting, "Client");
            var second = registry.Upsert(Peer(1), PeerState.Ready);

            Assert.Equal(first.ConnectedAtUtc, second.ConnectedAtUtc);
            Assert.Equal(PeerState.Ready, second.State);
            Assert.Equal("Client", second.Role);
            Assert.Single(updates);
            Assert.Equal(PeerState.Connecting, updates[0].Old.State);
            Assert.Equal(PeerState.Ready, updates[0].New.State);
        }

        [Fact]
        public void Upsert_WithNewRole_ReplacesRole()
        {
            var registry = new PeerRegistry();
            registry.Upsert(Peer(1), PeerState.Connecting, "Client");

            var updated = registry.Upsert(Peer(1), PeerState.Ready, "Host");

            Assert.Equal("Host", updated.Role);
        }

        [Fact]
        public void SetState_UnknownPeer_ReturnsFalse()
        {
            var registry = new PeerRegistry();
            Assert.False(registry.SetState(Peer(7), PeerState.Ready));
        }

        [Fact]
        public void SetState_KnownPeer_UpdatesAndRaisesUpdated()
        {
            var registry = new PeerRegistry();
            registry.Upsert(Peer(1), PeerState.Authenticated);
            PeerRecord updated = default;
            registry.OnPeerUpdated += (_, newRecord) => updated = newRecord;

            Assert.True(registry.SetState(Peer(1), PeerState.Ready));
            Assert.Equal(PeerState.Ready, updated.State);
            Assert.Equal(1, registry.CountInState(PeerState.Ready));
        }

        [Fact]
        public void ReadyPeers_ReturnsOnlyReadyPeers()
        {
            var registry = new PeerRegistry();
            registry.Upsert(Peer(1), PeerState.Connecting);
            registry.Upsert(Peer(2), PeerState.Authenticated);
            registry.Upsert(Peer(3), PeerState.Ready);

            var ready = registry.ReadyPeers;

            Assert.Single(ready);
            Assert.Equal(Peer(3), ready[0]);
        }

        [Fact]
        public void Remove_RemovesPeerAndRaisesRemoved()
        {
            var registry = new PeerRegistry();
            registry.Upsert(Peer(1), PeerState.Ready);
            PeerRecord removed = default;
            registry.OnPeerRemoved += record => removed = record;

            Assert.True(registry.Remove(Peer(1)));
            Assert.False(registry.Remove(Peer(1)));
            Assert.Equal(0, registry.Count);
            Assert.Equal(Peer(1), removed.Peer);
        }

        [Fact]
        public void Clear_RemovesEveryPeerAndRaisesRemovedForEach()
        {
            var registry = new PeerRegistry();
            registry.Upsert(Peer(1), PeerState.Ready);
            registry.Upsert(Peer(2), PeerState.Ready);
            int removedCount = 0;
            registry.OnPeerRemoved += _ => removedCount++;

            registry.Clear();

            Assert.Equal(0, registry.Count);
            Assert.Equal(2, removedCount);
        }

        [Fact]
        public void ConcurrentUpserts_RemainConsistent()
        {
            var registry = new PeerRegistry();
            var seen = new ConcurrentBag<NetworkPeerId>();
            registry.OnPeerAdded += record => seen.Add(record.Peer);

            Parallel.For(0, 200, i =>
            {
                registry.Upsert(Peer((ulong)(i + 1)), PeerState.Ready);
            });

            Assert.Equal(200, registry.Count);
            Assert.Equal(200, registry.ReadyPeers.Count);
            Assert.Equal(200, seen.Count);
        }
    }
}
