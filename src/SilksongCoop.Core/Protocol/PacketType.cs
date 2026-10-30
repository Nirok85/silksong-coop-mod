namespace SilksongCoop.Core.Protocol
{
    /// <summary>
    /// Identifies the type of network packet in the co-op protocol.
    /// </summary>
    public enum PacketType : byte
    {
        // Player Replication (0x10 - 0x1F)
        TransformSync       = 0x10,
        ActionTrigger       = 0x11,

        // Vitals & Progression (0x20 - 0x2F)
        VitalsSync          = 0x20,

        // Economy & Inventory (0x30 - 0x3F)
        CurrencySync        = 0x30,
        ItemUnlock          = 0x31,
        ItemUnlockSync      = ItemUnlock,

        // Death & Respawn Lifecycle (0x40 - 0x4F)
        PlayerDeath         = 0x40,
        BenchCheckpointSync = 0x44,

        // World & Scene Synchronization (0x50 - 0x5F)
        SceneTransition     = 0x50,
        EnemySync           = 0x52,
        EnemySnapshot       = 0x53,
        WorldEvent          = 0x54,
        PersistentState     = 0x55
    }
}
