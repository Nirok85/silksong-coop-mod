namespace SilksongCoop.Core.State
{
    /// <summary>
    /// Decides whether an enemy needs to be included in a host snapshot. Keeping the rule
    /// here makes the snapshot contract testable without Unity: periodic reconciliation
    /// resends everything, while change-driven snapshots only send first-seen, damaged,
    /// healed/changed or newly-dead enemies.
    /// </summary>
    public static class EnemySnapshotSendPolicy
    {
        /// <summary>
        /// Returns true when the enemy should be emitted in this snapshot.
        /// </summary>
        /// <param name="reconcile">Full reconciliation pass (resend every tracked enemy).</param>
        /// <param name="known">Whether a previous snapshot already recorded this enemy.</param>
        /// <param name="lastHp">HP recorded by the previous snapshot (ignored when unknown).</param>
        /// <param name="hp">Current local HP.</param>
        /// <param name="initHp">Enemy's initial/max HP, or a value below <paramref name="hp"/> when unknown.</param>
        /// <param name="dead">Whether the enemy is currently dead.</param>
        /// <param name="wasDead">Whether the previous snapshot recorded the enemy as dead.</param>
        public static bool ShouldSend(
            bool reconcile,
            bool known,
            int lastHp,
            int hp,
            int initHp,
            bool dead,
            bool wasDead)
        {
            if (reconcile) return true;

            if (dead)
                return !wasDead;

            bool firstSeen = !known;
            bool damaged = hp < initHp || firstSeen;
            bool changed = known && lastHp != hp;
            return damaged || changed;
        }
    }
}
