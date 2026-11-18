# Full Repository Audit Campaign Report

Date: 2026-11-18  
Repository: `D:/antigravity/silksong_coop_mod`  
HEAD: `a64ddc43b0df3e808a44025f94af88a767df14ac`  
Working tree: dirty (existing in-progress changes preserved)  
Protocol under review: v12  
Plugin version: 3.3.9  

## 1. Campaign result

This campaign produced an actionable audit report and remediation plan. No product code was changed during the campaign. Audit documentation was added/updated:

- `docs/teamwork-spec.md`
- `docs/audit-campaign-log.md`
- `docs/audit-campaign-report.md`

The audit confirms that the repository builds and its Core test suite passes, but it does not establish full runtime correctness. Several findings remain traced rather than dynamically reproduced, and Mod integration cannot be validated without Silksong assemblies and a live game.

## 2. Baseline verification

| Check | Result |
|---|---|
| Release build | PASS: 0 warnings, 0 errors |
| Debug build | PASS: 0 warnings, 0 errors |
| Core tests | PASS: 1438 passed, 0 failed, 0 skipped |
| Release metadata preflight (`-AllowDirty -MetadataOnly`) | PASS |
| Full preflight with `ManagedDir` | PASS (prior campaign baseline) |
| NuGet vulnerability scan (including transitive) | PASS: no vulnerable packages reported |
| Game binding check | PASS: 10 methods and required progression fields verified |
| Git whitespace check | PASS: no whitespace errors; LF/CRLF conversion notices emitted |

## 3. Findings

### F-01 — Reconnect can resume an incomplete wallet-seed bootstrap
- Severity: **High**
- Status: **Mitigated in code; requires game reproduction**
- Category: COMPLEX_FIX
- Location: `src/SilksongCoop.Mod/SilksongCoopPlugin.cs:1597-1679`, `:1981-1987`
- Mechanism: A reconnect with the same `PlayerData` and room sets `resume = true`. The peer is then marked `resumedPeer` when its save instance matches. In that path the host skips wallet-seed delivery, the client does not set `_awaitingWalletSeed`, and `BeginWalletSeed()` is not called. If the previous bootstrap never completed, currency synchronization can resume without an authoritative initial balance. Resetting `_walletSeedAssembler` does not repair the already-recorded peer identity.
- Impact: Currency divergence or missing bootstrap after an interrupted connection.
- Validation needed: Drop the connection before chunk 0, between chunks, and after application; reconnect to the same room/save and verify equal balances and exactly-once deltas for both peers.

### F-02 — Unconfirmed currency receipt has no in-session recovery
- Severity: **Medium**
- Status: **Traced; conditional on nonfatal send refusal**
- Category: COMPLEX_FIX
- Location: `src/SilksongCoop.Mod/SilksongCoopPlugin.cs:1960-1975`; `src/SilksongCoop.Mod/Services/VitalsSyncManager.cs:174-186`, `:218-234`
- Mechanism: After a currency packet is applied, the receiver sends one receipt. If that send fails, only a warning is logged. The sender retains the transaction in `_currencySent`, so later journal flushes skip it. There is no timeout or retry to remove or resend the unconfirmed entry. Journal exhaustion can follow if refusal persists.
- Important distinction: Ordinary reliable packet loss after acceptance is handled by transport retransmission. This finding concerns a transport send refusal that does not immediately disconnect the session.
- Validation needed: Transport accepts a transaction, refuses exactly its first receipt, then becomes healthy; both wallets must apply once and the sender journal must empty without reconnect.

### F-03 — Claimed-pickup catch-up is truncated
- Severity: **Medium to High, depending on live reproduction**
- Status: **Mitigated in code; requires game reproduction**
- Category: COMPLEX_FIX or bounded LOCAL_FIX depending on chosen design
- Location: `src/SilksongCoop.Core/State/VitalsSyncService.cs:917-958`; related constants at `:51-52`
- Mechanism: The service retains up to 1024 claimed pickups, but `BroadcastClaimedPickups()` sends only the newest 64. A reconnecting peer may not receive claim markers for older drops and could collect them again.
- Impact: Possible duplicate collection or currency divergence after long sessions with more than 64 claimed drops.
- Validation needed: Reconnect after more than 64 claimed pickups and verify that older drops are disabled in the reconnecting peer's scene.

### F-04 — E2E test suite cannot exercise Mod code and can mask regressions
- Severity: **High (verification gap)**
- Status: **Mitigated in current tree; production-connected source checks added**
- Verification: `SilksongCoop.ModSourceChecks` passed **54/54** whitelist/hook checks; `WalletPumpChecks` passed **13/13** production retry checks; `SharedSaveBoolPolicyTests` is included in the full suite.
- Mutation proof: an authorization-bypass mutation failed the source checks; a wallet-throttle-bypass mutation failed 5/13 checks; removing the linked production source caused compilation failure.
- Category: COMPLEX_FIX / test infrastructure
- Location: `tests/SilksongCoop.Tests/SilksongCoop.Tests.csproj:22-24`; `tests/SilksongCoop.Tests/E2ERemediationTests/RemediationTestHarness.cs:56-138`
- Mechanism: The test project references Core only. The helper attempts reflection into `SilksongCoop.Mod.Hooks.InventoryHooks.IsAllowedSaveBool`; if the assembly is unavailable or reflection throws, it silently falls back to embedded specification data. `PumpWalletSeedSafe` is a separate implementation of the production retry loop. These tests should be described as engine-agnostic simulation tests, not end-to-end production tests.
- Supporting evidence: `tests/SilksongCoop.Tests/E2ERemediationTests/RemediationTestHarness.cs:95-138` duplicates production retry logic.
- Fix direction: Add a production-connected seam or compile Mod against shims; make missing production code fail coverage explicitly instead of choosing a fallback. Keep existing tests unchanged; add new coverage.

### F-05 — Static source-text tests are brittle
- Severity: **Medium to Low**
- Status: **Verified**
- Category: COMPLEX_FIX / test quality
- Location: `tests/SilksongCoop.Tests/Milestone5RemediationTests.cs:687-735`; `tests/SilksongCoop.Tests/State/SessionStateWiringTests.cs:15-131`; `tests/SilksongCoop.Tests/Challenger2Milestone4InvariantsStressTests.cs:572-624`
- Mechanism: Several tests read repository source files and search for literal text or patterns. They can break on harmless refactors or partial-class movement and may miss sites that are not enumerated.
- Fix direction: Prefer executable composition checks where feasible; retain static checks only where runtime access is impossible and document them.

### F-06 — Preflight has operational sharp edges
- Severity: **Low to Medium (release process risk)**
- Status: **Verified**
- Category: LOCAL_FIX
- Location: `scripts/preflight-release.ps1:85-97`, `:120-175`; `src/SilksongCoop.Mod/SilksongCoop.Mod.csproj:13-21`
- Mechanism: Metadata-only and dirty-tree modes intentionally bypass checks. RuntimeChecks is outside the solution and is not restored by the solution build; with `-NoRestore`, callers must restore it separately. Game binding validation is silently skipped when `ManagedDir` is unset.
- Fix direction: Make skipped game-assembly verification explicit or fatal in release mode; ensure RuntimeChecks is restored before `--no-restore` execution.

### F-07 — Repository hygiene and generated artifacts
- Severity: **Low**
- Status: **Verified as checkout hygiene, not proven committed leakage**
- Category: LOCAL_FIX / repository hygiene
- Location: `.gitignore:1-35`; local `graphify-out/`, `artifacts/`, `dist/`, `final/`, `BenchmarkDotNet.Artifacts/`
- Mechanism: The checkout contains generated or historical artifacts. Most are ignored, but `graphify-out/` is not ignored, creating large untracked noise. No `.gitattributes` exists, and `core.autocrlf=true` produced LF/CRLF conversion notices during `git diff --check`.
- Fix direction: Decide commit-vs-ignore policy for `graphify-out/`; add `.gitattributes`; remove stale backup DLLs from `artifacts/`.

### F-08 — Documentation map is stale
- Severity: **Low**
- Status: **Verified**
- Category: LOCAL_FIX
- Location: `docs/README.md:24-28`
- Mechanism: The docs map links the 2026-11-14 verification record while omitting the newer 2026-11-16 verification record and audit campaign files.
- Fix direction: Update the docs map when newer audit/verification documents are promoted.

## 4. Conditions already addressed in the current tree

| Item | Status | Evidence |
|---|---|---|
| L1 — release tag could pass CI without Mod verification | Closed / stricter | `.github/workflows/ci.yml:211-224`: `REQUIRES_MOD` is true for `v*` tags even when the runner gate is disabled |
| L2 — wallet-seed retry was FPS-dependent | Closed | `src/SilksongCoop.Mod/SilksongCoopPlugin.cs:110-114`, `:1734-1781`: real-time deadline and throttled retry interval |
| C2 — pending counter application could block later counters | Mitigated in disconnect path; residual validation remains | `src/SilksongCoop.Core/State/InventorySyncService.cs:393-414`; `src/SilksongCoop.Mod/SilksongCoopPlugin.cs:1400-1410` calls `CancelPendingCounterApplication` on peer disconnect while preserving the sequence watermark |
| F4 — CI Mod gating | Closed / stricter by design | `.github/workflows/ci.yml:196-226`; policy documented in `docs/ci-mod-job.md` |

## 5. Items reviewed and not retained as independent findings

- **Stale VitalsSync currency watermarks on resume**: Not independently demonstrated. Retaining sequence watermarks across a same-save reconnect is intentional replay protection. It matters for F-01 only when bootstrap completion was not established.
- **Client wallet-seed waiting only for the primary peer**: Not established as a defect. The current client architecture uses the primary peer as the authoritative wallet source, while the host sends seeds per registered peer. Marked **requires 3+ game validation**, not verified.
- **“Mod is not compiled in the test project”**: Verified as a coverage boundary, but it is the root cause of F-04 and is not reported separately.
- **VitalsSyncService stale `_lastCurrencySequences`**: Not independently demonstrated; relevant only through F-01.

## 6. Areas that appear sound

- `PacketValidator` field ranges and NaN/Infinity checks for transform, vitals, currency, death, enemy, world, persistent, and quest packets.
- LiteNetLib handshake, room-code challenge/response, MAC validation, sender validation, rate limiting, and reliable reorder buffering.
- Inventory transaction deduplication, counter pending/retry queue, and sequence watermark logic.
- Death epoch adoption, per-peer death/revive tracking, revive target selection, and bench respawn sequencing.
- Enemy authority policy, deferred claims, snapshot stale/death handling.
- Quest-state allowlist and monotonic bool/int application.

## 7. Milestone log

| Milestone | Status | Result |
|---|---|---|
| M1 Repository inventory and baseline verification | Complete | Build/test/preflight/vulnerability checks recorded |
| M2 Runtime architecture and state synchronization audit | Complete | F-01, F-02, F-03 identified; core subsystems reviewed |
| M3 Test fidelity, CI, release automation, and documentation audit | Complete | F-04 to F-08 identified |
| M4 Independent audit consolidation | Complete | Auditor downgraded/merged several findings and added F-03 |
| M5 Final report and remediation plan | Complete | This report |

## 8. Recommended remediation order

1. **F-01** — Define and enforce completed wallet-seed bootstrap state before allowing resume.
2. **F-03** — Raise or paginate claimed-pickup catch-up, or otherwise prevent duplicate collection after reconnect.
3. **F-02** — Add bounded receipt retry or timed replay of unconfirmed currency transactions.
4. **F-04** — Add production-connected test coverage for Mod paths.
5. **F-05** — Reduce brittle static source-text assertions.
6. **F-06** — Make preflight skip states explicit or fatal in release mode.
7. **F-07 / F-08** — Clean checkout hygiene and update documentation map.

## 9. Residual risks

- F-04 now has production-source coverage for the whitelist and wallet retry paths, but the source-check shims do not replace a live Unity/Steam run.
- No two-client Unity/Steam runtime validation was performed.
- The Mod project was not compiled in this environment because it requires proprietary Silksong assemblies.
- Core-only tests do not exercise Mod integration.
- Several high-impact findings are traced but not dynamically reproduced.
- The working tree remains dirty; future changes must be attributed carefully.
- Protocol v12 behavior and its assumptions are not fully documented beyond `docs/protocol-v12.md`.
- Release artifacts in `artifacts/`, `dist/`, and `final/` may be inconsistent with the current source tree.