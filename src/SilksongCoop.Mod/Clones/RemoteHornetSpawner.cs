using System;
using System.Collections.Generic;
using SilksongCoop.Core.Clones;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Mod.Visuals;
using UnityEngine;

namespace SilksongCoop.Mod.Clones
{
    /// <summary>
    /// Spawns and manages standalone RemoteHornet GameObject hierarchies.
    /// Uses MeshRenderer, MeshFilter, tk2dSprite, and tk2dSpriteAnimator linked to
    /// HeroController.instance.AnimCtrl.animator.Library, completely bypassing
    /// the HeroController singleton Awake destruction guard.
    /// </summary>
    public class RemoteHornetSpawner : MonoBehaviour, IPlayerCloneManager
    {
        #region Singleton and Fields

        public static RemoteHornetSpawner? Instance { get; private set; }

        private readonly Dictionary<NetworkPeerId, RemotePlayerController> _activeClones =
            new Dictionary<NetworkPeerId, RemotePlayerController>();

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
                DontDestroyOnLoad(gameObject);
            }
            else if (Instance != this)
            {
                Destroy(this);
            }
        }

        #endregion

        #region Clone Lifecycle

        /// <summary>
        /// Spawns a remote player clone for the specified peer.
        /// </summary>
        /// <param name="peerId">The peer identifier.</param>
        /// <param name="isPlayer2">True if this is player 2, false for player 1.</param>
        public void SpawnRemotePlayer(NetworkPeerId peerId, bool isPlayer2)
        {
            try
            {
                // Edge Case E01: If clone already exists for this peer, clean up prior instance
                if (_activeClones.TryGetValue(peerId, out var existing))
                {
                    if (existing != null && existing.gameObject != null)
                    {
                        Destroy(existing.gameObject);
                    }
                    _activeClones.Remove(peerId);
                }

                // Create clean GameObject hierarchy with matching layer and position from local hero
                var hero = HeroController.instance;
                var cloneGo = new GameObject($"RemoteHornet_{peerId.Value}");
                if (hero != null)
                {
                    cloneGo.layer = hero.gameObject.layer;
                    cloneGo.transform.position = hero.transform.position;
                }
                else
                {
                    cloneGo.layer = 0;
                }
                DontDestroyOnLoad(cloneGo);

                // Get sprite collection and ID from hero BEFORE creating tk2dSprite
                tk2dSpriteCollectionData? collection = null;
                int spriteId = 0;
                if (hero != null && hero.AnimCtrl != null && hero.AnimCtrl.animator != null)
                {
                    if (hero.AnimCtrl.animator.Sprite != null && hero.AnimCtrl.animator.Sprite.Collection != null)
                    {
                        collection = hero.AnimCtrl.animator.Sprite.Collection;
                        spriteId = hero.AnimCtrl.animator.Sprite.spriteId;
                    }
                }

                // Create tk2dSprite with proper renderer using static AddComponent method
                // This creates MeshFilter + MeshRenderer automatically
                tk2dSprite sprite;
                if (collection != null)
                {
                    sprite = tk2dSprite.AddComponent(cloneGo, collection, spriteId);
                    SilksongCoopPlugin.LogInfo($"[RemoteHornetSpawner] Created tk2dSprite with collection '{collection.name}', spriteId={spriteId}");
                }
                else
                {
                    // Fallback: create without collection (will be set later via EnsureAnimationLibraryBound)
                    sprite = cloneGo.AddComponent<tk2dSprite>();
                    SilksongCoopPlugin.LogWarn($"[RemoteHornetSpawner] No sprite collection available, created bare tk2dSprite");
                }

                // Setup tk2dSpriteAnimator
                tk2dSpriteAnimator animator = cloneGo.AddComponent<tk2dSpriteAnimator>();

                // Borrow animation library from local HeroController if available
                if (hero != null && hero.AnimCtrl != null && hero.AnimCtrl.animator != null)
                {
                    var heroAnimator = hero.AnimCtrl.animator;
                    if (heroAnimator.Library != null)
                    {
                        animator.Library = heroAnimator.Library;
                    }
                }

                // Set sorting layer to match hero
                var spriteRenderer = sprite.GetComponent<Renderer>();
                if (spriteRenderer != null && hero != null)
                {
                    var heroRenderer = hero.GetComponent<Renderer>() ?? hero.GetComponentInChildren<Renderer>();
                    if (heroRenderer != null)
                    {
                        spriteRenderer.sortingLayerID = heroRenderer.sortingLayerID;
                        spriteRenderer.sortingLayerName = heroRenderer.sortingLayerName;
                        spriteRenderer.sortingOrder = heroRenderer.sortingOrder;
                    }
                }

                // Add visual controllers
                var paletteSwap = cloneGo.AddComponent<PaletteSwapController>();
                paletteSwap.SetPlayer2(isPlayer2);
                paletteSwap.ApplyPaletteSwap();

                var dashTrail = cloneGo.AddComponent<DashTrailController>();
                dashTrail.Initialize(isPlayer2);

                // Add remote player controller to drive interpolated motion and animation
                var controller = cloneGo.AddComponent<RemotePlayerController>();
                controller.Initialize(peerId, isPlayer2);

                if (animator.Library != null)
                {
                    controller.SetAnimationLibrary(animator.Library);
                }

                _activeClones[peerId] = controller;
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[RemoteHornetSpawner] SpawnRemotePlayer error for peer {peerId}: {ex.Message}");
            }
        }

        /// <summary>
        /// Despawns and destroys the remote player clone for the specified peer.
        /// </summary>
        /// <param name="peerId">The peer identifier.</param>
        public void DespawnRemotePlayer(NetworkPeerId peerId)
        {
            try
            {
                // Edge Case E02: Despawning non-existent peer is a safe no-op
                if (_activeClones.TryGetValue(peerId, out var controller))
                {
                    if (controller != null && controller.gameObject != null)
                    {
                        Destroy(controller.gameObject);
                    }
                    _activeClones.Remove(peerId);
                }
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[RemoteHornetSpawner] DespawnRemotePlayer error: {ex.Message}");
            }
        }

        /// <summary>
        /// BUG-B4: reports whether the remote peer's clone is currently present in the local
        /// scene. Used by revive logic to refuse cross-scene teleports.
        /// </summary>
        public bool IsRemoteInSameScene(NetworkPeerId peerId)
        {
            try
            {
                return _activeClones.TryGetValue(peerId, out var controller) &&
                       controller != null &&
                       controller.IsInSameScene;
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[RemoteHornetSpawner] IsRemoteInSameScene error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// BUG-20: marks whether the remote peer is present in the local scene. Called on an
        /// explicit SceneTransitionPacket so the clone is hidden the instant the partner
        /// leaves the scene, instead of lingering until the next transform tick.
        /// </summary>
        /// <param name="peerId">The peer identifier.</param>
        /// <param name="present">True if the partner is present in this scene.</param>
        public void SetRemoteScenePresence(NetworkPeerId peerId, bool present)
        {
            try
            {
                if (_activeClones.TryGetValue(peerId, out var controller) && controller != null)
                {
                    controller.SetScenePresence(present);
                }
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[RemoteHornetSpawner] SetRemoteScenePresence error: {ex.Message}");
            }
        }

        #endregion

        #region Remote Control

        /// <summary>
        /// Updates the remote player's transform with interpolation and animation sync.
        /// </summary>
        /// <param name="peerId">The peer identifier.</param>
        /// <param name="posX">X position.</param>
        /// <param name="posY">Y position.</param>
        /// <param name="velX">X velocity.</param>
        /// <param name="velY">Y velocity.</param>
        /// <param name="facingDirection">Facing direction (0 = left, 1 = right).</param>
        /// <param name="stateFlags">State flags (grounded, dashing, etc.).</param>
        /// <param name="animClipHash">Animation clip hash.</param>
        /// <param name="normalizedTime">Normalized animation time.</param>
        /// <param name="timestampMs">Packet timestamp in milliseconds.</param>
        public void UpdateRemoteTransform(
            NetworkPeerId peerId,
            float posX, float posY,
            float velX, float velY,
            byte facingDirection,
            ushort stateFlags,
            int animClipHash,
            ushort normalizedTime,
            uint timestampMs)
        {
            UpdateRemoteTransform(peerId, posX, posY, velX, velY, facingDirection, stateFlags, animClipHash, normalizedTime, timestampMs, true);
        }

        /// <summary>
        /// Updates the remote player's transform with interpolation and animation sync.
        /// </summary>
        /// <param name="peerId">The peer identifier.</param>
        /// <param name="posX">X position.</param>
        /// <param name="posY">Y position.</param>
        /// <param name="velX">X velocity.</param>
        /// <param name="velY">Y velocity.</param>
        /// <param name="facingDirection">Facing direction (0 = left, 1 = right).</param>
        /// <param name="stateFlags">State flags (grounded, dashing, etc.).</param>
        /// <param name="animClipHash">Animation clip hash.</param>
        /// <param name="normalizedTime">Normalized animation time.</param>
        /// <param name="timestampMs">Packet timestamp in milliseconds.</param>
        /// <param name="isSameScene">True if the remote player is in the same scene.</param>
        public void UpdateRemoteTransform(
            NetworkPeerId peerId,
            float posX, float posY,
            float velX, float velY,
            byte facingDirection,
            ushort stateFlags,
            int animClipHash,
            ushort normalizedTime,
            uint timestampMs,
            bool isSameScene)
        {
            try
            {
                if (_activeClones.TryGetValue(peerId, out var controller) && controller != null)
                {
                    controller.UpdateTransform(
                        posX, posY,
                        velX, velY,
                        facingDirection,
                        stateFlags,
                        animClipHash,
                        normalizedTime,
                        timestampMs,
                        isSameScene);
                }
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[RemoteHornetSpawner] UpdateRemoteTransform error: {ex.Message}");
            }
        }

        /// <summary>
        /// Triggers a remote action (dash, attack, etc.) on the remote player clone.
        /// </summary>
        /// <param name="peerId">The peer identifier.</param>
        /// <param name="action">The action type to trigger.</param>
        /// <param name="variant">Action variant or parameter.</param>
        public void TriggerRemoteAction(NetworkPeerId peerId, ActionType action, byte variant)
        {
            try
            {
                if (_activeClones.TryGetValue(peerId, out var controller) && controller != null)
                {
                    controller.TriggerAction(action, variant);
                }
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[RemoteHornetSpawner] TriggerRemoteAction error: {ex.Message}");
            }
        }

        /// <summary>
        /// Synchronizes animation state for the remote player clone.
        /// </summary>
        /// <param name="peerId">The peer identifier.</param>
        /// <param name="clipHash">Animation clip hash.</param>
        /// <param name="normalizedTime">Normalized animation time.</param>
        public void SyncAnimation(NetworkPeerId peerId, int clipHash, ushort normalizedTime)
        {
            try
            {
                if (_activeClones.TryGetValue(peerId, out var controller) && controller != null)
                {
                    controller.SyncAnimation(clipHash, normalizedTime);
                }
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[RemoteHornetSpawner] SyncAnimation error: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets the facing direction for the remote player clone.
        /// </summary>
        /// <param name="peerId">The peer identifier.</param>
        /// <param name="facingRight">True if facing right, false if facing left.</param>
        public void SetFacingDirection(NetworkPeerId peerId, bool facingRight)
        {
            try
            {
                if (_activeClones.TryGetValue(peerId, out var controller) && controller != null)
                {
                    controller.SetFacingDirection(facingRight);
                }
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[RemoteHornetSpawner] SetFacingDirection error: {ex.Message}");
            }
        }

        /// <summary>
        /// Triggers the dash visual effect on the remote player clone.
        /// </summary>
        /// <param name="peerId">The peer identifier.</param>
        /// <param name="isAirDash">True if this is an air dash, false for ground dash.</param>
        public void TriggerDashEffect(NetworkPeerId peerId, bool isAirDash)
        {
            try
            {
                if (_activeClones.TryGetValue(peerId, out var controller) && controller != null)
                {
                    controller.TriggerDashEffect(isAirDash);
                }
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[RemoteHornetSpawner] TriggerDashEffect error: {ex.Message}");
            }
        }

        #endregion

        #region Query and Cleanup

        /// <summary>
        /// Attempts to retrieve the current position of a remote player clone.
        /// </summary>
        /// <param name="peerId">The peer identifier.</param>
        /// <param name="posX">Output X position.</param>
        /// <param name="posY">Output Y position.</param>
        /// <returns>True if the position was retrieved, false otherwise.</returns>
        public bool TryGetRemotePosition(NetworkPeerId peerId, out float posX, out float posY)
        {
            posX = 0f;
            posY = 0f;
            
            try
            {
                if (_activeClones.TryGetValue(peerId, out var controller) && controller != null && controller.gameObject != null)
                {
                    var pos = controller.transform.position;
                    posX = pos.x;
                    posY = pos.y;
                    return true;
                }
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[RemoteHornetSpawner] TryGetRemotePosition error: {ex.Message}");
            }
            
            return false;
        }

        /// <summary>
        /// Attempts to retrieve the full state of a remote player clone.
        /// </summary>
        /// <param name="peerId">The peer identifier.</param>
        /// <param name="state">Output remote clone state.</param>
        /// <returns>True if the state was retrieved, false otherwise.</returns>
        public bool TryGetRemoteState(NetworkPeerId peerId, out RemoteCloneState state)
        {
            state = default;
            
            try
            {
                if (_activeClones.TryGetValue(peerId, out var controller) && controller != null && controller.gameObject != null)
                {
                    state = controller.GetState();
                    return true;
                }
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"[RemoteHornetSpawner] TryGetRemoteState error: {ex.Message}");
            }
            
            return false;
        }

        /// <summary>
        /// Checks if a remote player clone exists for the specified peer.
        /// </summary>
        /// <param name="peerId">The peer identifier.</param>
        /// <returns>True if a clone exists, false otherwise.</returns>
        public bool IsPlayerSpawned(NetworkPeerId peerId)
        {
            return _activeClones.ContainsKey(peerId);
        }

        /// <summary>
        /// Updates interpolation for all remote clones (no-op for Unity components).
        /// </summary>
        /// <param name="currentLocalTimeMs">Current local time in milliseconds.</param>
        public void UpdateInterpolation(double currentLocalTimeMs)
        {
            // Unity components update automatically via Update() pass
        }

        /// <summary>
        /// Despawns and destroys all remote player clones.
        /// </summary>
        public void ResetAllClones()
        {
            foreach (var kvp in _activeClones)
            {
                if (kvp.Value != null && kvp.Value.gameObject != null)
                {
                    Destroy(kvp.Value.gameObject);
                }
            }
            _activeClones.Clear();
        }

        private void OnDestroy()
        {
            ResetAllClones();
            if (Instance == this)
            {
                Instance = null;
            }
        }

        #endregion
    }
}
