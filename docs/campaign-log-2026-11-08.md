# Performance & Architecture Campaign Log — 2026-11-08

Spec: `docs/teamwork-spec.md`
Baseline: `652720b` (version 3.3.1, protocol v8)

## Status board

| Milestone | Status | Commit | Checks |
|---|---|---|---|
| M1 — shared session state | **Done** | `3ced065` | Release/Debug build 0 warnings; 908/908 tests; independent audit clean |
| M2a — cached peer snapshots | **Done** | `aed9176` | 916/916 tests; allocation tests 0 B/read; independent audit clean |
| M2b — UDP/HMAC allocation pass | Pending | — | — |
| M3 — UI and small hot paths | Pending | — | — |
| M4 — enemy/world indexing | Pending | — | — |
| M5 — targeted architecture & lifecycle | Pending | — | — |
| M6 — release 3.3.2 & GitHub | Pending | — | — |

## M2a — cached peer snapshots

**Changes:**

- `CoopSessionState.RemotePeerIds` returns a cached `Array.AsReadOnly` view over a
  private copy; rebuilt on peer add/remove/display-name refresh.
- `VitalsSyncService.RemotePeers` returns a cached read-only view; rebuilt
  under `_stateLock` on vitals add/remove/clear.
- `PeerRegistry.Peers` / `ReadyPeers` return cached read-only views; registry
  keys are replaced when the numeric peer id is unchanged but the display name
  changes.
- `LiteNetLibTransport.ConnectedPeers` caches its single-peer view under
  `_lock`; `Broadcast` sends directly without materializing a list.
- `SteamP2PTransport.ConnectedPeers` uses a dirty-flag snapshot rebuilt on
  primary/additional peer changes, promotion and clear; `Broadcast` iterates
  peers directly.
- `NetworkOrchestrator` reuses one fallback recipient buffer and compares both
  peer value and display name.
- New tests: `tests/SilksongCoop.Tests/Performance/PeerSnapshotAllocationTests.cs`
  and `tests/SilksongCoop.Tests/State/PeerSnapshotRefreshTests.cs`.

**Checks:**

- Release build: 0 errors, 0 warnings.
- Debug build: 0 errors, 0 warnings.
- `dotnet test -c Release --no-build`: 916 passed, 0 failed, 0 skipped.
- Allocation tests: 0 managed bytes over 1000 reads after warm-up for all four
  snapshot paths (including connected UDP).
- Independent audit: no remaining issues; M2a closed after build/test
  verification.
- Residual risk: Steam 3+ cache invalidations are compile-verified only; live
  Steam smoke remains M6.

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
