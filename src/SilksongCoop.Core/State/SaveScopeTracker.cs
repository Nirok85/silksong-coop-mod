using System;

namespace SilksongCoop.Core.State
{
    /// <summary>
    /// Detects when the local game moves to a different save. Persistent replay registries
    /// (world events, persistent items, dead bosses) may survive a reconnect within one
    /// playthrough, but they must never leak into another save or a newly loaded game.
    /// </summary>
    /// <remarks>
    /// The game exposes an integer profile id and a <c>PlayerData</c> singleton. The profile
    /// id catches a switch between save slots; the object identity catches a reload or a new
    /// game in the same slot, where the profile id alone does not change. A profile id of
    /// zero means "no save loaded" (menu) and is ignored so returning to the menu does not
    /// discard a still-valid replay cache.
    /// </remarks>
    public sealed class SaveScopeTracker
    {
        private bool _initialized;
        private int _profileId;
        private object? _playerData;

        /// <summary>
        /// Observes the current save identity.
        /// </summary>
        /// <param name="profileId">Game profile/save-slot id; values &lt;= 0 mean no save.</param>
        /// <param name="playerData">Current <c>PlayerData</c> singleton, used as a load generation.</param>
        /// <returns>
        /// True when a previously known save was replaced. The first observation only seeds
        /// the scope and returns false.
        /// </returns>
        public bool Observe(int profileId, object? playerData)
        {
            if (profileId <= 0)
                return false;

            bool changed = _initialized &&
                           (_profileId != profileId ||
                            (playerData != null && !ReferenceEquals(_playerData, playerData)));

            _initialized = true;
            _profileId = profileId;
            if (playerData != null)
                _playerData = playerData;

            return changed;
        }

        /// <summary>Forgets the tracked save, for example when the plugin is disposed.</summary>
        public void Reset()
        {
            _initialized = false;
            _profileId = 0;
            _playerData = null;
        }
    }
}
