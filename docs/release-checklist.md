# Release checklist (3.2.0)

## Pre-release

- [ ] `pwsh ./scripts/preflight-release.ps1 -AllowDirty` passes (versions, changelog, README, build, tests).
- [ ] All tests pass (850+).
- [ ] Core and Mod build with 0 compiler warnings (TreatWarningsAsErrors=true; NU1900 is a NuGet network warning).
- [ ] Protocol version is unchanged or bumped and documented (`docs/protocol-v8.md`).
- [ ] Plugin version and assembly versions bumped to 3.2.0.
- [ ] Migration guide updated.
- [ ] CHANGELOG.md updated.
- [ ] README.md / README.ru.md updated - limits section matches what is implemented.

## Build

- [ ] Clean checkout build (`dotnet build` from scratch).
- [ ] `pwsh ./scripts/package-release.ps1 -ManagedDir <path>` (or the bash script) produced both archives and SHA256.
- [ ] No `bin/`/`obj/` or secrets in package.
- [ ] Verify the DLL `ProductVersion` inside each archive starts with the release version.

## CI

- [ ] Core job green.
- [ ] Mod job green (self-hosted, if enabled). Same-repository PRs run it
      automatically; fork PRs are excluded because untrusted code must not run on
      the self-hosted runner, so a maintainer has to build such a branch manually.
- [ ] No workflow scope issues (the workflow file needs a token with the `workflow` scope to push).

## Smoke tests (manual)

- [ ] Host + client connect (UDP and Steam).
- [ ] Version mismatch rejected.
- [ ] Dual join rejected on host.
- [ ] Death/revive visibility.
- [ ] Scene transition.
- [ ] Reconnect after timeout.
- [ ] Late joiner catch-up.
- [ ] Quest interaction: offer/progress a quest and verify the partner sees the same quest state.
- [ ] Diagnostics bundle writes a file and the room code is redacted.

## Publish

- [ ] Tag `v3.2.0`.
- [ ] GitHub release with notes.
- [ ] Attach mod DLL + checksums.
- [ ] Announce breaking changes.

## Rollback

- [ ] Keep 2.2.2 artifacts available.
- [ ] Document rollback steps.
