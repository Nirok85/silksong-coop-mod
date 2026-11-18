# Teamwork Audit Specification

## Goal

Perform a full-repository audit of the Silksong co-op mod and deliver an actionable, prioritized findings report with a milestone-based remediation plan. The audit covers source code, tests, documentation, CI/release automation, packaging, and repository hygiene. It must distinguish verified findings from suspected risks and must not present green builds/tests as proof of full runtime correctness.

## Scope

- Repository root: `D:/antigravity/silksong_coop_mod`
- Current branch state: dirty working tree on top of `a64ddc4` (`docs: record beta.3 verification limits and runtime improvement plan`)
- Protocol under review: v12
- Plugin version under review: 3.3.9
- Areas:
  - `src/SilksongCoop.Core`
  - `src/SilksongCoop.Mod`
  - `tests/SilksongCoop.Tests`
  - `tests/SilksongCoop.RuntimeChecks`
  - `tools/SilksongCoop.BindingCheck`
  - `scripts/`
  - `.github/workflows/`
  - `docs/`
  - release artifacts and packaging metadata where present

## Boundaries

- No hard exclusions were requested.
- This campaign is initially an audit/reporting campaign, not an automatic remediation campaign.
- Code changes require a separate explicit approval after the audit report and milestone plan are reviewed.
- Destructive commands, file deletion, dependency upgrades, and protocol changes are out of scope unless separately approved.
- Existing dirty changes must be preserved and not overwritten.
- Findings must be tied to exact files and, where practical, line ranges.

## Success Criteria

- Produce a prioritized findings report with severity, evidence, affected files, and recommended fix scope.
- Separate findings into:
  - verified by build/test/static analysis
  - traced but not dynamically reproduced
  - requiring game/runtime validation
- Provide a 3-7 milestone remediation plan with owners, checks, and definition of done.
- Run and record the available build/test/metadata checks on the final audit state.
- Clearly list residual risks and unperformed validation.

## Non-Goals

- No new feature development.
- No refactoring for style alone.
- No claim that all runtime behavior is correct.
- No remote GitHub Actions execution unless explicitly requested.
- No real two-client game soak/fuzz campaign unless requested and feasible.

## Known Baseline

- Release build: 0 errors, 0 warnings.
- Debug build: 0 errors, 0 warnings.
- Tests: 1438 passed, 0 failed, 0 skipped in the latest local run.
- Prior audit: `docs/audit-2026-11-16.md`.
- Known high-risk areas from prior audit:
  - reconnect/wallet seed bootstrap
  - counter transaction pending state
  - E2E test fidelity
  - CI Mod coverage gating
  - FPS-dependent retry timing

## Risks

- Dirty worktree makes attribution of future changes harder.
- Mod integration cannot be fully validated without Silksong assemblies and a live game.
- Tests are Core-focused and may not exercise production Mod paths.
- Protocol v12 changes are large and may have undocumented assumptions.
- Existing audit findings may be partially fixed; status must be re-verified rather than assumed.
- Release artifacts and docs may be inconsistent with code.
