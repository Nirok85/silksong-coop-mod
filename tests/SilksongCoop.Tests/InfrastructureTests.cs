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
            string a = detector.ComputeChecksum("scene|5|1|27|100");
            string b = detector.ComputeChecksum("scene|5|1|27|100");
            string c = detector.ComputeChecksum("scene|5|1|27|101");

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

            detector.Update(10f, "AAAAAAAA");
            detector.OnRemoteChecksumReceived("AAAAAAAA", "state");
            Assert.Equal(0, events);

            detector.OnRemoteChecksumReceived("BBBBBBBB", "state");
            Assert.Equal(1, events);
        }

        [Fact]
        public void DesyncDetector_IgnoresRemoteChecksumBeforeLocalSample()
        {
            var bus = new EventBus();
            var detector = new Core.State.DesyncDetector(bus);
            int events = 0;
            bus.Subscribe<Core.State.DesyncDetector.DesyncDetectedEvent>(_ => events++);

            detector.OnRemoteChecksumReceived("AAAAAAAA", "state");
            Assert.Equal(0, events);
        }

        [Fact]
        public void PacketHeader_AcceptsBatchType()
        {
            var buffer = new byte[SilksongCoop.Core.Protocol.PacketBatch.HeaderSize];
            SilksongCoop.Core.Protocol.PacketBatch.WriteHeader(buffer, 0, 0, 2);

            Assert.True(SilksongCoop.Core.Protocol.PacketHeader.TryReadFrom(buffer, out var header));
            Assert.True(header.IsValidPacketType);
            Assert.Equal(SilksongCoop.Core.Protocol.PacketType.Batch, header.PacketId);
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

