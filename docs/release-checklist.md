# Release checklist (3.0.2)

## Pre-release

- [ ] All tests pass (764+).
- [ ] Core and Mod build with 0 compiler warnings (TreatWarningsAsErrors=true; NU1900 is a NuGet network warning).
- [ ] Protocol version bumped and documented (`docs/protocol-v8.md`).
- [ ] Plugin version and assembly versions bumped to 3.0.2.
- [ ] Migration guide updated.
- [ ] CHANGELOG.md updated.
- [ ] README.md / README.ru.md updated вЂ” limits section matches what is implemented.

## Build

- [ ] Clean checkout build (`dotnet build` from scratch).
- [ ] Package artifacts produced with checksums.
- [ ] No `bin/`/`obj/` or secrets in package.

## CI

- [ ] Core job green.
- [ ] Mod job green (self-hosted, if enabled).
- [ ] No workflow scope issues (the workflow file needs a token with the `workflow` scope to push).

## Smoke tests (manual)

- [ ] Host + client connect (UDP and Steam).
- [ ] Version mismatch rejected.
- [ ] Dual join rejected on host.
- [ ] Death/revive visibility.
- [ ] Scene transition.
- [ ] Reconnect after timeout.
- [ ] Late joiner catch-up.

## Publish

- [ ] Tag `v3.0.2`.
- [ ] GitHub release with notes.
- [ ] Attach mod DLL + checksums.
- [ ] Announce breaking changes.

## Rollback

- [ ] Keep 2.2.2 artifacts available.
- [ ] Document rollback steps.
