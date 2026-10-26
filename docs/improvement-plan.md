# Silksong Co-op Mod — Large-Scale Improvement Plan

**Date:** 2026-10-26
**Current state:** v1.0.0, 603 tests passing, 0 errors (30 pre-existing warnings), 4 commits of stability work.
**Goal:** Transform the mod from a "working prototype" into a robust, production-grade co-op experience.

---

## Phase 1 — Architecture & Code Quality (Steps 1–3)

### Step 1: Decompose the God Object (`SilksongCoopPlugin`)
**Problem:** `SilksongCoopPlugin` (~1000 LOC) mixes transport orchestration, event wiring, revive logic, item application, transform broadcasting, and Unity lifecycle.
**Action:**
- Extract `NetworkOrchestrator` (transport selection, connect/disconnect, polling).
- Extract `GameplaySyncController` (revive, team wipe, item application, vitals application).
- Extract `TransformReplicationService` (tick-based transform broadcast).
- Keep `SilksongCoopPlugin` as a thin BepInEx entry point that wires services.
**Acceptance:** Each service < 300 LOC; plugin < 200 LOC; all tests pass.

### Step 2: Replace Static Mutable Guard Flags with Scoped Context
**Problem:** `HeroVitalsHooks.IsApplyingRemoteUpgrade`, `CurrencyHooks.IsApplyingRemoteCurrency`, `InventoryHooks.RemoteUnlockDepth` are static bools — not thread-safe, rely on `finally` blocks.
**Action:**
- Introduce `RemoteApplyScope : IDisposable` (using-pattern) that sets/clears a `ThreadLocal<int>` or `AsyncLocal<int>` counter.
- All hook sites use `using (RemoteApplyScope.Begin())`.
**Acceptance:** No static mutable guards remain; nested application works; tests pass.

### Step 3: Eliminate Nullable-Reference Warnings (CS8602)
**Problem:** 30 warnings in `CoopMenuUI.cs` from nullable dereferences.
**Action:** Add null guards, use `?.` consistently, annotate fields with `[NotNull]` where appropriate, enable `TreatWarningsAsErrors` in CI.
**Acceptance:** 0 warnings in Debug + Release.

---

## Phase 2 — Network Layer Hardening (Steps 4–7)

### Step 4: Lock Reentrancy Elimination (LiteNetLib)
**Problem:** `LiteNetLibTransport` fires `OnPeerConnected/Disconnected/DataReceived` *under* `_lock`; handlers calling `Send`/`Disconnect` re-enter the monitor (deadlock risk).
**Action:** Collect events into a `List<Action>` during the locked section; invoke them after releasing `_lock`.
**Acceptance:** `DisconnectInsidePeerDisconnectedCallback_DoesNotThrow` still passes; no deadlocks under stress.

### Step 5: Multi-Peer Architecture
**Problem:** Single-peer design (one remote player). Co-op with 3+ players impossible.
**Action:**
- Replace `_remotePeerId`/`_remoteEndPoint` with `Dictionary<NetworkPeerId, PeerState>`.
- Add peer join/leave lobby management in Steam transport.
- Route packets by sender peer id.
- Update clone spawner to manage N remote players.
**Acceptance:** 4-player lobby connects; transforms replicate to all; tests extended.

### Step 6: Packet Compression & Fragmentation
**Problem:** Large packets (inventory sync 512B, transforms) sent uncompressed; no fragmentation for >MTU.
**Action:**
- Add optional zlib/lz4 compression flag in `PacketHeader` (1 bit in reserved).
- Implement fragmentation/reassembly for payloads > 1200 bytes.
- Add `PacketFragmenter` utility in Core.
**Acceptance:** Inventory sync payload reduced ~40%; no fragmentation bugs in tests.

### Step 7: Reliable Transport Improvements
**Problem:** Fixed 100ms–2000ms backoff; no RTT-based adaptive timeout; no SACK.
**Action:**
- Implement RTT estimator (Jacobson/Karels) with smoothed RTT + variance.
- Adaptive RTO = SRTT + 4*RTTVAR, clamped [50ms, 3000ms].
- Add selective acknowledgment (SACK) blocks in ACK packets.
**Acceptance:** RTT tracking matches measured values within 20%; retransmission reduces under loss.

---

## Phase 3 — Game Synchronization (Steps 8–10)

### Step 8: Scene Transition Synchronization
**Problem:** No scene-transition sync packet (listed as known leftover). Players can desync on door/bench transitions.
**Action:**
- Implement `SceneTransition` packet (scene name, spawn point, fade state).
- Host broadcasts transition; client waits for host's transition packet before loading.
- Add loading-screen sync (both players ready → proceed).
**Acceptance:** Both players transition together; no "stuck in void" reports.

### Step 9: Boss & Enemy State Sync
**Problem:** Only player transforms synced; bosses/enemies not replicated. Co-op boss fights desync.
**Action:**
- Add `EnemySyncPacket` (id, position, health, state flags) for bosses + mini-bosses.
- Host-authoritative enemy AI; clients receive state updates.
- Damage attribution: both players' damage counts toward boss health.
**Acceptance:** Boss health shared; boss attacks visible to both; no duplicate bosses.

### Step 10: Inventory & Progression Completeness
**Problem:** Item unlocks synced but equipped loadout is local-only; map exploration, journal entries, quest flags not synced.
**Action:**
- Sync map reveal state (fog of war).
- Sync journal/discovery entries.
- Sync equipped crest + tools (optional host-authoritative loadout).
**Acceptance:** Both players see same map; unlocks propagate within 1s.

---

## Phase 4 — Security & Anti-Cheat (Steps 11–12)

### Step 11: Input Validation & Rate Limiting
**Problem:** Packet handlers trust remote data; no rate limiting; malformed packets can crash (caught but logged).
**Action:**
- Add per-peer rate limiter (token bucket) in transport dispatch.
- Validate all packet fields (ranges, enums, string lengths) before processing.
- Add `PacketValidator` in Core with schema per packet type.
- Reject packets exceeding size limits (DoS protection).
**Acceptance:** Fuzzing 10k malformed packets → 0 crashes; rate limit triggers at 1000 pkt/s.

### Step 12: Connection Security (Steam + UDP)
**Problem:** UDP has no authentication; room codes have 2-bit CRC (weak); Steam accepts any lobby member.
**Action:**
- Add challenge-response handshake (host sends nonce, client signs with shared secret derived from room code).
- Upgrade CRC to CRC-8 (already in 12-char codes); deprecate 8-char compact codes.
- Steam: verify lobby owner matches expected; reject unknown members.
**Acceptance:** Replay attacks rejected; spoofed packets dropped.

---

## Phase 5 — UX & Observability (Steps 13–15)

### Step 13: In-Game Settings Panel & Keybinds
**Problem:** Settings only via BepInEx config file; no in-game keybind customization.
**Action:**
- Add Settings tab enhancements: keybind rebinding, quality presets (Low/Medium/High).
- Add F9 to toggle spectator camera, F11 for diagnostics overlay.
- Persist settings to BepInEx config automatically.
**Acceptance:** All settings adjustable in-game; changes persist across restarts.

### Step 14: Enhanced Diagnostics & Telemetry
**Problem:** Diagnostics only shows current rates; no historical graphs; no crash reporting.
**Action:**
- Add rolling 60s graphs for ping, packet loss, bandwidth (using IMGUI).
- Add `/coop diag` console command to dump full state to file.
- Add optional anonymous telemetry (opt-in) for crash rates and connection success rates.
**Acceptance:** Diagnostics overlay shows 4 live graphs; diag dump < 100ms.

### Step 15: Automated Integration Testing
**Problem:** Only unit tests (603); no end-to-end connection tests; no load testing.
**Action:**
- Add `DualClientTestHarness` (already exists) integration tests to CI.
- Add load test: 2 clients, 1000 packets/s for 60s, verify 0 drops on localhost.
- Add chaos test: random packet loss 5-30%, verify reconnect within 10s.
**Acceptance:** Integration tests run in CI; load test passes; chaos test passes.

---

## Cross-Cutting Concerns

### Documentation
- Update `docs/teamwork-spec.md` with new milestones.
- Add `docs/architecture.md` with service diagram.
- Add `docs/protocol.md` with packet format spec.

### CI/CD
- GitHub Actions: build + test on push; publish release on tag.
- Add code coverage reporting (target 80% for Core).

### Versioning
- Bump to v2.0.0 after Phase 1–3 complete (breaking protocol changes).
- Maintain v1.x for bug fixes only.

---

## Priority Order (if time-constrained)

1. Step 4 (lock reentrancy) — prevents deadlocks, low risk
2. Step 8 (scene sync) — biggest UX pain point
3. Step 11 (input validation) — security
4. Step 5 (multi-peer) — biggest feature gap
5. Step 1 (decompose god object) — maintainability enabler
6. Steps 2, 3 (code quality) — prerequisite for larger refactors
7. Steps 6, 7 (transport) — performance
8. Steps 9, 10 (game sync) — completeness
9. Steps 12, 13, 14, 15 (security/UX/testing) — polish

---

## Estimated Effort
- Phase 1 (3 steps): ~2 weeks
- Phase 2 (4 steps): ~3 weeks
- Phase 3 (3 steps): ~3 weeks
- Phase 4 (2 steps): ~2 weeks
- Phase 5 (3 steps): ~2 weeks
- **Total: ~12 weeks** for full implementation

Each step should be a separate PR with tests, reviewed before merge. No step should break the 603 existing tests without explicit justification.
