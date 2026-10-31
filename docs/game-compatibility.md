# Game compatibility checks

## Implemented today

- The mod targets the installed game via `ManagedDir` at build time: the Mod
  project references the game assemblies (`Assembly-CSharp`, `UnityEngine`,
  `TeamCherry.TK2D`, Steamworks and others), so it cannot be compiled without them.
- At runtime, protocol and mod version are enforced per session
  (`NetworkProtocol.Version`, `SilksongCoopPlugin.PluginVersion`), so a peer on a
  different build is rejected instead of silently desynchronising.
- Missing Steamworks → the Steam transport reports itself unavailable and logs why;
  UDP remains usable.

## Not implemented

There is no startup `GameCompatibilityReport`: the mod does not enumerate the
game/Unity version, does not verify individual Harmony patch targets, and does not
disable itself on an unsupported game build. If a Harmony target disappears after
a game update, the failure is logged by Harmony and the affected feature silently
stops working — verify patches manually after each game patch.
