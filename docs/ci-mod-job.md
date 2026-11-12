# Mod CI job

GitHub-hosted jobs build and test Core and run the release metadata preflight.
The Mod preflight needs the installed game's managed assemblies. It runs on
pushes to `master`, release tags (`v*`), or manual runs only when the repository
variable `SILKSONG_MOD_RUNNER_ENABLED` is set to the exact string `true`. With
the variable absent or false, GitHub skips the job so CI does not wait
indefinitely for a missing runner.

On a release tag the Mod job runs whenever the runner variable is enabled, and
the `mod-status` gate then fails CI if that build is skipped or red: a tag
cannot be published with a green pipeline unless the Mod compiled against the
game assemblies. When the variable is absent the job stays the documented
opt-in skip, because no runner exists on a hosted image.

Register a self-hosted Linux runner with labels `self-hosted`, `linux`, and
`silksong`, PowerShell, and access to the installed game. Set repository
variables `SILKSONG_MOD_RUNNER_ENABLED=true` and `SILKSONG_MANAGED_DIR` to the
absolute managed assembly directory outside the checkout. Restrict the
`silksong-mod-build` environment to protected `master`. The workflow then runs
the full release preflight, including Mod/Core builds and tests.

A skipped job means the game build was not checked. A successful build does not
exercise Unity or Steam. Use the live scenarios in the [release checklist](release-checklist.md)
before publishing.
