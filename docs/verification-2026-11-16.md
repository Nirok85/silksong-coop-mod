# Stability changes verification — 2026-11-16

Status: local, unreleased development changes on the 3.3.9 checkout. Published beta.3 remains on protocol v11; this checkout uses v12. No commit, tag, push, archive publication or game installation was performed during this work.

## Completed automated gate

Command: `scripts/preflight-release.ps1 -AllowDirty -NoRestore`, with `ManagedDir` pointing to the installed Silksong Managed directory.

- Full solution Release build with warnings treated as errors: zero errors and zero warnings.
- xUnit: 1437 passed, zero failed, zero skipped.
- Production-source UnityGroundProber adapter checks: 15 passed against API shims.
- BindingCheck: ten game methods, the currency queue field and four persistent Int32 progression fields verified against installed game metadata.
- Metadata/version checks and `git diff --check` passed.
- Local code-only graph updated to 4129 nodes and 14445 edges with `graphify update . --no-cluster`.

## Main changes

- Causal wallet snapshots carry origin sequence boundaries and credited physical pickup keys. Up to 1024 keys are assembled atomically from eight bounded reliable parts. Currency journals preserve order on refused sends and retain entries until receipts.
- Counter replay protection survives GUID eviction. Failed inventory application has a success contract and a bounded retry queue.
- Lifecycle snapshots establish per-peer epochs and report actual life state. Stale bench events cannot resurrect a peer.
- Reliable unordered receive windows preserve missing packets. Deferred enemy damage proof tracks sender/sequence, bounds memory and expires stale proof.
- Revive clearance uses real world collider bounds. Ground rays exclude triggers/self, grow within a bound and fail closed on saturation.
- Fragment progression accounts for local and remote earned upgrades, merges coherent initial maximum/partial baselines and commits earned credit after native application succeeds. Native conversion threshold states are deferred.
- Permanent silk progress uses `silkSpoolParts`, two parts per capacity upgrade, and raw `silkMax`. The transient `silkParts` resource is excluded.
- Runtime tick-rate changes are wired to the active orchestrator. CI and local preflight run production-source adapter checks separately from simulation tests.

## Remaining validation and limitations

No real gameplay session was run for these changes. API shims do not reproduce Unity physics. Two-instance testing is required for joins during pickups, scene changes, native fragment animation/conversion and reconnects.

Historical overlapping fragment pickups across independent saves cannot be proven unique from counts alone. Initial cross-channel fragment catch-up remains unproven under live simultaneous pickups. Conservative baseline merging does not supply persistent collectible identity.

Deferred damage has no wire encounter ID. Known reloads, HP increases and a 60-second proof lifetime reject stale evidence, but cannot identify every previously unseen delayed packet from another encounter.

All peers must use protocol v12 together. Do not publish this checkout as gameplay-verified based solely on the automated gate.
