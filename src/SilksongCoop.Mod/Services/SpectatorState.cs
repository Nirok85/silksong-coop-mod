using System.Collections.Generic;
using UnityEngine;

namespace SilksongCoop.Mod.Services
{
    /// <summary>
    /// Holds spectator-mode state (disabled colliders/renderers and the active flag) so it
    /// can be passed around without reaching into the plugin god object.
    /// </summary>
    public class SpectatorState
    {
        public bool IsActive { get; set; }
        public readonly List<Collider2D> DisabledColliders = new List<Collider2D>();
        public readonly List<Renderer> DisabledRenderers = new List<Renderer>();

        public void Clear()
        {
            DisabledColliders.Clear();
            DisabledRenderers.Clear();
            IsActive = false;
        }
    }
}
