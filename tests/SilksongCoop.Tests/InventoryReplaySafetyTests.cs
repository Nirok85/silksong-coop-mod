using System;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests
{
    public sealed class InventoryReplaySafetyTests
    {
        private static readonly NetworkPeerId Peer = new NetworkPeerId(2);
        private static ItemUnlockPacket Counter(uint sequence, Guid? transaction = null) => new ItemUnlockPacket
        {
            Header = new PacketHeader(PacketType.ItemUnlockSync, 3, sequence, 100),
            ItemId = "heartPieces:+1",
            Category = ItemCategory.KeyItem,
            TransactionId = transaction ?? Guid.NewGuid()
        };

        [Fact]
        public void CounterReplayAfterGuidEvictionDoesNotApplyAgain()
        {
            var service = new InventorySyncService();
            int applied = 0;
            service.OnRemoteItemUnlocked += (_, _) => applied++;
            var first = Counter(1);
            service.HandleItemUnlockPacket(Peer, first);
            for (uint i = 2; i <= 550; i++)
                service.HandleItemUnlockPacket(Peer, Counter(i));
            Assert.Equal(550, applied);
            service.HandleItemUnlockPacket(Peer, first);
            Assert.Equal(550, applied);
            Assert.True(service.ProcessedCounterTransactionsCount <= 500);
        }

        [Fact]
        public void CounterWatermarksArePerSenderAndWrapSafe()
        {
            var service = new InventorySyncService();
            int applied = 0;
            service.OnRemoteItemUnlocked += (_, _) => applied++;
            service.HandleItemUnlockPacket(Peer, Counter(uint.MaxValue - 1));
            service.HandleItemUnlockPacket(Peer, Counter(uint.MaxValue));
            service.HandleItemUnlockPacket(Peer, Counter(0));
            service.HandleItemUnlockPacket(Peer, Counter(1));
            service.HandleItemUnlockPacket(Peer, Counter(uint.MaxValue));
            service.HandleItemUnlockPacket(new NetworkPeerId(3), Counter(1));
            Assert.Equal(5, applied);
        }

        [Fact]
        public void LegacyCounterReplayRemainsRejectedAfterDiagnosticFifoEviction()
        {
            var service = new InventorySyncService();
            int applied = 0;
            service.OnRemoteItemUnlocked += (_, _) => applied++;
            var first = Counter(0);
            service.HandleItemUnlockPacket(Peer, first);
            for (int i = 0; i < 600; i++)
                service.HandleItemUnlockPacket(Peer, Counter(0));
            Assert.False(service.IsTransactionProcessed(first.TransactionId));
            service.HandleItemUnlockPacket(Peer, first);
            Assert.Equal(601, applied);
        }

        [Fact]
        public void FailedApplicationDoesNotCommitAndCannotBeOvertakenByLaterCounter()
        {
            var service = new InventorySyncService();
            bool ready = false;
            int applied = 0;
            service.RemoteItemApplier = (_, _) => { if (!ready) return false; applied++; return true; };
            var first = Counter(1);
            var next = Counter(2);
            Assert.False(service.TryHandleItemUnlockPacket(Peer, first));
            Assert.False(service.IsTransactionProcessed(first.TransactionId));
            Assert.False(service.TryHandleItemUnlockPacket(Peer, next));
            ready = true;
            Assert.True(service.TryHandleItemUnlockPacket(Peer, first));
            Assert.True(service.TryHandleItemUnlockPacket(Peer, next));
            Assert.Equal(2, applied);
        }

        [Fact]
        public void ObserverFailureCannotRepeatSuccessfulGameApplication()
        {
            var service = new InventorySyncService();
            int applied = 0;
            int notified = 0;
            service.RemoteItemApplier = (_, _) => { applied++; return true; };
            service.OnRemoteItemUnlocked += (_, _) => throw new InvalidOperationException("observer");
            service.OnRemoteItemUnlocked += (_, _) => notified++;
            var packet = Counter(1);
            Assert.True(service.TryHandleItemUnlockPacket(Peer, packet));
            Assert.True(service.TryHandleItemUnlockPacket(Peer, packet));
            Assert.Equal(1, applied);
            Assert.Equal(1, notified);
        }

        [Fact]
        public void RegularUnlockCanRetryWhenGameReturnsFalseOrThrows()
        {
            var service = new InventorySyncService();
            var packet = Counter(1);
            packet.ItemId = "hasDash";
            packet.Category = ItemCategory.Ability;
            service.RemoteItemApplier = (_, _) => false;
            Assert.False(service.TryHandleItemUnlockPacket(Peer, packet));
            Assert.False(service.IsItemUnlocked("hasDash"));
            service.RemoteItemApplier = (_, _) => throw new InvalidOperationException("game unavailable");
            Assert.False(service.TryHandleItemUnlockPacket(Peer, packet));
            Assert.False(service.IsItemUnlocked("hasDash"));
            service.RemoteItemApplier = (_, _) => true;
            Assert.True(service.TryHandleItemUnlockPacket(Peer, packet));
            Assert.True(service.IsItemUnlocked("hasDash"));
        }

        [Fact]
        public void LegacyHistoryExhaustionFailsClosedAndSessionResetRestoresCapacity()
        {
            var service = new InventorySyncService();
            int applied = 0;
            service.RemoteItemApplier = (_, _) => { applied++; return true; };
            var first = Counter(0);
            service.HandleItemUnlockPacket(Peer, first);
            for (int i = 0; i < 4200; i++) service.HandleItemUnlockPacket(Peer, Counter(0));
            service.HandleItemUnlockPacket(Peer, first);
            Assert.Equal(4096, applied);
            service.ResetSessionState();
            service.HandleItemUnlockPacket(Peer, Counter(0));
            Assert.Equal(4097, applied);
        }

        [Fact]
        public void PeerHistoryExhaustionDoesNotBlockAnAlreadyPendingRetry()
        {
            var service = new InventorySyncService();
            bool ready = false;
            int applied = 0;
            service.RemoteItemApplier = (_, _) => { if (!ready) return false; applied++; return true; };
            var first = Counter(1);
            Assert.False(service.TryHandleItemUnlockPacket(Peer, first));
            for (ulong i = 3; i <= 65; i++)
                Assert.False(service.TryHandleItemUnlockPacket(new NetworkPeerId(i), Counter(1)));
            Assert.True(service.TryHandleItemUnlockPacket(new NetworkPeerId(66), Counter(1)));
            ready = true;
            Assert.True(service.TryHandleItemUnlockPacket(Peer, first));
            Assert.Equal(1, applied);
        }

        [Fact]
        public void CancellingUnserviceablePendingApplicationUnblocksLaterCounterWithoutClearingWatermark()
        {
            var service = new InventorySyncService();
            bool ready = false;
            int applied = 0;
            service.RemoteItemApplier = (_, _) => { if (!ready) return false; applied++; return true; };
            var first = Counter(1);
            var blocked = Counter(2);
            Assert.False(service.TryHandleItemUnlockPacket(Peer, first));
            Assert.False(service.TryHandleItemUnlockPacket(Peer, blocked));
            service.CancelPendingCounterApplication(Peer);
            ready = true;
            Assert.True(service.TryHandleItemUnlockPacket(Peer, blocked));
            Assert.Equal(1, applied);
            // The committed watermark is retained, so the older event is still terminal.
            Assert.True(service.TryHandleItemUnlockPacket(Peer, first));
            Assert.Equal(1, applied);
        }

        [Fact]
        public void ExplicitSaveIdentityResetAllowsRestartedCounterSequence()
        {
            var service = new InventorySyncService();
            int applied = 0;
            service.RemoteItemApplier = (_, _) => { applied++; return true; };
            service.HandleItemUnlockPacket(Peer, Counter(500));
            service.HandleItemUnlockPacket(Peer, Counter(1));
            Assert.Equal(1, applied);
            service.ResetCounterPeer(Peer);
            service.HandleItemUnlockPacket(Peer, Counter(1));
            Assert.Equal(2, applied);
        }

    }
}
