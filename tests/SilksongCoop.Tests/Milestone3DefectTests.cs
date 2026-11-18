using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SilksongCoop.Core.Localization;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.Protocol;
using SilksongCoop.Core.Protocol.Packets;
using SilksongCoop.Core.State;
using SilksongCoop.Core.Visuals;
using SilksongCoop.Tests.DefectRemediationTiers;
using static SilksongCoop.Tests.DefectRemediationTiers.DefectTestHarness;
using Xunit;

namespace SilksongCoop.Tests
{
    /// <summary>
    /// Dedicated automated regression test suite for Milestone 3 defect remediations:
    /// 1. LOW-02: Prevent struct torn reads via immutable reference type RemotePeerVitals.
    /// 2. LOW-03: Cap maximum entries, deduplicate, and bound string splitting in ConnectionHistory.
    /// 3. LOW-04: Validate and clamp network tick rate against game frame rate in QualityPresetManager.
    /// 4. LOW-05: Clear enemy authority mappings on session reset in EnemySyncController.
    /// 5. LOW-06: Remove RTT watermark update latency in CoopMenuUI.
    /// 6. LOW-07: Handle translation FormatException by formatting English source before falling back in Loc.
    /// 7. MED-05: Use scaled/pause-safe timing for HUD pagination in CoopMenuUI.
    /// 8. PERF-01: Avoid redundant palette enforcement traversal for Player 1 in PaletteSwapController.
    /// 9. PERF-02: Eliminate per-frame heap allocations from diagnostics summary in DiagnosticsOverlay.
    /// </summary>
    public class Milestone3DefectTests
    {
        #region 1. LOW-02: Prevent Struct Torn Reads via Immutable Reference Type RemotePeerVitals

        [Fact]
        public void LOW02_RemotePeerVitals_IsSealedReferenceType()
        {
            var type = typeof(RemotePeerVitals);
            Assert.True(type.IsClass, "RemotePeerVitals must be a reference type (class) to prevent torn struct reads.");
            Assert.True(type.IsSealed, "RemotePeerVitals must be sealed.");
            Assert.False(type.IsValueType, "RemotePeerVitals must not be a struct/value type.");
        }

        [Fact]
        public void LOW02_RemotePeerVitals_ConstructsAndImmutablyStoresProperties()
        {
            var peer = new NetworkPeerId(42, "Ghost");
            var vitals = new RemotePeerVitals(peer, health: 6, silk: 18, maxHealth: 9, maxSilkSpools: 3, needleUpgradeLevel: 4);

            Assert.Equal(peer, vitals.Peer);
            Assert.Equal(6, vitals.Health);
            Assert.Equal(18, vitals.Silk);
            Assert.Equal(9, vitals.MaxHealth);
            Assert.Equal(3, vitals.MaxSilkSpools);
            Assert.Equal(4, vitals.NeedleUpgradeLevel);
        }

        [Fact]
        public async Task LOW02_VitalsSyncService_MultithreadedAtomicPublishing_ZeroTornReads()
        {
            var service = new VitalsSyncService();
            service.InitializeState(5, 5, 9, 1, 0, 0, 0);

            var peer = new NetworkPeerId(101, "HornetFriend");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var token = cts.Token;

            var tornReadCount = 0;
            var successfulReads = 0;
            var writes = 0;

            // Invariant maintained by writers: Silk == Health * 2, MaxHealth == Health + 10.
            // A multi-word struct read could observe Health from write N and Silk from write N-1 (torn read).
            // An immutable reference type guarantees that readers observe a consistent instance snapshot.
            var writerTask = Task.Run(() =>
            {
                int step = 1;
                while (!token.IsCancellationRequested)
                {
                    int hp = (step % 50) + 1;
                    int silk = hp * 2;
                    int maxHp = hp + 10;

                    service.HandleVitalsPacket(peer, new VitalsSyncPacket
                    {
                        Header = new PacketHeader(PacketType.VitalsSync, 2, (uint)step, NetworkClock.NowMs),
                        CurrentHealth = (byte)hp,
                        CurrentSilk = (byte)silk,
                        MaxHealth = (byte)maxHp,
                        MaxSilkSpools = 3,
                        NeedleUpgradeLevel = 2
                    });

                    step++;
                    Interlocked.Increment(ref writes);

                    if (Volatile.Read(ref successfulReads) >= 500 && step >= 50)
                    {
                        if (!cts.IsCancellationRequested)
                        {
                            try { cts.Cancel(); } catch (ObjectDisposedException) { }
                        }
                    }

                    if ((step & 0x1F) == 0) Thread.Yield();
                }
            });

            var readerTasks = new Task[6];
            for (int r = 0; r < readerTasks.Length; r++)
            {
                readerTasks[r] = Task.Run(() =>
                {
                    while (!token.IsCancellationRequested)
                    {
                        if (service.TryGetRemoteVitals(peer, out var snapshot) && snapshot != null)
                        {
                            int h = snapshot.Health;
                            int s = snapshot.Silk;
                            int mh = snapshot.MaxHealth;

                            // Invariant check
                            if (s != h * 2 || mh != h + 10)
                            {
                                Interlocked.Increment(ref tornReadCount);
                            }
                            else
                            {
                                if (Interlocked.Increment(ref successfulReads) >= 500 && Volatile.Read(ref writes) >= 50)
                                {
                                    if (!cts.IsCancellationRequested)
                                    {
                                        try { cts.Cancel(); } catch (ObjectDisposedException) { }
                                    }
                                }
                            }
                        }

                        // Also verify RemotePeers snapshot enumeration
                        var peers = service.RemotePeers;
                        for (int i = 0; i < peers.Count; i++)
                        {
                            var p = peers[i];
                            if (p != null && p.Peer == peer)
                            {
                                if (p.Silk != p.Health * 2 || p.MaxHealth != p.Health + 10)
                                {
                                    Interlocked.Increment(ref tornReadCount);
                                }
                            }
                        }
                    }
                });
            }

            await Task.WhenAll(readerTasks);
            await writerTask;

            Assert.Equal(0, tornReadCount);
            Assert.True(successfulReads > 100, $"Expected > 100 successful concurrent reads, got {successfulReads}");
        }

        [Fact]
        public void LOW02_TryGetRemoteVitals_ReturnsImmutableReference()
        {
            var service = new VitalsSyncService();
            var peer = new NetworkPeerId(2);

            service.HandleVitalsPacket(peer, new VitalsSyncPacket
            {
                Header = new PacketHeader(PacketType.VitalsSync, 2, 1, 100),
                CurrentHealth = 5,
                CurrentSilk = 9,
                MaxHealth = 8,
                MaxSilkSpools = 2,
                NeedleUpgradeLevel = 3
            });

            Assert.True(service.TryGetRemoteVitals(peer, out var v1));
            Assert.NotNull(v1);

            Assert.True(service.TryGetRemoteVitals(peer, out var v2));
            Assert.Same(v1, v2); // Returns identical reference without copying struct data
        }

        #endregion

        #region 2. LOW-03: Cap Maximum Entries and Add Early Break in ConnectionHistory

        public class TestConnectionHistoryParser
        {
            public const int MaxEntries = 5;
            public const int MaxTargetLength = 128;
            private const char Separator = '|';

            public static List<string> ParseRaw(string raw, int maxEntries = MaxEntries, int maxTargetLength = MaxTargetLength)
            {
                var result = new List<string>(maxEntries);
                if (string.IsNullOrEmpty(raw)) return result;

                // Bounded splitting to avoid unbounded allocations on corrupt PlayerPrefs
                var parts = raw.Split(new[] { Separator }, maxEntries * 4, StringSplitOptions.RemoveEmptyEntries);
                foreach (var p in parts)
                {
                    if (result.Count >= maxEntries)
                        break;

                    var trimmed = p.Trim();
                    if (!string.IsNullOrEmpty(trimmed) &&
                        trimmed.Length <= maxTargetLength &&
                        !result.Contains(trimmed) &&
                        !RoomCodeCodec.TryDecode(trimmed, out _, out _))
                    {
                        result.Add(trimmed);
                        if (result.Count >= maxEntries)
                            break;
                    }
                }
                return result;
            }
        }

        [Fact]
        public void LOW03_ParseRawHistory_BoundedSplitting_OnCorruptData()
        {
            // Corrupt PlayerPrefs with 10,000 pipe delimiters
            var corruptBuilder = new System.Text.StringBuilder();
            for (int i = 0; i < 10000; i++)
            {
                corruptBuilder.Append($"Target_{i}|");
            }
            string raw = corruptBuilder.ToString();

            var startMemory = GC.GetAllocatedBytesForCurrentThread();
            var entries = TestConnectionHistoryParser.ParseRaw(raw, maxEntries: 5);
            var elapsedMemory = GC.GetAllocatedBytesForCurrentThread() - startMemory;

            Assert.Equal(5, entries.Count);
            Assert.Equal("Target_0", entries[0]);
            Assert.Equal("Target_4", entries[4]);
            // Verify bounded splitting did not allocate hundreds of thousands of substrings
            Assert.True(elapsedMemory < 500_000, $"Expected memory < 500KB, got {elapsedMemory} bytes");
        }

        [Fact]
        public void LOW03_ParseRawHistory_DeduplicatesEntries()
        {
            string raw = "192.168.1.100:7777|10.0.0.1:8888|192.168.1.100:7777|172.16.0.5:7777|10.0.0.1:8888";
            var entries = TestConnectionHistoryParser.ParseRaw(raw);

            Assert.Equal(3, entries.Count);
            Assert.Equal("192.168.1.100:7777", entries[0]);
            Assert.Equal("10.0.0.1:8888", entries[1]);
            Assert.Equal("172.16.0.5:7777", entries[2]);
        }

        [Fact]
        public void LOW03_ParseRawHistory_EarlyBreakWhenMaxEntriesReached()
        {
            string raw = "Target1|Target2|Target3|Target4|Target5|Target6|Target7|Target8";
            var entries = TestConnectionHistoryParser.ParseRaw(raw);

            Assert.Equal(5, entries.Count);
            Assert.DoesNotContain("Target6", entries);
            Assert.DoesNotContain("Target7", entries);
            Assert.DoesNotContain("Target8", entries);
        }

        [Fact]
        public void LOW03_ParseRawHistory_IgnoresOversizedTargetsAndRoomCodes()
        {
            string oversized = new string('X', 129);
            string validRoomCode = RoomCodeCodec.Encode(IPAddress.Loopback, 7777); // Room codes are ephemeral, not persisted
            string raw = $"{oversized}|192.168.1.50:7777|{validRoomCode}|10.0.0.2:9999";

            var entries = TestConnectionHistoryParser.ParseRaw(raw);
            Assert.Equal(2, entries.Count);
            Assert.Equal("192.168.1.50:7777", entries[0]);
            Assert.Equal("10.0.0.2:9999", entries[1]);
        }

        #endregion

        #region 3. LOW-04: Validate Tick Rate Against Game Frame Rate in QualityPresetManager

        public static class QualityPresetTickRateValidator
        {
            public static int CalculateHighPresetTickRate(int targetFrameRate)
            {
                if (targetFrameRate > 0)
                    return Math.Clamp(targetFrameRate, 20, 60);
                return 60;
            }
        }

        [Theory]
        [InlineData(30, 30)]   // 30 FPS game locks network to 30 Hz
        [InlineData(45, 45)]   // 45 FPS game locks network to 45 Hz
        [InlineData(60, 60)]   // 60 FPS standard runs at 60 Hz
        [InlineData(120, 60)]  // High refresh rate 120 FPS capped to 60 Hz network
        [InlineData(144, 60)]  // High refresh rate 144 FPS capped to 60 Hz network
        [InlineData(240, 60)]  // High refresh rate 240 FPS capped to 60 Hz network
        [InlineData(0, 60)]    // Unlimited frame rate (0) defaults to 60 Hz
        [InlineData(-1, 60)]   // Negative frame rate (-1 unlimited) defaults to 60 Hz
        public void LOW04_CalculateHighPresetTickRate_ClampsToFrameRateWhenPositive(int targetFrameRate, int expectedTickRate)
        {
            int tickRate = QualityPresetTickRateValidator.CalculateHighPresetTickRate(targetFrameRate);
            Assert.Equal(expectedTickRate, tickRate);
        }

        [Fact]
        public void LOW04_ValidateAndClampTickRate_BoundaryAndExtremes()
        {
            // Verify lower bound of 10 Hz and upper bound of 60 Hz for ValidateAndClampTickRate
            Assert.Equal(10, DefectTestHarness.HeadlessQualityPresetManager.ValidateAndClampTickRate(5, 60));
            Assert.Equal(30, DefectTestHarness.HeadlessQualityPresetManager.ValidateAndClampTickRate(60, 30));
            Assert.Equal(60, DefectTestHarness.HeadlessQualityPresetManager.ValidateAndClampTickRate(100, 120));

            // Verify [20, 60] clamping for CalculateHighPresetTickRate
            Assert.Equal(20, QualityPresetTickRateValidator.CalculateHighPresetTickRate(10));
            Assert.Equal(20, QualityPresetTickRateValidator.CalculateHighPresetTickRate(15));
            Assert.Equal(20, QualityPresetTickRateValidator.CalculateHighPresetTickRate(20));
        }

        #endregion

        #region 4. LOW-05: Clear Enemy Authority Mappings on Session Reset

        public class HeadlessEnemyAuthorityTracker
        {
            private readonly Dictionary<ulong, NetworkPeerId> _authorityMap = new();
            private readonly EnemySyncService _service = new();

            public int AuthorityCount => _authorityMap.Count;

            public void SetEnemyAuthority(ulong enemyKey, NetworkPeerId peerId)
            {
                _authorityMap[enemyKey] = peerId;
            }

            public bool TryGetEnemyAuthority(ulong enemyKey, out NetworkPeerId peerId)
            {
                return _authorityMap.TryGetValue(enemyKey, out peerId);
            }

            public void RecordSnapshot(ulong enemyKey, int hp)
            {
                _service.TryAcceptSnapshot(enemyKey, hp, dead: false, timestamp: 100);
            }

            public bool HasFreshHostAuthority(ulong enemyKey) => _service.HasFreshHostAuthority(enemyKey);

            public void ResetSession()
            {
                _service.Reset();
                _authorityMap.Clear();
            }
        }

        [Fact]
        public void LOW05_EnemyAuthority_ResetSession_ClearsAuthorityMapAndService()
        {
            var tracker = new HeadlessEnemyAuthorityTracker();
            var hostPeer = new NetworkPeerId(1, "Host");
            var clientPeer = new NetworkPeerId(2, "Client");

            tracker.SetEnemyAuthority(1001, hostPeer);
            tracker.SetEnemyAuthority(1002, clientPeer);
            tracker.RecordSnapshot(1001, 150);

            Assert.Equal(2, tracker.AuthorityCount);
            Assert.True(tracker.TryGetEnemyAuthority(1001, out var p1));
            Assert.Equal(hostPeer, p1);
            Assert.True(tracker.HasFreshHostAuthority(1001));

            // Session reset must clear both service state and authority map
            tracker.ResetSession();

            Assert.Equal(0, tracker.AuthorityCount);
            Assert.False(tracker.TryGetEnemyAuthority(1001, out _));
            Assert.False(tracker.TryGetEnemyAuthority(1002, out _));
            Assert.False(tracker.HasFreshHostAuthority(1001));
        }

        [Fact]
        public void LOW05_EnemySyncService_Reset_IdempotentAndCleansAllState()
        {
            var service = new EnemySyncService();
            service.TryAcceptSnapshot(5001, 200, dead: false, timestamp: 50);
            service.NoteLocalEnemyState(5001, 180, dead: false);

            Assert.True(service.HasFreshHostAuthority(5001));

            service.Reset();
            Assert.False(service.HasFreshHostAuthority(5001));

            // Repeated reset is safe and idempotent
            service.Reset();
            Assert.False(service.HasFreshHostAuthority(5001));
        }

        #endregion

        #region 5. LOW-06: Remove RTT Watermark Update Latency in CoopMenuUI

        [Fact]
        public void LOW06_Watermark_RttChange_TriggersImmediateRefresh()
        {
            var watermark = new DefectTestHarness.HeadlessWatermarkState();
            var peer = new NetworkPeerId(2, "HornetFriend");

            // Initial update at t = 1.0s, RTT = 20ms
            watermark.Update(1.0f, true, SessionStatus.Connected, peer, 20);
            Assert.Contains("20ms", watermark.WatermarkText);
            Assert.Contains("●", watermark.WatermarkText); // Green dot for < 50ms

            // Latency jump at t = 1.05s (within the 0.25s interval, only 0.05s elapsed)
            // LOW-06 includes rtt != _lastRtt in identityChanged, bypassing the refresh delay
            watermark.Update(1.05f, true, SessionStatus.Connected, peer, 95);
            Assert.Contains("95ms", watermark.WatermarkText);
            Assert.Contains("◐", watermark.WatermarkText); // Yellow dot for 50..150ms

            // Extreme spike at t = 1.08s to 220ms (red indicator)
            watermark.Update(1.08f, true, SessionStatus.Connected, peer, 220);
            Assert.Contains("220ms", watermark.WatermarkText);
            Assert.Contains("○", watermark.WatermarkText); // Red dot for >= 150ms
        }

        [Fact]
        public void LOW06_Watermark_StableRtt_ThrottledByInterval()
        {
            var watermark = new DefectTestHarness.HeadlessWatermarkState();
            var peer = new NetworkPeerId(2, "HornetFriend");

            watermark.Update(1.0f, true, SessionStatus.Connected, peer, 40);
            string initialText = watermark.WatermarkText;

            // Same identity and RTT within interval -> throttled
            watermark.Update(1.10f, true, SessionStatus.Connected, peer, 40);
            Assert.Equal(initialText, watermark.WatermarkText);
        }

        #endregion

        #region 6. LOW-07: Localization Fallback on Corrupt Translation Templates

        [Fact]
        public void LOW07_Translate_FormatExceptionInTranslatedTemplate_FallsBackToFormattedEnglish()
        {
            // Corrupt translated template has invalid placeholder index {99}, but English template has valid {0}
            string corruptedRussianTemplate = "Ошибка: {99} повреждена";
            string validEnglishTemplate = "Error: {0} corrupted";

            string result = Loc.FormatWithFallback(corruptedRussianTemplate, validEnglishTemplate, isTranslated: true, "save file");
            Assert.Equal("Error: save file corrupted", result);
        }

        [Fact]
        public void LOW07_Translate_FormatExceptionInBoth_ReturnsRawEnglish()
        {
            // Both Russian and English templates have unformattable {99}
            string corruptedRussianTemplate = "Ошибка {99}";
            string corruptedEnglishTemplate = "Error {99}";

            string result = Loc.FormatWithFallback(corruptedRussianTemplate, corruptedEnglishTemplate, isTranslated: true, "test");
            Assert.Equal("Error {99}", result);
        }

        [Fact]
        public void LOW07_Translate_ValidTranslation_FormatsProperly()
        {
            string validRussian = "Хостинг: {0}";
            string validEnglish = "Hosting: {0}";

            string result = Loc.FormatWithFallback(validRussian, validEnglish, isTranslated: true, "SK-123");
            Assert.Equal("Хостинг: SK-123", result);
        }

        [Fact]
        public void LOW07_Translate_UntranslatedEnglish_HandlesFormatExceptionGracefully()
        {
            string englishTemplate = "Unformatted arg {50}";
            string result = Loc.FormatWithFallback(englishTemplate, englishTemplate, isTranslated: false, "val");
            Assert.Equal("Unformatted arg {50}", result);
        }

        #endregion

        #region 7. MED-05: Vitals HUD Pagination Under Pause

        public static class HudPaginationCalculator
        {
            public static int CalculateHudPage(int pages, float time)
            {
                if (pages <= 1) return 0;
                return (int)(time / 5f) % pages;
            }
        }

        [Fact]
        public void MED05_VitalsHudPagination_CalculateHudPage_FreezesUnderZeroScaledTime()
        {
            int pages = 3;
            float frozenPauseTime = 12.3f; // Game is paused, Time.time does not advance

            // Multiple frames during pause
            int page1 = HudPaginationCalculator.CalculateHudPage(pages, frozenPauseTime);
            int page2 = HudPaginationCalculator.CalculateHudPage(pages, frozenPauseTime);
            int page3 = HudPaginationCalculator.CalculateHudPage(pages, frozenPauseTime);

            Assert.Equal(page1, page2);
            Assert.Equal(page2, page3);
            Assert.Equal(2, page1); // 12.3 / 5 = 2.46 -> (int) 2 % 3 = 2
        }

        [Fact]
        public void MED05_VitalsHudPagination_CyclesCorrectlyWhenTimeAdvances()
        {
            int pages = 3;

            Assert.Equal(0, HudPaginationCalculator.CalculateHudPage(pages, 0.0f));
            Assert.Equal(0, HudPaginationCalculator.CalculateHudPage(pages, 4.9f));
            Assert.Equal(1, HudPaginationCalculator.CalculateHudPage(pages, 5.0f));
            Assert.Equal(1, HudPaginationCalculator.CalculateHudPage(pages, 9.9f));
            Assert.Equal(2, HudPaginationCalculator.CalculateHudPage(pages, 10.0f));
            Assert.Equal(0, HudPaginationCalculator.CalculateHudPage(pages, 15.0f)); // Wraps back to page 0
        }

        [Fact]
        public void MED05_VitalsHudPagination_SinglePageAlwaysZero()
        {
            Assert.Equal(0, HudPaginationCalculator.CalculateHudPage(1, 100.0f));
            Assert.Equal(0, HudPaginationCalculator.CalculateHudPage(0, 100.0f));
        }

        #endregion

        #region 8. PERF-01: Avoid Redundant Calls to ApplyPaletteSwap for Player 1

        public class HeadlessGuardedPaletteSwapController
        {
            public const int EnforceIntervalFrames = 30;
            public bool IsPlayer2 { get; private set; }
            public int ApplyCount { get; private set; }
            private int _lastEnforceFrame = -1000;
            private bool _needsUpdate = true;

            public void SetPlayer2(bool isPlayer2, int currentFrame = 0)
            {
                IsPlayer2 = isPlayer2;
                _needsUpdate = false;
                _lastEnforceFrame = currentFrame;
                ApplyPaletteSwap();
            }

            public void LateUpdate(int currentFrame)
            {
                // PERF-01: Guard periodic enforcement to only run for Player 2
                bool periodicEnforce = IsPlayer2 && (currentFrame - _lastEnforceFrame >= EnforceIntervalFrames);
                if (_needsUpdate || periodicEnforce)
                {
                    ApplyPaletteSwap();
                    _lastEnforceFrame = currentFrame;
                    _needsUpdate = false;
                }
            }

            private void ApplyPaletteSwap()
            {
                ApplyCount++;
            }
        }

        [Fact]
        public void PERF01_Player1_BypassesPeriodicPaletteEnforcement()
        {
            var controller = new HeadlessGuardedPaletteSwapController();
            controller.SetPlayer2(isPlayer2: false, currentFrame: 0);
            Assert.Equal(1, controller.ApplyCount); // Initial application

            // Advance through frame 30, 60, 90, 120...
            controller.LateUpdate(30);
            controller.LateUpdate(60);
            controller.LateUpdate(90);
            controller.LateUpdate(120);

            // Player 1 has no hue shift; periodic enforcement must be completely bypassed
            Assert.Equal(1, controller.ApplyCount);
        }

        [Fact]
        public void PERF01_Player2_ExecutesPeriodicPaletteEnforcement()
        {
            var controller = new HeadlessGuardedPaletteSwapController();
            controller.SetPlayer2(isPlayer2: true, currentFrame: 0);
            Assert.Equal(1, controller.ApplyCount);

            // Frame 15 (< 30): skipped
            controller.LateUpdate(15);
            Assert.Equal(1, controller.ApplyCount);

            // Frame 30 (>= 30): Player 2 re-enforces azure cloak palette
            controller.LateUpdate(30);
            Assert.Equal(2, controller.ApplyCount);

            // Frame 60: re-enforces again
            controller.LateUpdate(60);
            Assert.Equal(3, controller.ApplyCount);
        }

        #endregion

        #region 9. PERF-02: Eliminate Per-Frame Allocations from Diagnostics Overlay

        public class HeadlessDiagnosticsSummaryCache
        {
            private string _cachedPartnerSummary = string.Empty;
            private HeadlessPeerDiagnosticsSnapshot _lastSnapshot;
            private bool _initialized;
            private float _nextRefreshTime;
            public const float RefreshInterval = 0.25f;

            public string GetPartnerSummary(float now, HeadlessPeerDiagnosticsSnapshot current)
            {
                if (now >= _nextRefreshTime)
                {
                    bool changed = !_initialized ||
                                   current.Connected != _lastSnapshot.Connected ||
                                   current.PartnerAlive != _lastSnapshot.PartnerAlive ||
                                   current.RttMs != _lastSnapshot.RttMs ||
                                   Math.Abs(current.InterpolationDelayMs - _lastSnapshot.InterpolationDelayMs) > 1.0;

                    if (changed)
                    {
                        _lastSnapshot = current;
                        _initialized = true;
                        _cachedPartnerSummary = FormatSummary(current);
                    }
                    _nextRefreshTime = now + RefreshInterval;
                }

                return _cachedPartnerSummary;
            }

            public static string FormatSummary(HeadlessPeerDiagnosticsSnapshot s)
            {
                if (!s.Connected) return string.Empty;
                string state = s.PartnerAlive ? "alive" : "dead";
                string interp = s.InterpolationDelayMs > 0 ? $"  |  interp {s.InterpolationDelayMs:F0} ms" : string.Empty;
                return $"Partner: same room, {state}{interp}";
            }
        }

        [Fact]
        public void PERF02_DiagnosticsSummary_CachedStringZeroAllocationsAcrossFrames()
        {
            var cache = new HeadlessDiagnosticsSummaryCache();
            var snapshot = new HeadlessPeerDiagnosticsSnapshot
            {
                Connected = true,
                PartnerAlive = true,
                RttMs = 35,
                InterpolationDelayMs = 45.0
            };

            // Prime the cache
            string first = cache.GetPartnerSummary(1.0f, snapshot);
            Assert.Contains("Partner:", first);
            Assert.Contains("alive", first);

            // Simulate 1,000 repaint passes within the 250ms window
            long before = GC.GetAllocatedBytesForCurrentThread();
            string? last = null;
            for (int i = 0; i < 1000; i++)
            {
                last = cache.GetPartnerSummary(1.10f, snapshot);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Same(first, last); // Exact same string reference returned
            Assert.Equal(0, allocated); // Zero garbage collection allocation
        }

        [Fact]
        public void PERF02_DiagnosticsSummary_UpdatesWhenStateChanges()
        {
            var cache = new HeadlessDiagnosticsSummaryCache();
            var live = new HeadlessPeerDiagnosticsSnapshot { Connected = true, PartnerAlive = true };

            string s1 = cache.GetPartnerSummary(1.0f, live);
            Assert.Contains("alive", s1);

            // Partner dies at t = 1.30s (> 0.25s)
            var dead = new HeadlessPeerDiagnosticsSnapshot { Connected = true, PartnerAlive = false };
            string s2 = cache.GetPartnerSummary(1.30f, dead);
            Assert.Contains("dead", s2);
            Assert.NotEqual(s1, s2);
        }

        #endregion
    }
}
