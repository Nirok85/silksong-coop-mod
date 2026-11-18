using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.Revive;
using SilksongCoop.Core.State;

namespace SilksongCoop.Tests.DefectRemediationTiers
{
    /// <summary>
    /// Headless test models, doubles, and fixtures for the 25 defect remediation features.
    /// Free of Unity / BepInEx engine dependencies, completely deterministic and thread-safe.
    /// </summary>
    public static class DefectTestHarness
    {
        #region CRIT-02: Atomic Team Wipe Coordinator

        public class AtomicTeamWipeCoordinator
        {
            private int _teamWipeInProgress;
            public int TeamWipeTriggerCount { get; private set; }
            public bool IsWipeInProgress => Volatile.Read(ref _teamWipeInProgress) == 1;

            public bool TriggerTeamWipe()
            {
                if (Interlocked.CompareExchange(ref _teamWipeInProgress, 1, 0) != 0)
                {
                    return false; // Duplicate call suppressed
                }

                TeamWipeTriggerCount++;
                return true;
            }

            public void ResetAfterBenchRespawn()
            {
                Interlocked.Exchange(ref _teamWipeInProgress, 0);
            }
        }

        #endregion

        #region HIGH-01: Headless Safe Ground Prober

        public class HeadlessGroundProber : IGroundProber
        {
            public const float ProbeDistance = 15.0f;
            public List<(float MinX, float MaxX, float Y, bool IsHazard)> Platforms { get; } = new();
            public List<(float MinX, float MaxX, float MinY, float MaxY)> SolidWalls { get; } = new();

            public int CalculateReviveHealth(int maxHealth) => SafeGroundSolver.CalculateReviveHealth(maxHealth);

            public bool TryFindSafeGround(float partnerX, float partnerY, out float safeX, out float safeY)
            {
                safeX = partnerX;
                safeY = partnerY + 0.5f;

                // Check downward for floor within ProbeDistance
                float bestY = float.MinValue;
                bool foundFloor = false;

                foreach (var platform in Platforms)
                {
                    if (partnerX >= platform.MinX && partnerX <= platform.MaxX)
                    {
                        if (platform.Y <= partnerY && (partnerY - platform.Y) <= ProbeDistance)
                        {
                            if (platform.Y > bestY)
                            {
                                bestY = platform.Y;
                                if (!platform.IsHazard)
                                {
                                    // Verify clearance against solid walls
                                    bool blocked = false;
                                    float candidateY = platform.Y + 0.5f;
                                    foreach (var wall in SolidWalls)
                                    {
                                        if (partnerX >= wall.MinX && partnerX <= wall.MaxX &&
                                            candidateY >= wall.MinY && candidateY <= wall.MaxY)
                                        {
                                            blocked = true;
                                            break;
                                        }
                                    }

                                    if (!blocked)
                                    {
                                        foundFloor = true;
                                        safeX = partnerX;
                                        safeY = candidateY;
                                    }
                                }
                            }
                        }
                    }
                }

                return foundFloor;
            }
        }

        #endregion

        #region MED-03: Headless Spectator State & Keybind Model

        public class HeadlessSpectatorController
        {
            public bool IsSpectatorActive { get; set; }
            public bool SpectatorCameraFollow { get; set; } = true;
            public int ToastNotificationCount { get; private set; }
            public string LastToastMessage { get; private set; } = "";

            public bool HandleF9Keypress()
            {
                // Defect MED-03: Suppress spectator F9 camera toggle and toasts when spectator mode is inactive
                if (!IsSpectatorActive)
                {
                    return false;
                }

                SpectatorCameraFollow = !SpectatorCameraFollow;
                ToastNotificationCount++;
                LastToastMessage = SpectatorCameraFollow
                    ? "Spectator camera: follow partner"
                    : "Spectator camera: hold position";
                return true;
            }
        }

        #endregion

        #region MED-06: Headless Packet Sender with Peer Logging

        public class HeadlessPacketSender
        {
            public List<string> FailureLogs { get; } = new();
            public int SentBytes { get; private set; }

            public bool SendTo<T>(
                INetworkTransport? transport,
                NetworkPeerId target,
                T packet,
                DeliveryMethod method,
                byte channel,
                int bufferSize,
                string logContext = "[PacketSender]") where T : struct, IPacket
            {
                if (transport == null)
                {
                    FailureLogs.Add($"{logContext} Failed sending {typeof(T).Name} to peer {target.Value}: transport is null");
                    return false;
                }

                byte[]? buffer = null;
                try
                {
                    buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
                    var writer = new PacketWriter(buffer);
                    packet.Serialize(ref writer);

                    bool accepted = transport.Send(target, buffer, 0, writer.Position, method, channel);
                    if (!accepted)
                    {
                        FailureLogs.Add($"{logContext} Failed sending {typeof(T).Name} to peer {target.Value}: transport refused");
                        return false;
                    }

                    SentBytes += writer.Position;
                    return true;
                }
                catch (Exception ex)
                {
                    FailureLogs.Add($"{logContext} Failed sending {typeof(T).Name} to peer {target.Value}: {ex.Message}");
                    return false;
                }
                finally
                {
                    if (buffer != null)
                    {
                        ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                    }
                }
            }
        }

        #endregion

        #region LOW-03: Headless Connection History

        public class HeadlessConnectionHistory
        {
            public const int MaxEntries = 5;
            public const int MaxTargetLength = 128;
            private readonly List<string> _entries = new(MaxEntries);
            public bool PersistEnabled { get; set; } = true;

            public IReadOnlyList<string> GetAll() => _entries;

            public void Add(string target)
            {
                if (string.IsNullOrWhiteSpace(target)) return;

                target = target.Trim();
                if (target.Length > MaxTargetLength) return;

                for (int i = 0; i < _entries.Count; i++)
                {
                    if (string.Equals(_entries[i], target, StringComparison.OrdinalIgnoreCase))
                    {
                        _entries.RemoveAt(i);
                        break;
                    }
                }

                _entries.Insert(0, target);
                if (_entries.Count > MaxEntries)
                {
                    _entries.RemoveAt(_entries.Count - 1);
                }
            }

            public void Clear() => _entries.Clear();
        }

        #endregion

        #region LOW-04: Headless Quality Preset Manager

        public enum QualityLevel
        {
            Low,
            Medium,
            High,
            Custom
        }

        public class HeadlessQualityPresetManager
        {
            public QualityLevel CurrentLevel { get; private set; } = QualityLevel.Medium;
            public int ConfiguredTickRateHz { get; set; } = 30;
            public bool EnableDashTrail { get; set; } = true;

            public void ApplyPreset(QualityLevel level)
            {
                CurrentLevel = level;
                switch (level)
                {
                    case QualityLevel.Low:
                        ConfiguredTickRateHz = 20;
                        EnableDashTrail = false;
                        break;
                    case QualityLevel.Medium:
                        ConfiguredTickRateHz = 30;
                        EnableDashTrail = true;
                        break;
                    case QualityLevel.High:
                        ConfiguredTickRateHz = 60;
                        EnableDashTrail = true;
                        break;
                    case QualityLevel.Custom:
                        break;
                }
            }

            public static int ValidateAndClampTickRate(int requestedTickRate, int gameFrameRate)
            {
                int maxAllowed = Math.Min(60, Math.Max(10, gameFrameRate));
                return Math.Clamp(requestedTickRate, 10, maxAllowed);
            }
        }

        #endregion

        #region LOW-06: Headless Watermark State

        public class HeadlessWatermarkState
        {
            public const float RefreshIntervalSeconds = 0.25f;
            public string WatermarkText { get; private set; } = "[F10] Silksong Co-op";
            public string WatermarkColorHex { get; private set; } = "#FFF";

            private bool _initialized;
            private bool _lastConnected;
            private SessionStatus _lastStatus;
            private NetworkPeerId? _lastPeerId;
            private int _lastRtt;
            private float _nextRefreshTime;

            public void Update(float currentTime, bool connected, SessionStatus status, NetworkPeerId? peerId, int rtt)
            {
                bool identityChanged = !_initialized ||
                                       connected != _lastConnected ||
                                       status != _lastStatus ||
                                       peerId != _lastPeerId ||
                                       rtt != _lastRtt; // LOW-06: RTT included in identityChanged

                if (!identityChanged && currentTime < _nextRefreshTime) return;

                _initialized = true;
                _lastConnected = connected;
                _lastStatus = status;
                _lastPeerId = peerId;
                _lastRtt = rtt;
                _nextRefreshTime = currentTime + RefreshIntervalSeconds;

                if (connected)
                {
                    string quality = rtt <= 0 ? "–" : rtt < 50 ? "●" : rtt < 150 ? "◐" : "○";
                    string peerName = peerId.HasValue ? peerId.Value.DisplayName : "Unknown";
                    WatermarkText = $"[F10] Co-op  {quality} {peerName}  {(rtt > 0 ? $"{rtt}ms" : "n/a")}";
                    WatermarkColorHex = rtt <= 0 ? "#FFF" : rtt < 50 ? "#0F0" : rtt < 150 ? "#FF0" : "#F00";
                }
                else if (status == SessionStatus.Hosting)
                {
                    WatermarkText = "Hosting (waiting for partner...)";
                    WatermarkColorHex = "#FFD700";
                }
                else if (status == SessionStatus.Connecting)
                {
                    WatermarkText = "[F10] Connecting...";
                    WatermarkColorHex = "#00F";
                }
                else
                {
                    WatermarkText = "[F10] Silksong Co-op";
                    WatermarkColorHex = "#FFF";
                }
            }
        }

        #endregion

        #region PERF-01: Headless Palette Swap Throttling Controller

        public class HeadlessPaletteSwapController
        {
            public const int EnforceIntervalFrames = 30;
            public bool IsPlayer2 { get; private set; }
            public float HueShift { get; private set; }
            public int ApplyCount { get; private set; }
            public int DestroyedInstanceCount { get; private set; }

            private int _lastEnforceFrame = -1000;
            private bool _needsUpdate = true;
            private readonly List<object> _instanceMaterials = new();

            public void SetPlayer2(bool isPlayer2, float customHueShift = 0.58f, int currentFrame = 0)
            {
                IsPlayer2 = isPlayer2;
                HueShift = customHueShift;
                _needsUpdate = false;
                _lastEnforceFrame = currentFrame;
                ApplyPaletteSwap();
            }

            public void LateUpdate(int currentFrame)
            {
                if (_needsUpdate || currentFrame - _lastEnforceFrame >= EnforceIntervalFrames)
                {
                    ApplyPaletteSwap();
                    _lastEnforceFrame = currentFrame;
                    _needsUpdate = false;
                }
            }

            public void ApplyPaletteSwap()
            {
                ApplyCount++;
                if (IsPlayer2)
                {
                    _instanceMaterials.Add(new object());
                }
            }

            public void Destroy()
            {
                DestroyedInstanceCount += _instanceMaterials.Count;
                _instanceMaterials.Clear();
            }
        }

        #endregion

        #region PERF-02: Headless Peer Diagnostics Snapshot

        public struct HeadlessPeerDiagnosticsSnapshot
        {
            public bool Connected;
            public bool PartnerAlive;
            public NetworkPeerId? RemotePeerId;
            public int RttMs;
            public double InterpolationDelayMs;

            public static HeadlessPeerDiagnosticsSnapshot Build(bool connected, bool partnerAlive, NetworkPeerId? peerId, int rtt, double delay)
            {
                if (!connected) return default;
                return new HeadlessPeerDiagnosticsSnapshot
                {
                    Connected = true,
                    PartnerAlive = partnerAlive,
                    RemotePeerId = peerId,
                    RttMs = rtt,
                    InterpolationDelayMs = delay
                };
            }
        }

        #endregion

        #region PERF-03: Headless Steam Polling Liveness Monitor

        public class HeadlessSteamPollingMonitor
        {
            public const long HeartbeatTimeoutMs = 15000;
            private readonly Dictionary<ulong, long> _additionalPeersLastSeen = new();
            public int PollTickCount { get; private set; }
            public int AllocatedStaleLists { get; private set; }
            public int TimedOutPeersDisconnected { get; private set; }

            public void RegisterPeer(ulong steamId, long nowMs) => _additionalPeersLastSeen[steamId] = nowMs;
            public void RecordPacketReceived(ulong steamId, long nowMs) => _additionalPeersLastSeen[steamId] = nowMs;

            public void PollEvents(long nowMs)
            {
                PollTickCount++;
                if (_additionalPeersLastSeen.Count == 0) return;

                List<ulong>? stale = null;
                foreach (var (steamId, lastSeen) in _additionalPeersLastSeen)
                {
                    if (nowMs - lastSeen > HeartbeatTimeoutMs)
                    {
                        if (stale == null)
                        {
                            AllocatedStaleLists++;
                            stale = new List<ulong>();
                        }
                        stale.Add(steamId);
                    }
                }

                if (stale != null)
                {
                    foreach (var id in stale)
                    {
                        _additionalPeersLastSeen.Remove(id);
                        TimedOutPeersDisconnected++;
                    }
                }
            }
        }

        #endregion

        #region ARCH-03: Decoupled Gameplay Actions Contract

        public interface IInventoryGameplayActionsDouble
        {
            bool TryApplyRemoteItemUnlock(string itemId, ItemCategory category);
            void ApplyRemoteItemUnlock(string itemId, ItemCategory category);
        }

        public interface IReviveGameplayActionsDouble
        {
            void ReviveAtPartner(float x, float y);
            bool TryReviveAtPartner(float x, float y, NetworkPeerId? targetPeer);
            void TriggerTeamWipe();
        }

        public interface IGameplayActionsDouble : IInventoryGameplayActionsDouble, IReviveGameplayActionsDouble
        {
        }

        public class MockInventoryGameplayActions : IInventoryGameplayActionsDouble
        {
            private readonly object _lock = new();
            public List<(string ItemId, ItemCategory Category)> AppliedUnlocks { get; } = new();
            public bool ShouldSucceed { get; set; } = true;
            public int TryApplyCallCount { get; private set; }
            public int ApplyCallCount { get; private set; }

            public bool TryApplyRemoteItemUnlock(string itemId, ItemCategory category)
            {
                lock (_lock)
                {
                    TryApplyCallCount++;
                    if (!ShouldSucceed) return false;
                    AppliedUnlocks.Add((itemId, category));
                    return true;
                }
            }

            public void ApplyRemoteItemUnlock(string itemId, ItemCategory category)
            {
                lock (_lock)
                {
                    ApplyCallCount++;
                    AppliedUnlocks.Add((itemId, category));
                }
            }
        }

        public class MockReviveGameplayActions : IReviveGameplayActionsDouble
        {
            private readonly object _lock = new();
            public List<(float X, float Y, NetworkPeerId? TargetPeer)> ReviveAttempts { get; } = new();
            public int TeamWipeCount { get; private set; }
            public bool ShouldSucceed { get; set; } = true;
            public int ReviveCallCount { get; private set; }
            public int TryReviveCallCount { get; private set; }

            public void ReviveAtPartner(float x, float y)
            {
                lock (_lock)
                {
                    ReviveCallCount++;
                    ReviveAttempts.Add((x, y, null));
                }
            }

            public bool TryReviveAtPartner(float x, float y, NetworkPeerId? targetPeer)
            {
                lock (_lock)
                {
                    TryReviveCallCount++;
                    if (!ShouldSucceed) return false;
                    ReviveAttempts.Add((x, y, targetPeer));
                    return true;
                }
            }

            public void TriggerTeamWipe()
            {
                lock (_lock)
                {
                    TeamWipeCount++;
                }
            }
        }

        public class MockGameplayActions : IGameplayActionsDouble
        {
            private readonly object _lock = new();
            public List<(string ItemId, ItemCategory Category)> AppliedUnlocks { get; } = new();
            public List<(float X, float Y, NetworkPeerId? TargetPeer)> ReviveAttempts { get; } = new();
            public int TeamWipeCount { get; private set; }
            public bool ShouldSucceed { get; set; } = true;
            public int TryApplyCallCount { get; private set; }
            public int ApplyCallCount { get; private set; }
            public int ReviveCallCount { get; private set; }
            public int TryReviveCallCount { get; private set; }

            public bool TryApplyRemoteItemUnlock(string itemId, ItemCategory category)
            {
                lock (_lock)
                {
                    TryApplyCallCount++;
                    if (!ShouldSucceed) return false;
                    AppliedUnlocks.Add((itemId, category));
                    return true;
                }
            }

            public void ApplyRemoteItemUnlock(string itemId, ItemCategory category)
            {
                lock (_lock)
                {
                    ApplyCallCount++;
                    AppliedUnlocks.Add((itemId, category));
                }
            }

            public void ReviveAtPartner(float x, float y)
            {
                lock (_lock)
                {
                    ReviveCallCount++;
                    ReviveAttempts.Add((x, y, null));
                }
            }

            public bool TryReviveAtPartner(float x, float y, NetworkPeerId? targetPeer)
            {
                lock (_lock)
                {
                    TryReviveCallCount++;
                    if (!ShouldSucceed) return false;
                    ReviveAttempts.Add((x, y, targetPeer));
                    return true;
                }
            }

            public void TriggerTeamWipe()
            {
                lock (_lock)
                {
                    TeamWipeCount++;
                }
            }
        }

        #endregion
    }
}
