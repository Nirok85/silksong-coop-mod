using System.Collections.Generic;
using System.Numerics;

namespace SilksongCoop.Core.Networking
{
    /// <summary>
    /// Area of Interest manager for filtering network updates by distance.
    /// </summary>
    public class InterestManager
    {
        private readonly Dictionary<ulong, TrackedEntity> _entities = new Dictionary<ulong, TrackedEntity>();
        private readonly Dictionary<(ulong Observer, ulong Target), bool> _visibility =
            new Dictionary<(ulong Observer, ulong Target), bool>();
        private readonly object _lock = new object();

        public float DefaultRadius { get; set; } = 40f;
        public float Hysteresis { get; set; } = 4f;

        public struct TrackedEntity
        {
            public Vector2 Position;
            public float Radius;
            public bool IsActive;
        }

        public void UpdateEntity(ulong id, Vector2 position, float radius = 0f, bool active = true)
        {
            lock (_lock)
            {
                _entities[id] = new TrackedEntity
                {
                    Position = position,
                    Radius = radius > 0 ? radius : DefaultRadius,
                    IsActive = active
                };
            }
        }

        /// <summary>
        /// Returns whether <paramref name="targetId"/> is within interest range of the given
        /// observer. Hysteresis is tracked per observer/target pair: an object that is already
        /// visible stays visible until it leaves the enlarged radius, which prevents the
        /// "visible / hidden / visible" flicker right on the boundary.
        /// </summary>
        public bool IsInInterest(ulong observerId, Vector2 observerPos, ulong targetId, float? customRadius = null)
        {
            lock (_lock)
            {
                if (!_entities.TryGetValue(targetId, out var entity) || !entity.IsActive)
                {
                    _visibility.Remove((observerId, targetId));
                    return false;
                }

                float radius = customRadius ?? entity.Radius;
                float distSq = Vector2.DistanceSquared(observerPos, entity.Position);

                bool wasVisible = _visibility.TryGetValue((observerId, targetId), out bool seen) && seen;
                float limit = wasVisible ? radius + Hysteresis : radius;
                bool visible = distSq <= limit * limit;

                _visibility[(observerId, targetId)] = visible;
                return visible;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _entities.Clear();
                _visibility.Clear();
            }
        }
    }
}
