using System;

namespace SilksongCoop.Core.Networking
{
    /// <summary>
    /// Unique identifier for a network peer, combining a numeric value and optional display name.
    /// </summary>
    public readonly struct NetworkPeerId : IEquatable<NetworkPeerId>
    {
        public ulong Value { get; }
        public string DisplayName { get; }

        /// <summary>
        /// Initializes a new peer identifier with the specified value and optional display name.
        /// </summary>
        /// <param name="value">Numeric peer identifier.</param>
        /// <param name="displayName">Optional human-readable name for the peer.</param>
        public NetworkPeerId(ulong value, string displayName = "")
        {
            Value = value;
            DisplayName = string.IsNullOrEmpty(displayName) ? value.ToString() : displayName;
        }

        /// <summary>
        /// Determines whether this peer identifier equals another peer identifier.
        /// </summary>
        /// <param name="other">The other peer identifier to compare.</param>
        /// <returns>True if both identifiers have the same value, false otherwise.</returns>
        public bool Equals(NetworkPeerId other) => Value == other.Value;
        public override bool Equals(object? obj) => obj is NetworkPeerId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => string.IsNullOrEmpty(DisplayName) ? Value.ToString() : $"{DisplayName} ({Value})";

        /// <summary>
        /// Determines whether two peer identifiers are equal.
        /// </summary>
        public static bool operator ==(NetworkPeerId left, NetworkPeerId right) => left.Equals(right);
        
        /// <summary>
        /// Determines whether two peer identifiers are not equal.
        /// </summary>
        public static bool operator !=(NetworkPeerId left, NetworkPeerId right) => !left.Equals(right);
    }
}
