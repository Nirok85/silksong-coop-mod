using System;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;

namespace SilksongCoop.Core.State
{
    public class DeathLifecycleCoordinator : IDeathLifecycleCoordinator
    {
        public DeathState CurrentState { get; private set; } = DeathState.Alive;
        public bool IsPartnerAlive { get; set; } = true;
        public float RemainingSpectatorTime { get; private set; } = 0f;
        public float ReviveCooldownDuration { get; set; } = 20.0f; // Configurable between 15-30s
        public (float X, float Y) LastPartnerPosition { get; set; } = (0f, 0f);
        public uint CurrentEpoch { get; private set; } = 1;

        private bool _isPostBenchRespawn = false;
        private float _postBenchRespawnTimer = 0f;

        public event Action<float, float>? OnSoloDeathSuppressed;
        public event Action? OnTeamWipeTriggered;
        public event Action<float, float>? OnReviveAtPartnerRequested;
        public event Action<DeathLifecyclePacket>? OnOutgoingDeathPacket;

        public bool OnLocalPlayerDied(float deathX, float deathY)
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

        public void OnRemotePartnerDied(float partnerDeathX, float partnerDeathY)
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

        public void OnRemotePartnerRevived(float partnerX, float partnerY)
        {
            if (CurrentState == DeathState.TeamWipe)
            {
                return;
            }

            IsPartnerAlive = true;
            LastPartnerPosition = (partnerX, partnerY);
        }

        public void UpdateTick(float deltaTime)
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

        public void CompleteRevival()
        {
            CurrentState = DeathState.Alive;
            _isPostBenchRespawn = false;
            _postBenchRespawnTimer = 0f;
        }

        public void HandleDeathPacket(NetworkPeerId sender, DeathLifecyclePacket packet)
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

        public void ResetAfterBenchRespawn()
        {
            CurrentState = DeathState.Alive;
            IsPartnerAlive = true;
            RemainingSpectatorTime = 0f;
            CurrentEpoch++;
            _isPostBenchRespawn = true;
            _postBenchRespawnTimer = 5.0f;
        }
    }
}
