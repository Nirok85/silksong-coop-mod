using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using Xunit;

namespace SilksongCoop.Tests.State
{
    public sealed class WalletSeedCausalityTests
    {
        private static readonly NetworkPeerId Host = new NetworkPeerId(1);
        private static readonly NetworkPeerId Client = new NetworkPeerId(2);
        private static readonly NetworkPeerId Third = new NetworkPeerId(3);

        private static VitalsSyncService Wallet(int rosaries = 100, int shards = 20)
        {
            var wallet = new VitalsSyncService();
            wallet.InitializeState(5, 5, 9, 1, 0, rosaries, shards);
            return wallet;
        }

        private static CurrencySyncPacket Transaction(int delta, uint sequence,
            CurrencyType currency = CurrencyType.Rosaries) => new CurrencySyncPacket
        {
            Header = new PacketHeader(PacketType.CurrencySync, NetworkChannels.Currency, sequence, 100),
            Currency = currency,
            DeltaAmount = delta,
            PickupUniqueId = sequence,
            TransactionId = Guid.NewGuid()
        };

        private static CurrencySyncPacket ChangeLocal(VitalsSyncService wallet, int delta, int total,
            CurrencyType currency = CurrencyType.Rosaries)
        {
            CurrencySyncPacket result = default;
            Action<CurrencySyncPacket> capture = p => result = p;
            wallet.OnOutgoingCurrencyBroadcast += capture;
            wallet.OnLocalCurrencyChanged(currency, delta, total, Guid.NewGuid());
            wallet.OnOutgoingCurrencyBroadcast -= capture;
            Assert.NotEqual(Guid.Empty, result.TransactionId);
            return result;
        }

        private static WalletSeedPacket Capture(VitalsSyncService host) =>
            host.CaptureWalletSeed(Host, new[] { Client, Third });

        [Fact]
        public void ThirdPeerDeltaAlreadyInHostSeedIsNotAddedTwice()
        {
            var host = Wallet();
            var client = Wallet();
            client.BeginWalletSeed();
            var delta = Transaction(10, 1);
            host.HandleCurrencyPacket(Third, delta);
            client.HandleCurrencyPacket(Third, delta);
            client.ApplyWalletSeed(Capture(host), Client, Array.Empty<CurrencySyncPacket>());
            Assert.Equal(110, client.SharedRosaries);
        }

        [Fact]
        public void ThirdPeerDeltaNotInHostSeedIsRetained()
        {
            var host = Wallet();
            var client = Wallet();
            client.BeginWalletSeed();
            var seed = Capture(host);
            client.HandleCurrencyPacket(Third, Transaction(10, 1));
            client.ApplyWalletSeed(seed, Client, Array.Empty<CurrencySyncPacket>());
            Assert.Equal(110, client.SharedRosaries);
        }

        [Fact]
        public void DelayedTransactionIncludedInSeedIsSuppressedAfterSeedApplication()
        {
            var host = Wallet();
            var client = Wallet();
            client.BeginWalletSeed();
            var delta = Transaction(10, 1);
            host.HandleCurrencyPacket(Third, delta);
            client.ApplyWalletSeed(Capture(host), Client, Array.Empty<CurrencySyncPacket>());
            client.HandleCurrencyPacket(Third, delta);
            Assert.Equal(110, client.SharedRosaries);
            client.HandleCurrencyPacket(Third, Transaction(5, 2));
            Assert.Equal(115, client.SharedRosaries);
        }

        [Fact]
        public void PendingLocalTransactionAlreadyReceivedByHostIsNotAddedTwice()
        {
            var host = Wallet();
            var client = Wallet();
            client.BeginWalletSeed();
            var local = ChangeLocal(client, 10, 110);
            host.HandleCurrencyPacket(Client, local);
            client.ApplyWalletSeed(Capture(host), Client, new[] { local });
            Assert.Equal(110, client.SharedRosaries);
        }

        [Fact]
        public void PendingLocalSpendNotReceivedByHostRetainsDebt()
        {
            var host = Wallet(20);
            var client = Wallet();
            client.BeginWalletSeed();
            var spend = ChangeLocal(client, -30, 70);
            client.ApplyWalletSeed(Capture(host), Client, new[] { spend });
            Assert.Equal(-10, client.GetWalletBalances().Rosaries);
            Assert.Equal(0, client.SharedRosaries);
        }


        [Fact]
        public void SharedPhysicalPickupAlreadyCreditedThroughAnotherOriginIsNotAddedFromLocalJournal()
        {
            var host = Wallet();
            var client = Wallet();
            client.BeginWalletSeed();
            const ulong pickup = 12345;
            var thirdPickup = Transaction(10, 1);
            thirdPickup.PickupKey = pickup;
            host.HandleCurrencyPacket(Third, thirdPickup);

            CurrencySyncPacket local = default;
            client.OnOutgoingCurrencyBroadcast += p => local = p;
            client.OnLocalCurrencyChanged(CurrencyType.Rosaries, 10, 110, Guid.NewGuid(), pickup);
            Assert.NotEqual(Guid.Empty, local.TransactionId);

            client.ApplyWalletSeed(Capture(host), Client, new[] { local });
            host.HandleCurrencyPacket(Client, local);
            Assert.Equal(110, host.SharedRosaries);
            Assert.Equal(host.SharedRosaries, client.SharedRosaries);
        }


        [Fact]
        public void CurrencySpecificWatermarksDoNotDiscardOtherCurrencyDeltas()
        {
            var host = Wallet();
            var client = Wallet();
            client.BeginWalletSeed();
            var rosaries = Transaction(10, 1);
            var shards = Transaction(-3, 2, CurrencyType.ShellShards);
            host.HandleCurrencyPacket(Third, rosaries);
            client.HandleCurrencyPacket(Third, rosaries);
            client.HandleCurrencyPacket(Third, shards);
            client.ApplyWalletSeed(Capture(host), Client, Array.Empty<CurrencySyncPacket>());
            Assert.Equal(110, client.SharedRosaries);
            Assert.Equal(17, client.SharedShellShards);
        }

        [Fact]
        public void ClientNewerWatermarkIsNotRolledBackByOlderSeed()
        {
            var host = Wallet();
            var client = Wallet();
            client.BeginWalletSeed();
            var first = Transaction(10, 1);
            var second = Transaction(5, 2);
            host.HandleCurrencyPacket(Third, first);
            client.HandleCurrencyPacket(Third, first);
            client.HandleCurrencyPacket(Third, second);
            client.ApplyWalletSeed(Capture(host), Client, Array.Empty<CurrencySyncPacket>());
            Assert.Equal(115, client.SharedRosaries);
            second.TransactionId = Guid.NewGuid();
            client.HandleCurrencyPacket(Third, second);
            Assert.Equal(115, client.SharedRosaries);
        }

        [Fact]
        public void WatermarkComparisonIncludesWrappedHistoryButNotFuture()
        {
            var seed = new WalletSeedPacket
            {
                Watermarks = new[] { new WalletSeedWatermark { Peer = Third.Value, Rosaries = 0, Flags = 1 } }
            };
            Assert.True(seed.Includes(Third, CurrencyType.Rosaries, uint.MaxValue));
            Assert.True(seed.Includes(Third, CurrencyType.Rosaries, 0));
            Assert.False(seed.Includes(Third, CurrencyType.Rosaries, 1));
            Assert.False(seed.Includes(Third, CurrencyType.ShellShards, 0));
            Assert.False(seed.Includes(Client, CurrencyType.Rosaries, 0));
        }

        [Fact]
        public void WrappedZeroTransactionIncludedInSeedCannotBeReapplied()
        {
            var client = Wallet();
            client.BeginWalletSeed();
            var seed = new WalletSeedPacket
            {
                Header = new PacketHeader(PacketType.WalletSeed, NetworkChannels.Currency, 0, 100),
                Rosaries = 110,
                Shards = 20,
                Watermarks = new[] { new WalletSeedWatermark { Peer = Third.Value, Rosaries = 0, Flags = 1 } }
            };
            client.ApplyWalletSeed(seed, Client, Array.Empty<CurrencySyncPacket>());
            client.HandleCurrencyPacket(Third, Transaction(10, 0));
            Assert.Equal(110, client.SharedRosaries);
            client.HandleCurrencyPacket(Third, Transaction(5, 1));
            Assert.Equal(115, client.SharedRosaries);
        }

        [Fact]
        public void RepeatedSeedDoesNotOverwriteTransactionsAppliedAfterFirstSeed()
        {
            var host = Wallet();
            var client = Wallet();
            client.BeginWalletSeed();
            var seed = Capture(host);
            client.ApplyWalletSeed(seed, Client, Array.Empty<CurrencySyncPacket>());
            client.HandleCurrencyPacket(Third, Transaction(10, 1));
            client.ApplyWalletSeed(seed, Client, Array.Empty<CurrencySyncPacket>());
            Assert.Equal(110, client.SharedRosaries);
        }

        [Fact]
        public void SeedBufferOverflowFailsClosedInsteadOfLosingUnrepresentedTransactions()
        {
            var host = Wallet();
            var client = Wallet();
            client.BeginWalletSeed();
            for (uint i = 1; i <= 4097; i++)
                client.HandleCurrencyPacket(Third, Transaction(1, i));
            Assert.True(client.WalletSeedOverflow);
            Assert.Throws<InvalidOperationException>(() =>
                client.ApplyWalletSeed(Capture(host), Client, Array.Empty<CurrencySyncPacket>()));
        }
        [Fact]
        public async Task CaptureReadsBalanceAndLocalSequenceAtomicallyDuringConcurrentChanges()
        {
            var host = Wallet(0, 0);
            using var start = new ManualResetEventSlim();
            var changes = Task.Run(() =>
            {
                start.Wait();
                for (int i = 1; i <= 1000; i++)
                    host.OnLocalCurrencyChanged(CurrencyType.Rosaries, 1, i, Guid.NewGuid());
            });
            start.Set();
            for (int i = 0; i < 1000; i++)
            {
                var seed = host.CaptureWalletSeed(Host, Array.Empty<NetworkPeerId>());
                var mark = Assert.Single(seed.Watermarks!);
                Assert.Equal((uint)seed.Rosaries, mark.Rosaries);
            }
            await changes;
            var final = host.CaptureWalletSeed(Host, Array.Empty<NetworkPeerId>());
            Assert.Equal(1000, final.Rosaries);
            Assert.Equal(1000u, Assert.Single(final.Watermarks!).Rosaries);
        }

        private static byte[] Encode(WalletSeedPacket seed)
        {
            if (seed.ChunkCount == 0) seed = Assert.Single(seed.CreateChunks());
            byte[] buffer = new byte[WalletSeedPacket.MaxWireSize];
            var writer = new PacketWriter(buffer);
            seed.Serialize(ref writer);
            return writer.WrittenSpan.ToArray();
        }

        private static WalletSeedPacket Decode(byte[] data)
        {
            var reader = new PacketReader(data);
            return WalletSeedPacket.Deserialize(ref reader);
        }

        [Fact]
        public void PacketRoundTripPreservesAllOriginsBothCurrenciesAndDebt()
        {
            var marks = Enumerable.Range(1, WalletSeedPacket.MaxWatermarks)
                .Select(i => new WalletSeedWatermark
                {
                    Peer = (ulong)i, Rosaries = (uint)i, Shards = uint.MaxValue - (uint)i, Flags = 3
                }).ToArray();
            var original = new WalletSeedPacket
            {
                Header = new PacketHeader(PacketType.WalletSeed, NetworkChannels.Currency, 0, 100),
                Rosaries = -10, Shards = 17, Watermarks = marks
            };
            original.CreditedPickups = Enumerable.Range(1, WalletSeedPacket.MaxPickupsPerChunk)
                .Select(i => new WalletSeedPickup { Currency = CurrencyType.Rosaries, Key = (ulong)i }).ToArray();
            var bytes = Encode(original);
            Assert.Equal(WalletSeedPacket.MaxWireSize, bytes.Length);
            var decoded = Decode(bytes);
            Assert.Equal(original.Header, decoded.Header);
            Assert.Equal(-10, decoded.Rosaries);
            Assert.Equal(17, decoded.Shards);
            Assert.Equal(marks.Length, decoded.Watermarks!.Length);
            for (int i = 0; i < marks.Length; i++)
            {
                Assert.Equal(marks[i].Peer, decoded.Watermarks[i].Peer);
                Assert.Equal(marks[i].Rosaries, decoded.Watermarks[i].Rosaries);
                Assert.Equal(marks[i].Shards, decoded.Watermarks[i].Shards);
                Assert.Equal(marks[i].Flags, decoded.Watermarks[i].Flags);
            }
        }

        [Theory]
        [InlineData(0UL, 1)]
        [InlineData(1UL, 4)]
        [InlineData(1UL, 255)]
        public void PacketRejectsZeroOriginAndUnknownFlagBits(ulong origin, int flags)
        {
            var seed = Capture(Wallet());
            seed.Watermarks = new[] { new WalletSeedWatermark { Peer = origin, Flags = (byte)flags } };
            Assert.Throws<FormatException>(() => Decode(Encode(seed)));
        }

        [Fact]
        public void PacketRejectsDuplicateOrigins()
        {
            var seed = Capture(Wallet());
            seed.Watermarks = new[]
            {
                new WalletSeedWatermark { Peer = Third.Value, Flags = 1 },
                new WalletSeedWatermark { Peer = Third.Value, Flags = 2 }
            };
            Assert.Throws<FormatException>(() => Decode(Encode(seed)));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void PacketRejectsBalanceAboveProtocolLimit(bool rosaries)
        {
            var seed = Capture(Wallet());
            if (rosaries) seed.Rosaries = PacketValidator.MaxReasonableCurrency + 1;
            else seed.Shards = PacketValidator.MaxReasonableCurrency + 1;
            Assert.Throws<FormatException>(() => Decode(Encode(seed)));
        }

        [Fact]
        public void PacketRejectsOversizedWatermarkCountBeforeAllocatingEntries()
        {
            var seed = Capture(Wallet());
            seed.Watermarks = Array.Empty<WalletSeedWatermark>();
            var bytes = Encode(seed);
            bytes[PacketHeader.HeaderSize + 26] = WalletSeedPacket.MaxWatermarks + 1;
            Assert.Throws<FormatException>(() => Decode(bytes));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(11)]
        [InlineData(12)]
        [InlineData(20)]
        [InlineData(21)]
        [InlineData(37)]
        public void PacketRejectsTruncatedFrame(int length)
        {
            var seed = Capture(Wallet());
            seed.Watermarks = new[] { new WalletSeedWatermark { Peer = Host.Value, Flags = 3 } };
            var bytes = Encode(seed).Take(length).ToArray();
            Assert.Throws<InvalidOperationException>(() => Decode(bytes));
        }

        [Fact]
        public void PacketParserLeavesTrailingDataVisibleForCallerRejection()
        {
            var bytes = Encode(Capture(Wallet())).Concat(new byte[] { 0xFF }).ToArray();
            var reader = new PacketReader(bytes);
            _ = WalletSeedPacket.Deserialize(ref reader);
            Assert.Equal(1, reader.Remaining);
        }

        [Fact]
        public void CaptureRejectsMoreThanSupportedOrigins()
        {
            var peers = Enumerable.Range(2, WalletSeedPacket.MaxWatermarks)
                .Select(i => new NetworkPeerId((ulong)i));
            Assert.Throws<InvalidOperationException>(() => Wallet().CaptureWalletSeed(Host, peers));
        }

        private static WalletSeedPacket SeedWithPickups(int count)
        {
            var seed = Capture(Wallet());
            seed.CreditedPickups = Enumerable.Range(1, count)
                .Select(i => new WalletSeedPickup { Currency = CurrencyType.Rosaries, Key = (ulong)i }).ToArray();
            return seed;
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(128)]
        [InlineData(129)]
        [InlineData(1024)]
        public void ChunkRoundTripIsBoundedAndOnlyPublishesCompleteSnapshots(int pickupCount)
        {
            var seed = SeedWithPickups(pickupCount);
            var parts = seed.CreateChunks();
            Assert.InRange(parts.Length, 1, WalletSeedPacket.MaxChunks);
            var assembler = new WalletSeedAssembler();
            WalletSeedPacket complete = default;
            for (int i = 0; i < parts.Length; i++)
            {
                var bytes = Encode(parts[i]);
                Assert.InRange(bytes.Length, 1, WalletSeedPacket.MaxWireSize);
                Assert.True(bytes.Length < 2024);
                Assert.Equal(i, parts[i].ChunkIndex);
                Assert.Equal(parts[0].SnapshotId, parts[i].SnapshotId);
                bool ready = assembler.TryAdd(Decode(bytes), out complete);
                Assert.Equal(i == parts.Length - 1, ready);
                if (!ready) Assert.Null(complete.CreditedPickups);
            }
            Assert.Equal(pickupCount, complete.CreditedPickups!.Length);
            Assert.Equal(seed.Rosaries, complete.Rosaries);
            Assert.Equal(seed.Shards, complete.Shards);
            for (int i = 1; i <= pickupCount; i++)
                Assert.True(complete.IncludesPickup(CurrencyType.Rosaries, (ulong)i));
        }

        [Fact]
        public void FullClaimWindowSurvivesWireAssemblyAndPreventsLocalAndDelayedPickupDoubleCredit()
        {
            var host = Wallet();
            for (ulong key = 1; key <= WalletSeedPacket.MaxPickups; key++)
                host.OnLocalCurrencyChanged(CurrencyType.Rosaries, 1, (int)(100 + key), Guid.NewGuid(), key);
            var client = Wallet();
            client.BeginWalletSeed();
            CurrencySyncPacket local = default;
            client.OnOutgoingCurrencyBroadcast += p => local = p;
            client.OnLocalCurrencyChanged(CurrencyType.Rosaries, 1, 101, Guid.NewGuid(), WalletSeedPacket.MaxPickups);
            var snapshot = Capture(host);
            Assert.Equal(WalletSeedPacket.MaxPickups, snapshot.CreditedPickups!.Length);
            var assembler = new WalletSeedAssembler();
            WalletSeedPacket complete = default;
            var parts = snapshot.CreateChunks();
            Assert.Equal(WalletSeedPacket.MaxChunks, parts.Length);
            foreach (var part in parts)
                assembler.TryAdd(Decode(Encode(part)), out complete);
            client.ApplyWalletSeed(complete, Client, new[] { local });
            host.HandleCurrencyPacket(Client, local);
            Assert.Equal(1124, host.SharedRosaries);
            Assert.Equal(host.SharedRosaries, client.SharedRosaries);
            var latePickup = Transaction(1, 1);
            latePickup.PickupKey = 1;
            client.HandleCurrencyPacket(Third, latePickup);
            Assert.Equal(1124, client.SharedRosaries);
        }

        [Fact]
        public void AssemblerRejectsPartBeforeStartAndDuplicateParts()
        {
            var parts = SeedWithPickups(129).CreateChunks();
            var assembler = new WalletSeedAssembler();
            Assert.Throws<FormatException>(() => assembler.TryAdd(parts[1], out _));
            Assert.False(assembler.TryAdd(parts[0], out _));
            Assert.Throws<FormatException>(() => assembler.TryAdd(parts[0], out _));
            Assert.True(assembler.TryAdd(parts[1], out _));
            Assert.Throws<FormatException>(() => assembler.TryAdd(parts[1], out _));
        }

        [Theory]
        [InlineData("snapshot")]
        [InlineData("rosaries")]
        [InlineData("shards")]
        [InlineData("watermark")]
        [InlineData("count")]
        public void AssemblerRejectsMixedSnapshotMetadataWithoutConsumingTheValidPart(string change)
        {
            var parts = SeedWithPickups(129).CreateChunks();
            var assembler = new WalletSeedAssembler();
            Assert.False(assembler.TryAdd(parts[0], out _));
            var inconsistent = parts[1];
            inconsistent.Watermarks = (WalletSeedWatermark[])inconsistent.Watermarks!.Clone();
            switch (change)
            {
                case "snapshot": inconsistent.SnapshotId = Guid.NewGuid(); break;
                case "rosaries": inconsistent.Rosaries++; break;
                case "shards": inconsistent.Shards++; break;
                case "watermark": inconsistent.Watermarks[0].Rosaries++; break;
                case "count": inconsistent.ChunkCount++; break;
            }
            Assert.Throws<FormatException>(() => assembler.TryAdd(inconsistent, out _));
            Assert.True(assembler.TryAdd(parts[1], out var completed));
            Assert.Equal(129, completed.CreditedPickups!.Length);
        }

        [Fact]
        public void AssemblerRejectsDuplicatePickupAcrossParts()
        {
            var parts = SeedWithPickups(129).CreateChunks();
            parts[1].CreditedPickups![0] = parts[0].CreditedPickups![0];
            var assembler = new WalletSeedAssembler();
            Assert.False(assembler.TryAdd(parts[0], out _));
            Assert.Throws<FormatException>(() => assembler.TryAdd(parts[1], out _));
        }

        [Fact]
        public void AssemblerResetPermitsNewAuthorizedSnapshot()
        {
            var assembler = new WalletSeedAssembler();
            Assert.True(assembler.TryAdd(SeedWithPickups(1).CreateChunks()[0], out _));
            assembler.Reset();
            Assert.True(assembler.TryAdd(SeedWithPickups(2).CreateChunks()[0], out var result));
            Assert.Equal(2, result.CreditedPickups!.Length);
        }

        [Fact]
        public void AssemblerOwnsBufferedArraysRatherThanRetainingMutableCallerBuffers()
        {
            var parts = SeedWithPickups(129).CreateChunks();
            var assembler = new WalletSeedAssembler();
            Assert.False(assembler.TryAdd(parts[0], out _));
            parts[0].CreditedPickups![0].Key = 9999;
            parts[0].Watermarks![0].Rosaries = 9999;
            Assert.True(assembler.TryAdd(parts[1], out var result));
            Assert.Equal(1UL, result.CreditedPickups![0].Key);
            Assert.NotEqual(9999u, result.Watermarks![0].Rosaries);
        }

        [Fact]
        public void SerializerRequiresChunkingAndRefusesSnapshotsAboveGlobalPickupLimit()
        {
            var complete = SeedWithPickups(129);
            Assert.Throws<InvalidOperationException>(() => EncodeRaw(complete));
            Assert.Throws<InvalidOperationException>(() => SeedWithPickups(1025).CreateChunks());
        }

        private static byte[] EncodeRaw(WalletSeedPacket part)
        {
            var writer = new PacketWriter(new byte[WalletSeedPacket.MaxWireSize]);
            part.Serialize(ref writer);
            return writer.WrittenSpan.ToArray();
        }

        [Theory]
        [InlineData(0UL, 0)]
        [InlineData(1UL, 2)]
        [InlineData(1UL, 255)]
        public void PacketRejectsZeroPickupKeysAndUnknownCurrency(ulong key, int currency)
        {
            var part = SeedWithPickups(1).CreateChunks()[0];
            part.CreditedPickups![0] = new WalletSeedPickup { Key = key, Currency = (CurrencyType)currency };
            Assert.Throws<FormatException>(() => Decode(Encode(part)));
        }

        [Fact]
        public void PacketRejectsDuplicatePickupInSinglePartButAllowsSameKeyInDifferentCurrencies()
        {
            var part = SeedWithPickups(2).CreateChunks()[0];
            part.CreditedPickups![1] = part.CreditedPickups[0];
            Assert.Throws<FormatException>(() => Decode(Encode(part)));
            part.CreditedPickups[1].Currency = CurrencyType.ShellShards;
            var valid = Decode(Encode(part));
            Assert.True(valid.IncludesPickup(CurrencyType.Rosaries, 1));
            Assert.True(valid.IncludesPickup(CurrencyType.ShellShards, 1));
        }

        [Fact]
        public void PacketRejectsPickupCountAbovePerDatagramLimit()
        {
            var part = SeedWithPickups(0).CreateChunks()[0];
            var bytes = Encode(part);
            int pickupCountOffset = PacketHeader.HeaderSize + 27 + part.Watermarks!.Length * 17;
            bytes[pickupCountOffset] = WalletSeedPacket.MaxPickupsPerChunk + 1;
            Assert.Throws<FormatException>(() => Decode(bytes));
        }

    }
}
