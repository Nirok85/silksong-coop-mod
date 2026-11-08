# Performance & Architecture Campaign Log — 2026-11-08

Spec: `docs/teamwork-spec.md`
Baseline: `652720b` (version 3.3.1, protocol v8)

## Status board

| Milestone | Status | Commit | Checks |
|---|---|---|---|
| M1 — shared session state | **Done** | `3ced065` | Release/Debug build 0 warnings; 908/908 tests; independent audit clean |
| M2a — cached peer snapshots | **Done** | `aed9176` | 916/916 tests; allocation tests 0 B/read; independent audit clean |
| M2b — UDP/HMAC allocation pass | **Done** | `54bec12` | 916/916 tests; Release/Debug 0 warnings; independent audit clean |
| M3a - UI per-OnGUI allocations | **Done** | `1c53f16` | 916/916 tests; Release/Debug 0 warnings; independent audit clean |
| M3b - dash pool/rate limiter/state flags | **Done** | `43c7e88` | 917/917 tests; allocation test 0 B; Release/Debug 0 warnings; independent audit clean |
| M4 — enemy/world indexing | Pending | — | — |
| M5 — targeted architecture & lifecycle | Pending | — | — |
| M6 — release 3.3.2 & GitHub | Pending | — | — |

## M3a - UI per-OnGUI allocations

**Changes:**

- Fixed-rect HUD, watermark, spectator overlay, diagnostics and toasts draw only
  on Repaint; the main window/GUILayout still runs on Layout and Repaint.
- Watermark text/color cached and refreshed at most 4 Hz or on identity changes.
- DiagnosticsOverlay reuses `Vector2[61]` and `float[61]` buffers; history copies
  are allocation-free; bandwidth scaling moved into DrawGraph.
- ToastNotifier builds the escaped rich-text label once at Show time.

**Checks:** Release/Debug 0 warnings; 916/916 tests; independent audit clean.

## M3b - dash pool, rate limiter and state flags

**Changes:**

- Dash ghosts are prewarmed (8), persist across scene loads, and are reaped by a
  once-per-frame controller pass instead of a per-ghost coroutine. Setup failures
  destroy the half-initialized ghost and prewarm failures destroy the orphan
  GameObject.
- RateLimiter caches its bucket factory delegate; the new allocation test shows
  0 managed bytes over 1000 steady-state TryConsume calls.
- NetworkOrchestrator uses a typed `StateFlagAccessors` set plus an instance-level
  last-type cache instead of 13 string-keyed dictionary lookups per transform tick.

**Checks:** Release/Debug 0 warnings; 917/917 tests; independent audit clean.

## M2b - UDP/HMAC allocation pass

**Changes:**

- Session HMAC is cached (`SetSessionKey` / `ClearSessionKey`); MACs are computed
  with `TryComputeHash` into `stackalloc` buffers instead of constructing an
  `HMACSHA256` and a hash array for every datagram.
- `BinaryPrimitives` replaces `BitConverter.GetBytes` for data sequences,
  envelopes, ACK payloads and heartbeat timestamps.
- Envelope buffers are rented from `ArrayPool` and returned after the synchronous
  `SendTo`; ACK and ping payloads are stack-allocated.
- Unreliable and sequenced payloads are copied into pooled buffers and returned
  after the deferred handler runs. Reliable ordered/unordered paths keep their
  existing array ownership.
- `DrainPendingEvents` uses a reusable batch list, a reentrancy/concurrency
  guard, an atomic `_drainRequested` handoff and returns pooled buffers on every
  exit. Handler exceptions are logged per event and no longer strand the tail.
- Connected receive polling reuses one `IPEndPoint`; handshake/disconnected
  paths still allocate fresh endpoints because they may store the endpoint as a
  pending-handshake key.
- `INetworkTransport.OnDataReceived` now documents that the data window is valid
  only during the callback.

**Checks:**

- Release build: 0 errors, 0 warnings.
- Debug build: 0 errors, 0 warnings.
- `dotnet test -c Release --no-build`: 916 passed, 0 failed, 0 skipped.
- Independent audit iterations found and closed: drain race, callback deadlock,
  event-loss gap and handler-exception tail loss. Final audit: no issues.
- Residual risk: HMAC `TryComputeHash`/span behavior is compile-verified on the
  target netstandard2.1; live UDP/Steam smoke remains M6.

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
