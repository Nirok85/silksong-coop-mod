using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests.State
{
    /// <summary>
    /// Regressions for the authority policy added after the 3.3.0 audit:
    /// a host snapshot already contains every hit the host received, so a later client hit
    /// must not be subtracted again, and a client must not declare a death the host has not
    /// confirmed. These tests are separate from the legacy additive contract in
    /// <see cref="EnemySyncServiceDamageTests"/>, which stays unchanged.
    /// </summary>
    public class EnemySyncAuthorityTests
    {
        [Fact]
        public void HostSnapshotThenHostHit_ReplacesHpInsteadOfSubtractingDamage()
        {
            var service = new EnemySyncService();
            Assert.True(service.TryAcceptSnapshot(1, 90, dead: false, timestamp: 10));

            // The 90 HP snapshot already includes a 10-damage hit whose packet arrives late.
            // Absolute authority semantics must keep 90 rather than subtracting to 80.
            Assert.False(service.TryAcceptRemoteHit(
                1, 90, dead: false, damage: 10, senderId: 2, sequence: 1,
                RemoteHitPolicy.Authoritative, out _));

            Assert.True(service.TryAcceptRemoteHit(
                1, 80, dead: false, damage: 10, senderId: 2, sequence: 2,
                RemoteHitPolicy.Authoritative, out int hp));
            Assert.Equal(80, hp);
        }

        [Fact]
        public void HostHit_AfterALocalHit_IsCombinedImmediately()
        {
            var service = new EnemySyncService();
            Assert.True(service.TryAcceptSnapshot(1, 100, dead: false, timestamp: 10));

            // The local player hit after that snapshot, so the snapshot cannot contain the
            // local hit; combining the host's 10 damage must be immediate rather than
            // waiting for the next snapshot.
            service.NoteLocalEnemyState(1, 90, dead: false);

            Assert.True(service.TryAcceptRemoteHit(
                1, 90, dead: false, damage: 10, senderId: 2, sequence: 1,
                RemoteHitPolicy.Authoritative, out int hp));
            Assert.Equal(80, hp);
        }

        [Fact]
        public void NonHostHit_IsDeferredWhileHostIsAuthoritative()
        {
            var service = new EnemySyncService();
            Assert.True(service.TryAcceptSnapshot(1, 90, dead: false, timestamp: 10));

            Assert.False(service.TryAcceptRemoteHit(
                1, 80, dead: false, damage: 10, senderId: 3, sequence: 1,
                RemoteHitPolicy.Deferred, out _));

            // The deferred packet must not change the baseline: the host's next snapshot is
            // the only thing allowed to move it.
            Assert.True(service.TryAcceptRemoteHit(
                1, 70, dead: false, damage: 20, senderId: 2, sequence: 2,
                RemoteHitPolicy.Authoritative, out int hp));
            Assert.Equal(70, hp);
        }

        [Fact]
        public void NonHostDeath_IsDeferredWhileHostIsAuthoritative()
        {
            var service = new EnemySyncService();
            Assert.True(service.TryAcceptSnapshot(1, 50, dead: false, timestamp: 10));

            // Another client reports a kill the host never confirmed.
            Assert.False(service.TryAcceptRemoteHit(
                1, 0, dead: true, damage: 50, senderId: 3, sequence: 1,
                RemoteHitPolicy.Deferred, out _));

            // The enemy stays alive and can receive the host's authoritative damage.
            Assert.True(service.TryAcceptRemoteHit(
                1, 40, dead: false, damage: 10, senderId: 2, sequence: 3,
                RemoteHitPolicy.Authoritative, out int hp));
            Assert.Equal(40, hp);
        }

        [Fact]
        public void HostDeath_ConfirmsTheKill()
        {
            var service = new EnemySyncService();
            Assert.True(service.TryAcceptSnapshot(1, 10, dead: false, timestamp: 10));

            Assert.True(service.TryAcceptRemoteHit(
                1, 0, dead: true, damage: 10, senderId: 2, sequence: 1,
                RemoteHitPolicy.Authoritative, out int hp));
            Assert.Equal(0, hp);

            // Once dead, neither a stale host hit nor another client can resurrect it.
            Assert.False(service.TryAcceptRemoteHit(
                1, 10, dead: false, damage: 5, senderId: 2, sequence: 2,
                RemoteHitPolicy.Authoritative, out _));
            Assert.False(service.TryAcceptRemoteHit(
                1, 5, dead: false, damage: 5, senderId: 3, sequence: 1,
                RemoteHitPolicy.NoAuthority, out _));
        }

        [Fact]
        public void NoAuthority_ClientKillIsAccepted()
        {
            var service = new EnemySyncService();
            service.NoteLocalEnemyState(1, 5, dead: false);

            // Without a host snapshot for this enemy, clients may resolve the kill locally.
            Assert.True(service.TryAcceptRemoteHit(
                1, 0, dead: true, damage: 10, senderId: 3, sequence: 1,
                RemoteHitPolicy.NoAuthority, out int hp));
            Assert.Equal(0, hp);
        }

        [Fact]
        public void LocalHost_RemainsAdditive()
        {
            var service = new EnemySyncService();
            service.NoteLocalEnemyState(1, 90, dead: false);

            Assert.True(service.TryAcceptRemoteHit(
                1, 90, dead: false, damage: 10, senderId: 2, sequence: 1,
                RemoteHitPolicy.LocalHost, out int hp));
            Assert.Equal(80, hp);
        }

        [Fact]
        public void StaleDeath_IsRejectedBySequence()
        {
            var service = new EnemySyncService();
            service.NoteLocalEnemyState(1, 80, dead: false);

            Assert.True(service.TryAcceptRemoteHit(
                1, 70, dead: false, damage: 10, senderId: 2, sequence: 10,
                RemoteHitPolicy.Authoritative, out _));

            // A delayed death packet with an older sequence must not override the newer hit.
            Assert.False(service.TryAcceptRemoteHit(
                1, 0, dead: true, damage: 10, senderId: 2, sequence: 9,
                RemoteHitPolicy.Authoritative, out _));
        }

        [Fact]
        public void DuplicateAuthoritativeHit_IsRejected()
        {
            var service = new EnemySyncService();
            Assert.True(service.TryAcceptSnapshot(1, 100, dead: false, timestamp: 1));

            Assert.True(service.TryAcceptRemoteHit(
                1, 90, dead: false, damage: 10, senderId: 2, sequence: 1,
                RemoteHitPolicy.Authoritative, out _));
            Assert.False(service.TryAcceptRemoteHit(
                1, 90, dead: false, damage: 10, senderId: 2, sequence: 1,
                RemoteHitPolicy.Authoritative, out _));
        }

        [Fact]
        public void DeferredPacket_DoesNotConsumeTheEnemySlot()
        {
            var service = new EnemySyncService();
            Assert.True(service.TryAcceptSnapshot(1, 90, dead: false, timestamp: 10));

            for (uint sequence = 1; sequence <= 50; sequence++)
            {
                Assert.False(service.TryAcceptRemoteHit(
                    1, 80, dead: false, damage: 10, senderId: 3, sequence,
                    RemoteHitPolicy.Deferred, out _));
            }

            // Deferred packets must not poison the host baseline.
            Assert.True(service.TryAcceptRemoteHit(
                1, 85, dead: false, damage: 5, senderId: 2, sequence: 51,
                RemoteHitPolicy.Authoritative, out int hp));
            Assert.Equal(85, hp);
        }
    }
}
