using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Regression tests for the boss-death catch-up registry introduced in 3.0.1.
    /// </summary>
    public class DeadEnemyRegistryTests
    {
        [Fact]
        public void DropRespawnable_KeepsBossesAndRemovesRegularEnemies()
        {
            var registry = new DeadEnemyRegistry();
            registry.Register(1, 10f, 20f, isBoss: false);
            registry.Register(2, 30f, 40f, isBoss: true);

            int removed = registry.DropRespawnable();

            Assert.Equal(1, removed);
            Assert.False(registry.Contains(1));
            Assert.True(registry.Contains(2));
            Assert.Equal(1, registry.Count);
            Assert.Equal(30f, registry.Records[2].X);
            Assert.Equal(40f, registry.Records[2].Y);
            Assert.True(registry.Records[2].IsBoss);
        }

        [Fact]
        public void Register_SameKeyUpdatesRecordWithoutGrowingTheRegistry()
        {
            var registry = new DeadEnemyRegistry(capacity: 2);
            registry.Register(1, 1f, 2f, isBoss: false);
            registry.Register(2, 3f, 4f, isBoss: false);

            registry.Register(1, 9f, 8f, isBoss: true);

            Assert.Equal(2, registry.Count);
            Assert.True(registry.Contains(1));
            Assert.True(registry.Contains(2));
            Assert.Equal(9f, registry.Records[1].X);
            Assert.True(registry.Records[1].IsBoss);
        }

        [Fact]
        public void Register_EvictsTheOldestRecordAtCapacity()
        {
            var registry = new DeadEnemyRegistry(capacity: 2);
            registry.Register(1, 0f, 0f, isBoss: false);
            registry.Register(2, 0f, 0f, isBoss: false);
            registry.Register(3, 0f, 0f, isBoss: true);

            Assert.False(registry.Contains(1));
            Assert.True(registry.Contains(2));
            Assert.True(registry.Contains(3));
            Assert.Equal(2, registry.Count);
        }

        [Fact]
        public void DropRespawnable_RemovesEverythingWhenNoBossWasKilled()
        {
            var registry = new DeadEnemyRegistry();
            registry.Register(1, 0f, 0f, isBoss: false);
            registry.Register(2, 0f, 0f, isBoss: false);

            Assert.Equal(2, registry.DropRespawnable());
            Assert.Equal(0, registry.Count);
        }
    }
}
