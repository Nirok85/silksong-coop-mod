using System;

namespace SilksongCoop.Core.Interpolation
{
    public struct TransformSnapshot : IEquatable<TransformSnapshot>
    {
        public uint Tick;
        public double TimestampMs;
        public float PositionX;
        public float PositionY;
        public float VelocityX;
        public float VelocityY;
        public bool FacingRight;
        public ushort Flags;
        public int AnimHash;

        public bool Equals(TransformSnapshot other) =>
            Tick == other.Tick &&
            Math.Abs(TimestampMs - other.TimestampMs) < 0.001 &&
            Math.Abs(PositionX - other.PositionX) < 0.0001f &&
            Math.Abs(PositionY - other.PositionY) < 0.0001f;

        public override bool Equals(object? obj) => obj is TransformSnapshot other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Tick, TimestampMs, PositionX, PositionY);
    }
}
