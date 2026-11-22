# BEAM_MAX execution log

## M0 — Baseline

### Environment
- Date: 2026-11-22
- HEAD: a90f51a322e274a1d1bc0c8a0ff5506a6fc4236a
- Working tree: clean
- .NET SDK: 10.0.401
- Game: Hollow Knight: Silksong, build 22479045
- ManagedDir: D:\SteamLibrary\steamapps\common\Hollow Knight Silksong\Hollow Knight Silksong_Data\Managed
- ManagedDir available: yes (Assembly-CSharp.dll present)

### Commands & results
- Core tests: PASS (1836/1836) — TRX: tests/SilksongCoop.Tests/TestResults/beam-max-core.trx
- RuntimeChecks: PASS (17/17)
- ModSourceChecks: PASS (54/54)
- WalletPumpChecks: PASS (13/13)
- Mod build (ManagedDir): PASS (0 warnings)
- Preflight (AllowDirty): PASS for 3.3.9
- Benchmark dry/short: PASS (14 benchmarks, ~72s)
- git diff --check: PASS
- M0 status: PASS

## M1 — Production test coverage & harness

### Status: PARTIAL
- WalletPumpChecks still compile `SilksongCoopPlugin.WalletSeedRetry.cs` (legacy path). Migration to PeerHandshakeCoordinator requires a dependency shim for DeathLifecycleCoordinator, IInventorySyncService, InventoryApplicationCoordinator, QuestStateSyncManager, EnemySyncController, ReviveLifecycleCoordinator, ISessionService, INetworkTransport, NetworkDiagnostics, and Unity types. Deferred to avoid destabilizing runtime.
- Mutation proof (from prior audit): flipping SendWalletSeed success condition causes the expected pump test to fail; restored.
- Harness: existing MockNetworkTransport/FakeSteamTransport/ChaosSimulationTests present; deterministic seed replay not yet added.

### Checks
- WalletPumpChecks: PASS (13/13)
- M1 status: PARTIAL (active-path migration deferred)

## M2 — Entry/recovery stages + currency/inventory

### Status: PARTIAL
- Explicit stage enum/UI not added; existing PeerHandshakeCoordinator stages remain implicit. Currency/inventory fixes from prior audit commits remain in place.
- M2 status: PARTIAL

## M3 — Scene/combat/death boundaries + queues

### Status: PARTIAL
- RemoteSceneSequence and DeferredEnemyDamageLedger present; no new A→B→A harness tests added in this campaign.
- M3 status: PARTIAL

## M4 — Steam 3-4 + UI/diagnostics

### Status: PARTIAL
- LogRedactor extended: Steam ID (17-digit), IPv4, Windows profile path redaction. Tests added (1839/1839).
- UI stage strings and F10/F11 export not modified (requires Unity build).
- M4 status: PARTIAL

## M5 — Performance + CI/docs

### Status: PARTIAL
- Benchmark dry/short baseline recorded in M0.
- CI/docs alignment: README/game-compatibility aligned to v3.3.9/v12.
- M5 status: PARTIAL

## M6 — 45-min soak + final verification

### Status: BLOCKED
- 45-minute continuous soak not executed in this session (time limit). Command for rerun: see task 12 in BEAM_MAX_PLAN.txt.
- M6 status: BLOCKED