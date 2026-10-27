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
            // Validate HP is reasonable (byte range 0-255, but game max is ~9 masks)
            if (packet.CurrentHealth > 255 || packet.MaxHealth < 1 || packet.MaxHealth > 255)
            {
                return false;
            }

            // Validate silk is reasonable (byte range 0-255)
            if (packet.CurrentSilk > 255 || packet.MaxSilkSpools > 255)
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

            // Validate total wallet amount is non-negative
            if (packet.TotalWalletAmount < 0)
            {
                return false;
            }

            return true;
        }
    }
}
