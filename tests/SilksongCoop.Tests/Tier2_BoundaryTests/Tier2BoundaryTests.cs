using System;
using System.Net;
using SilksongCoop.Core.Interpolation;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.Revive;
using SilksongCoop.Core.State;
using SilksongCoop.Tests.TestHarness;
using Xunit;

namespace SilksongCoop.Tests.Tier2_BoundaryTests
{
    /// <summary>
    /// Tier 2: 25 Boundary & Error Handling Tests.
    /// Verifies system robustness under protocol limits, CRC errors, zero-HP/silk, payload buffers, and edge conditions.
    /// </summary>
    public class Tier2BoundaryTests
    {
        // -------------------------------------------------------------------------
        // 1. Protocol Limits: Reader Buffer Underflow (< 12 bytes header)
        // -------------------------------------------------------------------------
        [Fact]
        public void B01_ProtocolLimit_ReaderBufferUnderflow_ShortHeader()
        {
            byte[] truncated = new byte[] { 0x53, 0x4B, 0x01 }; // Only 3 bytes instead of 12
            var reader = new PacketReader(truncated);
            bool success = reader.TryReadHeader(out _);
            Assert.False(success);
        }

        // -------------------------------------------------------------------------
        // 2. Protocol Limits: Writer Buffer Overflow Throws Gracefully
        // -------------------------------------------------------------------------
        [Fact]
        public void B02_ProtocolLimit_WriterBufferOverflow_ThrowsGracefully()
        {
            Span<byte> tinyBuffer = stackalloc byte[4];
            var writer = new PacketWriter(tinyBuffer);
            var header = new PacketHeader(PacketType.TransformSync, 0, 1, 100);

            bool threw = false;
            try
            {
                writer.WriteHeader(header); // Needs 12 bytes, only 4 available
            }
            catch (Exception)
            {
                threw = true;
            }
            Assert.True(threw);
        }

        // -------------------------------------------------------------------------
        // 3. Protocol Limits: Truncated Payload Handled Safely
        // -------------------------------------------------------------------------
        [Fact]
        public void B03_ProtocolLimit_TruncatedPayload_HandledSafely()
        {
            // Valid header but payload cuts off mid-float
            Span<byte> buffer = stackalloc byte[14];
            var writer = new PacketWriter(buffer);
            writer.WriteHeader(new PacketHeader(PacketType.TransformSync, 0, 1, 100));
            writer.WriteByte(0xAA);
            writer.WriteByte(0xBB);

            var reader = new PacketReader(buffer);
            Assert.True(reader.TryReadHeader(out _));

            bool threw = false;
            try
            {
                reader.ReadFloat(); // Needs 4 bytes, only 2 remain
            }
            catch (Exception)
            {
                threw = true;
            }
            Assert.True(threw);
        }

        // -------------------------------------------------------------------------
        // 4. Codec CRC: Invalid Checksum Returns False
        // -------------------------------------------------------------------------
        [Fact]
        public void B04_CodecCRC_InvalidChecksum_ReturnsFalse()
        {
            var ip = IPAddress.Parse("127.0.0.1");
            string validCode = RoomCodeCodec.Encode(ip, 7777);

            // Corrupt the last character
            char corruptedChar = validCode[^1] == 'A' ? 'B' : 'A';
            string corruptedCode = validCode.Substring(0, validCode.Length - 1) + corruptedChar;

            bool success = RoomCodeCodec.TryDecode(corruptedCode, out _, out _);
            Assert.False(success);
        }

        // -------------------------------------------------------------------------
        // 5. Codec CRC: Invalid Non-Crockford Characters Returns False
        // -------------------------------------------------------------------------
        [Fact]
        public void B05_CodecCRC_InvalidNonCrockfordCharacters_ReturnsFalse()
        {
            // Characters like 'I', 'O', 'U' are intentionally excluded in Crockford Base32
            string invalidCode = "SK-IIII-OOOO-UUUU";
            bool success = RoomCodeCodec.TryDecode(invalidCode, out _, out _);
            Assert.False(success);
        }

        // -------------------------------------------------------------------------
        // 6. Codec CRC: Code Length Mismatch Returns False
        // -------------------------------------------------------------------------
        [Fact]
        public void B06_CodecCRC_LengthMismatch_ReturnsFalse()
        {
            string shortCode = "SK-ABC";
            bool success = RoomCodeCodec.TryDecode(shortCode, out _, out _);
            Assert.False(success);
        }

        // -------------------------------------------------------------------------
        // 7. Zero-HP: Damage Exceeding Current Health Clamps to Zero
        // -------------------------------------------------------------------------
        [Fact]
        public void B07_ZeroHP_DamageExceedingCurrentHealth_ClampsToZero()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(3, 5, 9, 1, 1, 0, 0);

            // Take 10 damage when HP is 3
            harness.HostVitals.OnLocalDamageTaken(10, 0);
            harness.DeliverAll();

            Assert.Equal(0, harness.HostVitals.LocalHealth);
        }

        // -------------------------------------------------------------------------
        // 8. Zero-Silk: Silk Gauge Depleted Clamps to Zero
        // -------------------------------------------------------------------------
        [Fact]
        public void B08_ZeroSilk_ExcessUsage_ClampsToZero()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(5, 5, 2, 1, 1, 0, 0);

            // Try to use 5 silk when only 2 remain
            harness.HostVitals.OnLocalSilkUsed(5, 0);
            harness.DeliverAll();

            Assert.Equal(0, harness.HostVitals.LocalSilk);
        }

        // -------------------------------------------------------------------------
        // 9. Payload Buffer: Max Supported ItemId String (250 chars) Serializes Cleanly
        // -------------------------------------------------------------------------
        [Fact]
        public void B09_PayloadBuffer_MaxSupportedItemIdString_SerializesCleanly()
        {
            string largeItemId = new string('A', 250);
            var packet = new ItemUnlockPacket
            {
                Header = new PacketHeader(PacketType.ItemUnlock, 3, 1, 500),
                ItemId = largeItemId,
                Category = ItemCategory.Tool,
                TransactionId = Guid.NewGuid(),
                Timestamp = 500
            };

            Span<byte> buffer = stackalloc byte[512];
            var writer = new PacketWriter(buffer);
            packet.Serialize(ref writer);

            var reader = new PacketReader(buffer.Slice(0, writer.Position));
            var roundtrip = ItemUnlockPacket.Deserialize(ref reader);

            Assert.Equal(largeItemId, roundtrip.ItemId);
            Assert.Equal(packet.TransactionId, roundtrip.TransactionId);
        }

        // -------------------------------------------------------------------------
        // 10. Payload Buffer: Large Integer Currency Preserved Without Overflow
        // -------------------------------------------------------------------------
        [Fact]
        public void B10_PayloadBuffer_MaxIntCurrencyAmount_PreservedWithoutOverflow()
        {
            var packet = new CurrencySyncPacket
            {
                Header = new PacketHeader(PacketType.CurrencySync, 3, 1, 600),
                Currency = CurrencyType.Rosaries,
                TotalWalletAmount = 2_000_000_000,
                DeltaAmount = 15_000,
                PickupUniqueId = 9999,
                TransactionId = Guid.NewGuid()
            };

            Span<byte> buffer = stackalloc byte[64];
            var writer = new PacketWriter(buffer);
            packet.Serialize(ref writer);

            var reader = new PacketReader(buffer.Slice(0, writer.Position));
            var roundtrip = CurrencySyncPacket.Deserialize(ref reader);

            Assert.Equal(2_000_000_000, roundtrip.TotalWalletAmount);
            Assert.Equal(15_000, roundtrip.DeltaAmount);
        }

        // -------------------------------------------------------------------------
        // 11. Max Progression: Max Health Upgrade Monotonicity
        // -------------------------------------------------------------------------
        [Fact]
        public void B11_MaxProgressionLimits_MaxHealthUpgrade_ClampsOrPreservesMonotonicity()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(5, 5, 9, 1, 1, 0, 0);
            harness.ClientVitals.InitializeState(5, 5, 9, 1, 1, 0, 0);

            harness.HostVitals.OnLocalMaxHealthUpgrade(9);
            harness.DeliverAll();
            Assert.Equal(9, harness.ClientVitals.SharedMaxHealth);

            // Attempting to send a lower tier packet must not regress progression
            var lowerPacket = new VitalsSyncPacket
            {
                Header = new PacketHeader(PacketType.VitalsSync, 2, 2, 800),
                CurrentHealth = 5,
                MaxHealth = 6, // Lower than 9
                CurrentSilk = 0,
                MaxSilkSpools = 1,
                NeedleUpgradeLevel = 1
            };
            harness.ClientVitals.HandleVitalsPacket(harness.HostTransport.LocalPeerId, lowerPacket);

            Assert.Equal(9, harness.ClientVitals.SharedMaxHealth);
        }

        // -------------------------------------------------------------------------
        // 12. Max Progression: Needle Upgrade Monotonicity
        // -------------------------------------------------------------------------
        [Fact]
        public void B12_MaxProgressionLimits_NeedleUpgrade_ClampsOrPreservesMonotonicity()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(5, 5, 9, 1, 1, 0, 0);
            harness.ClientVitals.InitializeState(5, 5, 9, 1, 1, 0, 0);

            harness.HostVitals.OnLocalNeedleUpgrade(3);
            harness.DeliverAll();
            Assert.Equal(3, harness.ClientVitals.SharedNeedleUpgradeLevel);

            // Stale packet with needle level 1 must not downgrade
            var stale = new VitalsSyncPacket
            {
                Header = new PacketHeader(PacketType.VitalsSync, 2, 2, 850),
                CurrentHealth = 5,
                MaxHealth = 5,
                CurrentSilk = 0,
                MaxSilkSpools = 1,
                NeedleUpgradeLevel = 1
            };
            harness.ClientVitals.HandleVitalsPacket(harness.HostTransport.LocalPeerId, stale);

            Assert.Equal(3, harness.ClientVitals.SharedNeedleUpgradeLevel);
        }

        // -------------------------------------------------------------------------
        // 13. Max Progression: Max Silk Upgrade Monotonicity
        // -------------------------------------------------------------------------
        [Fact]
        public void B13_MaxProgressionLimits_MaxSilkUpgrade_ClampsOrPreservesMonotonicity()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(5, 5, 9, 1, 1, 0, 0);
            harness.ClientVitals.InitializeState(5, 5, 9, 1, 1, 0, 0);

            harness.HostVitals.OnLocalMaxSilkUpgrade(3);
            harness.DeliverAll();
            Assert.Equal(3, harness.ClientVitals.SharedMaxSilkSpools);

            // Lower spool capacity cannot overwrite higher
            var lower = new VitalsSyncPacket
            {
                Header = new PacketHeader(PacketType.VitalsSync, 2, 2, 900),
                CurrentHealth = 5,
                MaxHealth = 5,
                CurrentSilk = 0,
                MaxSilkSpools = 2,
                NeedleUpgradeLevel = 1
            };
            harness.ClientVitals.HandleVitalsPacket(harness.HostTransport.LocalPeerId, lower);

            Assert.Equal(3, harness.ClientVitals.SharedMaxSilkSpools);
        }

        // -------------------------------------------------------------------------
        // 14. Extreme Coordinates: Large World Coordinates Preserve Float Precision
        // -------------------------------------------------------------------------
        [Fact]
        public void B14_ExtremeCoordinates_LargeWorldPositions_PreserveFloatPrecision()
        {
            var packet = new TransformSyncPacket
            {
                Header = new PacketHeader(PacketType.TransformSync, 0, 1, 1000),
                PositionX = 10000.5f,
                PositionY = -5000.25f,
                VelocityX = 40.0f,
                VelocityY = -25.0f,
                FacingDirection = 0,
                StateFlags = TransformSyncPacket.FlagFalling
            };

            Span<byte> buffer = stackalloc byte[64];
            var writer = new PacketWriter(buffer);
            packet.Serialize(ref writer);

            var reader = new PacketReader(buffer.Slice(0, writer.Position));
            var roundtrip = TransformSyncPacket.Deserialize(ref reader);

            Assert.Equal(10000.5f, roundtrip.PositionX, precision: 2);
            Assert.Equal(-5000.25f, roundtrip.PositionY, precision: 2);
            Assert.Equal((byte)0, roundtrip.FacingDirection);
        }

        // -------------------------------------------------------------------------
        // 15. Dead Reckoning: Zero Velocity Extrapolation Maintains Static Position
        // -------------------------------------------------------------------------
        [Fact]
        public void B15_DeadReckoningBoundary_ZeroVelocity_MaintainsStaticPosition()
        {
            var (posX, posY) = DeadReckoning.ExtrapolatePosition(12.0f, 8.0f, 0f, 0f, isGrounded: true, deltaSeconds: 0.1f);

            Assert.Equal(12.0f, posX);
            Assert.Equal(8.0f, posY);
        }

        // -------------------------------------------------------------------------
        // 16. Dead Reckoning: Drift Exceeding Cutoff Threshold Triggers Snap
        // -------------------------------------------------------------------------
        [Fact]
        public void B16_DeadReckoningBoundary_DriftExceedingSnapCutoff_TriggersSnap()
        {
            float currentX = 0f, currentY = 0f;
            float targetX = 10f, targetY = 0f; // Distance is 10 units > 2.5 unit cutoff

            bool shouldSnap = DeadReckoning.ShouldSnap(currentX, currentY, targetX, targetY);
            Assert.True(shouldSnap);
        }

        // -------------------------------------------------------------------------
        // 17. Spline Boundary: Parameter t Clamped to [0, 1]
        // -------------------------------------------------------------------------
        [Fact]
        public void B17_HermiteSplineBoundary_TimeParameterOutsideZeroOne_ClampsCorrectly()
        {
            float p0 = 0f, v0 = 0f, p1 = 10f, v1 = 0f, dt = 1.0f;

            float pUnder = HermiteSpline.InterpolatePosition(p0, v0, p1, v1, dt, -0.5f);
            float pOver = HermiteSpline.InterpolatePosition(p0, v0, p1, v1, dt, 1.5f);

            Assert.Equal(p0, pUnder);
            Assert.Equal(p1, pOver);
        }

        // -------------------------------------------------------------------------
        // 18. Spline Boundary: Zero or Negative dt Handles Safely
        // -------------------------------------------------------------------------
        [Fact]
        public void B18_HermiteSplineBoundary_ZeroOrNegativeDt_HandlesWithoutDivisionByZero()
        {
            float p0 = 5f, v0 = 2f, p1 = 10f, v1 = 2f;

            float pZeroDt = HermiteSpline.InterpolatePosition(p0, v0, p1, v1, 0f, 0.5f);
            Assert.False(float.IsNaN(pZeroDt));
            Assert.False(float.IsInfinity(pZeroDt));
        }

        // -------------------------------------------------------------------------
        // 19. Revive Health: Odd Max Health Rounds Down with Minimum 1
        // -------------------------------------------------------------------------
        [Fact]
        public void B19_ReviveHealthBoundary_OddMaxHealth_RoundsDownWithMinimumOne()
        {
            Assert.Equal(1, SafeGroundSolver.CalculateReviveHealth(1));
            Assert.Equal(1, SafeGroundSolver.CalculateReviveHealth(2));
            Assert.Equal(2, SafeGroundSolver.CalculateReviveHealth(5));
            Assert.Equal(4, SafeGroundSolver.CalculateReviveHealth(9));
            Assert.Equal(5, SafeGroundSolver.CalculateReviveHealth(11));
        }

        // -------------------------------------------------------------------------
        // 20. Revive Timer Boundary: Clamps Within Configured Range
        // -------------------------------------------------------------------------
        [Fact]
        public void B20_ReviveTimerBoundary_ClampsWithinConfiguredRange()
        {
            var coordinator = new DeathLifecycleCoordinator { ReviveCooldownDuration = 10f };
            // In DeathLifecycleCoordinator: Math.Clamp(ReviveCooldownDuration, 15f, 30f)
            coordinator.OnLocalPlayerDied(0f, 0f);
            Assert.True(coordinator.RemainingSpectatorTime >= 15.0f);

            var coordinator2 = new DeathLifecycleCoordinator { ReviveCooldownDuration = 45f };
            coordinator2.OnLocalPlayerDied(0f, 0f);
            Assert.True(coordinator2.RemainingSpectatorTime <= 30.0f);
        }

        // -------------------------------------------------------------------------
        // 21. Duplicate GUID Boundary: Empty GUID Rejected Without Sync
        // -------------------------------------------------------------------------
        [Fact]
        public void B21_DuplicateGuidBoundary_EmptyGuid_RejectedWithoutSync()
        {
            using var harness = new DualClientTestHarness();
            harness.HostVitals.InitializeState(5, 5, 9, 1, 1, 0, 0);

            // Empty GUID is rejected
            harness.HostVitals.OnLocalCurrencyChanged(CurrencyType.Rosaries, 100, 100, Guid.Empty);
            harness.DeliverAll();

            Assert.Equal(0, harness.HostVitals.SharedRosaries);
        }

        // -------------------------------------------------------------------------
        // 22. Echo Suppression Boundary: Incoming Item Unlock Never Rebroadcasts
        // -------------------------------------------------------------------------
        [Fact]
        public void B22_EchoSuppressionBoundary_IncomingItemUnlock_NeverRebroadcasts()
        {
            using var harness = new DualClientTestHarness();

            int outgoingPacketsFromClient = 0;
            harness.ClientInventory.OnOutgoingItemUnlockPacket += _ => outgoingPacketsFromClient++;

            harness.HostInventory.OnLocalItemDiscovered("Tool_NeedlePhial", ItemCategory.Tool);
            harness.DeliverAll();

            // Client received unlock, but must NEVER rebroadcast it back to host
            Assert.Equal(0, outgoingPacketsFromClient);
        }

        // -------------------------------------------------------------------------
        // 23. Safe Ground Solver Boundary: Candidate Offsets and Distance Constants
        // -------------------------------------------------------------------------
        [Fact]
        public void B23_SafeGroundSolverBoundary_CandidateOffsetsAndConstants()
        {
            Assert.NotEmpty(SafeGroundSolver.CandidateOffsets);
            Assert.Equal(0f, SafeGroundSolver.CandidateOffsets[0]); // Direct below is first priority
            Assert.True(SafeGroundSolver.ProbeDistance >= 10.0f);
            Assert.Equal(2.5f, SafeGroundSolver.InvulnerabilityDuration);
        }

        // -------------------------------------------------------------------------
        // 24. Loadout Boundary: Whitespace or Null ItemId Dropped Safely
        // -------------------------------------------------------------------------
        [Fact]
        public void B24_LoadoutBoundary_WhitespaceOrNullItemId_DroppedSafely()
        {
            using var harness = new DualClientTestHarness();

            harness.HostInventory.OnLocalItemDiscovered(null!, ItemCategory.Tool);
            harness.HostInventory.OnLocalItemDiscovered("   ", ItemCategory.Crest);
            harness.DeliverAll();

            Assert.Empty(harness.HostInventory.UnlockedItems);
            Assert.Empty(harness.ClientInventory.UnlockedItems);
        }

        // -------------------------------------------------------------------------
        // 25. Channel Segregation: Unknown Packet Type Handled Gracefully
        // -------------------------------------------------------------------------
        [Fact]
        public void B25_ChannelSegregationBoundary_OutOfRangePacketType_HandledSafely()
        {
            using var harness = new DualClientTestHarness();

            // Construct packet with undefined PacketType (e.g. 0xFF)
            Span<byte> buffer = stackalloc byte[16];
            var writer = new PacketWriter(buffer);
            writer.WriteHeader(new PacketHeader((PacketType)0xFF, 0, 1, 100));

            // Transmit to client
            harness.HostTransport.Send(harness.ClientTransport.LocalPeerId, buffer.ToArray(), 0, writer.Position, DeliveryMethod.Unreliable, 0);

            // Delivering must not throw or crash client
            var ex = Record.Exception(() => harness.DeliverAll());
            Assert.Null(ex);
        }
    }
}
