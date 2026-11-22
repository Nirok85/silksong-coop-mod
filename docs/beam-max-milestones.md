# BEAM_MAX milestone plan

## M0 — Baseline & scoping (task 01)
- Tasks: record SHA, clean tree, SDK, game assemblies, transport availability; run Core + adapter checks + Mod build; run BenchmarkDotNet dry/short; save TRX.
- Owner: explore (research), general (execution).
- Checks: `dotnet test`, adapter runs, preflight, benchmark dry.
- DoD: reproducible command/result record in `docs/beam-max-execution.md`.

## M1 — Production test coverage & harness (tasks 02-03)
- Tasks: migrate WalletPumpChecks to PeerHandshakeCoordinator (or minimal callable logic); add mutation proof; extend MockNetworkTransport/FakeSteamTransport with seeded chaos (delay, loss, Send==false, disconnect/reconnect); deterministic replay.
- Owner: general (implementation), explore (audit).
- Checks: WalletPumpChecks pass + mutation (flip send-success → test fails); two identical seeds → identical decision log.
- DoD: production path tested; chaos harness deterministic.

## M2 — Entry/recovery stages + currency/inventory (tasks 04-05)
- Tasks: explicit sync stages (save wait, ready announce, initial state, playable, failure) with owner/exit/deadline/memory; CatchUpComplete semantics; retry without restarting completed parts; cancel on save change. Currency/inventory/permanent upgrades: wallet seed split, duplicate pickup idempotency, journal overflow, temporary apply failure retry, silkSpoolParts/silkMax handling.
- Owner: general.
- Checks: Core + adapter tests; new stage/currency tests.
- DoD: stage transitions documented and tested; currency exactness for identified keys.

## M3 — Scene/combat/death boundaries + queues (tasks 06-07)
- Tasks: A→B→A with deferred transform; scene/lifecycle validation; enemy/boss/persistent dedup with generation; join fallen partner, team death, revive, bench fallback. Queue limits, overflow policy, counters, uint wrap, slow-peer fairness, channel budget.
- Owner: general + explore.
- Checks: new scene/death/queue tests; queue counters within limits.
- DoD: stale scene cannot move clone; old combat cannot alter new HP; queues drain by deadline.

## M4 — Steam 3-4 + UI/diagnostics (tasks 08-09)
- Tasks: 3-4 peer model tests (join/leave/rejoin, host end); per-peer state binding; experimental status documented. UI: F10 stages/reasons/actions/escape; F11 diagnostics export with version/protocol/transport/stage/reason/counters; redaction of room code/IP/Steam ID/path/token.
- Owner: general.
- Checks: model tests; redaction tests with sample secrets.
- DoD: no secret leakage in export; stages have strings.

## M5 — Performance + CI/docs/packaging (tasks 10-11)
- Tasks: p50/p95/p99 headless, alloc counts; optimize one measured hotspot with before/after; CI hosted short mode + nightly locked-mode; preflight/verify-artifacts; docs EN/RU alignment, historical marking, Unreleased.
- Owner: general + explore.
- Checks: benchmark report; CI workflow valid; preflight pass; git diff --check.
- DoD: comparable numbers + raw reports; commands reproducible from clean copy.

## M6 — 45-min soak + final verification (tasks 12-13)
- Tasks: 3 phases × 15 min (UDP loopback; virtual clients with loss/jitter/refusal; 3-4 nodes guest swap/rewards/host leave); 2700s active; record start/end, monotonic duration, scenarios, queue maxima, failures, first error, rerun command. Final: full test/build, preflight, game smoke (if available), final report.
- Owner: general (execution), explore (audit).
- Checks: soak report; final gate run.
- DoD: 2700s active, all phases, zero invariant violations; final report in execution log.

## Execution order
M0 → M1 → M2 → M3 → M4 → M5 → M6. Parallelize only independent research. Milestone closes only after checks pass. Auditor reviews each milestone.

## Approval
Awaiting user approval to proceed.