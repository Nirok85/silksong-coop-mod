using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests.State
{
    public class StableObjectKeyTests
    {
        [Fact]
        public void HashString_IsDeterministicAndNonZero()
        {
            ulong first = StableObjectKey.HashString("Silksong|Cave_01|Object#3");
            ulong second = StableObjectKey.HashString("Silksong|Cave_01|Object#3");

            Assert.Equal(first, second);
            Assert.NotEqual(0UL, first);
        }

        [Fact]
        public void HashString_Empty_ReturnsFnvOffset()
        {
            Assert.Equal(14695981039346656037UL, StableObjectKey.HashString(string.Empty));
        }

        [Fact]
        public void Persistent_SameInputs_AreStable()
        {
            ulong first = StableObjectKey.Persistent("Cave_01", "door_03", 0);
            ulong second = StableObjectKey.Persistent("Cave_01", "door_03", 0);

            Assert.Equal(first, second);
            Assert.NotEqual(0UL, first);
        }

        [Fact]
        public void Persistent_DifferentSceneIdOrType_ProduceDifferentKeys()
        {
            ulong baseline = StableObjectKey.Persistent("Cave_01", "door_03", 0);

            Assert.NotEqual(baseline, StableObjectKey.Persistent("Cave_02", "door_03", 0));
            Assert.NotEqual(baseline, StableObjectKey.Persistent("Cave_01", "door_04", 0));
            Assert.NotEqual(baseline, StableObjectKey.Persistent("Cave_01", "door_03", 1));
        }

        [Fact]
        public void Persistent_EmptyId_IsZero()
        {
            Assert.Equal(0UL, StableObjectKey.Persistent("Cave_01", string.Empty, 0));
            Assert.Equal(0UL, StableObjectKey.Persistent("Cave_01", null, 1));
        }

        [Fact]
        public void PickupFromPersistent_IsStableAndDisjointFromWorldState()
        {
            ulong pickup = StableObjectKey.PickupFromPersistent("Cave_01", "drop_17");
            ulong samePickup = StableObjectKey.PickupFromPersistent("Cave_01", "drop_17");
            ulong boolKey = StableObjectKey.Persistent("Cave_01", "drop_17", 0);
            ulong intKey = StableObjectKey.Persistent("Cave_01", "drop_17", 1);

            Assert.Equal(pickup, samePickup);
            Assert.NotEqual(0UL, pickup);
            Assert.NotEqual(boolKey, pickup);
            Assert.NotEqual(intKey, pickup);
        }

        [Fact]
        public void PickupFromPersistent_EmptyId_IsZero()
        {
            Assert.Equal(0UL, StableObjectKey.PickupFromPersistent("Cave_01", string.Empty));
            Assert.Equal(0UL, StableObjectKey.PickupFromPersistent("Cave_01", null));
        }
    }
}
