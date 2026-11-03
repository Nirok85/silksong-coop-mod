using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Boss death records must survive capacity pressure: they are the only records a
    /// late joiner still needs after the boss room has been unloaded.
    /// </summary>
    public class DeadEnemyRegistryBossEvictionTests
    {
        [Fact]
        public void Register_AtCapacity_EvictsRegularEnemyBeforeBoss()
        {
            var registry = new DeadEnemyRegistry(capacity: 3);

            registry.Register(100, 0f, 0f, isBoss: true);
            registry.Register(1, 1f, 1f, isBoss: false);
            registry.Register(2, 2f, 2f, isBoss: false);

            // Capacity reached: the oldest ordinary enemy goes first, not the boss.
            registry.Register(3, 3f, 3f, isBoss: false);

            Assert.True(registry.Contains(100));
            Assert.False(registry.Contains(1));
            Assert.True(registry.Contains(2));
            Assert.True(registry.Contains(3));
            Assert.Equal(3, registry.Count);
        }

        [Fact]
        public void Register_WhenOnlyBossesRemain_EvictsOldestBoss()
        {
            var registry = new DeadEnemyRegistry(capacity: 2);

            registry.Register(100, 0f, 0f, isBoss: true);
            registry.Register(200, 0f, 0f, isBoss: true);
            registry.Register(300, 0f, 0f, isBoss: true);

            Assert.False(registry.Contains(100));
            Assert.True(registry.Contains(200));
            Assert.True(registry.Contains(300));
            Assert.Equal(2, registry.Count);
        }
    }
}
