# Game compatibility checks

## Implemented today

- The mod targets the installed game via `ManagedDir` at build time: the Mod
  project references the game assemblies (`Assembly-CSharp`, `UnityEngine`,
  `TeamCherry.TK2D`, Steamworks and others), so it cannot be compiled without them.
- Steam and UDP require matching game versions and protocol revisions.
  Steam also checks the full mod version on lobby entry.
- Startup writes a compatibility banner to `BepInEx/LogOutput.log`:
  `[Coop] Compatibility: game <version>, Unity <version>, mod <version>, protocol v11,
  runtime <version>`. Attach that line to any bug report; it identifies the exact
  game build the mod ran against.
- Missing Steamworks → the Steam transport reports itself unavailable and logs why;
  UDP remains usable.

## Not implemented

There is no per-patch `Harmony` target report: the mod does not verify that each
individual patch target still exists before patching, and does not disable only the
affected feature on an unsupported game build. If a target disappears after a game
update, Harmony logs the failure and that feature silently stops working — check
`LogOutput.log` for `Harmony`/`patch` errors after each game patch.

The startup banner is a diagnostic aid, not a support guarantee. A build that
compiles against the installed DLLs can still miss a target that was renamed at
runtime; record the game version and a two-client smoke result before calling a
game build supported.
