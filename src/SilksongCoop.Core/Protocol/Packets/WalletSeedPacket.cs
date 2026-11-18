using System;
using System.Collections.Generic;
using SilksongCoop.Core.Networking;

namespace SilksongCoop.Core.Protocol.Packets
{
    public struct WalletSeedWatermark
    {
        public ulong Peer;
        public uint Rosaries;
        public uint Shards;
        public byte Flags;
        public bool TryGet(CurrencyType currency, out uint value)
        {
            value = currency == CurrencyType.Rosaries ? Rosaries : Shards;
            return (Flags & (currency == CurrencyType.Rosaries ? 1 : 2)) != 0;
        }
    }

    public struct WalletSeedPickup
    {
        public CurrencyType Currency;
        public ulong Key;
    }

    public struct WalletSeedPacket : IPacket
    {
        public const int MaxWatermarks = 8;
        public const int MaxPickups = 1024;
        public const int MaxPickupsPerChunk = 128;
        public const int MaxChunks = MaxPickups / MaxPickupsPerChunk;
        public const int MaxWireSize = PacketHeader.HeaderSize + 28 + MaxWatermarks * 17 + MaxPickupsPerChunk * 9;
        public PacketHeader Header;
        public int Rosaries;
        public int Shards;
        public Guid SnapshotId;
        public byte ChunkIndex;
        public byte ChunkCount;
        public WalletSeedWatermark[]? Watermarks;
        public WalletSeedPickup[]? CreditedPickups;

        public bool Includes(NetworkPeerId peer, CurrencyType currency, uint sequence)
        {
            foreach (var mark in Watermarks ?? Array.Empty<WalletSeedWatermark>())
                if (mark.Peer == peer.Value && mark.TryGet(currency, out uint boundary))
                    return unchecked((int)(sequence - boundary)) <= 0;
            return false;
        }

        public bool IncludesPickup(CurrencyType currency, ulong key)
        {
            if (key == 0) return false;
            foreach (var pickup in CreditedPickups ?? Array.Empty<WalletSeedPickup>())
                if (pickup.Currency == currency && pickup.Key == key) return true;
            return false;
        }

        /// <summary>
        /// Splits a complete in-memory snapshot into bounded reliable ordered datagrams.
        /// Retain these returned chunks when retrying; do not recapture a changing wallet.
        /// </summary>
        public WalletSeedPacket[] CreateChunks()
        {
            var pickups = CreditedPickups ?? Array.Empty<WalletSeedPickup>();
            if (pickups.Length > MaxPickups) throw new InvalidOperationException("Too many credited pickups");
            int count = Math.Max(1, (pickups.Length + MaxPickupsPerChunk - 1) / MaxPickupsPerChunk);
            Guid id = SnapshotId == Guid.Empty ? Guid.NewGuid() : SnapshotId;
            var parts = new WalletSeedPacket[count];
            for (int i = 0; i < count; i++)
            {
                var part = this;
                part.SnapshotId = id;
                part.ChunkIndex = (byte)i;
                part.ChunkCount = (byte)count;
                part.Watermarks = (WalletSeedWatermark[])(Watermarks ?? Array.Empty<WalletSeedWatermark>()).Clone();
                int length = Math.Min(MaxPickupsPerChunk, pickups.Length - i * MaxPickupsPerChunk);
                part.CreditedPickups = new WalletSeedPickup[length];
                Array.Copy(pickups, i * MaxPickupsPerChunk, part.CreditedPickups, 0, length);
                parts[i] = part;
            }
            return parts;
        }

        public void Serialize(ref PacketWriter writer)
        {
            var marks = Watermarks ?? Array.Empty<WalletSeedWatermark>();
            var pickups = CreditedPickups ?? Array.Empty<WalletSeedPickup>();
            if (marks.Length > MaxWatermarks) throw new InvalidOperationException("Too many wallet watermarks");
            if (pickups.Length > MaxPickupsPerChunk) throw new InvalidOperationException("Wallet seed must be chunked before serialization");
            if (SnapshotId == Guid.Empty || ChunkCount == 0 || ChunkCount > MaxChunks || ChunkIndex >= ChunkCount)
                throw new FormatException("Invalid wallet chunk identity");
            writer.WriteHeader(Header);
            writer.WriteInt32(Rosaries);
            writer.WriteInt32(Shards);
            writer.WriteGuid(SnapshotId);
            writer.WriteByte(ChunkIndex);
            writer.WriteByte(ChunkCount);
            writer.WriteByte((byte)marks.Length);
            foreach (var mark in marks)
            {
                writer.WriteUInt64(mark.Peer);
                writer.WriteUInt32(mark.Rosaries);
                writer.WriteUInt32(mark.Shards);
                writer.WriteByte(mark.Flags);
            }
            writer.WriteByte((byte)pickups.Length);
            foreach (var pickup in pickups)
            {
                writer.WriteByte((byte)pickup.Currency);
                writer.WriteUInt64(pickup.Key);
            }
        }

        internal void ValidateChunk()
        {
            if (Header.PacketId != PacketType.WalletSeed || Header.ChannelId != NetworkChannels.Currency ||
                SnapshotId == Guid.Empty || ChunkCount == 0 || ChunkCount > MaxChunks || ChunkIndex >= ChunkCount ||
                Rosaries > PacketValidator.MaxReasonableCurrency || Shards > PacketValidator.MaxReasonableCurrency)
                throw new FormatException("Invalid wallet seed");
            var marks = Watermarks ?? Array.Empty<WalletSeedWatermark>();
            if (marks.Length > MaxWatermarks) throw new FormatException("Too many wallet watermarks");
            for (int i = 0; i < marks.Length; i++)
            {
                if (marks[i].Peer == 0 || (marks[i].Flags & ~3) != 0) throw new FormatException("Invalid wallet watermark");
                for (int j = 0; j < i; j++)
                    if (marks[j].Peer == marks[i].Peer) throw new FormatException("Duplicate wallet origin");
            }
            var pickups = CreditedPickups ?? Array.Empty<WalletSeedPickup>();
            if (pickups.Length > MaxPickupsPerChunk) throw new FormatException("Too many wallet chunk pickups");
            var keys = new HashSet<(CurrencyType, ulong)>();
            foreach (var pickup in pickups)
            {
                if ((pickup.Currency != CurrencyType.Rosaries && pickup.Currency != CurrencyType.ShellShards) ||
                    pickup.Key == 0 || !keys.Add((pickup.Currency, pickup.Key)))
                    throw new FormatException("Invalid or duplicate wallet pickup");
            }
        }

        public static WalletSeedPacket Deserialize(ref PacketReader reader)
        {
            var seed = new WalletSeedPacket {
                Header = reader.ReadHeader(), Rosaries = reader.ReadInt32(), Shards = reader.ReadInt32(),
                SnapshotId = reader.ReadGuid(), ChunkIndex = reader.ReadByte(), ChunkCount = reader.ReadByte()
            };
            int count = reader.ReadByte();
            if (count > MaxWatermarks) throw new FormatException("Invalid wallet watermark count");
            seed.Watermarks = new WalletSeedWatermark[count];
            for (int i = 0; i < count; i++)
                seed.Watermarks[i] = new WalletSeedWatermark {
                    Peer = reader.ReadUInt64(), Rosaries = reader.ReadUInt32(), Shards = reader.ReadUInt32(), Flags = reader.ReadByte()
                };
            int pickupCount = reader.ReadByte();
            if (pickupCount > MaxPickupsPerChunk) throw new FormatException("Invalid wallet pickup count");
            seed.CreditedPickups = new WalletSeedPickup[pickupCount];
            for (int i = 0; i < pickupCount; i++)
                seed.CreditedPickups[i] = new WalletSeedPickup { Currency = (CurrencyType)reader.ReadByte(), Key = reader.ReadUInt64() };
            seed.ValidateChunk();
            return seed;
        }
    }
}
