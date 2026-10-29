using System;
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
        private readonly object _lock = new object();

        public float DefaultRadius { get; set; } = 40f;
        public float Hysteresis { get; set; } = 4f;

        public struct TrackedEntity
        {
            public ulong Id;
            public Vector2 Position;
            public float Radius;
            public bool IsActive;
            public DateTime LastUpdate;
        }

        public void UpdateEntity(ulong id, Vector2 position, float radius = 0f, bool active = true)
        {
            lock (_lock)
            {
                _entities[id] = new TrackedEntity
                {
                    Id = id,
                    Position = position,
                    Radius = radius > 0 ? radius : DefaultRadius,
                    IsActive = active,
                    LastUpdate = DateTime.UtcNow
                };
            }
        }

        public void RemoveEntity(ulong id)
        {
            lock (_lock)
            {
                _entities.Remove(id);
            }
        }

        public bool IsInInterest(ulong observerId, Vector2 observerPos, ulong targetId, float? customRadius = null)
        {
            lock (_lock)
            {
                if (!_entities.TryGetValue(targetId, out var entity) || !entity.IsActive)
                    return false;

                float radius = customRadius ?? entity.Radius;
                float distSq = Vector2.DistanceSquared(observerPos, entity.Position);
                float enterRange = radius * radius;
                float leaveRange = (radius + Hysteresis) * (radius + Hysteresis);
                
                // Use hysteresis to avoid flickering
                return distSq <= enterRange || (distSq <= leaveRange && entity.Id == observerId);
            }
        }

        public List<ulong> GetEntitiesInRange(Vector2 center, float radius)
        {
            var result = new List<ulong>();
            lock (_lock)
            {
                float rangeSq = radius * radius;
                foreach (var kv in _entities)
                {
                    var entity = kv.Value;
                    if (!entity.IsActive) continue;
                    if (Vector2.DistanceSquared(center, entity.Position) <= rangeSq)
                        result.Add(entity.Id);
                }
            }
            return result;
        }

        public void Clear()
        {
            lock (_lock)
            {
                _entities.Clear();
            }
        }
    }
}
