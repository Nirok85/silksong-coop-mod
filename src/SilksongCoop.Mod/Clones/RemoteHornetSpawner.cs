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

                // Add mesh filter and renderer
                cloneGo.AddComponent<MeshFilter>();
                var meshRenderer = cloneGo.AddComponent<MeshRenderer>();
                if (hero != null)
                {
                    var heroRenderer = hero.GetComponent<Renderer>() ?? hero.GetComponentInChildren<Renderer>();
                    if (heroRenderer != null)
                    {
                        meshRenderer.sortingLayerID = heroRenderer.sortingLayerID;
                        meshRenderer.sortingLayerName = heroRenderer.sortingLayerName;
                        meshRenderer.sortingOrder = heroRenderer.sortingOrder;
                    }
                }

                // Setup tk2dSprite
                tk2dSprite sprite = cloneGo.AddComponent<tk2dSprite>();

                // Setup tk2dSpriteAnimator
                tk2dSpriteAnimator animator = cloneGo.AddComponent<tk2dSpriteAnimator>();

                // Borrow Hornet sprite collection and animation library from local HeroController if available
                if (hero != null && hero.AnimCtrl != null && hero.AnimCtrl.animator != null)
                {
                    var heroAnimator = hero.AnimCtrl.animator;
                    if (heroAnimator.Library != null)
                    {
                        animator.Library = heroAnimator.Library;
                    }

                    if (heroAnimator.Sprite != null && heroAnimator.Sprite.Collection != null)
                    {
                        sprite.SetSprite(heroAnimator.Sprite.Collection, heroAnimator.Sprite.spriteId);
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

        public bool IsPlayerSpawned(NetworkPeerId peerId)
        {
            return _activeClones.ContainsKey(peerId);
        }

        public void UpdateInterpolation(double currentLocalTimeMs)
        {
            // Unity components update automatically via Update() pass
        }

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
    }
}
