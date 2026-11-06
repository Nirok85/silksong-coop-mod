# Release checklist (3.3.0)

## Pre-release

- [ ] `pwsh ./scripts/preflight-release.ps1 -AllowDirty` passes while preparing the release commit.
- [ ] After the release commit, `pwsh ./scripts/preflight-release.ps1` passes on a clean tree (versions, changelog, both READMEs, ROADMAP, build, tests).
- [ ] All tests pass and none are unexpectedly skipped (do not gate on a hard-coded count).
- [ ] Core and Mod build with 0 compiler warnings (TreatWarningsAsErrors=true; NU1900 is a NuGet network warning).
- [ ] Protocol version is unchanged or bumped and documented (`docs/protocol-v8.md`).
- [ ] Plugin version and assembly versions bumped to 3.3.0.
- [ ] Migration guide updated.
- [ ] CHANGELOG.md updated.
- [ ] README.md / README.ru.md updated - limits section matches what is implemented.

## Build

- [ ] Clean checkout build (`dotnet build` from scratch).
- [ ] `pwsh ./scripts/package-release.ps1 -ManagedDir <path> -BepInExSha256 <pinned-hash>` (or the bash script) produced both archives plus `SilksongCoop_v<version>_SHA256SUMS.txt`.
- [ ] Run the packager twice and confirm the second run does not keep files removed from staging (no stale ZIP entries).
- [ ] Extract both archives on Windows and Unix and confirm entry names use `/`, required docs/licenses are present, and no `bin/`/`obj/` or secrets are included.
- [ ] Verify the DLL `ProductVersion` inside each archive starts with the release version.
- [ ] Re-download the published assets and compare their SHA256 with the manifest.

## CI

- [ ] Core job green.
- [ ] Mod job green, or a maintainer recorded a manual Mod build of the exact
      release commit (SHA, game version, OS and result). The Mod job is skipped when
      `ENABLE_MOD_CI` is not enabled; a green Core job alone is not a release gate.
      Same-repository PRs run it automatically; fork PRs are excluded because
      untrusted code must not run on the self-hosted runner, so build them manually.
- [ ] No workflow scope issues (the workflow file needs a token with the `workflow` scope to push).

## Smoke tests (manual)

Record game version, mod version, OS, transport and pass/fail for every run.

- [ ] Host + client connect (UDP and Steam).
- [ ] Version mismatch rejected.
- [ ] Dual join rejected on host.
- [ ] Death/revive visibility, including a partner who dies immediately after a bench respawn.
- [ ] Simultaneous hits: both players hit the same enemy and the combined damage is not lost.
- [ ] Scene transition + cross-scene revive falls back to the local bench.
- [ ] Reconnect to the same host after a graceful disconnect and after a timeout.
- [ ] Late joiner catch-up: third player sees existing enemy HP/deaths, opened doors and inventory.
- [ ] Steam 3+ lobby (`SteamMaxPlayers=3`): all peers see each other, per-peer vitals/HUD work, death/revive affects only the correct player, a quiet peer is not timed out, and primary promotion keeps the session alive.
- [ ] Quest interaction: offer/progress a quest and verify the partner sees the same quest state.
- [ ] Diagnostics bundle writes a file and the room code is redacted.

## Publish

- [ ] Tag `v3.3.0`.
- [ ] GitHub release with notes.
- [ ] Attach both ZIP archives and `SilksongCoop_v<version>_SHA256SUMS.txt`.
- [ ] Announce breaking changes.

## Rollback

- [ ] Keep the previous release artifacts and checksums available.
- [ ] Document rollback steps.
