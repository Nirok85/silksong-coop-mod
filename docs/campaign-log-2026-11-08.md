# Performance & Architecture Campaign Log — 2026-11-08

Spec: `docs/teamwork-spec.md`
Baseline: `652720b` (version 3.3.1, protocol v8)

## Status board

| Milestone | Status | Commit | Checks |
|---|---|---|---|
| M1 — shared session state | **Done** | `3ced065` | Release/Debug build 0 warnings; 908/908 tests; independent audit clean |
| M2 — network & session allocation pass | Pending | — | — |
| M3 — UI and small hot paths | Pending | — | — |
| M4 — enemy/world indexing | Pending | — | — |
| M5 — targeted architecture & lifecycle | Pending | — | — |
| M6 — release 3.3.2 & GitHub | Pending | — | — |

## M1 — shared session state

**Problem:** `SilksongCoopPlugin` and `SessionService` each created a separate
`CoopSessionState`. The UI/hooks used the plugin instance while
`NetworkOrchestrator`, `VitalsSyncManager`, `InventorySyncManager` and
`DeathSyncManager` used the service instance. Managers never observed
`Connected`/`RemotePeerId`.

**Changes:**

- `SessionService` now requires an injected `CoopSessionState`, stores it and
  throws on null; the duplicate `new CoopSessionState()` is gone.
- `SilksongCoopPlugin.Awake` passes its existing `SessionState` and logs a
  composition error if the references ever differ.
- `SessionService.StartHost`/`Connect`/Steam variants now call
  `_plugin.SetActiveTransport(...)`, keeping the plugin and transport service
  views in sync.
- New source-structure regression tests in
  `tests/SilksongCoop.Tests/State/SessionStateWiringTests.cs`:
  exactly one production `new CoopSessionState()`, no creation in
  `SessionService`, the injected state is stored, and the plugin constructs the
  service with `SessionState`.
- Existing test files untouched; no protocol change.

**Checks:**

- `dotnet build SilksongCoop.sln -c Release --no-restore -p:TreatWarningsAsErrors=true` — 0 errors, 0 warnings.
- `dotnet build SilksongCoop.sln -c Debug --no-restore -p:TreatWarningsAsErrors=true` — 0 errors, 0 warnings.
- `dotnet test -c Release --no-build --verbosity minimal` — 908 passed, 0 failed, 0 skipped.
- Independent `code-audit` re-review: **M1 can be closed**.
- Residual risk: live two-client smoke not run yet; scheduled for M6.

## Notes

- Protocol remains v8. SACK, fragmentation and delta/quantized snapshots are
  out of scope for this campaign.
- The old v2.x campaign spec in `docs/teamwork-spec.md` is superseded and
  remains in git history.
