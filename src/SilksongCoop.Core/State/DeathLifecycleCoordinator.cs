using System;
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

        private static void Log(string msg) => System.Diagnostics.Debug.WriteLine(msg);

        private readonly object _lock = new object();

        private DeathState _currentState = DeathState.Alive;
        private bool _isPartnerAlive = true;
        private float _remainingSpectatorTime;
        private (float X, float Y) _lastPartnerPosition = (0f, 0f);
        private uint _currentEpoch = 1;
        private bool _isPostBenchRespawn = false;
        private float _postBenchRespawnTimer = 0f;

        #endregion

        #region Properties

        public DeathState CurrentState
        {
            get { lock (_lock) { return _currentState; } }
            private set { lock (_lock) { _currentState = value; } }
        }

        public bool IsPartnerAlive
        {
            get { lock (_lock) { return _isPartnerAlive; } }
            set { lock (_lock) { _isPartnerAlive = value; } }
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
            get { lock (_lock) { return _lastPartnerPosition; } }
            set { lock (_lock) { _lastPartnerPosition = value; } }
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
                _isPostBenchRespawn = false;
                _postBenchRespawnTimer = 0f;

                if (CurrentState == DeathState.TeamWipe)
                {
                    return false;
                }

                if (IsPartnerAlive)
                {
                    // Solo death while partner is alive: suppress bench reload and enter spectator
                    CurrentState = DeathState.Spectating;
                    RemainingSpectatorTime = Math.Clamp(ReviveCooldownDuration, 15f, 30f);

                    OnSoloDeathSuppressed?.Invoke(deathX, deathY);

                    var packet = new DeathLifecyclePacket
                    {
                        Header = new PacketHeader(PacketType.PlayerDeath, 4, 0, (uint)Environment.TickCount),
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
                        Header = new PacketHeader(PacketType.PlayerDeath, 4, 0, (uint)Environment.TickCount),
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
        /// </summary>
        /// <param name="partnerDeathX">X coordinate where the partner died.</param>
        /// <param name="partnerDeathY">Y coordinate where the partner died.</param>
        public void OnRemotePartnerDied(float partnerDeathX, float partnerDeathY)
        {
            try
            {
                IsPartnerAlive = false;

                if (CurrentState == DeathState.Spectating || CurrentState == DeathState.RespawningAtPartner)
                {
                    // Both are dead: partner died while local player was waiting to revive or reviving -> Team Wipe!
                    CurrentState = DeathState.TeamWipe;
                    OnTeamWipeTriggered?.Invoke();

                    var packet = new DeathLifecyclePacket
                    {
                        Header = new PacketHeader(PacketType.PlayerDeath, 4, 0, (uint)Environment.TickCount),
                        EventType = DeathEventType.TeamWipeOccurred,
                        CoordinateX = partnerDeathX,
                        CoordinateY = partnerDeathY,
                        RemainingSeconds = 0f,
                        Epoch = CurrentEpoch
                    };
                    OnOutgoingDeathPacket?.Invoke(packet);
                }
            }
            catch (Exception ex)
            {
                Log($"[DeathLifecycleCoordinator] OnRemotePartnerDied error: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles remote partner revival, marking them as alive and updating their position.
        /// </summary>
        /// <param name="partnerX">X coordinate of the revived partner.</param>
        /// <param name="partnerY">Y coordinate of the revived partner.</param>
        public void OnRemotePartnerRevived(float partnerX, float partnerY)
        {
            if (CurrentState == DeathState.TeamWipe)
            {
                return;
            }

            IsPartnerAlive = true;
            LastPartnerPosition = (partnerX, partnerY);
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
                                Header = new PacketHeader(PacketType.PlayerDeath, 4, 0, (uint)Environment.TickCount),
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
        /// Handles incoming death lifecycle packets from the remote peer.
        /// </summary>
        /// <param name="sender">The peer that sent the packet.</param>
        /// <param name="packet">The death lifecycle packet to process.</param>
        public void HandleDeathPacket(NetworkPeerId sender, DeathLifecyclePacket packet)
        {
            try
            {
                // Fix: Check epoch to filter stale packets from previous death/revive cycles
                if (packet.Epoch != CurrentEpoch)
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
                        OnRemotePartnerDied(packet.CoordinateX, packet.CoordinateY);
                        break;

                    case DeathEventType.PartnerReviveTrigger:
                        if (CurrentState == DeathState.TeamWipe)
                        {
                            // Stale revive packet during team wipe: do not mark partner alive
                            break;
                        }
                        IsPartnerAlive = true;
                        LastPartnerPosition = (packet.CoordinateX, packet.CoordinateY);
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
                        IsPartnerAlive = false;
                        CurrentState = DeathState.TeamWipe;
                        OnTeamWipeTriggered?.Invoke();
                        break;
                }
            }
            catch (Exception ex)
            {
                Log($"[DeathLifecycleCoordinator] HandleDeathPacket error: {ex.Message}");
            }
        }

        /// <summary>
        /// Forces a bench respawn by triggering team wipe state.
        /// </summary>
        public void ForceBenchRespawn()
        {
            _isPostBenchRespawn = false;
            _postBenchRespawnTimer = 0f;

            if (CurrentState == DeathState.TeamWipe) return;

            CurrentState = DeathState.TeamWipe;
            IsPartnerAlive = false;
            OnTeamWipeTriggered?.Invoke();

            var packet = new DeathLifecyclePacket
            {
                Header = new PacketHeader(PacketType.PlayerDeath, 4, 0, (uint)Environment.TickCount),
                EventType = DeathEventType.TeamWipeOccurred,
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
