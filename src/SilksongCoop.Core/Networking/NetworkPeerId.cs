using System;

namespace SilksongCoop.Core.Networking
{
    public readonly struct NetworkPeerId : IEquatable<NetworkPeerId>
    {
        public ulong Value { get; }
        public string DisplayName { get; }

        public NetworkPeerId(ulong value, string displayName = "")
        {
            Value = value;
            DisplayName = string.IsNullOrEmpty(displayName) ? value.ToString() : displayName;
        }

        public bool Equals(NetworkPeerId other) => Value == other.Value;
        public override bool Equals(object? obj) => obj is NetworkPeerId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => string.IsNullOrEmpty(DisplayName) ? Value.ToString() : $"{DisplayName} ({Value})";

        public static bool operator ==(NetworkPeerId left, NetworkPeerId right) => left.Equals(right);
        public static bool operator !=(NetworkPeerId left, NetworkPeerId right) => !left.Equals(right);
    }
}
