using System;
using System.Collections.Generic;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests.Reliability
{
    /// <summary>
    /// Deterministic chaos scenarios layered on the in-memory transport. The tests assert
    /// application invariants (ordering, no double damage, monotonic snapshots), not just
    /// "did not throw": a lossy, jittery link is allowed to delay data, never to corrupt
    /// already-applied state.
    /// </summary>
    public class ChaosSimulationTests
    {
        [Fact]
        public void ReliableOrdered_UnderJitterAndSimulatedLoss_DeliversEveryPacketInOrder()
        {
            var (host, client) = MockNetworkTransport.CreatePair(
                latencyMs: 60,
                jitterMs: 120,
                lossRate: 0.45f,
                useVirtualTime: true,
                seed: 20261109);

            const int packetCount = 200;
            var received = new List<int>(packetCount);
            client.OnDataReceived += (_, data, offset, length) =>
            {
                if (length == 4) received.Add(BitConverter.ToInt32(data, offset));
            };

            for (int i = 0; i < packetCount; i++)
            {
                byte[] payload = BitConverter.GetBytes(i);
                host.Send(client.LocalPeerId, payload, 0, payload.Length,
                    DeliveryMethod.ReliableOrdered, NetworkChannels.World);
            }

            for (int step = 0; step < 20 && received.Count < packetCount; step++)
            {
                host.AdvanceVirtualTime(100);
                client.AdvanceVirtualTime(100);
                host.PollEvents();
                client.PollEvents();
            }

            Assert.Equal(packetCount, received.Count);
            for (int i = 0; i < packetCount; i++)
                Assert.Equal(i, received[i]);
        }

        [Fact]
        public void SequencedChannel_ReorderedArrival_AppliesOnlyIncreasingSequences()
        {
            var transport = new MockNetworkTransport(2, "Client", seed: 7)
            {
                UseVirtualTime = true
            };

            // Simulate the network delivering these in an arbitrary order. The transport
            // must drop every packet older than the newest one it has accepted.
            uint[] arrivalOrder = { 5, 3, 9, 4, 8, 2, 7, 6, 1, 10 };
            var delivered = new List<uint>();
            transport.OnDataReceived += (_, data, offset, length) =>
                delivered.Add(BitConverter.ToUInt32(data, offset));

            // Connect a peer so PollEvents does not return early.
            var peer = new MockNetworkTransport(1, "Host", seed: 8) { UseVirtualTime = true };
            transport.SetPeer(peer);
            peer.SetPeer(transport);
            peer.StartHost();
            transport.Connect("host");

            foreach (uint sequence in arrivalOrder)
            {
                byte[] payload = BitConverter.GetBytes(sequence);
                transport.EnqueueIncoming(new MockNetworkTransport.QueuedPacket
                {
                    DeliveryTimeTicks = 0,
                    Channel = NetworkChannels.Transform,
                    Method = DeliveryMethod.Sequenced,
                    Sequence = sequence,
                    Data = payload,
                    Sender = peer.LocalPeerId
                });
            }

            transport.AdvanceVirtualTime(1);
            transport.PollEvents();

            Assert.NotEmpty(delivered);
            for (int i = 1; i < delivered.Count; i++)
                Assert.True(delivered[i] > delivered[i - 1],
                    $"sequence regressed: {delivered[i - 1]} -> {delivered[i]}");
        }

        [Fact]
        public void HostAuthority_DuplicateAndReplayedHits_ApplyDamageExactlyOnce()
        {
            var service = new EnemySyncService();
            const ulong enemyKey = 0xA11CE;
            const short damage = 25;
            int appliedHp = 80;

            // The host is at 80 HP. The client's claim arrives with sequence 7 and reports
            // the same post-hit value: the host treats the claim as the partner's hit and
            // subtracts it from its own simulation once.
            service.NoteCurrentEnemyState(enemyKey, appliedHp, dead: false);

            bool first = service.TryAcceptRemoteHit(
                enemyKey, appliedHp, dead: false, damage, senderId: 2, sequence: 7,
                RemoteHitPolicy.LocalHost, out int firstHp);

            Assert.True(first);
            Assert.Equal(55, firstHp);

            // A reliable retransmission/replay of the same event must not subtract again.
            // The out value is undefined on rejection, so only the return value is asserted.
            for (int i = 0; i < 100; i++)
            {
                bool duplicate = service.TryAcceptRemoteHit(
                    enemyKey, appliedHp, dead: false, damage, senderId: 2, sequence: 7,
                    RemoteHitPolicy.LocalHost, out _);

                Assert.False(duplicate);
            }

            // A newer hit from the same sender is a different event and must apply.
            bool next = service.TryAcceptRemoteHit(
                enemyKey, 55, dead: false, damage, senderId: 2, sequence: 8,
                RemoteHitPolicy.LocalHost, out int nextHp);

            Assert.True(next);
            Assert.Equal(30, nextHp);
        }

        [Fact]
        public void SnapshotStream_OutOfOrderTimestamps_NeverAcceptsStaleOrResurrects()
        {
            var service = new EnemySyncService();
            const ulong enemyKey = 0xBEEF;
            var random = new Random(424242);

            uint acceptedTimestamp = 0;
            bool acceptedDead = false;

            for (int i = 0; i < 2000; i++)
            {
                uint timestamp = (uint)random.Next(0, 5000);
                int hp = random.Next(0, 200);
                bool dead = random.Next(0, 10) == 0;

                bool accepted = service.TryAcceptSnapshot(enemyKey, hp, dead, timestamp);
                if (!accepted) continue;

                // A dead enemy is terminal for this service instance: repeated death
                // confirmations are allowed, resurrection is not.
                if (!dead)
                    Assert.False(acceptedDead, "a snapshot resurrected a dead enemy");
                Assert.True(timestamp > acceptedTimestamp,
                    $"accepted a stale snapshot: {timestamp} <= {acceptedTimestamp}");

                acceptedTimestamp = timestamp;
                acceptedDead = acceptedDead || dead;
            }
        }

        [Fact]
        public void SnapshotBeforeLocalHitIsAcknowledged_CannotRaiseHp()
        {
            var service = new EnemySyncService();
            const ulong enemyKey = 0xCAFE;

            Assert.True(service.TryAcceptSnapshot(enemyKey, 100, dead: false, timestamp: 1));
            service.NoteLocalEnemyState(enemyKey, 70, dead: false);

            // A snapshot that predates the local hit reports 95 HP. Accepting it would
            // visually heal the enemy until the host receives the hit, so it is rejected.
            Assert.False(service.TryAcceptSnapshot(enemyKey, 95, dead: false, timestamp: 2));

            // A snapshot generated after the hit may lower HP further.
            Assert.True(service.TryAcceptSnapshot(enemyKey, 60, dead: false, timestamp: 3));

            // Once a post-hit snapshot has been accepted, the host value is authoritative
            // again; a later higher value is a legitimate host-side correction (heal/phase)
            // and is allowed by the authority model.
            Assert.True(service.TryAcceptSnapshot(enemyKey, 80, dead: false, timestamp: 4));
        }
    }
}
