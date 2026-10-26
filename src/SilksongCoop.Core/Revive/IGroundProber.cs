namespace SilksongCoop.Core.Revive
{
    /// <summary>
    /// Contract for probing solid, hazard-free ground for reviving fallen partners.
    /// </summary>
    public interface IGroundProber
    {
        /// <summary>
        /// Probes candidate ground points around the partner position and returns safe ground coordinates if found.
        /// </summary>
        /// <param name="partnerX">Partner's current X position.</param>
        /// <param name="partnerY">Partner's current Y position.</param>
        /// <param name="safeX">Resolved safe ground X coordinate.</param>
        /// <param name="safeY">Resolved safe ground Y coordinate.</param>
        /// <returns>True if safe ground was found; otherwise false.</returns>
        bool TryFindSafeGround(float partnerX, float partnerY, out float safeX, out float safeY);

        /// <summary>
        /// Calculates the reviving player's starting health based on their maximum health (50% max HP, clamped >= 1).
        /// </summary>
        int CalculateReviveHealth(int maxHealth);
    }
}
