using System;
using SilksongCoop.Core.Protocol.Packets;

namespace SilksongCoop.Core.Protocol
{
    /// <summary>
    /// Validates packet field ranges to prevent malformed or malicious data from corrupting state.
    /// </summary>
    public static class PacketValidator
    {
        // Field range limits
        private const float MaxPositionValue = 10000f;
        private const float MaxVelocityValue = 1000f;

        /// <summary>
        /// Upper bounds for progression values received from a peer. A modified client
        /// could otherwise push arbitrary values into the partner's save file (the old
        /// validator accepted MaxHealth = 255). These ceilings sit far above anything the
        /// game legitimately produces, so honest sessions are unaffected.
        /// </summary>
        public const int MaxReasonableMaxHealth = 64;
        public const int MaxReasonableSilkSpools = 16;
        public const int MaxReasonableCurrency = 100_000_000;

        // Enemy HP is much larger than player HP (bosses reach thousands), but still finite.
        public const int MaxEnemyHp = 10_000_000;
        public const int MaxEnemyDamage = short.MaxValue;

        /// <summary>
        /// Upper bound for a persistent int item. These counters encode small game
        /// quantities (chests, caches, collectibles); an arbitrary int could corrupt state.
        /// </summary>
        public const int MaxPersistentIntValue = 1_000_000;

        /// <summary>
        /// Validates a TransformSync packet's field values.
        /// </summary>
        public static bool ValidateTransformSync(TransformSyncPacket packet)
        {
            // BUG-C2: reject NaN / Infinity — Math.Abs(NaN) > limit evaluates to false,
            // so a corrupted packet could poison the Transform matrix and break Unity's
            // 2D physics for the session.
            if (float.IsNaN(packet.PositionX) || float.IsInfinity(packet.PositionX) ||
                float.IsNaN(packet.PositionY) || float.IsInfinity(packet.PositionY) ||
                float.IsNaN(packet.VelocityX) || float.IsInfinity(packet.VelocityX) ||
                float.IsNaN(packet.VelocityY) || float.IsInfinity(packet.VelocityY))
            {
                return false;
            }

            // Validate position is within reasonable game bounds
            if (Math.Abs(packet.PositionX) > MaxPositionValue || Math.Abs(packet.PositionY) > MaxPositionValue)
            {
                return false;
            }

            // Validate velocity is within reasonable bounds
            if (Math.Abs(packet.VelocityX) > MaxVelocityValue || Math.Abs(packet.VelocityY) > MaxVelocityValue)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Validates a VitalsSync packet's field values.
        /// </summary>
        public static bool ValidateVitalsSync(VitalsSyncPacket packet)
        {
            // Bounds are deliberately generous but finite: a malicious peer must not be
            // able to write MaxHealth = 255 (or any other absurd value) into our save.
            if (packet.MaxHealth < 1 || packet.MaxHealth > MaxReasonableMaxHealth)
            {
                return false;
            }

            if (packet.CurrentHealth > packet.MaxHealth)
            {
                return false;
            }

            if (packet.CurrentHealth > MaxReasonableMaxHealth)
            {
                return false;
            }

            if (packet.MaxSilkSpools > MaxReasonableSilkSpools)
            {
                return false;
            }

            // Silk is stored per spool (9 units each); allow a small margin, then cap.
            int maxSilk = Math.Max(9, packet.MaxSilkSpools * 9);
            if (packet.CurrentSilk > maxSilk)
            {
                return false;
            }

            // Validate needle level is reasonable (0-4 upgrades)
            if (packet.NeedleUpgradeLevel > 4)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Validates a CurrencySync packet's field values.
        /// </summary>
        public static bool ValidateCurrencySync(CurrencySyncPacket packet)
        {
            // Validate currency type is valid enum value
            if (packet.Currency != CurrencyType.Rosaries && packet.Currency != CurrencyType.ShellShards)
            {
                return false;
            }

            // Validate total wallet amount is non-negative and within a sane ceiling
            if (packet.TotalWalletAmount < 0 || packet.TotalWalletAmount > MaxReasonableCurrency)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Validates a DeathLifecycle packet's field values.
        /// </summary>
        public static bool ValidateDeathLifecycle(DeathLifecyclePacket packet)
        {
            if (!IsDefined(packet.EventType))
                return false;

            if (float.IsNaN(packet.CoordinateX) || float.IsInfinity(packet.CoordinateX) ||
                float.IsNaN(packet.CoordinateY) || float.IsInfinity(packet.CoordinateY))
                return false;

            if (Math.Abs(packet.CoordinateX) > MaxPositionValue || Math.Abs(packet.CoordinateY) > MaxPositionValue)
                return false;

            if (packet.RemainingSeconds < 0f || float.IsNaN(packet.RemainingSeconds) || float.IsInfinity(packet.RemainingSeconds))
                return false;

            if (packet.Epoch == 0)
                return false;

            return true;
        }

        /// <summary>
        /// Validates an ItemUnlock packet's field values.
        /// </summary>
        public static bool ValidateItemUnlock(ItemUnlockPacket packet)
        {
            if (string.IsNullOrWhiteSpace(packet.ItemId))
                return false;

            if (packet.ItemId.Length > 256)
                return false;

            if (!IsDefined(packet.Category))
                return false;

            if (packet.TransactionId == Guid.Empty)
                return false;

            return true;
        }

        /// <summary>
        /// Validates a SceneTransition packet's field values.
        /// </summary>
        public static bool ValidateSceneTransition(SceneTransitionPacket packet)
        {
            if (packet.TransitionPhase != 0 && packet.TransitionPhase != 1)
                return false;

            if (packet.TargetScene != null && packet.TargetScene.Length > 256)
                return false;

            if (packet.EntryGate != null && packet.EntryGate.Length > 256)
                return false;

            return true;
        }

        /// <summary>
        /// Validates an ActionTrigger packet's field values.
        /// </summary>
        public static bool ValidateActionTrigger(ActionTriggerPacket packet)
        {
            if (!IsDefined(packet.Action))
                return false;

            if (float.IsNaN(packet.OriginX) || float.IsInfinity(packet.OriginX) ||
                float.IsNaN(packet.OriginY) || float.IsInfinity(packet.OriginY) ||
                float.IsNaN(packet.DirectionX) || float.IsInfinity(packet.DirectionX) ||
                float.IsNaN(packet.DirectionY) || float.IsInfinity(packet.DirectionY))
                return false;

            if (Math.Abs(packet.OriginX) > MaxPositionValue || Math.Abs(packet.OriginY) > MaxPositionValue)
                return false;

            return true;
        }

        /// <summary>
        /// Validates an EnemySync packet's field values.
        /// </summary>
        public static bool ValidateEnemySync(EnemySyncPacket packet)
        {
            if (packet.EnemyKey == 0)
                return false;

            if (!IsDefined(packet.EventType))
                return false;

            if (packet.Hp < 0 || packet.Hp > MaxEnemyHp)
                return false;

            if (packet.EventType == EnemySyncEventType.Death && packet.Hp != 0)
                return false;

            if (packet.Damage < 0 || packet.Damage > MaxEnemyDamage)
                return false;

            if (float.IsNaN(packet.PositionX) || float.IsInfinity(packet.PositionX) ||
                float.IsNaN(packet.PositionY) || float.IsInfinity(packet.PositionY))
                return false;

            if (Math.Abs(packet.PositionX) > MaxPositionValue || Math.Abs(packet.PositionY) > MaxPositionValue)
                return false;

            // AttackTypes is a game enum; cap the raw byte so a malicious peer cannot index
            // outside the small expected range.
            if (packet.AttackType > 64)
                return false;

            return true;
        }

        /// <summary>
        /// Validates an EnemySnapshot packet's field values.
        /// </summary>
        public static bool ValidateEnemySnapshot(EnemySnapshotPacket packet)
        {
            if (packet.Entries == null)
                return packet.Count == 0;

            if (packet.Count < 0 || packet.Count > EnemySnapshotPacket.MaxEntries || packet.Count > packet.Entries.Length)
                return false;

            for (int i = 0; i < packet.Count; i++)
            {
                var entry = packet.Entries[i];
                if (entry.EnemyKey == 0)
                    return false;

                if (entry.Hp < 0 || entry.Hp > MaxEnemyHp)
                    return false;

                if ((entry.Flags & ~EnemySnapshotEntry.FlagDead) != 0)
                    return false;

                if (float.IsNaN(entry.PositionX) || float.IsInfinity(entry.PositionX) ||
                    float.IsNaN(entry.PositionY) || float.IsInfinity(entry.PositionY))
                    return false;

                if (Math.Abs(entry.PositionX) > MaxPositionValue || Math.Abs(entry.PositionY) > MaxPositionValue)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Validates a WorldEvent packet's field values.
        /// </summary>
        public static bool ValidateWorldEvent(WorldEventPacket packet)
        {
            if (packet.ObjectKey == 0)
                return false;

            if (!IsDefined(packet.EventType))
                return false;

            if (float.IsNaN(packet.PositionX) || float.IsInfinity(packet.PositionX) ||
                float.IsNaN(packet.PositionY) || float.IsInfinity(packet.PositionY))
                return false;

            if (Math.Abs(packet.PositionX) > MaxPositionValue || Math.Abs(packet.PositionY) > MaxPositionValue)
                return false;

            return true;
        }

        /// <summary>
        /// Validates a PersistentState packet's field values.
        /// </summary>
        public static bool ValidatePersistentState(PersistentStatePacket packet)
        {
            if (packet.ItemKey == 0)
                return false;

            if (!IsDefined(packet.ValueType))
                return false;

            if (packet.ValueType == PersistentValueType.Bool && packet.Value != 0 && packet.Value != 1)
                return false;

            if (packet.ValueType == PersistentValueType.Int &&
                (packet.Value < -MaxPersistentIntValue || packet.Value > MaxPersistentIntValue))
                return false;

            return true;
        }

        /// <summary>
        /// Validates a BenchCheckpointSync packet's field values.
        /// </summary>
        public static bool ValidateBenchCheckpoint(BenchCheckpointSyncPacket packet)
        {
            if (packet.SceneName == null || packet.SceneName.Length > 256)
                return false;

            if (packet.RespawnMarker == null || packet.RespawnMarker.Length > 256)
                return false;

            // Spawn type and facing direction are independent fields: the old check
            // compared the spawn type against {0,1} whenever facing-right was set, which
            // silently dropped legitimate checkpoints whose spawn type was, e.g., 2.
            // SpawnType is intentionally an opaque enum value here.
            if (packet.SpawnType < 0)
                return false;

            return true;
        }

        // Explicit switches replace Enum.IsDefined on the per-packet path: the reflection
        // overload boxes the value and is measurably slower on Mono. Add new enum members
        // here as well as in their enum declaration.
        private static bool IsDefined(DeathEventType value) => value switch
        {
            DeathEventType.SoloDeathOccurred => true,
            DeathEventType.SpectatorCountdownSync => true,
            DeathEventType.PartnerReviveTrigger => true,
            DeathEventType.TeamWipeOccurred => true,
            DeathEventType.BenchCheckpointUpdate => true,
            DeathEventType.SoloBenchRespawn => true,
            _ => false
        };

        private static bool IsDefined(ItemCategory value) => value switch
        {
            ItemCategory.Crest => true,
            ItemCategory.Tool => true,
            ItemCategory.Ability => true,
            ItemCategory.KeyItem => true,
            _ => false
        };

        private static bool IsDefined(ActionType value) => value switch
        {
            ActionType.SlashNormal => true,
            ActionType.SlashUp => true,
            ActionType.SlashDown => true,
            ActionType.HarpoonDash => true,
            ActionType.NeedleThrow => true,
            ActionType.ThreadBindStart => true,
            ActionType.ThreadBindComplete => true,
            ActionType.ToolDeploy => true,
            ActionType.Jump => true,
            ActionType.AirDash => true,
            ActionType.WallJump => true,
            _ => false
        };

        private static bool IsDefined(EnemySyncEventType value) => value switch
        {
            EnemySyncEventType.Hit => true,
            EnemySyncEventType.Death => true,
            _ => false
        };

        private static bool IsDefined(WorldEventType value) => value switch
        {
            WorldEventType.BreakableBroken => true,
            WorldEventType.GateOpened => true,
            _ => false
        };

        private static bool IsDefined(PersistentValueType value) => value switch
        {
            PersistentValueType.Bool => true,
            PersistentValueType.Int => true,
            _ => false
        };
}
}
