namespace SilksongCoop.Core.Protocol
{
    /// <summary>
    /// Identifies the type of network packet in the co-op protocol.
    /// </summary>
    public enum PacketType : byte
    {
        // System & Handshake (0x01 - 0x0F)
        HandshakeRequest    = 0x01,
        HandshakeResponse   = 0x02,
        Ping                = 0x03,
        Pong                = 0x04,
        Disconnect          = 0x05,
        Batch               = 0x06,

        // Player Replication (0x10 - 0x1F)
        TransformSync       = 0x10,
        ActionTrigger       = 0x11,
        AnimationSync       = 0x12,

        // Vitals & Progression (0x20 - 0x2F)
        VitalsSync          = 0x20,
        MaxUpgradeSync      = 0x21,
        DamageNotify        = 0x22,

        // Economy & Inventory (0x30 - 0x3F)
        CurrencySync        = 0x30,
        ItemUnlock          = 0x31,
        ItemUnlockSync      = ItemUnlock,
        LoadoutSync         = 0x32,

        // Death & Respawn Lifecycle (0x40 - 0x4F)
        PlayerDeath         = 0x40,
        SpectatorTimerSync  = 0x41,
        PartnerRevive       = 0x42,
        TeamWipe            = 0x43,
        BenchCheckpointSync = 0x44,

        // World & Scene Synchronization (0x50 - 0x5F)
        SceneTransition     = 0x50,
        DoorInteract        = 0x51,
        EnemySync           = 0x52,
        EnemySnapshot       = 0x53,
        WorldEvent          = 0x54,
        PersistentState     = 0x55
    }
}
