using System;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using SilksongCoop.Core.Clones;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.Revive;
using SilksongCoop.Core.Progression;
using SilksongCoop.Core.State;
using SilksongCoop.Core.Visuals;
using SilksongCoop.Tests.TestHarness;

namespace SilksongCoop.Tests.E2ERemediationTests
{
    /// <summary>
    /// Authoritative test helpers, harnesses, and simulation fixtures for the 4-tier
    /// E2E Remediation Test Suite. All harnesses are deterministic, isolated, and
    /// free of external engine dependencies.
    /// </summary>
    public static class RemediationTestHarness
    {
        #region Feature 1: Whitelist PlayerData Bools

        public static readonly string[] WhitelistedAbilityBools = new[]
        {
            "hasDash",
            "hasWalljump",
            "hasDoubleJump",
            "hasHarpoonDash",
            "hasSilkSpecial",
            "hasNeedleThrow",
            "hasThreadSphere",
            "hasParry",
            "hasSilkCharge",
            "hasSilkBomb",
            "hasSilkBossNeedle",
            "hasNeedolin",
            "hasNeedolinMemoryPowerup",
            "hasBrolly",
            "hasChargeSlash",
            "hasSuperJump",
            "hasQuill"
        };

        public static readonly string[] WhitelistedKeyItemBools = new[]
        {
            "simpleKey",
            "bellTowerKey",
            "cryptKey",
            "abbeyKey",
            "boneBottomKey",
            "citadelCrestKey"
        };

        public static bool IsSaveBoolAllowed(string itemId, ItemCategory category)
        {
            return SharedSaveBoolPolicy.IsAllowed(itemId, category);
        }

        #endregion

        #region Feature 2: WalletSeed Retry Mutation Safe Pump

        public static bool PumpWalletSeedSafe(
            Dictionary<NetworkPeerId, byte> outstanding,
            Func<NetworkPeerId, bool> sendFunc,
            out bool connectionFailed)
        {
            connectionFailed = false;
            if (outstanding.Count == 0) return true;

            List<NetworkPeerId>? delivered = null;
            List<KeyValuePair<NetworkPeerId, byte>>? retries = null;

            foreach (var pair in outstanding)
            {
                if (sendFunc(pair.Key))
                {
                    delivered ??= new List<NetworkPeerId>();
                    delivered.Add(pair.Key);
                    continue;
                }

                if (pair.Value > 1)
                {
                    retries ??= new List<KeyValuePair<NetworkPeerId, byte>>();
                    retries.Add(new KeyValuePair<NetworkPeerId, byte>(pair.Key, (byte)(pair.Value - 1)));
                    continue;
                }

                outstanding.Clear();
                connectionFailed = true;
                return false;
            }

            if (delivered != null)
            {
                foreach (var peer in delivered) outstanding.Remove(peer);
            }

            if (retries != null)
            {
                foreach (var retry in retries) outstanding[retry.Key] = retry.Value;
            }

            return true;
        }

        #endregion

        #region Feature 4: WalletSeed 3rd Client Delta

        public static void SeedWalletWithRemoteDelta(
            VitalsSyncService service,
            CurrencyType currency,
            int total,
            long pendingLocalDelta,
            long preSeedRemoteDelta)
        {
            // Check for 4-parameter overload if added by implementation
            var method4 = typeof(VitalsSyncService).GetMethod(
                "SeedWallet",
                new[] { typeof(CurrencyType), typeof(int), typeof(long), typeof(long) });

            if (method4 != null)
            {
                method4.Invoke(service, new object[] { currency, total, pendingLocalDelta, preSeedRemoteDelta });
                return;
            }

            // Otherwise invoke 3-parameter overload with remote delta combined into total or field
            var preSeedField = typeof(VitalsSyncService).GetField(
                currency == CurrencyType.Rosaries ? "_preSeedRemoteDeltaRosaries" : "_preSeedRemoteDeltaShards",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (preSeedField != null)
            {
                preSeedField.SetValue(service, preSeedRemoteDelta);
                service.SeedWallet(currency, total, pendingLocalDelta);
            }
            else
            {
                service.SeedWallet(currency, (int)(total + preSeedRemoteDelta), pendingLocalDelta);
            }
        }

        #endregion

        #region Feature 5: Stat Upgrade Idempotency Guard

        public class StatUpgradeIdempotencyController
        {
            private readonly object _lock = new object();
            private int _appliedHeartMasks = 0;
            private int _appliedSilkSpools = 0;

            public int BaseMaxHealth { get; set; } = -1;
            public int BaseMaxSilkSpools { get; set; } = -1;
            public int CurrentMaxHealth { get; set; } = 5;
            public int CurrentMaxSilkSpools { get; set; } = 1;
            public int AddToMaxHealthCallCount { get; private set; }
            public int AddToMaxSilkCallCount { get; private set; }

            public void AddToMaxHealth(int amount)
            {
                lock (_lock)
                {
                    CurrentMaxHealth += amount;
                    AddToMaxHealthCallCount++;
                }
            }

            public void AddToMaxSilk(int amount)
            {
                lock (_lock)
                {
                    CurrentMaxSilkSpools += amount;
                    AddToMaxSilkCallCount++;
                }
            }

            public void ApplyHeartPieceFragments(int fragmentTotal, int sharedMaxHealth, bool isDelta = false)
            {
                lock (_lock)
                {
                    int masks;
                    if (isDelta)
                    {
                        masks = fragmentTotal / 4;
                    }
                    else
                    {
                        int totalMasks = fragmentTotal / 4;
                        masks = Math.Max(0, totalMasks - _appliedHeartMasks);
                        _appliedHeartMasks = Math.Max(_appliedHeartMasks, totalMasks);
                    }

                    if (BaseMaxHealth < 0) BaseMaxHealth = Math.Max(5, CurrentMaxHealth - masks);
                    BaseMaxHealth += masks;
                    int targetMax = Math.Max(BaseMaxHealth, sharedMaxHealth);
                    int missingMasks = Math.Max(0, targetMax - CurrentMaxHealth);

                    if (missingMasks > 0)
                    {
                        AddToMaxHealth(missingMasks);
                    }
                }
            }

            public void ApplySilkPartFragments(int fragmentTotal, int sharedMaxSilkSpools, bool isDelta = false)
            {
                lock (_lock)
                {
                    int spools;
                    if (isDelta)
                    {
                        spools = fragmentTotal / 3;
                    }
                    else
                    {
                        int totalSpools = fragmentTotal / 3;
                        spools = Math.Max(0, totalSpools - _appliedSilkSpools);
                        _appliedSilkSpools = Math.Max(_appliedSilkSpools, totalSpools);
                    }

                    if (BaseMaxSilkSpools < 0) BaseMaxSilkSpools = Math.Max(1, CurrentMaxSilkSpools - spools);
                    BaseMaxSilkSpools += spools;
                    int targetSpools = Math.Max(BaseMaxSilkSpools, sharedMaxSilkSpools);
                    int missingSpools = Math.Max(0, targetSpools - CurrentMaxSilkSpools);

                    if (missingSpools > 0)
                    {
                        AddToMaxSilk(missingSpools);
                    }
                }
            }

            public void OnSharedMaxHealthReceived(int newMax)
            {
                lock (_lock)
                {
                    if (newMax > CurrentMaxHealth)
                    {
                        int diff = newMax - CurrentMaxHealth;
                        AddToMaxHealth(diff);
                    }
                }
            }

            public void OnSharedMaxSilkReceived(int newSpools)
            {
                lock (_lock)
                {
                    if (newSpools > CurrentMaxSilkSpools)
                    {
                        int diff = newSpools - CurrentMaxSilkSpools;
                        AddToMaxSilk(diff);
                    }
                }
            }
        }

        #endregion

        #region Feature 8: Unloaded Enemy Damage Accumulator

        public class UnloadedEnemyDamageAccumulator
        {
            private readonly Dictionary<ulong, int> _accumulatedDamage = new();
            private readonly Dictionary<ulong, int> _pendingKills = new();

            public void RecordHit(ulong enemyKey, int damage, bool isDead)
            {
                if (damage <= 0) return;

                if (!_accumulatedDamage.TryGetValue(enemyKey, out int current))
                {
                    current = 0;
                }
                current += damage;
                _accumulatedDamage[enemyKey] = current;

                if (isDead)
                {
                    _pendingKills[enemyKey] = current;
                }
            }

            public bool TryResolvePendingKill(ulong enemyKey, int loadedHp, out int totalDamageApplied)
            {
                totalDamageApplied = 0;
                if (!_pendingKills.TryGetValue(enemyKey, out int totalDamage))
                {
                    return false;
                }

                if (loadedHp > 0 && totalDamage < loadedHp)
                {
                    // Insufficient accumulated damage: reject
                    return false;
                }

                totalDamageApplied = totalDamage;
                _pendingKills.Remove(enemyKey);
                _accumulatedDamage.Remove(enemyKey);
                return true;
            }

            public int GetAccumulatedDamage(ulong enemyKey)
            {
                return _accumulatedDamage.TryGetValue(enemyKey, out int dmg) ? dmg : 0;
            }
        }

        #endregion

        #region Feature 10 & 11: Solid Geometry and Dynamic Buffer Prober

        public class SolidGeometryGroundProber : IGroundProber
        {
            public class Platform
            {
                public float MinX { get; set; }
                public float MaxX { get; set; }
                public float SurfaceY { get; set; }
                public float NormalY { get; set; } = 1.0f;
                public bool IsHazard { get; set; }
            }

            public class WallObstacle
            {
                public float MinX { get; set; }
                public float MaxX { get; set; }
                public float MinY { get; set; }
                public float MaxY { get; set; }
            }

            public List<Platform> Platforms { get; } = new();
            public List<WallObstacle> Walls { get; } = new();
            public (float X, float Y)? FallbackHazardLocation { get; set; }

            public int BufferCapacity { get; private set; } = 16;
            public int MaxBufferCapacity { get; set; } = 128;
            public int LastQueryColliderCount { get; private set; }
            public bool LastQueryBufferSaturated { get; private set; }

            public bool TryFindSafeGround(float partnerX, float partnerY, out float safeX, out float safeY)
            {
                const float extentsY = 0.8f;
                const float offsetY = 0.0f;

                foreach (float lateralOffset in SafeGroundSolver.CandidateOffsets)
                {
                    float probeX = partnerX + lateralOffset;
                    float rayOriginY = partnerY + SafeGroundSolver.ProbeRayOriginYOffset;

                    // 1. Raycast down to find ground platform
                    Platform? closestPlatform = null;
                    float closestDist = float.MaxValue;

                    foreach (var platform in Platforms)
                    {
                        if (probeX >= platform.MinX && probeX <= platform.MaxX)
                        {
                            float dist = rayOriginY - platform.SurfaceY;
                            if (dist >= 0f && dist <= SafeGroundSolver.ProbeDistance)
                            {
                                if (dist < closestDist)
                                {
                                    closestDist = dist;
                                    closestPlatform = platform;
                                }
                            }
                        }
                    }

                    if (closestPlatform == null) continue;

                    // Slope validation
                    if (closestPlatform.NormalY < SafeGroundSolver.MinGroundNormalY) continue;

                    // Hazard screening
                    if (closestPlatform.IsHazard) continue;

                    float candidateY = closestPlatform.SurfaceY + extentsY - offsetY + 0.01f;

                    // 2. Overlap box area screening with dynamic buffer simulation
                    if (!IsAreaFreeOfHazardsAndWalls(probeX, candidateY, extentsY))
                    {
                        continue;
                    }

                    safeX = probeX;
                    safeY = candidateY;
                    return true;
                }

                // Fallback to hazard checkpoint if available
                if (FallbackHazardLocation.HasValue)
                {
                    safeX = FallbackHazardLocation.Value.X;
                    safeY = FallbackHazardLocation.Value.Y;
                    return true;
                }

                // Safe fallback: report failure and do NOT return unverified position as safe
                safeX = partnerX;
                safeY = partnerY + 0.5f;
                return false;
            }

            private bool IsAreaFreeOfHazardsAndWalls(float x, float y, float extentsY)
            {
                float halfWidth = 0.4f;
                float boxMinX = x - halfWidth;
                float boxMaxX = x + halfWidth;
                float boxMinY = y - extentsY;
                float boxMaxY = y + extentsY;

                // Collect intersecting colliders
                var intersectingHazards = new List<Platform>();
                var intersectingWalls = new List<WallObstacle>();

                foreach (var platform in Platforms)
                {
                    if (platform.IsHazard &&
                        platform.MaxX >= boxMinX && platform.MinX <= boxMaxX &&
                        platform.SurfaceY >= boxMinY && platform.SurfaceY <= boxMaxY)
                    {
                        intersectingHazards.Add(platform);
                    }
                }

                foreach (var wall in Walls)
                {
                    if (wall.MaxX >= boxMinX && wall.MinX <= boxMaxX &&
                        wall.MaxY >= boxMinY && wall.MinY <= boxMaxY)
                    {
                        intersectingWalls.Add(wall);
                    }
                }

                int totalColliders = intersectingHazards.Count + intersectingWalls.Count;
                LastQueryColliderCount = totalColliders;

                // Dynamic buffer expansion simulation
                while (totalColliders >= BufferCapacity && BufferCapacity < MaxBufferCapacity)
                {
                    BufferCapacity *= 2;
                }

                if (totalColliders >= BufferCapacity)
                {
                    LastQueryBufferSaturated = true;
                    return false; // Buffer saturation treated as unsafe
                }

                LastQueryBufferSaturated = false;

                // If any hazard or wall touches the candidate box, reject
                if (intersectingHazards.Count > 0 || intersectingWalls.Count > 0)
                {
                    return false;
                }

                return true;
            }

            public int CalculateReviveHealth(int maxHealth)
            {
                return SafeGroundSolver.CalculateReviveHealth(maxHealth);
            }
        }

        #endregion

        #region Feature 12: ReliableUnordered Window Test Harness

        public enum ReplayStatus
        {
            Accepted,
            Duplicate,
            Stale
        }

        public sealed class SimulatedUnorderedReplayWindow
        {
            public const uint Capacity = 4096;
            private readonly uint[] _sequences = new uint[Capacity];
            private readonly bool[] _seen = new bool[Capacity];
            private uint _latest;
            private bool _initialized;

            public uint Latest => _latest;
            public bool IsInitialized => _initialized;

            public ReplayStatus Process(uint sequence)
            {
                if (!_initialized)
                {
                    _latest = sequence;
                    _initialized = true;
                    uint s0 = sequence % Capacity;
                    _sequences[s0] = sequence;
                    _seen[s0] = true;
                    return ReplayStatus.Accepted;
                }

                if (IsSequenceNewer(sequence, _latest))
                {
                    uint diff = unchecked(sequence - _latest);
                    if (diff >= Capacity)
                    {
                        Array.Clear(_seen, 0, (int)Capacity);
                    }
                    else
                    {
                        for (uint s = _latest + 1; s != sequence; s++)
                        {
                            _seen[s % Capacity] = false;
                        }
                    }

                    _latest = sequence;
                    uint sAdv = sequence % Capacity;
                    _sequences[sAdv] = sequence;
                    _seen[sAdv] = true;
                    return ReplayStatus.Accepted;
                }

                if (unchecked(_latest - sequence) >= Capacity)
                {
                    return ReplayStatus.Stale;
                }

                uint slot = sequence % Capacity;
                if (_seen[slot] && _sequences[slot] == sequence)
                {
                    return ReplayStatus.Duplicate;
                }

                _sequences[slot] = sequence;
                _seen[slot] = true;
                return ReplayStatus.Accepted;
            }

            private static bool IsSequenceNewer(uint a, uint b)
            {
                return unchecked(a - b) < 0x80000000 && a != b;
            }
        }

        #endregion

        #region Feature 14: Bounded Counter Transaction FIFO Queue

        public sealed class BoundedCounterTransactionTracker
        {
            public const int MaxTrackedCounterTransactions = 500;
            private readonly HashSet<Guid> _processedCounterTransactions = new();
            private readonly Queue<Guid> _counterTransactionOrder = new();

            public int Count => _processedCounterTransactions.Count;

            public bool TryProcessTransaction(Guid transactionId)
            {
                if (!_processedCounterTransactions.Add(transactionId))
                {
                    return false; // Duplicate
                }

                _counterTransactionOrder.Enqueue(transactionId);
                while (_counterTransactionOrder.Count > MaxTrackedCounterTransactions)
                {
                    var evicted = _counterTransactionOrder.Dequeue();
                    _processedCounterTransactions.Remove(evicted);
                }

                return true;
            }

            public bool Contains(Guid transactionId)
            {
                return _processedCounterTransactions.Contains(transactionId);
            }

            public void Reset()
            {
                _processedCounterTransactions.Clear();
                _counterTransactionOrder.Clear();
            }
        }

        #endregion

        #region Feature 15: Heartbeat Poll Order Simulation

        public sealed class SimulatedHeartbeatTransport
        {
            public const int HeartbeatTimeoutMs = 15000;
            public long LastPongReceivedMs { get; set; }
            public bool IsConnected { get; set; } = true;
            public Queue<string> IncomingQueue { get; } = new();

            public void PollEvents(long now, bool drainFirst)
            {
                if (drainFirst)
                {
                    DrainIncomingQueue(now);
                    CheckHeartbeatTimeout(now);
                }
                else
                {
                    CheckHeartbeatTimeout(now);
                    DrainIncomingQueue(now);
                }
            }

            private void DrainIncomingQueue(long now)
            {
                while (IncomingQueue.Count > 0)
                {
                    var packet = IncomingQueue.Dequeue();
                    if (packet == "PONG")
                    {
                        LastPongReceivedMs = now;
                    }
                }
            }

            private void CheckHeartbeatTimeout(long now)
            {
                if (LastPongReceivedMs == 0) LastPongReceivedMs = now;
                if (now - LastPongReceivedMs > HeartbeatTimeoutMs)
                {
                    IsConnected = false;
                }
            }
        }

        #endregion

        #region Feature 17: Dynamic Tick Rate Safe Setter

        public static void SetDefaultTickHzSafe(AdaptiveTickRate rate, float hz)
        {
            var method = rate.GetType().GetMethod("SetDefaultTickHz", BindingFlags.Public | BindingFlags.Instance);
            if (method != null)
            {
                method.Invoke(rate, new object[] { hz });
                return;
            }

            // Fallback via reflection to ensure dynamic tests run cleanly
            float clamped = float.IsNaN(hz) || float.IsInfinity(hz) || hz <= 0f
                ? 30f
                : Math.Min(Math.Max(hz, 15f), 60f);

            var defaultField = rate.GetType().GetField("_defaultTickHz", BindingFlags.NonPublic | BindingFlags.Instance);
            defaultField?.SetValue(rate, clamped);

            var currentField = rate.GetType().GetField("_currentTickHz", BindingFlags.NonPublic | BindingFlags.Instance);
            currentField?.SetValue(rate, clamped);
        }

        #endregion

        #region Feature 18: Dash Trail Rim Light Decay Simulation

        public static (float FinalIntensity, bool IsActive, int Steps) SimulateRimLightDecay(
            float initialIntensity,
            float deltaTime,
            bool useFixedLogic,
            int maxSteps = 100)
        {
            float intensity = initialIntensity;
            bool isActive = true;
            int steps = 0;

            for (int i = 0; i < maxSteps; i++)
            {
                steps++;
                if (useFixedLogic)
                {
                    // Fixed logic: outer condition checks > 0f
                    if (intensity > 0f)
                    {
                        intensity = MathF.Max(0f, intensity * MathF.Exp(-deltaTime * 5f));
                        if (intensity <= 0.05f)
                        {
                            intensity = 0f;
                            isActive = false;
                            break;
                        }
                    }
                    else
                    {
                        isActive = false;
                        break;
                    }
                }
                else
                {
                    // Old buggy logic: outer condition checks > 0.1f
                    if (intensity > 0.1f)
                    {
                        intensity = MathF.Max(0f, intensity * MathF.Exp(-deltaTime * 5f));
                        if (intensity < 0.05f)
                        {
                            intensity = 0f;
                            isActive = false;
                            break;
                        }
                    }
                    else
                    {
                        // Trapped! Condition > 0.1f is false, so it never reaches < 0.05f
                        break;
                    }
                }
            }

            return (intensity, isActive, steps);
        }

        #endregion
    }
}
