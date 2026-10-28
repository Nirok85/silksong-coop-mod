using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using SilksongCoop.Tests.TestHarness;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Regression tests for the findings in GitHub issues #1 and #2.
    /// </summary>
    public class IssueFixesVerificationTests
    {
        // Issue #1.6 — a positive delta may advertise a higher snapshot than the local
        // wallet (a previous gain was missed); reconcile upward.
        [Fact]
        public void Currency_PositiveDelta_ReconcilesToSenderSnapshot()
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, 0, initialRosaries: 100, initialShards: 0);

            service.HandleCurrencyPacket(new NetworkPeerId(1), new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 1, 1),
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 10,
                TotalWalletAmount = 120,
                TransactionId = Guid.NewGuid()
            });

            Assert.Equal(120, service.SharedRosaries);
        }

        // Issue #1.6 — a stale negative packet must not resurrect a balance that was
        // already spent down (the old "max with total" would have restored 15).
        [Fact]
        public void Currency_NegativeDelta_DoesNotResurrectStaleTotal()
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, 0, initialRosaries: 10, initialShards: 0);

            service.HandleCurrencyPacket(new NetworkPeerId(1), new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 1, 1),
                Currency = CurrencyType.Rosaries,
                DeltaAmount = -35,
                TotalWalletAmount = 15,
                TransactionId = Guid.NewGuid()
            });

            Assert.Equal(0, service.SharedRosaries);
        }

        // Issue #1.6 — the GUID window is finite (500). A replayed packet must also be
        // rejected by the monotonic per-sender sequence even with a fresh GUID.
        [Fact]
        public void Currency_ReplayedSequence_IsRejectedEvenWithNewGuid()
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, 0, initialRosaries: 0, initialShards: 0);

            service.HandleCurrencyPacket(new NetworkPeerId(1), new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 1, 1),
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 10,
                TotalWalletAmount = 10,
                PickupUniqueId = 1,
                TransactionId = Guid.NewGuid()
            });

            service.HandleCurrencyPacket(new NetworkPeerId(1), new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 2, 2),
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 10,
                TotalWalletAmount = 20,
                PickupUniqueId = 2,
                TransactionId = Guid.NewGuid()
            });

            Assert.Equal(20, service.SharedRosaries);

            // Replay of sequence #1 with a brand-new GUID: rejected.
            service.HandleCurrencyPacket(new NetworkPeerId(1), new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 3, 3),
                Currency = CurrencyType.Rosaries,
                DeltaAmount = 10,
                TotalWalletAmount = 30,
                PickupUniqueId = 1,
                TransactionId = Guid.NewGuid()
            });

            Assert.Equal(20, service.SharedRosaries);
        }

        // Issue #2.1 — the two-coordinator scenario from the review: host spectates,
        // partner walks into another room, host's revive timer falls back to a solo bench
        // respawn. The living partner must not be killed and must mark the host alive.
        [Fact]
        public void SoloBenchFallback_OverTransport_DoesNotWipeLivingPartner()
        {
            using var harness = new DualClientTestHarness();

            bool clientWiped = false;
            harness.ClientDeath.OnTeamWipeTriggered += () => clientWiped = true;

            // Host is spectating; partner is alive.
            harness.HostDeath.IsPartnerAlive = true;
            harness.HostDeath.OnLocalPlayerDied(1f, 2f);
            harness.DeliverAll();
            Assert.Equal(DeathState.Spectating, harness.HostDeath.CurrentState);

            // Cross-scene revive fallback (as ReviveAtPartner does).
            harness.HostDeath.ForceBenchRespawn(notifyPartner: false);
            harness.DeliverAll();

            Assert.False(clientWiped);
            Assert.Equal(DeathState.Alive, harness.ClientDeath.CurrentState);
            Assert.True(harness.ClientDeath.IsPartnerAlive);
        }

        // Issue #1.1 — the legacy code is a deterministic encoding of IP:port, so it is not
        // a shared secret. The menu appends 40 random bits and the HMAC is keyed on the full
        // canonical string: knowing the address alone must not satisfy the challenge.
        [Fact]
        public void RoomCode_WithSecret_IsNotDerivableFromAddress()
        {
            var ip = IPAddress.Parse("192.168.1.42");
            string legacy = RoomCodeCodec.Encode(ip, 7777);
            string withSecret = RoomCodeCodec.EncodeWithSecret(ip, 7777);

            Assert.NotEqual(legacy, withSecret);
            Assert.True(withSecret.Length > legacy.Length);
            Assert.False(RoomCodeCodec.IsDeprecatedCode(withSecret));

            Assert.True(RoomCodeCodec.TryDecode(withSecret, out IPAddress decodedIp, out int decodedPort));
            Assert.Equal(ip, decodedIp);
            Assert.Equal(7777, decodedPort);

            // Fresh randomness on every host start.
            Assert.NotEqual(withSecret, RoomCodeCodec.EncodeWithSecret(ip, 7777));

            // Both peers must key the HMAC on the same canonical string.
            Assert.True(RoomCodeCodec.TryCanonicalize(withSecret.ToLowerInvariant(), out string canonical));
            Assert.Equal(withSecret, canonical);

            // A client that only knows IP:port can rebuild the legacy code, but that
            // signature is rejected by the host that uses the secret-bearing code.
            var hostAuth = new ConnectionAuthenticator();
            byte[] nonce = hostAuth.GenerateNonce();
            byte[] attackerSignature = new ConnectionAuthenticator().ComputeSignature(nonce, legacy);
            Assert.False(hostAuth.ValidateSignature(attackerSignature, withSecret));

            var hostAuth2 = new ConnectionAuthenticator();
            byte[] nonce2 = hostAuth2.GenerateNonce();
            byte[] clientSignature = new ConnectionAuthenticator().ComputeSignature(nonce2, canonical);
            Assert.True(hostAuth2.ValidateSignature(clientSignature, withSecret));
        }

        // Issue #2.1 — the solo fallback must not mark the living partner as dead. The
        // coordinator's IsPartnerAlive flag gates the next dual-wipe decision.
        [Fact]
        public void SoloBenchRespawn_KeepsPartnerAliveLocally()
        {
            var coordinator = new DeathLifecycleCoordinator();
            coordinator.IsPartnerAlive = true;

            coordinator.ForceBenchRespawn(notifyPartner: false);

            Assert.True(coordinator.IsPartnerAlive);
            Assert.Equal(DeathState.TeamWipe, coordinator.CurrentState);
        }

        // The SoloBenchRespawn packet carries no coordinates. Receiving it used to reset
        // LastPartnerPosition to (0,0), so a later revive timer teleported the player to the
        // world origin. It must keep the last known position and ignore the epoch gap.
        [Fact]
        public void SoloBenchRespawn_PreservesLastPartnerPosition_AndIgnoresEpoch()
        {
            var receiver = new DeathLifecycleCoordinator();
            receiver.LastPartnerPosition = (123.5f, -45.25f);
            receiver.IsPartnerAlive = false;

            receiver.HandleDeathPacket(new NetworkPeerId(2), new DeathLifecyclePacket
            {
                Header = new PacketHeader(PacketType.PlayerDeath, 4, 1, 1),
                EventType = DeathEventType.SoloBenchRespawn,
                Epoch = receiver.CurrentEpoch + 7,
                CoordinateX = 0f,
                CoordinateY = 0f
            });

            Assert.True(receiver.IsPartnerAlive);
            Assert.Equal(123.5f, receiver.LastPartnerPosition.X);
            Assert.Equal(-45.25f, receiver.LastPartnerPosition.Y);
        }

        // Issue #1.1 — a failed challenge response must not invalidate the pending nonce:
        // otherwise an unauthenticated (spoofed) reply locks the real client out.
        [Fact]
        public void Authenticator_FailedResponse_KeepsChallengeUsable()
        {
            var authenticator = new ConnectionAuthenticator();
            byte[] nonce = authenticator.GenerateNonce();

            byte[] wrong = authenticator.ComputeSignature(nonce, "SK-WRONG");
            Assert.False(authenticator.ValidateSignature(wrong, "SK-CORRECT"));

            // The legitimate client can still answer the same challenge.
            byte[] correct = authenticator.ComputeSignature(nonce, "SK-CORRECT");
            Assert.True(authenticator.ValidateSignature(correct, "SK-CORRECT"));
        }

        // Issue #1.1 — the host must not pin its single peer slot to the first
        // unauthenticated handshake. A wrong-secret client that reaches the challenge step
        // must not prevent the real client from connecting.
        [Fact]
        public void UdpHost_FailedHandshake_DoesNotBlockAuthenticatedClient()
        {
            using var host = new LiteNetLibTransport(1, "Host");
            host.StartHost(0);
            string hostCode = RoomCodeCodec.EncodeWithSecret(IPAddress.Loopback, host.BoundPort);
            host.SetRoomCode(hostCode);
            host.RequireRoomCode = true;

            var wrongSecret = new byte[RoomCodeCodec.SecretBytes];
            wrongSecret[0] = 0xFF;
            string wrongCode = RoomCodeCodec.Encode(IPAddress.Loopback, host.BoundPort, wrongSecret);

            using var attacker = new LiteNetLibTransport(3, "Attacker");
            attacker.Connect(wrongCode);

            // Let the attacker reach ChallengeResponse and fail HMAC validation.
            for (int i = 0; i < 40; i++)
            {
                host.PollEvents();
                attacker.PollEvents();
                Thread.Sleep(10);
            }
            Assert.False(attacker.IsConnected);

            using var client = new LiteNetLibTransport(2, "Client");
            client.Connect(hostCode);

            for (int i = 0; i < 200 && (!host.IsConnected || !client.IsConnected); i++)
            {
                host.PollEvents();
                client.PollEvents();
                Thread.Sleep(10);
            }

            Assert.True(client.IsConnected, "the authenticated client must not be blocked by a failed handshake");
            Assert.True(host.IsConnected, "host must accept the authenticated client");
        }

        // Issue #1.2 — a reconnect must resend the full inventory. The once-per-item dedup
        // used to survive the session, so the initial dump was swallowed and the new peer
        // never learned previously synced abilities/tools/crests.
        [Fact]
        public void Inventory_ResetSessionState_AllowsInitialDumpAgain()
        {
            var inventory = new InventorySyncService();
            int sent = 0;
            inventory.OnOutgoingItemUnlockPacket += _ => sent++;

            inventory.OnLocalItemDiscovered("hasDash", ItemCategory.Ability);
            Assert.Equal(1, sent);

            inventory.OnLocalItemDiscovered("hasDash", ItemCategory.Ability);
            Assert.Equal(1, sent); // same session: dedup still applies

            inventory.ResetSessionState();
            inventory.OnLocalItemDiscovered("hasDash", ItemCategory.Ability);
            Assert.Equal(2, sent); // new peer receives the unlock again
        }

        // Issue #1.5 — spawn type is independent from the facing flag. A checkpoint with
        // spawn type 2 and facing right used to be rejected by the validator.
        [Fact]
        public void BenchCheckpoint_SpawnTypeTwoAndFacingRight_IsValid()
        {
            var packet = new BenchCheckpointSyncPacket
            {
                Header = new PacketHeader(PacketType.BenchCheckpointSync, 0, 1, 1),
                SceneName = "DeepDocks_01",
                RespawnMarker = "Bench_Docks_Rest",
                SpawnType = 2,
                FacingRight = true
            };

            Assert.True(PacketValidator.ValidateBenchCheckpoint(packet));
            Assert.Equal(2, packet.FacingDirection);
            Assert.True(packet.AtBench);
        }

        // Issue #1.3 — a modified peer must not be able to push MaxHealth = 255 into the
        // partner's save. Honest values still pass.
        [Fact]
        public void Vitals_OutOfRangeProgression_IsRejected()
        {
            Assert.False(PacketValidator.ValidateVitalsSync(new VitalsSyncPacket
            {
                MaxHealth = 255,
                CurrentHealth = 5,
                MaxSilkSpools = 1,
                NeedleUpgradeLevel = 0
            }));

            Assert.False(PacketValidator.ValidateVitalsSync(new VitalsSyncPacket
            {
                MaxHealth = 5,
                CurrentHealth = 5,
                MaxSilkSpools = 200,
                NeedleUpgradeLevel = 0
            }));

            Assert.True(PacketValidator.ValidateVitalsSync(new VitalsSyncPacket
            {
                MaxHealth = 10,
                CurrentHealth = 10,
                MaxSilkSpools = 3,
                CurrentSilk = 27,
                NeedleUpgradeLevel = 4
            }));
        }

        // Issue #1.3 — absurd wallet values are rejected before they reach PlayerData.
        [Fact]
        public void Currency_TotalAboveSaneCeiling_IsRejected()
        {
            Assert.False(PacketValidator.ValidateCurrencySync(new CurrencySyncPacket
            {
                Currency = CurrencyType.Rosaries,
                TotalWalletAmount = int.MaxValue,
                DeltaAmount = 1,
                TransactionId = Guid.NewGuid()
            }));

            Assert.True(PacketValidator.ValidateCurrencySync(new CurrencySyncPacket
            {
                Currency = CurrencyType.Rosaries,
                TotalWalletAmount = 100_000_000,
                DeltaAmount = 1,
                TransactionId = Guid.NewGuid()
            }));
        }

        // Issue #2.1 — a solo bench fallback must not broadcast TeamWipe, and the peer
        // must merely mark us alive again instead of killing its own player.
        [Fact]
        public void SoloBenchRespawn_DoesNotWipeLivingPartner()
        {
            var sender = new DeathLifecycleCoordinator();
            DeathLifecyclePacket? outgoing = null;
            sender.OnOutgoingDeathPacket += p => outgoing = p;

            sender.ForceBenchRespawn(notifyPartner: false);

            Assert.NotNull(outgoing);
            Assert.Equal(DeathEventType.SoloBenchRespawn, outgoing!.Value.EventType);

            var receiver = new DeathLifecycleCoordinator();
            bool wiped = false;
            receiver.OnTeamWipeTriggered += () => wiped = true;

            receiver.HandleDeathPacket(new NetworkPeerId(2), outgoing.Value);

            Assert.False(wiped);
            Assert.Equal(DeathState.Alive, receiver.CurrentState);
            Assert.True(receiver.IsPartnerAlive);
        }

        // Issue #2.3 — the new signed-delta counter ids must bypass the once-per-item
        // unlock dedup, otherwise a second pickup of the same value is swallowed.
        [Fact]
        public void CounterDeltaPseudoItems_AreNotDeduplicatedByItemId()
        {
            var inventory = new InventorySyncService();
            var received = new List<string>();
            inventory.OnRemoteItemUnlocked += (id, _) => received.Add(id);

            inventory.HandleItemUnlockPacket(new NetworkPeerId(1), new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlockSync, 3, 1, 1),
                ItemId = "heartPieces:+1",
                Category = ItemCategory.KeyItem,
                TransactionId = Guid.NewGuid()
            });
            inventory.HandleItemUnlockPacket(new NetworkPeerId(1), new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlockSync, 3, 1, 2),
                ItemId = "heartPieces:+1",
                Category = ItemCategory.KeyItem,
                TransactionId = Guid.NewGuid()
            });

            Assert.Equal(2, received.Count);
        }
    }
}
