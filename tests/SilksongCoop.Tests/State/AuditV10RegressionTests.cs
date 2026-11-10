using System;
using System.Collections.Generic;
using SilksongCoop.Core.DI;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests.State
{
    public sealed class AuditV10RegressionTests
    {
        [Fact]
        public void LedgerAcceptsWrappedRevisionAndRejectsOldWatermark()
        {
            var ledger = new RevisionLedger();
            ledger.Record(1, uint.MaxValue, 2);
            Assert.False(ledger.IsStale(1, 1, 2));
            ledger.Record(1, 1, 2);
            Assert.True(ledger.IsStale(1, uint.MaxValue, 2));
        }

        [Fact]
        public void DeadRegistrySnapshotCannotMutateBackingState()
        {
            var registry = new DeadEnemyRegistry();
            registry.Register(1, 0, 0, true);
            var snapshot = registry.Records;
            Assert.Throws<NotSupportedException>(() => ((IDictionary<ulong, DeadEnemyRecord>)snapshot).Clear());
            registry.Clear();
            Assert.Single(snapshot);
            Assert.Empty(registry.Records);
        }

        [Fact]
        public void SingletonCycleFailsInsteadOfRecursing()
        {
            var container = new ServiceContainer();
            container.RegisterSingleton<string>(_ => container.Resolve<string>());
            Assert.Throws<InvalidOperationException>(() => container.Resolve<string>());
        }

        [Fact]
        public void InvalidActionDirectionAndPickupCoordinatesAreRejected()
        {
            Assert.False(PacketValidator.ValidateActionTrigger(new ActionTriggerPacket
            {
                Action = ActionType.HarpoonDash,
                DirectionX = 50
            }));
            Assert.False(PacketValidator.ValidateCurrencySync(new CurrencySyncPacket
            {
                Currency = CurrencyType.Rosaries,
                PickupX = float.NaN,
                PickupY = float.PositiveInfinity
            }));
        }
    }
}
