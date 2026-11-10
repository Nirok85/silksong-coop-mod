# Release checklist

> Substitute the release version for `<version>` below. The checklist is intentionally
> version-neutral so it cannot silently describe a previous release.

## Pre-release

- [ ] `pwsh ./scripts/preflight-release.ps1 -AllowDirty` passes while preparing the release commit.
- [ ] After the release commit, `pwsh ./scripts/preflight-release.ps1` passes on a clean tree (versions, changelog, both READMEs, ROADMAP, build, tests).
- [ ] All tests pass and none are unexpectedly skipped (do not gate on a hard-coded count).
- [ ] Core and Mod build with 0 compiler warnings (TreatWarningsAsErrors=true; NU1900 is a NuGet network warning).
- [ ] Protocol version is unchanged or bumped and documented (`docs/protocol-v10.md` and `docs/state-authority.md`).
- [ ] Plugin version and assembly versions bumped to `<version>` in both csproj files and `PluginVersion`.
- [ ] Release commit SHA recorded together with the version.
- [ ] Migration guide updated.
- [ ] CHANGELOG.md updated (`[Unreleased]` moved to `[<version>]`).
- [ ] README.md / README.ru.md updated - limits section matches what is implemented.

## Build

- [ ] Clean checkout build (`dotnet build` from scratch).
- [ ] `pwsh ./scripts/package-release.ps1 -ManagedDir <path> -BepInExSha256 <pinned-hash>` (or the bash script) produced both archives plus `SilksongCoop_v<version>_SHA256SUMS.txt`.
- [ ] `pwsh ./scripts/verify-artifacts.ps1 -Version <version>` passes: hashes match, both DLLs are present, and no proprietary game assemblies leaked.
- [ ] `pwsh ./scripts/release-notes.ps1 -Version <version> -OutputPath dist/<version>/release-notes.md` produces the GitHub release body from CHANGELOG.md.
- [ ] Run the packager twice and confirm the second run does not keep files removed from staging (no stale ZIP entries).
- [ ] Extract both archives on Windows and Unix and confirm entry names use `/`, required docs/licenses are present, and no `bin/`/`obj/` or secrets are included.
- [ ] Verify the DLL `ProductVersion` inside each archive starts with the release version.
- [ ] Re-download the published assets and compare their SHA256 with the manifest.
- [ ] Optional: `dotnet run --project tools/SilksongCoop.Benchmarks -c Release -- --filter "*" --job dry` succeeds.

## CI

- [ ] Core job green.
- [ ] Mod job green, or a maintainer recorded a manual Mod build of the exact
      release commit (SHA, game version, OS and result). The job is opt-in via
      repository variable `SILKSONG_MOD_RUNNER_ENABLED=true`; a skipped job means
      Mod compilation was not checked. A green Core job alone is not a release gate.
- [ ] No workflow scope issues (the workflow file needs a token with the `workflow` scope to push).

## Smoke tests (manual)

Record game version, mod version, release commit SHA, OS, transport and pass/fail for every run.

- [ ] Host + client connect (UDP and Steam).
- [ ] Version mismatch rejected.
- [ ] Dual join rejected on host.
- [ ] Death/revive visibility, including a partner who dies immediately after a bench respawn.
- [ ] Simultaneous hits: both players hit the same enemy and the combined damage is not lost.
- [ ] Snapshot + late hit: a hit that is already in the host snapshot is not counted twice.
- [ ] Pending world state: a door/chest changed while the other player is in another room does not roll back after that player enters.
- [ ] Scene transition + cross-scene revive falls back to the local bench.
- [ ] Reconnect to the same host after a graceful disconnect and after a timeout.
- [ ] Late joiner catch-up: third player sees existing enemy HP/deaths, opened doors and inventory.
- [ ] Steam 3+ lobby (`SteamMaxPlayers=3`): all peers see each other, per-peer vitals/HUD work, death/revive affects only the correct player, a quiet peer is not timed out, and primary promotion keeps the session alive.
- [ ] Quest interaction: offer/progress a quest and verify the partner sees the same quest state; no duplicate flags are re-sent.
- [ ] Diagnostics bundle writes a file and the room code is redacted.

## Publish

- [ ] Tag the tested commit SHA as `vX.Y.Z` for a stable release or
      `vX.Y.Z-beta.N` for a beta.
- [ ] Publish the GitHub release with notes. Mark a beta as a prerelease
      (`--prerelease` with the GitHub CLI). Use the numeric version `X.Y.Z`
      in plugin metadata and artifact names for either release type.
- [ ] Attach both ZIP archives and `SilksongCoop_v<version>_SHA256SUMS.txt`.
- [ ] Announce breaking changes (Steam compares the full mod version).

## Rollback

- [ ] Keep the previous release artifacts and checksums available.
- [ ] Document rollback steps.


## Live checks against the installed game

Use disposable copies of saves on both clients.

- Connect during scene loading. Check initial vitals, inventory, wallet and checkpoint after the save loads.
- Switch rooms in quick succession and revive while the partner changes scenes. Check clone visibility and spawn position.
- Spend currency during an outage, reconnect to the same room and check each expense appears once on both peers.
- Open inventory and collect an upgrade remotely. Check the apply hook emits no echo.
- Invite, cancel, rejoin and disconnect in Steam. Verify stale lobby callbacks, timeout, kick, clone cleanup and plugin unload.
- Exercise a real three-player lobby, then remove one guest and check surviving peers.
