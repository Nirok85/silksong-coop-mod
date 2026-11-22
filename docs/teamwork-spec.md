# Historical teamwork spec — BEAM_MAX campaign

## Goal
Execute the full BEAM_MAX plan (tasks 01–13) for the Silksong co-op mod: baseline, production test coverage, reproducible session harness, entry/recovery stages, currency/inventory/permanent upgrades, scene/combat/death boundaries, queue metrics/fairness, Steam 3–4, UI/diagnostics, performance, CI/docs/packaging, 45-minute soak, final verification. Deliver predictable join, transitions, and reconnect with visible sync stage and failure reason; single-counting of identifiable shared progress; diagnostics without secrets.

## Scope
- Tasks 01–13 from `BEAM_MAX_PLAN.txt`.
- Repository: `D:\antigravity\silksong_coop_mod`, branch `master`, HEAD `a90f51a`, mod 3.3.9, protocol v12.
- Environment: ManagedDir game assemblies available; self-hosted Mod runner available.

## Boundaries
- No files are off-limits.
- Preserve binary compatibility of protocol v12. Any new field or mandatory ack requires a documented migration and a recorded v12 limitation.
- Preserve owner's work; no destructive git commands.
- Changes pushed only on explicit request; release install on game PCs only on explicit request.

## Acceptance criteria
- All local gates green: Core tests, RuntimeChecks, ModSourceChecks, WalletPumpChecks, preflight.
- 45-minute soak (3 phases × 15 min) completes with zero invariant violations.
- Production test coverage exercises the same component the plugin uses (PeerHandshakeCoordinator), with mutation proof.
- Reproducible session harness: deterministic replay for a given seed.
- Visible sync stages and failure reasons in UI; diagnostics export redacts secrets.
- Queue metrics within limits; recovery to empty by deadline.

## Risks
- Game assemblies/runner availability may change mid-campaign.
- 45-min soak requires sustained runtime; session time limits may interrupt.
- Protocol v12 limitations may block some features (generation tracking).
- Test harness fidelity vs real Unity physics remains a known gap.

## Checkpoint log
See `docs/beam-max-execution.md`.