using System;
using System.Collections.Generic;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;

namespace SilksongCoop.Core.State
{
    public partial class VitalsSyncService
    {
        private readonly List<(NetworkPeerId Peer, CurrencySyncPacket Packet, int Delta)> _seedTransactions = new();
        private bool _collectingWalletSeed;
        public bool WalletSeedOverflow { get; private set; }

        public void BeginWalletSeed()
        {
            lock (_transactionLock)
            {
                _seedTransactions.Clear();
                WalletSeedOverflow = false;
                _collectingWalletSeed = true;
            }
        }

        public WalletSeedPacket CaptureWalletSeed(NetworkPeerId localPeer, IEnumerable<NetworkPeerId> peers)
        {
            lock (_transactionLock)
            {
                var marks = new List<WalletSeedWatermark>();
                marks.Add(new WalletSeedWatermark { Peer = localPeer.Value, Rosaries = _currencySequence, Shards = _currencySequence, Flags = 3 });
                foreach (var peer in peers)
                {
                    if (peer == localPeer) continue;
                    var mark = new WalletSeedWatermark { Peer = peer.Value };
                    if (_lastCurrencySequences.TryGetValue((peer, CurrencyType.Rosaries), out uint r)) { mark.Rosaries = r; mark.Flags |= 1; }
                    if (_lastCurrencySequences.TryGetValue((peer, CurrencyType.ShellShards), out uint s)) { mark.Shards = s; mark.Flags |= 2; }
                    marks.Add(mark);
                }
                if (marks.Count > WalletSeedPacket.MaxWatermarks) throw new InvalidOperationException("Too many wallet origins");
                return new WalletSeedPacket {
                    Header = new PacketHeader(PacketType.WalletSeed, NetworkChannels.Currency, 0, NetworkClock.NowMs),
                    Rosaries = _sharedRosaries, Shards = _sharedShellShards, Watermarks = marks.ToArray(),
                    CreditedPickups = _claimedPickups.ConvertAll(p => new WalletSeedPickup { Currency = p.Currency, Key = p.Key }).ToArray()
                };
            }
        }

        public void ApplyWalletSeed(WalletSeedPacket seed, NetworkPeerId localPeer, IEnumerable<CurrencySyncPacket> pendingLocal)
        {
            lock (_transactionLock)
            {
                if (!_collectingWalletSeed) return;
                if (WalletSeedOverflow) throw new InvalidOperationException("Wallet seed transaction buffer exhausted");
                long r = seed.Rosaries, s = seed.Shards;
                foreach (var item in _seedTransactions)
                {
                    if (seed.Includes(item.Peer, item.Packet.Currency, item.Packet.PickupUniqueId) ||
                        seed.IncludesPickup(item.Packet.Currency, item.Packet.PickupKey)) continue;
                    if (item.Packet.Currency == CurrencyType.Rosaries) r += item.Delta; else s += item.Delta;
                }
                foreach (var packet in pendingLocal)
                {
                    if (seed.Includes(localPeer, packet.Currency, packet.PickupUniqueId) ||
                        seed.IncludesPickup(packet.Currency, packet.PickupKey)) continue;
                    if (packet.Currency == CurrencyType.Rosaries) r += packet.DeltaAmount; else s += packet.DeltaAmount;
                }
                foreach (var mark in seed.Watermarks ?? Array.Empty<WalletSeedWatermark>())
                {
                    var peer = new NetworkPeerId(mark.Peer);
                    if (peer == localPeer) continue;
                    foreach (var currency in new[] { CurrencyType.Rosaries, CurrencyType.ShellShards })
                    {
                        if (!mark.TryGet(currency, out uint sequence)) continue;
                        var key = (peer, currency);
                        if (!_lastCurrencySequences.TryGetValue(key, out uint previous) || IsNewerSequence(sequence, previous))
                            _lastCurrencySequences[key] = sequence;
                    }
                }
                foreach (var pickup in seed.CreditedPickups ?? Array.Empty<WalletSeedPickup>())
                    if (!IsPickupAlreadyClaimed(pickup.Currency, pickup.Key)) TrackClaimedPickup(pickup.Currency, pickup.Key, 0f, 0f);
                _sharedRosaries = (int)Math.Clamp(r, int.MinValue, PacketValidator.MaxReasonableCurrency);
                _sharedShellShards = (int)Math.Clamp(s, int.MinValue, PacketValidator.MaxReasonableCurrency);
                _seedTransactions.Clear();
                _collectingWalletSeed = false;
                _seededCurrencies.Add(CurrencyType.Rosaries);
                _seededCurrencies.Add(CurrencyType.ShellShards);
                _activeCurrencies.Add(CurrencyType.Rosaries);
                _activeCurrencies.Add(CurrencyType.ShellShards);
            }
            OnSharedCurrencyChanged?.Invoke(CurrencyType.Rosaries, 0, SharedRosaries);
            OnSharedCurrencyChanged?.Invoke(CurrencyType.ShellShards, 0, SharedShellShards);
        }
    }
}
