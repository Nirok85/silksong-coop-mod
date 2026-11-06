using System;
using System.Collections.Generic;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;

namespace SilksongCoop.Core.State
{
    /// <summary>
    /// Coordinates the death lifecycle, spectator mode, and revive mechanics for co-op players.
    /// </summary>
    public class DeathLifecycleCoordinator : IDeathLifecycleCoordinator
    {
        #region Fields

        private static void Log(string msg) => CoopLog.Warn(msg);

        private readonly object _lock = new object();

        private DeathState _currentState = DeathState.Alive;
        private bool _isPartnerAlive = true;
        private float _remainingSpectatorTime;
        private (float X, float Y) _lastPartnerPosition = (0f, 0f);
        private uint _currentEpoch = 1;

        // The peer selected for the current revive attempt. Kept separate from
        // LastPartnerPosition so the plugin can verify that the scene it is about to
        // revive into belongs to the same player the coordinator picked.
        private NetworkPeerId? _reviveTargetPeer;
        private (float X, float Y) _reviveTargetPosition = (0f, 0f);

        // Local death position is used to pick the nearest living partner when a
        // session has more than one. Without it the selection depended on dictionary
        // order and could target a player on the other side of the room.
        private (float X, float Y) _localDeathPosition = (0f, 0f);

        private sealed class PartnerState
        {
            public bool Alive;
            public float X;
            public float Y;
        }

        private readonly Dictionary<NetworkPeerId, PartnerState> _partners =
            new Dictionary<NetworkPeerId, PartnerState>();

        // Peers explicitly registered by the plugin. Packets from a legacy 2-player peer
        // also create _partners entries lazily, so a non-empty _partners dictionary alone
        // cannot tell a real 3+ session from the legacy single-partner path.
        private readonly HashSet<NetworkPeerId> _explicitPeers =
            new HashSet<NetworkPeerId>();

        // Peers whose clone is currently in the local scene. Revive and spectator-camera
        // selection prefer these so a nearby player in another room is not chosen over a
        // reachable one. An empty set means "unknown" and preserves legacy behaviour.
        private readonly HashSet<NetworkPeerId> _sameScenePeers =
            new HashSet<NetworkPeerId>();

        // A solo bench fallback must not announce success before the reload finishes.
        // The plugin uses BeginSoloBenchRespawn; the legacy ForceBenchRespawn(false)
        // overload keeps its immediate packet for existing callers/tests.
        private bool _pendingSoloBenchRespawn;

        #endregion

        #region Properties

        public DeathState CurrentState
        {
            get { lock (_lock) { return _currentState; } }
            private set { lock (_lock) { _currentState = value; } }
        }

        public bool IsPartnerAlive
        {
            get
            {
                lock (_lock)
                {
                    if (_partners.Count > 0)
                    {
                        foreach (var partner in _partners.Values)
                        {
                            if (partner.Alive) return true;
                        }
                        return false;
                    }
                    return _isPartnerAlive;
                }
            }
            set
            {
                lock (_lock)
                {
                    _isPartnerAlive = value;
                    // Legacy callers/tests set the aggregate directly. Keep per-peer
                    // entries consistent with that explicit override.
                    foreach (var partner in _partners.Values)
                    {
                        partner.Alive = value;
                    }
                }
            }
        }

        public float RemainingSpectatorTime
        {
            get { lock (_lock) { return _remainingSpectatorTime; } }
            private set { lock (_lock) { _remainingSpectatorTime = value; } }
        }

        public float ReviveCooldownDuration { get; set; } = 20.0f; // Configurable between 15-30s

        /// <summary>
        /// Gets or sets the last known position of the remote partner for revive targeting.
        /// </summary>
        public (float X, float Y) LastPartnerPosition
        {
            get
            {
                lock (_lock)
                {
                    if (_partners.Count > 0)
                    {
                        // Prefer the nearest living partner in the local scene; then any
                        // living partner; then same-scene dead; then any dead. This keeps
                        // both the revive target and the spectator camera from jumping to
                        // a player in another room while a reachable one exists.
                        PartnerState? bestSameSceneAlive = null;
                        PartnerState? bestAnyAlive = null;
                        PartnerState? bestSameSceneDead = null;
                        PartnerState? bestAnyDead = null;
                        float sameSceneAliveDistance = float.MaxValue;
                        float anyAliveDistance = float.MaxValue;
                        float sameSceneDeadDistance = float.MaxValue;
                        float anyDeadDistance = float.MaxValue;

                        foreach (var pair in _partners)
                        {
                            var partner = pair.Value;
                            bool sameScene = _sameScenePeers.Contains(pair.Key);
                            float dx = partner.X - _localDeathPosition.X;
                            float dy = partner.Y - _localDeathPosition.Y;
                            float distanceSq = dx * dx + dy * dy;

                            if (partner.Alive)
                            {
                                if (distanceSq < anyAliveDistance)
                                {
                                    anyAliveDistance = distanceSq;
                                    bestAnyAlive = partner;
                                }
                                if (sameScene && distanceSq < sameSceneAliveDistance)
                                {
                                    sameSceneAliveDistance = distanceSq;
                                    bestSameSceneAlive = partner;
                                }
                            }
                            else
                            {
                                if (distanceSq < anyDeadDistance)
                                {
                                    anyDeadDistance = distanceSq;
                                    bestAnyDead = partner;
                                }
                                if (sameScene && distanceSq < sameSceneDeadDistance)
                                {
                                    sameSceneDeadDistance = distanceSq;
                                    bestSameSceneDead = partner;
                                }
                            }
                        }

                        var chosen = bestSameSceneAlive ?? bestAnyAlive ?? bestSameSceneDead ?? bestAnyDead;
                        if (chosen != null) return (chosen.X, chosen.Y);
                    }
                    return _lastPartnerPosition;
                }
            }
            set
            {
                lock (_lock)
                {
                    _lastPartnerPosition = value;
                    if (_partners.Count == 1)
                    {
                        foreach (var partner in _partners.Values)
                        {
                            partner.X = value.X;
                            partner.Y = value.Y;
                        }
                    }
                }
            }
        }

        public uint CurrentEpoch
        {
            get { lock (_lock) { return _currentEpoch; } }
            private set { lock (_lock) { _currentEpoch = value; } }
        }

        #endregion

        #region Events

        public event Action<float, float>? OnSoloDeathSuppressed;
        public event Action? OnTeamWipeTriggered;
        public event Action<float, float>? OnReviveAtPartnerRequested;
        public event Action<DeathLifecyclePacket>? OnOutgoingDeathPacket;

        #endregion

        #region Revive target

        /// <summary>
        /// Peer selected for the revive that is currently in progress, or null for a
        /// legacy single-partner session that never registered a peer.
        /// </summary>
        public NetworkPeerId? ReviveTargetPeer
        {
            get { lock (_lock) { return _reviveTargetPeer; } }
        }

        /// <summary>
        /// Position selected for the revive that is currently in progress.
        /// </summary>
        public (float X, float Y) ReviveTargetPosition
        {
            get { lock (_lock) { return _reviveTargetPosition; } }
        }

        /// <summary>
        /// Picks the nearest living peer (or the stored legacy partner position when no
        /// peers are registered). Returns false only when no living target exists.
        /// </summary>
        public bool TryGetReviveTarget(out NetworkPeerId? peer, out float x, out float y)
        {
            lock (_lock)
            {
                return TrySelectReviveTargetLocked(out peer, out x, out y);
            }
        }

        private bool TrySelectReviveTargetLocked(out NetworkPeerId? peer, out float x, out float y)
        {
            peer = null;
            x = 0f;
            y = 0f;

            if (_partners.Count == 0)
            {
                if (!_isPartnerAlive) return false;
                x = _lastPartnerPosition.X;
                y = _lastPartnerPosition.Y;
                return true;
            }

            NetworkPeerId? bestSameScenePeer = null;
            PartnerState? bestSameSceneState = null;
            float bestSameSceneDistanceSq = float.MaxValue;
            NetworkPeerId? bestAnyPeer = null;
            PartnerState? bestAnyState = null;
            float bestAnyDistanceSq = float.MaxValue;

            foreach (var pair in _partners)
            {
                var partner = pair.Value;
                if (!partner.Alive) continue;

                float dx = partner.X - _localDeathPosition.X;
                float dy = partner.Y - _localDeathPosition.Y;
                float distanceSq = dx * dx + dy * dy;

                if (distanceSq < bestAnyDistanceSq)
                {
                    bestAnyDistanceSq = distanceSq;
                    bestAnyPeer = pair.Key;
                    bestAnyState = partner;
                }

                if (_sameScenePeers.Contains(pair.Key) && distanceSq < bestSameSceneDistanceSq)
                {
                    bestSameSceneDistanceSq = distanceSq;
                    bestSameScenePeer = pair.Key;
                    bestSameSceneState = partner;
                }
            }

            // A same-scene target is reachable without a scene transition; only fall back
            // to a living peer elsewhere when no same-scene peer is available (the plugin
            // will then use the bench fallback).
            var bestPeer = bestSameScenePeer ?? bestAnyPeer;
            var bestState = bestSameSceneState ?? bestAnyState;
            if (bestState == null) return false;

            peer = bestPeer;
            x = bestState.X;
            y = bestState.Y;
            return true;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Handles local player death, determining whether to enter spectator mode or trigger team wipe.
        /// </summary>
        /// <param name="deathX">X coordinate where the player died.</param>
        /// <param name="deathY">Y coordinate where the player died.</param>
        /// <returns>True if death should be suppressed (spectator mode); false to allow bench reload.</returns>
        public bool OnLocalPlayerDied(float deathX, float deathY)
        {
            try
            {
                // BUG-05: the post-bench stale-packet guard must not be cleared before we
                // know the death is legitimate. A stale SoloDeathOccurred / TeamWipeOccurred
                // packet that arrives within the 5-second window must still be filterable
                // while the local player is Alive; clearing the guard on entry would let
                // later stale packets slip through for the rest of the window.
                if (CurrentState == DeathState.TeamWipe)
                {
                    return false;
                }

                lock (_lock)
                {
                    _localDeathPosition = (deathX, deathY);
                    _reviveTargetPeer = null;
                }

                if (IsPartnerAlive)
                {
                    // Solo death while partner is alive: suppress bench reload and enter spectator
                    CurrentState = DeathState.Spectating;
                    RemainingSpectatorTime = Math.Clamp(ReviveCooldownDuration, 15f, 30f);

                    OnSoloDeathSuppressed?.Invoke(deathX, deathY);

                    var packet = new DeathLifecyclePacket
                    {
                        Header = new PacketHeader(PacketType.PlayerDeath, 4, 0, NetworkClock.NowMs),
                        EventType = DeathEventType.SoloDeathOccurred,
                        CoordinateX = deathX,
                        CoordinateY = deathY,
                        RemainingSeconds = RemainingSpectatorTime,
                        Epoch = CurrentEpoch
                    };
                    OnOutgoingDeathPacket?.Invoke(packet);

                    return true; // Suppress bench reload
                }
                else
                {
                    // Dual wipe: both players are dead
                    CurrentState = DeathState.TeamWipe;
                    OnTeamWipeTriggered?.Invoke();

                    var packet = new DeathLifecyclePacket
                    {
                        Header = new PacketHeader(PacketType.PlayerDeath, 4, 0, NetworkClock.NowMs),
                        EventType = DeathEventType.TeamWipeOccurred,
                        CoordinateX = deathX,
                        CoordinateY = deathY,
                        RemainingSeconds = 0f,
                        Epoch = CurrentEpoch
                    };
                    OnOutgoingDeathPacket?.Invoke(packet);

                    return false; // Allow standard bench reload
                }
            }
            catch (Exception ex)
            {
                Log($"[DeathLifecycleCoordinator] OnLocalPlayerDied error: {ex.Message}");
                return false; // Allow bench reload on error
            }
        }

        /// <summary>
        /// Handles remote partner death, potentially triggering team wipe if both players are dead.
        /// This legacy aggregate overload marks every registered peer dead and is kept for
        /// the single-partner callers/tests. Multi-peer packet handling uses the
        /// <see cref="NetworkPeerId"/> overload below.
        /// </summary>
        /// <param name="partnerDeathX">X coordinate where the partner died.</param>
        /// <param name="partnerDeathY">Y coordinate where the partner died.</param>
        public void OnRemotePartnerDied(float partnerDeathX, float partnerDeathY)
        {
            try
            {
                bool enterWipe;
                lock (_lock)
                {
                    _isPartnerAlive = false;
                    _lastPartnerPosition = (partnerDeathX, partnerDeathY);
                    foreach (var partner in _partners.Values)
                    {
                        partner.Alive = false;
                        partner.X = partnerDeathX;
                        partner.Y = partnerDeathY;
                    }

                    enterWipe = _currentState == DeathState.Spectating ||
                                _currentState == DeathState.RespawningAtPartner;
                }

                if (enterWipe)
                {
                    EnterTeamWipe(partnerDeathX, partnerDeathY);
                }
            }
            catch (Exception ex)
            {
                Log($"[DeathLifecycleCoordinator] OnRemotePartnerDied error: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles an incoming death for one specific peer. Other peers keep their own
        /// alive/dead state, so a third player dying does not kill the session when a
        /// different partner is still available for the revive.
        /// </summary>
        public void OnRemotePartnerDied(NetworkPeerId peer, float partnerDeathX, float partnerDeathY)
        {
            try
            {
                bool enterWipe;
                lock (_lock)
                {
                    var partner = GetOrCreatePartnerLocked(peer);
                    partner.Alive = false;
                    partner.X = partnerDeathX;
                    partner.Y = partnerDeathY;

                    bool anyAlive = AnyPartnerAliveLocked();
                    _isPartnerAlive = anyAlive;
                    enterWipe = !anyAlive &&
                                (_currentState == DeathState.Spectating ||
                                 _currentState == DeathState.RespawningAtPartner);
                }

                if (enterWipe)
                {
                    EnterTeamWipe(partnerDeathX, partnerDeathY);
                }
            }
            catch (Exception ex)
            {
                Log($"[DeathLifecycleCoordinator] OnRemotePartnerDied(peer) error: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles remote partner revival, marking them as alive and updating their position.
        /// Legacy aggregate overload: every registered peer becomes alive.
        /// </summary>
        /// <param name="partnerX">X coordinate of the revived partner.</param>
        /// <param name="partnerY">Y coordinate of the revived partner.</param>
        public void OnRemotePartnerRevived(float partnerX, float partnerY)
        {
            lock (_lock)
            {
                if (_currentState == DeathState.TeamWipe)
                {
                    return;
                }

                _isPartnerAlive = true;
                _lastPartnerPosition = (partnerX, partnerY);
                foreach (var partner in _partners.Values)
                {
                    partner.Alive = true;
                    partner.X = partnerX;
                    partner.Y = partnerY;
                }
            }
        }

        /// <summary>
        /// Handles a revive for one specific peer without touching the others.
        /// </summary>
        public void OnRemotePartnerRevived(NetworkPeerId peer, float partnerX, float partnerY)
        {
            lock (_lock)
            {
                if (_currentState == DeathState.TeamWipe)
                {
                    return;
                }

                var partner = GetOrCreatePartnerLocked(peer);
                partner.Alive = true;
                partner.X = partnerX;
                partner.Y = partnerY;
                _isPartnerAlive = true;
                if (_partners.Count == 1)
                {
                    _lastPartnerPosition = (partnerX, partnerY);
                }
            }
        }

        /// <summary>
        /// Registers a peer for per-peer death/revive tracking. New peers start alive.
        /// </summary>
        public void RegisterRemotePeer(NetworkPeerId peer)
        {
            lock (_lock)
            {
                _explicitPeers.Add(peer);
                if (_partners.ContainsKey(peer)) return;
                _partners[peer] = new PartnerState
                {
                    Alive = true,
                    X = _lastPartnerPosition.X,
                    Y = _lastPartnerPosition.Y
                };
            }
        }

        /// <summary>
        /// Updates whether the peer's clone is currently in the local scene. Revive and
        /// camera selection prefer same-scene peers; removing the flag on scene exit keeps
        /// a player in another room from being chosen over a reachable one.
        /// </summary>
        public void SetRemotePeerInSameScene(NetworkPeerId peer, bool sameScene)
        {
            lock (_lock)
            {
                if (sameScene)
                {
                    _sameScenePeers.Add(peer);
                }
                else
                {
                    _sameScenePeers.Remove(peer);
                }
            }
        }

        /// <summary>
        /// Removes a disconnected peer. The legacy aggregate falls back to "alive" once no
        /// peers remain so a future session does not inherit a dead-partner latch.
        /// </summary>
        public void RemoveRemotePeer(NetworkPeerId peer)
        {
            lock (_lock)
            {
                _partners.Remove(peer);
                _explicitPeers.Remove(peer);
                _sameScenePeers.Remove(peer);
                if (_partners.Count == 0)
                {
                    _isPartnerAlive = true;
                }
                else
                {
                    _isPartnerAlive = AnyPartnerAliveLocked();
                }
            }
        }

        /// <summary>Clears every per-peer entry (session teardown / new session).</summary>
        public void ClearRemotePeers()
        {
            lock (_lock)
            {
                _partners.Clear();
                _explicitPeers.Clear();
                _sameScenePeers.Clear();
                _reviveTargetPeer = null;
                _pendingSoloBenchRespawn = false;
                _isPartnerAlive = true;
            }
        }

        /// <summary>True when the given registered peer is alive.</summary>
        public bool IsRemotePeerAlive(NetworkPeerId peer)
        {
            lock (_lock)
            {
                if (_partners.TryGetValue(peer, out var partner)) return partner.Alive;
                // Unknown peer on the legacy single-partner path: fall back to the
                // aggregate flag so existing callers keep their behaviour.
                return _isPartnerAlive;
            }
        }

        /// <summary>Last known position of one registered peer.</summary>
        public bool TryGetRemotePeerPosition(NetworkPeerId peer, out float x, out float y)
        {
            lock (_lock)
            {
                if (_partners.TryGetValue(peer, out var partner))
                {
                    x = partner.X;
                    y = partner.Y;
                    return true;
                }
            }

            x = 0f;
            y = 0f;
            return false;
        }

        /// <summary>Updates the cached position used as a revive target for one peer.</summary>
        public void UpdatePartnerPosition(NetworkPeerId peer, float x, float y)
        {
            lock (_lock)
            {
                if (_partners.TryGetValue(peer, out var partner))
                {
                    partner.X = x;
                    partner.Y = y;
                }
                else if (_partners.Count == 0)
                {
                    // Legacy single-partner path without explicit registration.
                    _lastPartnerPosition = (x, y);
                }
            }
        }

        private PartnerState GetOrCreatePartnerLocked(NetworkPeerId peer)
        {
            if (_partners.TryGetValue(peer, out var partner)) return partner;

            partner = new PartnerState
            {
                Alive = true,
                X = _lastPartnerPosition.X,
                Y = _lastPartnerPosition.Y
            };
            _partners[peer] = partner;
            return partner;
        }

        private bool AnyPartnerAliveLocked()
        {
            foreach (var partner in _partners.Values)
            {
                if (partner.Alive) return true;
            }
            return false;
        }

        private void EnterTeamWipe(float x, float y)
        {
            CurrentState = DeathState.TeamWipe;
            OnTeamWipeTriggered?.Invoke();

            var packet = new DeathLifecyclePacket
            {
                Header = new PacketHeader(PacketType.PlayerDeath, 4, 0, NetworkClock.NowMs),
                EventType = DeathEventType.TeamWipeOccurred,
                CoordinateX = x,
                CoordinateY = y,
                RemainingSeconds = 0f,
                Epoch = CurrentEpoch
            };
            OnOutgoingDeathPacket?.Invoke(packet);
        }

        /// <summary>
        /// Updates the spectator countdown and handles revive logic when timer expires.
        /// </summary>
        /// <param name="deltaTime">Time elapsed since last update in seconds.</param>
        public void UpdateTick(float deltaTime)
        {
            try
            {
                if (CurrentState == DeathState.Spectating)
                {
                    RemainingSpectatorTime -= deltaTime;
                    if (RemainingSpectatorTime <= 0f)
                    {
                        NetworkPeerId? targetPeer;
                        float targetX;
                        float targetY;
                        bool hasTarget;
                        lock (_lock)
                        {
                            hasTarget = TrySelectReviveTargetLocked(out targetPeer, out targetX, out targetY);
                            if (hasTarget)
                            {
                                _reviveTargetPeer = targetPeer;
                                _reviveTargetPosition = (targetX, targetY);
                            }
                        }

                        if (hasTarget)
                        {
                            // The revive request is only a request: the plugin may still
                            // fall back to a bench respawn (different scene, missing hero,
                            // target disconnected). PartnerReviveTrigger is sent from
                            // ConfirmReviveAtPartner after the local revive actually
                            // succeeded, so other peers never mark us alive too early.
                            CurrentState = DeathState.RespawningAtPartner;
                            try
                            {
                                OnReviveAtPartnerRequested?.Invoke(targetX, targetY);
                            }
                            catch (Exception invokeEx)
                            {
                                // A throwing handler used to leave the coordinator stuck in
                                // RespawningAtPartner forever. Retry shortly instead.
                                Log($"[DeathLifecycleCoordinator] Revive request failed: {invokeEx.Message}");
                                lock (_lock)
                                {
                                    _reviveTargetPeer = null;
                                }
                                CurrentState = DeathState.Spectating;
                                RemainingSpectatorTime = 1f;
                            }
                        }
                        else
                        {
                            // Partner died while waiting, fall back to team wipe
                            CurrentState = DeathState.TeamWipe;
                            OnTeamWipeTriggered?.Invoke();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[DeathLifecycleCoordinator] UpdateTick error: {ex.Message}");
            }
        }

        /// <summary>
        /// Completes the revive process and returns the player to alive state.
        /// </summary>
        public void CompleteRevival()
        {
            bool wasRespawning;
            float x;
            float y;
            lock (_lock)
            {
                wasRespawning = _currentState == DeathState.RespawningAtPartner;
                x = _reviveTargetPosition.X;
                y = _reviveTargetPosition.Y;
                _currentState = DeathState.Alive;
                _remainingSpectatorTime = 0f;
                _reviveTargetPeer = null;
                _pendingSoloBenchRespawn = false;
            }

            // Legacy completion path (tests and single-partner callers). The plugin now
            // uses ConfirmReviveAtPartner, which carries the real spawn coordinates.
            if (!wasRespawning) return;

            var packet = new DeathLifecyclePacket
            {
                Header = new PacketHeader(PacketType.PlayerDeath, 4, 0, NetworkClock.NowMs),
                EventType = DeathEventType.PartnerReviveTrigger,
                CoordinateX = x,
                CoordinateY = y,
                RemainingSeconds = 0f,
                Epoch = CurrentEpoch
            };
            OnOutgoingDeathPacket?.Invoke(packet);
        }

        /// <summary>
        /// Confirms that the local player was actually revived at the selected partner.
        /// Only now is <see cref="DeathEventType.PartnerReviveTrigger"/> broadcast, so a
        /// bench fallback or an exception cannot make other peers believe we are alive
        /// at a position we never reached.
        /// </summary>
        /// <param name="x">Actual revive X coordinate.</param>
        /// <param name="y">Actual revive Y coordinate.</param>
        public void ConfirmReviveAtPartner(float x, float y)
        {
            NetworkPeerId? targetPeer;
            lock (_lock)
            {
                targetPeer = _reviveTargetPeer;
                _currentState = DeathState.Alive;
                _remainingSpectatorTime = 0f;
                _lastPartnerPosition = (x, y);

                if (targetPeer.HasValue &&
                    _partners.TryGetValue(targetPeer.Value, out var partner))
                {
                    partner.Alive = true;
                    partner.X = x;
                    partner.Y = y;
                }

                _reviveTargetPeer = null;
                _pendingSoloBenchRespawn = false;
            }

            var packet = new DeathLifecyclePacket
            {
                Header = new PacketHeader(PacketType.PlayerDeath, 4, 0, NetworkClock.NowMs),
                EventType = DeathEventType.PartnerReviveTrigger,
                CoordinateX = x,
                CoordinateY = y,
                RemainingSeconds = 0f,
                Epoch = CurrentEpoch
            };
            OnOutgoingDeathPacket?.Invoke(packet);
        }

        /// <summary>
        /// Abandons the in-progress revive request without emitting a success packet.
        /// The caller is expected to start a bench respawn (solo fallback) instead.
        /// </summary>
        public void FailReviveAtPartner()
        {
            lock (_lock)
            {
                _reviveTargetPeer = null;
                if (_currentState == DeathState.RespawningAtPartner)
                {
                    _currentState = DeathState.Alive;
                    _remainingSpectatorTime = 0f;
                }
            }
        }

        /// <summary>
        /// BUG-A4: resynchronizes the death epoch for a newly established session. The epoch
        /// was never transmitted over the wire, so a client reconnecting to a host that had
        /// already survived bench wipes carried a stale epoch and every incoming death
        /// packet was silently dropped — both players then waited for a revive that could
        /// never arrive. Both peers reset to epoch 1 at connect, keeping them in lockstep.
        /// </summary>
        public void SynchronizeEpochForNewSession()
        {
            lock (_lock)
            {
                _currentState = DeathState.Alive;
                _isPartnerAlive = true;
                _remainingSpectatorTime = 0f;
                _currentEpoch = 1;
                _lastPartnerPosition = (0f, 0f);
                _reviveTargetPeer = null;
                _reviveTargetPosition = (0f, 0f);
                _localDeathPosition = (0f, 0f);
                _partners.Clear();
                _explicitPeers.Clear();
                _sameScenePeers.Clear();
                _pendingSoloBenchRespawn = false;
            }
        }

        /// <summary>
        /// Handles incoming death lifecycle packets from the remote peer.
        /// </summary>
        /// <param name="sender">The peer that sent the packet.</param>
        /// <param name="packet">The death lifecycle packet to process.</param>
        public void HandleDeathPacket(NetworkPeerId sender, DeathLifecyclePacket packet)
        {
            try
            {
                // A solo bench-respawn is a recovery signal: it only clears our "partner
                // is dead" flag and must survive an epoch mismatch (the two peers advance
                // epochs independently when only one of them reloads at a bench).
                if (packet.EventType == DeathEventType.SoloBenchRespawn)
                {
                    // Mark only the sender alive: in a 3+ session the other peers keep
                    // their own state. Do NOT overwrite the position: this packet carries
                    // no coordinates (the sender is mid-reload).
                    lock (_lock)
                    {
                        GetOrCreatePartnerLocked(sender).Alive = true;
                        _isPartnerAlive = true;
                    }
                    return;
                }

                // BUG-A4: epochs are compared against our counter, but the peer may have
                // survived more wipe cycles than we tracked (e.g. we reconnected mid-session).
                // Adopt a HIGHER epoch so the session resynchronizes instead of silently
                // dropping every death event; lower epochs remain stale and are ignored.
                if (packet.Epoch > CurrentEpoch)
                {
                    CurrentEpoch = packet.Epoch;
                }
                else if (packet.Epoch != CurrentEpoch)
                {
                    // Stale packet from a different epoch, ignore
                    return;
                }

                switch (packet.EventType)
                {
                    case DeathEventType.SoloDeathOccurred:
                        // Epoch validation above is the authoritative stale-packet filter.
                        // The old five-second "post bench" window also discarded genuine
                        // deaths that happened right after a respawn, so it is gone.
                        OnRemotePartnerDied(sender, packet.CoordinateX, packet.CoordinateY);
                        break;

                    case DeathEventType.PartnerReviveTrigger:
                        if (CurrentState == DeathState.TeamWipe)
                        {
                            // Stale revive packet during team wipe: do not mark partner alive
                            break;
                        }
                        OnRemotePartnerRevived(sender, packet.CoordinateX, packet.CoordinateY);
                        break;

                    case DeathEventType.TeamWipeOccurred:
                        bool alreadyWiping = CurrentState == DeathState.TeamWipe;
                        lock (_lock)
                        {
                            // A team wipe means the sender saw no living partner left, so
                            // every registered peer is down (each of them sends its own
                            // death packet too, but this keeps a 3+ session consistent even
                            // if one of those packets is delayed or lost). This also runs
                            // during a local solo bench reload: the last living partner may
                            // die while we are reloading and must not be left "alive".
                            foreach (var partner in _partners.Values)
                            {
                                partner.Alive = false;
                            }
                            GetOrCreatePartnerLocked(sender).Alive = false;
                            _isPartnerAlive = false;
                        }

                        if (alreadyWiping)
                        {
                            // Duplicate/echo packet: state is already updated, do not fire
                            // the wipe callback twice.
                            break;
                        }

                        CurrentState = DeathState.TeamWipe;
                        OnTeamWipeTriggered?.Invoke();
                        break;

                    case DeathEventType.BenchCheckpointUpdate:
                        // Sent after every bench respawn. Besides syncing the epoch (adopted
                        // above) it proves that THIS sender completed the reload and is alive
                        // again. Only the sender is marked: in a 3+ session the other peers
                        // keep their own alive/dead state.
                        lock (_lock)
                        {
                            GetOrCreatePartnerLocked(sender).Alive = true;
                            _isPartnerAlive = true;
                        }
                        break;

                    case DeathEventType.SoloBenchRespawn:
                        // Handled before the epoch check (see entry point).
                        break;
                }
            }
            catch (Exception ex)
            {
                Log($"[DeathLifecycleCoordinator] HandleDeathPacket error: {ex.Message}");
            }
        }

        public void ForceBenchRespawn()
        {
            ForceBenchRespawn(notifyPartner: true);
        }

        /// <summary>
        /// Forces a bench respawn by triggering team wipe state.
        /// </summary>
        /// <param name="notifyPartner">
        /// True to broadcast <see cref="DeathEventType.TeamWipeOccurred"/> (both players
        /// must reload at a bench). False for a SOLO fallback — the partner is alive and
        /// must not be killed; a <see cref="DeathEventType.SoloBenchRespawn"/> event tells
        /// it that this player is alive again.
        /// </param>
        public void ForceBenchRespawn(bool notifyPartner)
        {
            BeginBenchRespawn(notifyPartner, notifyImmediately: true);
        }

        /// <summary>
        /// Starts a solo bench fallback without announcing success yet. The plugin uses
        /// this path so peers keep treating the player as down until
        /// <see cref="ResetAfterBenchRespawn"/> runs after the actual reload.
        /// </summary>
        public void BeginSoloBenchRespawn()
        {
            BeginBenchRespawn(notifyPartner: false, notifyImmediately: false);
        }

        private void BeginBenchRespawn(bool notifyPartner, bool notifyImmediately)
        {
            if (CurrentState == DeathState.TeamWipe) return;

            CurrentState = DeathState.TeamWipe;
            lock (_lock)
            {
                _reviveTargetPeer = null;
                _pendingSoloBenchRespawn = !notifyPartner && !notifyImmediately;

                if (notifyPartner)
                {
                    // A real team wipe: every peer is dead and will reload its own bench.
                    foreach (var partner in _partners.Values)
                    {
                        partner.Alive = false;
                    }
                    _isPartnerAlive = false;
                }
                else if (_explicitPeers.Count == 0)
                {
                    // Legacy single-partner path: a solo fallback leaves the partner
                    // alive in another room, so the aggregate must stay true. Lazy
                    // packet-created entries belong to the same legacy pair.
                    foreach (var partner in _partners.Values)
                    {
                        partner.Alive = true;
                    }
                    _isPartnerAlive = true;
                }
                // Multi-peer solo fallback: do not touch other peers. Marking every
                // registered peer alive resurrected a genuinely dead third player.
            }
            OnTeamWipeTriggered?.Invoke();

            if (notifyImmediately)
            {
                SendBenchRespawnPacket(notifyPartner);
            }
        }

        private void SendBenchRespawnPacket(bool teamWipe)
        {
            var packet = new DeathLifecyclePacket
            {
                Header = new PacketHeader(PacketType.PlayerDeath, 4, 0, NetworkClock.NowMs),
                EventType = teamWipe
                    ? DeathEventType.TeamWipeOccurred
                    : DeathEventType.SoloBenchRespawn,
                RemainingSeconds = 0f,
                Epoch = CurrentEpoch
            };
            OnOutgoingDeathPacket?.Invoke(packet);
        }

        /// <summary>
        /// Sends the current epoch to the peer after a bench reload. Without this, a peer
        /// that respawned once more than us advances its epoch and then silently ignores
        /// our death packets forever (the receive path only adopts HIGHER epochs).
        /// </summary>
        public void BroadcastEpochSync()
        {
            var packet = new DeathLifecyclePacket
            {
                Header = new PacketHeader(PacketType.PlayerDeath, 4, 0, NetworkClock.NowMs),
                EventType = DeathEventType.BenchCheckpointUpdate,
                RemainingSeconds = 0f,
                Epoch = CurrentEpoch
            };
            OnOutgoingDeathPacket?.Invoke(packet);
        }

        /// <summary>
        /// Resets the coordinator state after a bench respawn, advancing the epoch.
        /// </summary>
        public void ResetAfterBenchRespawn()
        {
            CurrentState = DeathState.Alive;
            RemainingSpectatorTime = 0f;
            CurrentEpoch++;

            bool notifySoloBenchRespawn;
            lock (_lock)
            {
                _reviveTargetPeer = null;
                notifySoloBenchRespawn = _pendingSoloBenchRespawn;
                _pendingSoloBenchRespawn = false;

                if (_explicitPeers.Count == 0)
                {
                    // Legacy behaviour: both players reload the same bench after a wipe,
                    // so lazy packet-created partner entries become alive again too.
                    foreach (var partner in _partners.Values)
                    {
                        partner.Alive = true;
                    }
                    _isPartnerAlive = true;
                }
                else
                {
                    // Multi-peer: the local reload says nothing about the other peers.
                    // Each of them re-announces BenchCheckpointUpdate after its own
                    // reload, and only that sender is marked alive.
                    _isPartnerAlive = AnyPartnerAliveLocked();
                }
            }

            // Only now is the solo fallback actually complete. Sending this in
            // ForceBenchRespawn made peers mark the player alive while still reloading.
            if (notifySoloBenchRespawn)
            {
                SendBenchRespawnPacket(teamWipe: false);
            }
        }

        #endregion
    }
}
