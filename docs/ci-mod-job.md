# Mod CI job

GitHub-hosted jobs build and test Core and run the release metadata preflight.
The Mod preflight needs the installed game's managed assemblies and runs only
for pushes or manual runs on `master`. Neither same-repository nor fork pull
requests run code on the game runner.

Use a self-hosted Linux runner with the `silksong` label, PowerShell and read
access to the installed game. Set `SILKSONG_MANAGED_DIR` to the absolute managed
assembly directory outside the checkout. Restrict the `silksong-mod-build`
environment to the protected `master` branch. The workflow builds the solution
with warnings as errors, checks the DLL versions, and runs Core tests.

A successful build does not exercise Unity or Steam. Use the live scenarios in
the [release checklist](release-checklist.md) before publishing.
