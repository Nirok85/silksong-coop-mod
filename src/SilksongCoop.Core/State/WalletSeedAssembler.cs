using System;
using System.Collections.Generic;
using SilksongCoop.Core.Protocol.Packets;

namespace SilksongCoop.Core.State
{
    /// <summary>
    /// Holds one bounded wallet snapshot until every chunk is present. Partial snapshots
    /// never escape to wallet application, and inconsistent snapshots fail closed.
    /// </summary>
    public sealed class WalletSeedAssembler
    {
        private WalletSeedPacket _first;
        private readonly WalletSeedPickup[]?[] _chunks = new WalletSeedPickup[WalletSeedPacket.MaxChunks][];
        private readonly HashSet<(CurrencyType, ulong)> _pickupKeys = new HashSet<(CurrencyType, ulong)>();
        private bool _started;
        private bool _completed;
        private int _received;

        public void Reset()
        {
            _first = default;
            Array.Clear(_chunks, 0, _chunks.Length);
            _pickupKeys.Clear();
            _started = false;
            _completed = false;
            _received = 0;
        }

        /// <summary>
        /// Returns true only for a completed snapshot. Incomplete valid snapshots return
        /// false. Duplicate chunks and inconsistent identities/metadata throw FormatException.
        /// Call Reset only when beginning a newly authorized wallet-seed exchange.
        /// </summary>
        public bool TryAdd(WalletSeedPacket part, out WalletSeedPacket completed)
        {
            completed = default;
            part.ValidateChunk();
            if (_completed) throw new FormatException("Wallet seed already completed");
            if (!_started)
            {
                if (part.ChunkIndex != 0) throw new FormatException("Wallet seed must start with chunk zero");
            }
            else if (part.SnapshotId != _first.SnapshotId || part.ChunkCount != _first.ChunkCount ||
                part.Rosaries != _first.Rosaries || part.Shards != _first.Shards || !SameWatermarks(part, _first))
                throw new FormatException("Inconsistent wallet snapshot");
            if (_chunks[part.ChunkIndex] != null) throw new FormatException("Duplicate wallet chunk");
            var pickups = part.CreditedPickups ?? Array.Empty<WalletSeedPickup>();
            foreach (var pickup in pickups)
                if (_pickupKeys.Contains((pickup.Currency, pickup.Key)))
                    throw new FormatException("Duplicate pickup across wallet chunks");
            if (_pickupKeys.Count + pickups.Length > WalletSeedPacket.MaxPickups)
                throw new FormatException("Wallet snapshot pickup limit exceeded");

            if (!_started)
            {
                _first = part;
                _first.Watermarks = (WalletSeedWatermark[])(part.Watermarks ?? Array.Empty<WalletSeedWatermark>()).Clone();
                _first.CreditedPickups = null;
                _started = true;
            }
            _chunks[part.ChunkIndex] = (WalletSeedPickup[])pickups.Clone();
            foreach (var pickup in pickups) _pickupKeys.Add((pickup.Currency, pickup.Key));
            _received++;
            if (_received != _first.ChunkCount) return false;

            var merged = new WalletSeedPickup[_pickupKeys.Count];
            int offset = 0;
            for (int i = 0; i < _first.ChunkCount; i++)
            {
                var chunk = _chunks[i]!;
                Array.Copy(chunk, 0, merged, offset, chunk.Length);
                offset += chunk.Length;
            }
            completed = _first;
            completed.CreditedPickups = merged;
            completed.ChunkIndex = 0;
            completed.ChunkCount = 1;
            _completed = true;
            return true;
        }

        private static bool SameWatermarks(WalletSeedPacket left, WalletSeedPacket right)
        {
            var a = left.Watermarks ?? Array.Empty<WalletSeedWatermark>();
            var b = right.Watermarks ?? Array.Empty<WalletSeedWatermark>();
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i].Peer != b[i].Peer || a[i].Rosaries != b[i].Rosaries ||
                    a[i].Shards != b[i].Shards || a[i].Flags != b[i].Flags) return false;
            return true;
        }
    }
}
