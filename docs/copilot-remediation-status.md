# Copilot findings remediation status

This document tracks the findings reported by GitHub Copilot against the current master state. It is the single status source for those items.

## Status legend

- Fixed: changed in code, docs, or CI and verified by available gates.
- Partial: mitigation or verification seam exists, but live-game/runtime proof is still missing.
- Planned: not yet implemented; requires game assemblies, a self-hosted runner, or a larger design change.

## Findings

| Finding | Status | Evidence / next step |
|---|---|---|
| Unfinished wallet-seed and inventory retry state after reconnect | Partial | WalletSeedRetry.cs, InventoryRetry.cs; source checks 54/13; live reconnect soak still required. |
| Pending currency receipt has no bounded wait/retry | Partial | Bounded receipt retry exists in current wallet path; refusal-after-accept scenario still needs in-game proof. |
| Abandoned pending counter application after peer/state change | Partial | CancelPendingCounterApplication on disconnect; same-save reconnect and peer replacement need live proof. |
| Scene/enemy/boss state sync under script differences | Planned | Requires Unity/Harmony target checks and two-client scene transition runs. |
| Handshake, peer identity and room-state replay correctness | Planned | Property/replay tests and packet fuzzing are still needed. |
| Steam P2P vs UDP ordering, sequence and reconnect divergence | Planned | Transport-level differential tests and live NAT/VPN runs are required. |
| Experimental 3+ peer mode mostly tested with two clients | Partial | Core is per-peer; source/runtime checks are still two-client oriented. |
| Races between network callbacks, Unity frame loop and retry pumps | Partial | Real-time wallet retry and bounded pumps added; concurrency stress tests remain. |
| UDP symmetric NAT, VPN, jitter, loss and port conflicts | Planned | Network-condition lab validation is required. |
| Mod build skipped on GitHub-hosted CI | Fixed | Mod remains self-hosted by design; non-game source checks run in hosted CI. |
| Tagged release verification skipped when runner is disabled | Fixed | mod-status no longer fails solely because the protected runner is closed. |
| Tests use mock/fake/Core harnesses instead of production Mod integration | Partial | ModSourceChecks and WalletPumpChecks compile production files; deeper Mod integration still needs game assemblies. |
| Helpers may hide production mismatches | Partial | Fallback helper removed from whitelist path; wallet helper remains historical and is superseded by source checks. |
| No mutation testing for whitelist and wallet pump | Partial | Manual mutation proof recorded; automated mutation harness is still a future task. |
| Insufficient real-game death/revive/bench respawn coverage under packet loss | Planned | Requires in-game soak tests. |
| Packet overflow, ACK queues and queue backpressure | Planned | Queue limits and backpressure tests are still needed. |
| Unverified outbound queue limit and prioritization under high traffic | Planned | Transport queue policy needs explicit tests. |
| Interpolator clock stability under burst, rollback and late replay | Planned | Interpolation stress tests are needed. |
| No malformed-input fuzzing or engine-level crash paths | Planned | Fuzz harness and crash-path tests are needed. |
| No proven log redaction policy for Steam IDs, room codes and scene names | Partial | LogRedactor covers room codes and credentials; Steam ID and scene-name redaction can be extended. |
| Protocol v12 docs partially stale vs v8-v11 | Fixed/Partial | protocol-v12.md, architecture.md, game-compatibility.md and docs/README.md updated; older protocol files remain historical. |
| Floating GitHub Action tags instead of full pinning | Fixed | Actions in .github/workflows/ci.yml are pinned to commit SHAs. |
| No NuGet lock / immutable dependency policy | Fixed | packages.lock.json files added and CI restore uses --locked-mode. |
| Contradictory audit/TODO/roadmap documentation | Partial | This status document added; full consolidation of historical documents is still pending. |

## Verification snapshot

- Release build: PASS, 0 errors, 0 warnings
- Core tests: 1473/1473
- Unity ground-prober checks: 15/15
- InventoryHooks source checks: 54/54
- WalletPump source checks: 13/13
- Release preflight: PASS
