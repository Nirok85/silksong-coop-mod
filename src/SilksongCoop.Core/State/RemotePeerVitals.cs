using SilksongCoop.Core.Networking;

namespace SilksongCoop.Core.State
{
    /// <summary>
    /// Vitals snapshot for one remote peer. The legacy single-partner properties on
    /// <see cref="VitalsSyncService"/> mirror the first peer (primary) for compatibility.
    /// </summary>
    public readonly struct RemotePeerVitals
    {
        public NetworkPeerId Peer { get; }
        public int Health { get; }
        public int Silk { get; }
        public int MaxHealth { get; }
        public int MaxSilkSpools { get; }
        public int NeedleUpgradeLevel { get; }

        public RemotePeerVitals(
            NetworkPeerId peer,
            int health,
            int silk,
            int maxHealth,
            int maxSilkSpools,
            int needleUpgradeLevel)
        {
            Peer = peer;
            Health = health;
            Silk = silk;
            MaxHealth = maxHealth;
            MaxSilkSpools = maxSilkSpools;
            NeedleUpgradeLevel = needleUpgradeLevel;
        }
    }
}
