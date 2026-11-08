# Silksong Co-op Mod — Performance & Architecture Campaign Spec

> **Current campaign (2026-11-08).** This supersedes the v2.x campaign spec
> previously stored in this file. The old plan remains in git history.

## Baseline

- Repository: `Nirok85/silksong-coop-mod`
- Branch: `master`
- Code baseline: `652720b` (docs-only commit after `6a7167f`)
- Version: `3.3.1`
- Protocol: **v8**
- Known test baseline: 904/904 tests, 0 compiler warnings (Release/Debug)
- Known limitation: no live two-client or Steam 3+ smoke has been run for 3.3.1

## Goal

Apply the non-breaking optimization and improvement set identified in the
2026-11-08 code analysis:

1. Fix the `SessionService` / `SessionState` composition bug so the UI and all
   network managers share one session state.
2. Remove avoidable allocations from the network, session-state, enemy-sync and
   UI hot paths.
3. Add algorithmic improvements (enemy/world indexes, reusable snapshot state,
   cached object keys) without changing protocol v8.
4. Make targeted architecture improvements: a single session-state owner, a
   shared packet sender, clearer lifecycle/disposal and fewer duplicated
   constants.
5. Add regression/allocation tests and a repeatable local smoke attempt.
6. Publish the result to GitHub as a patch release after all gates pass.

## In Scope

- P0: one shared `CoopSessionState` between `SilksongCoopPlugin`, `SessionService`
  and every sync manager.
- P1: cached peer snapshots (`RemotePeerIds`, `ConnectedPeers`, `RemotePeers`,
  `PeerRegistry` snapshots), UDP packet-path allocation removal, pooled receive
  dispatch, UI OnGUI allocation reduction.
- P2: enemy index, world-object index, combined snapshot bookkeeping, cached
  `WorldObjectKey`, compiled quest-field accessors, combined state-flag accessor.
- P3 (targeted): extract 2-3 focused services (session-state owner / packet
  sender / enemy key index), centralize backlog constants, improve event
  subscription/disposal and add composition/regression coverage.
- Tests: new files only. Existing test files must not be modified.
- Release: version `3.3.2`, changelog, README/ROADMAP updates, archives,
  SHA-256 manifest, tag and GitHub Release.
- GitHub: commit/push to `origin/master`, update repository metadata if needed,
  create tag and release only after the release gate passes.

## Out of Scope

- No protocol changes: SACK, fragmentation, delta/quantized snapshots, priority
  scheduling or new packet types. The wire format remains v8.
- No full rewrite of the five large classes. Targeted extraction only.
- No new gameplay features, art changes or unrelated UX work.
- No changes to the game installation, BepInEx archive or release signing model.
- No tag/release before the release gate in the Milestone Plan.

## Constraints

- Build with `TreatWarningsAsErrors=true`; 0 errors and 0 compiler warnings
  (`NU1900` is the only allowed network warning).
- All existing tests must pass; new coverage goes into new files.
- The two-player UDP and Steam path must remain the primary supported path.
- Steam 3+ remains experimental.
- The protocol stays v8; no packet layout or semantic changes.
- Every milestone is a separate commit with its own checks.
- Do not modify existing test files.
- Live smoke is mandatory before publishing the release. If two local instances
  cannot be launched, stop and ask; do not publish on automated checks alone.

## Acceptance Criteria

1. `SessionService.SessionState` and `SilksongCoopPlugin.SessionState` are the
   same object; a regression guard/test proves it.
2. `dotnet build SilksongCoop.sln -c Release --no-restore -p:TreatWarningsAsErrors=true`
   succeeds with 0 errors and 0 warnings.
3. `dotnet test -c Release --no-build` passes; new tests are in new files.
4. Steady-state transform hot path has 0 managed allocations in allocation tests
   after warm-up; combat target is <=1 KB/frame where measurable.
5. `RemotePeerIds`, `ConnectedPeers`, `RemotePeers` and `PeerRegistry` snapshot
   reads allocate no managed memory after warm-up.
6. Enemy exact-key lookup is O(1) through an index; proximity fallback is used
   only when the exact key is absent.
7. Protocol remains v8; no new packet types or field layout changes.
8. Preflight passes on a clean tree.
9. Local two-instance UDP smoke and, if possible, Steam 2-player smoke pass.
10. Both release archives and `SHA256SUMS` are produced, hashes match, and the
    GitHub Release is created only after the smoke gate.

## Risks

- The session-state fix may expose latent wiring bugs that were previously
  masked by the duplicate state.
- Transport pooling can cause buffer reuse/ownership bugs; every pooled buffer
  must have a single clear return path.
- UI Repaint-only changes can alter layout behaviour; verify F10/F11 and HUD.
- Enemy/world indexes can go stale on scene changes or dynamic spawns; rebuild
  and invalidation rules must be explicit.
- Local two-instance smoke may be blocked by Steam/DRM/single-instance locks;
  Steam 3+ still needs three accounts.
- Tag/release is irreversible; it is gated on build, tests, preflight and smoke.

## Roles and Artifacts

- Orchestrator: maintains this spec, milestone log and release gate.
- `explore`: repository research, risk checks, independent verification.
- `general`: implementation of each milestone.
- Independent auditor: checks each milestone and the final state.
- Log: `docs/campaign-log-2026-11-08.md` (milestone, changes, checks, status).
- Release evidence: archive hashes, test TRX, CI run URL, smoke notes.

## Decision Log

- Protocol scope: non-breaking only; protocol remains v8.
- Refactor depth: targeted extraction, not full decomposition.
- GitHub delivery: direct commits/push, tag and release after gates.
- Smoke: agent attempts local two-instance; stop and ask if impossible.
- Tests: existing files untouched; new files only.
- Performance gate: existing plan targets (0 managed alloc/frame transform,
  <=1 KB/frame combat where measurable).
