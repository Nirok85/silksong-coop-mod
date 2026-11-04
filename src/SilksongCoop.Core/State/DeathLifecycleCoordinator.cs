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
        private bool _isPostBenchRespawn = false;
        private float _postBenchRespawnTimer = 0f;

        private sealed class PartnerState
        {
            public bool Alive;
            public float X;
            public float Y;
        }

        private readonly Dictionary<NetworkPeerId, PartnerState> _partners =
            new Dictionary<NetworkPeerId, PartnerState>();

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
                        // Prefer a living partner; if everyone is dead, the last known
                        // position is still a better fallback than (0,0).
                        PartnerState? fallback = null;
                        foreach (var partner in _partners.Values)
                        {
                            if (partner.Alive) return (partner.X, partner.Y);
                            fallback ??= partner;
                        }
                        if (fallback != null) return (fallback.X, fallback.Y);
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

                _isPostBenchRespawn = false;
                _postBenchRespawnTimer = 0f;

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
        /// Removes a disconnected peer. The legacy aggregate falls back to "alive" once no
        /// peers remain so a future session does not inherit a dead-partner latch.
        /// </summary>
        public void RemoveRemotePeer(NetworkPeerId peer)
        {
            lock (_lock)
            {
                _partners.Remove(peer);
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
                if (_postBenchRespawnTimer > 0f)
                {
                    _postBenchRespawnTimer = Math.Max(0f, _postBenchRespawnTimer - deltaTime);
                    if (_postBenchRespawnTimer <= 0f)
                    {
                        _isPostBenchRespawn = false;
                    }
                }

                if (CurrentState == DeathState.Spectating)
                {
                    RemainingSpectatorTime -= deltaTime;
                    if (RemainingSpectatorTime <= 0f)
                    {
                        if (IsPartnerAlive)
                        {
                            // Revive beside surviving partner!
                            CurrentState = DeathState.RespawningAtPartner;
                            OnReviveAtPartnerRequested?.Invoke(LastPartnerPosition.X, LastPartnerPosition.Y);

                            var packet = new DeathLifecyclePacket
                            {
                                Header = new PacketHeader(PacketType.PlayerDeath, 4, 0, NetworkClock.NowMs),
                                EventType = DeathEventType.PartnerReviveTrigger,
                                CoordinateX = LastPartnerPosition.X,
                                CoordinateY = LastPartnerPosition.Y,
                                RemainingSeconds = 0f,
                                Epoch = CurrentEpoch
                            };
                            OnOutgoingDeathPacket?.Invoke(packet);
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
            CurrentState = DeathState.Alive;
            _isPostBenchRespawn = false;
            _postBenchRespawnTimer = 0f;
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
                _isPostBenchRespawn = false;
                _postBenchRespawnTimer = 0f;
                _lastPartnerPosition = (0f, 0f);
                _partners.Clear();
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
                        if (_isPostBenchRespawn && CurrentState == DeathState.Alive && IsPartnerAlive)
                        {
                            // Stale solo death packet arriving from prior life cycle before bench reset
                            break;
                        }
                        _isPostBenchRespawn = false;
                        _postBenchRespawnTimer = 0f;
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
                        if (CurrentState == DeathState.TeamWipe)
                        {
                            // Already in TeamWipe: ignore echo / duplicate packets
                            break;
                        }
                        if (_isPostBenchRespawn && CurrentState == DeathState.Alive && IsPartnerAlive)
                        {
                            // Stale wipe packet arriving while alive post-reset: discard
                            break;
                        }
                        lock (_lock)
                        {
                            GetOrCreatePartnerLocked(sender).Alive = false;
                            _isPartnerAlive = false;
                        }
                        CurrentState = DeathState.TeamWipe;
                        OnTeamWipeTriggered?.Invoke();
                        break;

                    case DeathEventType.BenchCheckpointUpdate:
                        // Epoch-only sync sent after a bench respawn; the epoch was already
                        // adopted above. Keeps both peers from silently dropping each
                        // other's death packets after an uneven number of solo respawns.
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
            _isPostBenchRespawn = false;
            _postBenchRespawnTimer = 0f;

            if (CurrentState == DeathState.TeamWipe) return;

            CurrentState = DeathState.TeamWipe;
            // A solo bench fallback leaves the partner alive in another room. Marking it
            // dead made the next partner death look like a dual wipe and could suppress a
            // legitimate revive.
            IsPartnerAlive = !notifyPartner;
            OnTeamWipeTriggered?.Invoke();

            var packet = new DeathLifecyclePacket
            {
                Header = new PacketHeader(PacketType.PlayerDeath, 4, 0, NetworkClock.NowMs),
                EventType = notifyPartner
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
            IsPartnerAlive = true;
            RemainingSpectatorTime = 0f;
            CurrentEpoch++;
            _isPostBenchRespawn = true;
            _postBenchRespawnTimer = 5.0f;
        }

        #endregion
    }
}
