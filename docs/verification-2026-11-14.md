# Verification status — 2026-11-14

Applies to v3.3.9-beta.3, source commit 6e2b532.

## Automated verification

Release Mod build against locally installed game assemblies completed with zero warnings and errors. The downloaded source passed 1001 tests. These tests exercise Core and do not establish that Unity/Steam integration works.

Additional isolated checks reproduced four remaining issues.

- A newer zero-delta pickup claim can make an older journaled currency delta stale. A balance of 100 remained 100 instead of becoming 110.
- A host wallet seed can overwrite a third-peer delta received before the seed. A balance of 110 became 100.
- An equal confirming enemy snapshot after a local hit does not refresh host authority.
- ReliableUnordered rejects a previously unseen sequence 0 after its replay window advances to sequence 4096.

Code review also identified unrestricted PlayerData.SetBool application through Ability/KeyItem unlock identifiers, unbounded counter transaction GUID storage, and a missing explicit initial death/epoch snapshot. The death/epoch scenario requires in-game confirmation.

## In-game verification limits

The installed Mod DLL hash matched the beta.3 build. The game launched through Steam and reached the main menu and save selection. Repeated F10 attempts did not open the co-op menu. The available BepInEx log reported `Unable to start Unity log writer` and did not establish completed plugin initialization. The cause remains unresolved, including whether the available log represented the final Steam launch.

No save was loaded. Combat, currency, scene changes, death/revive and multiplayer gameplay remain unverified in this run. Direct executable launch exited because Steam initialization was required.

## Planned environment work

See [TODO.md](../TODO.md) for 14 scoped tasks with source paths and acceptance criteria. The repository contains runtime mod components, not editable level geometry or source lighting scenes. These tasks are a review plan, not a list of confirmed defects.
