# Documentation map

Player instructions live in the root [README](../README.md) and
[Russian README](../README.ru.md). They describe the current release.

## Maintainer references

- [Architecture](architecture.md) — current project structure and data flow.
- [Protocol v12](protocol-v12.md) — current wire contract for mod v4.0.1 and v4.0.0; older v3.3.9 beta builds used [v11](protocol-v11.md).
- [State authority](state-authority.md) — current state ownership and replication rules.
- [Testing](testing.md) — automated coverage and live-game test boundaries.
- [Game compatibility](game-compatibility.md) — build and runtime compatibility checks.
- [Mod CI job](ci-mod-job.md) — runner requirements and CI behavior.
- [Release process](RELEASING.md) and [release checklist](release-checklist.md).
- [Security policy](../SECURITY.md) and [third-party notices](../THIRD-PARTY-NOTICES.md).

## Historical records

Versioned smoke reports, audit reports, campaign logs, protocol v8/v10
specifications, and superseded plans record past project states. Their version,
test counts, protocol numbers, and conclusions are historical snapshots. Use the
current references above when making changes or preparing a release.

## Latest verification and planned work

Current release is v4.0.1. Live Steam groups, Unity scene transitions and revive remain unverified. See the release notes in [CHANGELOG](../CHANGELOG.md) and the current [game compatibility status](game-compatibility.md).


- [Beam Max implementation and verification](beam-max-execution.md) — historical checkpoint before v4.0.0 release.

- [Verification status, 2026-11-14](verification-2026-11-14.md)
- [Verification status, 2026-11-16](verification-2026-11-16.md)
- [Full repository audit campaign report, 2026-11-18](audit-campaign-report.md)
- [Runtime visual/environment work checklist](../TODO.md)

