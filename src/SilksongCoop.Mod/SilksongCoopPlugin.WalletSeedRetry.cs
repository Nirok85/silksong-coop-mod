using System.Collections.Generic;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol.Packets;
using UnityEngine;

namespace SilksongCoop.Mod
{
    public partial class SilksongCoopPlugin
    {
        private const float WalletSeedRetryIntervalSeconds = 0.25f;

        internal readonly struct WalletSeedRetryState
        {
            public readonly float Deadline;
            public readonly float NextAttempt;
            public WalletSeedRetryState(float deadline, float nextAttempt)
            {
                Deadline = deadline;
                NextAttempt = nextAttempt;
            }
        }

        private readonly Dictionary<NetworkPeerId, WalletSeedRetryState> _walletSeedOutstanding = new();

        private void PumpWalletSeed()
        {
            if (_walletSeedOutstanding.Count == 0) return;
            var session = SessionState;
            var transport = ActiveTransport;
            if (session?.IsConnected != true || session.IsHost != true || transport == null)
            {
                _walletSeedOutstanding.Clear();
                return;
            }

            var now = Time.unscaledTime;
            List<NetworkPeerId>? delivered = null;
            List<KeyValuePair<NetworkPeerId, WalletSeedRetryState>>? retries = null;
            foreach (var pair in _walletSeedOutstanding)
            {
                if (now < pair.Value.NextAttempt)
                {
                    retries ??= new List<KeyValuePair<NetworkPeerId, WalletSeedRetryState>>();
                    retries.Add(pair);
                    continue;
                }

                if (_vitalsSyncManager?.SendWalletSeed(pair.Key,
                    VitalsSync.CaptureWalletSeed(transport.LocalPeerId, session.RemotePeerIds)) == true)
                {
                    _walletPeerBootstrapCompleted[pair.Key] = true;
                    delivered ??= new List<NetworkPeerId>();
                    delivered.Add(pair.Key);
                    continue;
                }

                if (now < pair.Value.Deadline)
                {
                    retries ??= new List<KeyValuePair<NetworkPeerId, WalletSeedRetryState>>();
                    retries.Add(new KeyValuePair<NetworkPeerId, WalletSeedRetryState>(
                        pair.Key, new WalletSeedRetryState(pair.Value.Deadline, now + WalletSeedRetryIntervalSeconds)));
                    continue;
                }

                _walletSeedOutstanding.Clear();
                LogWarn("[Coop] Wallet seed could not be delivered; disconnecting.");
                DisconnectFromSession(DisconnectReason.ConnectionFailed);
                return;
            }

            if (delivered != null)
            {
                foreach (var peer in delivered) _walletSeedOutstanding.Remove(peer);
            }
            if (retries != null)
            {
                foreach (var retry in retries) _walletSeedOutstanding[retry.Key] = retry.Value;
            }
        }
    }
}
