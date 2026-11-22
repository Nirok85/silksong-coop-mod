# Beam Max execution log

This checkpoint follows tasks 01–13 in the Beam Max plan. The starting revision
for the plan was `a90f51a`. Implementation continued on
Historical execution checkpoint: work began on `codex/beam-max-completion` from `a0cb69d`, before release v4.0.0. It records the tested candidate at mod v3.3.9 and protocol v12. Release v4.0.1 carries documentation corrections only.

## What Beam Max contributed

OpenCode ran `opencode/beam-free#max`. Beam reviewed the coordinator, soak,
diagnostic export and CI, and drafted the testing and reconnect documentation.
It supplied the save-readiness guard and its regression. The regression was
adapted to assert the typed packet kind and the serialized nonempty save token.
It failed before the guard and passed afterward. Beam also reviewed the existing
concurrent UDP stress test and drafted an isolated export fixture. Review notes
were checked against source and measured results before use.

## Acceptance checklist

| Task | Status | Evidence and remaining checks |
|---|---|---|
| 01 Baseline | PASS | Baseline log confirmed 1836 Core tests at `a90f51a`, with TRX and raw benchmarks. The continuation baseline at `a0cb69d` passed 1839 Core tests and built with local game assemblies. |
| 02 Running code | PASS | WalletPumpChecks links `PeerHandshakeCoordinator` and `PacketSender`. Ten checks pass, including guest isolation and waiting for a real save. Inverting the seed success condition in an isolated copy made two checks fail. |
| 03 Reproducible sessions | PASS | Fixed seed produces identical simulated delivery traces, with counted loss, retry and refusal. Standalone smoke covers real UDP loopback and two- to four-node simulation. |
| 04 Entry and recovery | PARTIAL | Stages, a retryable currency marker and bounded seed sends are implemented. The v12 marker cannot prove complete state across channels. A refused bench snapshot still lacks a separate retry. |
| 05 Currency and inventory | PARTIAL | Wallet snapshots survive refusal, assembly interruption and repeated receipt. The group simulation verifies physical pickup deduplication and exact balance. Independent saves and native fragment conversion during a live join remain unverified. |
| 06 Scene, combat and death | PARTIAL | Tests exercise `A → B → A` and consecutive departure across wrap using `RemoteSceneSequence`. Existing Core death and damage tests pass. Real Unity boss, persistent-object and revive cases remain unverified. |
| 07 Queues and fairness | PARTIAL | Real UDP confirms ordered drain after relay loss and a peak of 512 unacknowledged packets. Sender refusal, reordering and multi-peer independence are tested in the model and source adapter. The report does not measure all Unity/Steam queues. |
| 08 Three to four nodes | PARTIAL | Model covers shared rewards, guest return and host end through real Core state. Live Steam lobbies remain experimental. |
| 09 Status and privacy | PARTIAL | F10 labels the local stage in EN and RU. Structured export removes peer names, identifiers and absolute paths. The log tail masks known data and should be reviewed before sharing. An isolated executable linked the actual `DiagnosticsBundle.Save` and verified the complete file with synthetic paths, peer and lobby IDs, display name, room code, IPv4 and credential while retaining counters. F11 and the UI do not have live-game verification. |
| 10 Performance | PARTIAL | The runner records phase percentiles and driving-thread allocated bytes. Core microbenchmarks compare packet and interpolation code. No Unity FPS claim. |
| 11 CI, docs and package | PARTIAL | Hosted smoke and manual 45-minute workflow are wired with locked restore. YAML is checked locally. Both ZIPs and their SHA-256 manifest passed local verification on the current candidate. Hosted workflows have not run. |
| 12 Long soak | PASS | Seed 1337 ran for 2700.14 seconds across all three phases on the final candidate. The source hashes matched after execution. No invariant failures. |
| 13 Final verification | PARTIAL | Release build passed with no warnings. Core: 1844 passed. Adapter checks: 17 ground, 54 inventory hooks, and 10 handshake. BindingCheck and full preflight passed. One prior concurrent run lost a connection in the existing UDP stress test; three isolated tests and the full rerun passed. Live two-instance validation remains pending. |

## Core benchmark comparison

BenchmarkDotNet 0.14.0, .NET 8, Release, same Windows machine and ShortRun job.
Baseline `a0cb69d` and candidate results use three measured iterations.

| Benchmark | Baseline mean ± error | Candidate mean ± error | Allocated |
|---|---:|---:|---:|
| AddAndInterpolate | 595.8 ± 218.4 ns | 591.9 ± 224.5 ns | 0 B |
| SerializeDeserialize | 97.646 ± 31.853 ns | 103.189 ± 84.561 ns | 0 B |
| EnemySnapshotBatchSerialize | 9.332 ± 3.942 ns | 8.992 ± 12.046 ns | 0 B |

These differences are within the reported error. The measurements do not
justify changes to those Core hot paths. They do not cover Unity frame
time. The first baseline attempt found several checkouts with the same project
name. Including the original solution file in the archived baseline isolated
project discovery. The baseline retry and candidate logs each confirm three
executed benchmarks. A process exit code alone did not detect the first failure.

## Completed reliability soak

Monotonic duration: 2700.14 seconds. Seed: 1337. Candidate: a0cb69d355f85b4c76df4f77864fec984b1cc413-746b3928fed8f1e1707318d6a1b3a43fa760842311ae28e0dcd5ef7e9524a300.

| Profile | Seconds | Scenarios | Peak queue | Datagrams dropped | Send refusals | Model retries |
|---|---:|---:|---:|---:|---:|---:|
System.Object[]

The UDP queue is the unacknowledged backlog. Two- and group profiles measure
the per-link receive and reorder queues. UDP retransmissions were verified
by ordered delivery after loss; the report does not count individual UDP retries.

## Reproduce

Commands and profile differences are in [testing.md](testing.md). Use the
JSON report for the exact runtime and outcome of a long run. Use
`scripts/preflight-release.ps1 -AllowDirty -NoRestore` with `ManagedDir` set
and every check project already restored to verify the game build and local
checks. Package and verify both formats separately.

The full-history baseline appears in the Git revision `a0cb69d` of this file.
No live Unity or Steam session has been counted as a pass.
