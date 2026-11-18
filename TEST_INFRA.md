# TEST_INFRA.md — E2E Test Suite Architecture & Infrastructure Specification
**Project**: Hollow Knight: Silksong Co-op Mod (`SilksongCoop`)  
**Specification Version**: 2.0.0  
**Target Runtime**: .NET 8.0 (`net8.0`), C# 12  
**Test Framework**: xUnit 2.9.2 (`Microsoft.NET.Test.Sdk` 17.11.1)  
**Execution Environment**: Headless CI / Deterministic Virtual Simulation  

---

## 1. Test Philosophy

The `SilksongCoop` automated test suite enforces an **opaque-box, requirement-driven, deterministic test philosophy**. The system treats the mod's assemblies as black-box or opaque-box components: verification is governed strictly by behavioral contracts, protocol specifications, state invariants, and public interfaces, rather than internal implementation details.

### Four-Tier Testing Methodology
The suite is structured into four complementary verification tiers:

1. **Tier 1: Category-Partition Functional Coverage**  
   - Every feature from `ORIGINAL_REQUEST.md` is partitioned into distinct input/state equivalence classes and happy paths.
   - **Threshold**: At least **5 isolated unit/regression tests per feature** ($\ge 5 \times 25 = 125$ tests).
   - **Focus**: Verifies primary functional behavior, correct state transitions, and contract adherence under nominal conditions.

2. **Tier 2: Boundary Value Analysis (BVA) & Error Handling**  
   - Exercises boundary limits, buffer limits, arithmetic overflow/wraparound, concurrency contention, malformed packets, and network loss.
   - **Threshold**: At least **5 boundary/negative tests per feature** ($\ge 5 \times 25 = 125$ tests).
   - **Focus**: Zero unhandled exceptions, bounded memory usage, graceful degradation, and invariant enforcement under hostile or degraded conditions.

3. **Tier 3: Pairwise Cross-Feature Interactions**  
   - Systematically pairs interacting features across subsystems (e.g., combat action triggering during transform replication, ground probing during spectator timeout, multi-peer discovery during batch inventory replication).
   - **Threshold**: Combinatorial interaction coverage across critical operational pairings.
   - **Focus**: Elimination of cross-feature race conditions, lock contention, and state corruption.

4. **Tier 4: Real-World Application Workload Scenarios**  
   - Simulates complete, end-to-end cooperative gameplay sessions mimicking human co-op experiences over simulated network transports.
   - **Threshold**: At least **5 comprehensive real-world scenarios**.
   - **Focus**: Validates multi-minute gameplay sequences, boss battles, mutual wipes, reconnects, and severe network degradation (latency, jitter, packet loss).

---

## 2. Feature Inventory (ORIGINAL_REQUEST.md Mapping)

The 25 features and defect contracts defined in `ORIGINAL_REQUEST.md` are mapped across all four testing tiers:

| # | Feature Code | Severity / Area | Feature Description | Tier 1 | Tier 2 | Tier 3 | Tier 4 |
|---|--------------|-----------------|---------------------|:------:|:------:|:------:|:------:|
| 1 | **CRIT-01** | Critical / Lifecycle | Eliminate epoch race condition in `DeathLifecycleCoordinator.cs` (`advanceEpoch` / `CurrentEpoch` synchronization with `AdoptHigherEpoch`) | 5 | 5 | P01 | S03 |
| 2 | **CRIT-02** | Critical / Lifecycle | Prevent duplicate bench reloads / `TriggerTeamWipe()` in `SilksongCoopPlugin.cs` on disconnect when wipe or recovery is active | 5 | 5 | P02 | S03, S04 |
| 3 | **HIGH-01** | High / Ground Probing | Fix fallback spawn logic on revive in `SilksongCoopPlugin.cs` and `UnityGroundProber.cs` when raycast finds no floor | 5 | 5 | P03 | S02 |
| 4 | **HIGH-02** | High / Transport | Prevent `LocalPeerId` collision (`Id=0`) in `SteamP2PTransport.cs` when Steam is offline (defaults to `localId = 1`) | 5 | 5 | P03 | S04 |
| 5 | **HIGH-03** | High / Transport | Eliminate O(n) enumerator allocation in `LiteNetLibTransport.cs` on channel buffer trimming | 5 | 5 | P08 | S05 |
| 6 | **HIGH-04** | High / Interpolation | Add thread-safe synchronization to `SnapshotBuffer.cs` for concurrent updates and circular integrity | 5 | 5 | P01 | S01, S05 |
| 7 | **MED-01** | Medium / Lifecycle | Protect `_pendingSoloBenchRespawn` against lost respawn packets during repeated deaths in `DeathLifecycleCoordinator.cs` | 5 | 5 | P02 | S02 |
| 8 | **MED-02** | Medium / Session | Reset `_reviveInvulnerabilityActive` in `PrepareForNewSession` and session disconnect cleanup | 5 | 5 | P02 | S02, S04 |
| 9 | **MED-03** | Medium / Spectator | Suppress spectator F9 camera toggle and status toasts when spectator mode is inactive | 5 | 5 | P03 | S02 |
| 10 | **MED-04** | Medium / Inventory | Batch initial inventory packets in `BroadcastInitialInventory` to prevent transport queue saturation | 5 | 5 | P04 | S01, S04 |
| 11 | **MED-05** | Medium / UI | Use scaled time or pause-safe timing (`Time.unscaledDeltaTime`) for HUD pagination and watermark in `CoopMenuUI.cs` | 5 | 5 | P05 | S01 |
| 12 | **MED-06** | Medium / Services | Log target peer IDs on transmission failures in `PacketSender.cs` | 5 | 5 | P08 | S04, S05 |
| 13 | **LOW-01** | Low / Transport | Optimize initial pending entry lookup in `LiteNetLibTransport.cs` | 5 | 5 | P08 | S05 |
| 14 | **LOW-02** | Low / Vitals | Prevent struct torn reads in `VitalsSyncService.cs` by using immutable `RemotePeerVitals` | 5 | 5 | P05 | S01 |
| 15 | **LOW-03** | Low / UI | Cap maximum entries (5) and add early break in `ConnectionHistory.cs` | 5 | 5 | P06 | S04 |
| 16 | **LOW-04** | Low / Settings | Validate tick rate against game frame rate in `QualityPresetManager.cs` | 5 | 5 | P07 | S01 |
| 17 | **LOW-05** | Low / Enemy Sync | Clear `_authorityMap` and enemy tracked state on session reset in `EnemySyncController.cs` | 5 | 5 | P06 | S01, S04 |
| 18 | **LOW-06** | Low / UI | Remove RTT watermark update latency in `CoopMenuUI.cs` by including RTT in `identityChanged` | 5 | 5 | P05 | S01, S05 |
| 19 | **LOW-07** | Low / Localization | Handle missing localization key and `FormatException` fallbacks in `Loc.cs` | 5 | 5 | P06 | S01 |
| 20 | **PERF-01** | Perf / Visuals | Avoid redundant calls to `ApplyPaletteSwap` for Player 1 in `PaletteSwapController.cs` | 5 | 5 | P07 | S01 |
| 21 | **PERF-02** | Perf / UI | Eliminate per-frame allocations from `BuildPeerDiagnostics` in `CoopMenuUI.cs:OnGUI` | 5 | 5 | P05 | S01 |
| 22 | **PERF-03** | Perf / Transport | Eliminate per-tick `List<CSteamID>` allocations in `SteamP2PTransport.cs:PollEvents` | 5 | 5 | P08 | S05 |
| 23 | **ARCH-01** | Arch / Inventory | Reconcile multi-peer API contracts (3+ player support) in `InventorySyncService.cs` | 5 | 5 | P04 | S01 |
| 24 | **ARCH-02** | Arch / Coordinators | Decompose monolithic plugin by extracting discrete coordinators and managers | 5 | 5 | P02 | S01..S05 |
| 25 | **ARCH-03** | Arch / Interfaces | Decouple `IGameplayActions` from concrete Unity plugin objects for headless testability | 5 | 5 | P03 | S02, S03 |

---

## 3. Test Architecture & Execution Semantics

### Test Runner Invocation
All tests are executed headlessly via the standard .NET CLI test runner:
```powershell
dotnet test D:\antigravity\silksong_coop_mod\SilksongCoop.sln --configuration Release
```

### Pass/Fail Semantics & Invariants
1. **Zero Failures**: Process exit code must be `0`. A non-zero exit code represents a build or assertion failure.
2. **Zero Skipped Tests**: Every test is executed; no `Skip` attributes or conditional test suppression.
3. **Deterministic Virtual Timing**: Tests utilizing network impairment models (latency, jitter, packet loss) utilize `MockNetworkTransport` with deterministic seeds and virtual clocks (`harness.DeliverAll()`) rather than real-time `Thread.Sleep`, guaranteeing 100% reproducible execution.
4. **Isolated Test State**: Every test initializes its own state instances (`DualClientTestHarness`, `VitalsSyncService`, `DeathLifecycleCoordinator`, etc.), executes in complete isolation, and disposes resources cleanly.

---

## 4. Real-World Application Scenarios Table

| Scenario ID | Name | Subsystems Involved | Authentic Gameplay Conditions | Success Criteria |
|-------------|------|---------------------|--------------------------------|------------------|
| **S01** | **Tri-Client Boss Encounter Simulation** | Combat, Vitals, Inventory, Currency, Visuals, UI | 3 players (Host, Client 1, Client 2). Client 1 takes damage; Client 2 uses silk; Client 1 unlocks Tool; Boss defeated -> currency drop. | Local damage decoupled across all 3 players; tool unlock mirrored to all peers; currency wallet converges across all 3 peers. |
| **S02** | **Solo Death, Spectator Mode & Safe Ground Revival** | Death Lifecycle, Spectator Camera, Ground Probing, Vitals | Player 1 dies to arena hazard; partner continues fighting; spectator countdown ticks down; safe ground probed; Player 1 revives with half HP. | Bench reload suppressed; partner HP untouched; hero spawns on verified safe surface with 50% HP and invulnerability window. |
| **S03** | **Dual Wipe Simultaneous Fatal Strike & Synchronized Reload** | Death Lifecycle, Concurrency, Atomic CAS, Bench Reload | Both players suffer lethal damage within 20ms in boss arena; simultaneous death packets cross in flight; atomic team wipe triggered. | Zero duplicate bench reloads; `Interlocked.CompareExchange` drops concurrent wipe; both players reset and advance epoch in lockstep. |
| **S04** | **Mid-Session Disconnect & Seamless Reconnection Catch-Up** | Transport, Session Management, Inventory, Vitals, Enemy Sync | Client loses connection during room traversal; Host maintains listener; Client rejoins new session; catch-up payloads broadcast. | Stale session state cleanly purged; initial inventory and vitals stream to rejoining client; zero desync or duplicate unlocks. |
| **S05** | **High-Latency Network Degradation & Recovery** | LiteNetLib Transport, Reliable Channels, Snapshot Buffer, Diagnostics | Harsh network conditions: 150ms RTT, 10% packet drop, 20ms jitter. 50 rapid combat actions, transforms, and reliable packets. | Reliable channels recover all lost packets; sequence order strictly preserved; zero buffer leaks; diagnostics record metrics. |

---

## 5. Coverage Thresholds & Quality Metrics

| Tier | Category | Minimum Required | Delivered Count | Status |
|:----:|----------|:----------------:|:---------------:|:------:|
| **Tier 1** | Category-Partition Feature Tests | $\ge 5$ per feature ($\ge 125$) | **125** | PASS |
| **Tier 2** | Boundary Value & Error Handling Tests | $\ge 5$ per feature ($\ge 125$) | **125** | PASS |
| **Tier 3** | Pairwise Cross-Feature Tests | Combinatorial pairs ($\ge 8$) | **8** | PASS |
| **Tier 4** | Real-World Application Workload Scenarios | $\ge 5$ end-to-end scenarios | **5** | PASS |
| **Total** | **Comprehensive Regression Suite** | **$\ge 263$ new + 1473 existing** | **1736+ tests** | **VERIFIED** |
