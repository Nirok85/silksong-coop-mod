using System;
using SilksongCoop.Core.DI;
using SilksongCoop.Core.Events;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Pooling;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Tests for the new DI container, EventBus and network optimization helpers.
    /// </summary>
    public class InfrastructureTests
    {
        [Fact]
        public void ServiceContainer_RegistersAndResolvesInstance()
        {
            var container = new ServiceContainer();
            var interest = new StubInterestManager();
            container.Register<IInterestManager>(interest);

            var resolved = container.Resolve<IInterestManager>();
            Assert.Same(interest, resolved);
        }

        [Fact]
        public void ServiceContainer_ResolvesTransient()
        {
            var container = new ServiceContainer();
            container.Register<IInterestManager, StubInterestManager>();

            var a = container.Resolve<IInterestManager>();
            var b = container.Resolve<IInterestManager>();
            Assert.NotSame(a, b);
        }

        [Fact]
        public void EventBus_PublishesToSubscribers()
        {
            var bus = new EventBus();
            int calls = 0;
            bus.Subscribe<TestEvent>(_ => calls++);
            bus.Publish(new TestEvent { Value = 1 });
            bus.Publish(new TestEvent { Value = 2 });
            Assert.Equal(2, calls);
        }

        [Fact]
        public void EventBus_UnsubscribeWorks()
        {
            var bus = new EventBus();
            int calls = 0;
            void Handler(TestEvent e) => calls++;
            bus.Subscribe<TestEvent>(Handler);
            bus.Publish(new TestEvent());
            bus.Unsubscribe<TestEvent>(Handler);
            bus.Publish(new TestEvent());
            Assert.Equal(1, calls);
        }

        [Fact]
        public void AdaptiveTickRate_ReducesRateUnderBadConditions()
        {
            var rate = new AdaptiveTickRate(30f);
            Assert.Equal(30f, rate.CurrentTickHz);

            // Simulate poor connection
            for (int i = 0; i < 20; i++)
            {
                rate.Update(1f + i * 0.6f, 300f, 8f);
            }

            Assert.True(rate.CurrentTickHz < 30f, $"Expected tick rate to drop, got {rate.CurrentTickHz}");
        }

        [Fact]
        public void AdaptiveTickRate_IgnoresInvalidConfiguredRate()
        {
            var rate = new AdaptiveTickRate(0f);

            Assert.Equal(30f, rate.CurrentTickHz);
            Assert.True(float.IsFinite(rate.CurrentInterval));
            Assert.True(rate.CurrentInterval > 0f);
        }

        [Fact]
        public void AdaptiveTickRate_ResetAppliesNextSampleImmediately()
        {
            var rate = new AdaptiveTickRate(30f);
            for (int i = 0; i < 20; i++)
                rate.Update(1f + i * 0.6f, 500f, 20f);
            Assert.True(rate.CurrentTickHz < 30f);

            rate.Reset();
            Assert.Equal(30f, rate.CurrentTickHz);

            rate.Update(1000f, 500f, 20f);
            Assert.True(rate.CurrentTickHz < 30f);
        }

        [Fact]
        public void InterestManager_FiltersByDistance()
        {
            var interest = new global::SilksongCoop.Core.Networking.InterestManager { DefaultRadius = 10f, Hysteresis = 2f };
            interest.UpdateEntity(1, new System.Numerics.Vector2(0, 0));
            interest.UpdateEntity(2, new System.Numerics.Vector2(50, 0));

            bool inside = interest.IsInInterest(1, new System.Numerics.Vector2(0, 0), 2);
            bool outside = interest.IsInInterest(1, new System.Numerics.Vector2(50, 0), 2);

            Assert.False(inside);
            Assert.True(outside);
        }

        [Fact]
        public void InterestManager_HysteresisKeepsVisibleEntityInsideLeaveRange()
        {
            var interest = new global::SilksongCoop.Core.Networking.InterestManager
            {
                DefaultRadius = 10f,
                Hysteresis = 5f
            };
            interest.UpdateEntity(2, new System.Numerics.Vector2(0, 0));

            // Enter at zero distance, then move to a point outside the radius but inside
            // radius + hysteresis: the pair must stay visible.
            Assert.True(interest.IsInInterest(1, new System.Numerics.Vector2(0, 0), 2));
            Assert.True(interest.IsInInterest(1, new System.Numerics.Vector2(12, 0), 2));

            // Beyond radius + hysteresis it leaves, and a later re-entry needs the full radius.
            Assert.False(interest.IsInInterest(1, new System.Numerics.Vector2(16, 0), 2));
            Assert.False(interest.IsInInterest(1, new System.Numerics.Vector2(12, 0), 2));
        }

        [Fact]
        public void InterestManager_HysteresisDoesNotApplyBeforeFirstSighting()
        {
            var interest = new global::SilksongCoop.Core.Networking.InterestManager
            {
                DefaultRadius = 10f,
                Hysteresis = 5f
            };
            interest.UpdateEntity(2, new System.Numerics.Vector2(12, 0));

            Assert.False(interest.IsInInterest(1, System.Numerics.Vector2.Zero, 2));
        }

        [Fact]
        public void ObjectPool_ReusesObjects()
        {
            var pool = new ObjectPool<Poolable>(initialSize: 2);
            var a = pool.Get();
            var b = pool.Get();
            pool.Return(a);
            var c = pool.Get();
            Assert.Same(a, c);
        }

        [Fact]
        public void DesyncDetector_ComputesStableChecksums()
        {
            var detector = new Core.State.DesyncDetector(new EventBus());
            string a = detector.ComputeChecksumValue("scene|5|1|27|100").ToString("X8");
            string b = detector.ComputeChecksumValue("scene|5|1|27|100").ToString("X8");
            string c = detector.ComputeChecksumValue("scene|5|1|27|101").ToString("X8");

            Assert.Equal(a, b);
            Assert.NotEqual(a, c);
            Assert.Equal(8, a.Length);
        }

        [Fact]
        public void DesyncDetector_PublishesEventOnlyOnMismatch()
        {
            var bus = new EventBus();
            var detector = new Core.State.DesyncDetector(bus);
            int events = 0;
            bus.Subscribe<Core.State.DesyncDetector.DesyncDetectedEvent>(_ => events++);

            uint a = detector.ComputeChecksumValue("AAAAAAAA");
            uint b = detector.ComputeChecksumValue("BBBBBBBB");

            detector.Update(10f, a, round: 1);
            Assert.False(detector.OnRemoteChecksumReceived(a, round: 1, now: 10.1f));
            Assert.Equal(0, events);

            Assert.True(detector.OnRemoteChecksumReceived(b, round: 1, now: 10.2f));
            Assert.Equal(1, events);
        }

        [Fact]
        public void DesyncDetector_IgnoresMismatchFromAnotherRound()
        {
            var bus = new EventBus();
            var detector = new Core.State.DesyncDetector(bus);
            int events = 0;
            bus.Subscribe<Core.State.DesyncDetector.DesyncDetectedEvent>(_ => events++);

            uint local = detector.ComputeChecksumValue("local");
            uint remote = detector.ComputeChecksumValue("remote");

            detector.Update(10f, local, round: 5);

            // A sample from a different round describes another moment in time.
            Assert.False(detector.OnRemoteChecksumReceived(remote, round: 4, now: 10.1f));
            Assert.Equal(0, events);
        }

        [Fact]
        public void DesyncDetector_ComparesBufferedRemoteSampleOnNextUpdate()
        {
            var bus = new EventBus();
            var detector = new Core.State.DesyncDetector(bus);
            int events = 0;
            bus.Subscribe<Core.State.DesyncDetector.DesyncDetectedEvent>(_ => events++);

            uint local = detector.ComputeChecksumValue("local");
            uint remote = detector.ComputeChecksumValue("remote");

            // Remote sample for round 3 arrives before the local peer samples round 3.
            Assert.False(detector.OnRemoteChecksumReceived(remote, round: 3, now: 1f));
            Assert.Equal(0, events);

            detector.Update(2f, local, round: 3);
            Assert.Equal(1, events);
        }

        [Fact]
        public void DesyncDetector_CooldownPreventsEventStorm()
        {
            var bus = new EventBus();
            var detector = new Core.State.DesyncDetector(bus) { ReportCooldownSeconds = 10f };
            int events = 0;
            bus.Subscribe<Core.State.DesyncDetector.DesyncDetectedEvent>(_ => events++);

            uint local = detector.ComputeChecksumValue("local");
            uint remote = detector.ComputeChecksumValue("remote");

            detector.Update(10f, local, round: 1);
            Assert.True(detector.OnRemoteChecksumReceived(remote, round: 1, now: 11f));
            Assert.False(detector.OnRemoteChecksumReceived(remote, round: 1, now: 12f));
            Assert.Equal(1, events);
        }

        [Fact]
        public void DesyncDetector_DefersRemoteChecksumBeforeLocalSample()
        {
            var bus = new EventBus();
            var detector = new Core.State.DesyncDetector(bus);
            int events = 0;
            bus.Subscribe<Core.State.DesyncDetector.DesyncDetectedEvent>(_ => events++);

            Assert.False(detector.OnRemoteChecksumReceived(1u, round: 2, now: 1f));
            Assert.Equal(0, events);
        }

        [Fact]
        public void DesyncDetector_ComparesLateRemoteSampleFromRetainedHistory()
        {
            var bus = new EventBus();
            var detector = new Core.State.DesyncDetector(bus);
            int events = 0;
            bus.Subscribe<Core.State.DesyncDetector.DesyncDetectedEvent>(_ => events++);

            uint round1Local = detector.ComputeChecksumValue("round-1-local");
            uint round2Local = detector.ComputeChecksumValue("round-2-local");
            uint round1Remote = detector.ComputeChecksumValue("round-1-remote");

            detector.Update(10f, round1Local, round: 1);
            detector.Update(20f, round2Local, round: 2);

            // The checksum frame for round 1 was delayed on the unreliable channel; it must
            // still be compared against the retained local sample for round 1.
            Assert.True(detector.OnRemoteChecksumReceived(round1Remote, round: 1, now: 30f));
            Assert.Equal(1, events);
        }

        [Fact]
        public void DesyncDetector_LateMatchingSampleDoesNotReport()
        {
            var bus = new EventBus();
            var detector = new Core.State.DesyncDetector(bus);
            int events = 0;
            bus.Subscribe<Core.State.DesyncDetector.DesyncDetectedEvent>(_ => events++);

            uint local = detector.ComputeChecksumValue("same-state");
            detector.Update(10f, local, round: 1);
            detector.Update(20f, local, round: 2);

            Assert.False(detector.OnRemoteChecksumReceived(local, round: 1, now: 30f));
            Assert.Equal(0, events);
        }

        [Fact]
        public void DesyncDetector_RetainsCurrentRoundWhenHistoryIsFull()
        {
            var bus = new EventBus();
            var detector = new Core.State.DesyncDetector(bus) { ReportCooldownSeconds = 0f };
            int events = 0;
            bus.Subscribe<Core.State.DesyncDetector.DesyncDetectedEvent>(_ => events++);

            uint local = detector.ComputeChecksumValue("local");
            for (uint round = 1; round <= 17; round++)
                detector.Update(round, local, round);

            // Round 17 is the ninth consecutive sample: pruning must evict the oldest
            // retained round, not the sample that was just inserted.
            uint remote = detector.ComputeChecksumValue("remote");
            Assert.True(detector.OnRemoteChecksumReceived(remote, round: 17, now: 100f));
            Assert.Equal(1, events);
        }

        [Fact]
        public void DesyncDetector_BackwardClockStep_ReanchorsHistory()
        {
            var bus = new EventBus();
            var detector = new Core.State.DesyncDetector(bus) { ReportCooldownSeconds = 0f };
            int events = 0;
            bus.Subscribe<Core.State.DesyncDetector.DesyncDetectedEvent>(_ => events++);

            uint local = detector.ComputeChecksumValue("local");
            for (uint round = 100; round <= 108; round++)
                detector.Update(round, local, round);

            // A manual clock change moved the round far backwards. Stale samples from the
            // old timeline must not disable comparison until the clock catches up.
            detector.Update(10f, local, round: 10);

            uint remote = detector.ComputeChecksumValue("remote");
            Assert.True(detector.OnRemoteChecksumReceived(remote, round: 10, now: 200f));
            Assert.Equal(1, events);
        }

        private class TestEvent : GameEvent
        {
            public int Value { get; set; }
        }

        private class Poolable { }

        private interface IInterestManager { }
        private class StubInterestManager : IInterestManager { }
    }
}

