namespace SilksongCoop.Core.Networking
{
    /// <summary>
    /// Channel assignments shared by every transport.
    /// </summary>
    /// <remarks>
    /// The values are part of the runtime contract between both peers, not of the packet
    /// layout: Steam polls only a fixed range of channels, and the RTT probes must not
    /// share a channel with game traffic (on Steam channel 1 is read as transport control
    /// and is never dispatched to the game). Keep this list in sync with
    /// <c>SteamP2PTransport</c> and <c>SilksongCoopPlugin</c>.
    /// </remarks>
    public static class NetworkChannels
    {
        /// <summary>Unreliable transform/animation stream.</summary>
        public const byte Transform = 0;

        /// <summary>Action triggers (reliable).</summary>
        public const byte Action = 1;

        /// <summary>Vitals (health/silk/upgrades).</summary>
        public const byte Vitals = 2;

        /// <summary>Currency and item unlocks.</summary>
        public const byte Currency = 3;

        /// <summary>Death/revive lifecycle.</summary>
        public const byte Death = 4;

        /// <summary>Enemy HP, world events, persistent state and scene control.</summary>
        public const byte World = 5;

        /// <summary>Transport-level control (Steam RTT probes). Never carries game data.</summary>
        public const byte Control = 6;

        /// <summary>Lowest channel a transport has to poll.</summary>
        public const byte Min = Transform;

        /// <summary>Highest channel a transport has to poll.</summary>
        public const byte Max = Control;
    }
}
