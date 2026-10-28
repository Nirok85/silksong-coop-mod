# Third-party notices

The Silksong Co-op Mod itself is released under the MIT License (see `LICENSE`).

The self-contained Windows package (`SilksongCoop_v<version>.zip`) additionally
redistributes unmodified binaries from the projects below. The plugin-only
package expects BepInEx to be installed by the user and redistributes none of
them.

## BepInEx 5.4.21

- Project: https://github.com/BepInEx/BepInEx
- License: GNU Lesser General Public License v2.1 — `LICENSES/LGPL-2.1.txt`
- Copyright: BepInEx team and contributors
- Redistributed files: `BepInEx/core/*`, `doorstop_config.ini`
- Source code: https://github.com/BepInEx/BepInEx/tree/v5.4.21

BepInEx is distributed in the hope that it will be useful, but WITHOUT ANY
WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
PARTICULAR PURPOSE. See the license text for details. The complete corresponding
source for the binaries above is available at the project URL.

## UnityDoorstop (winhttp.dll)

- Project: https://github.com/NeighTools/UnityDoorstop
- License: GNU Lesser General Public License v2.1 — `LICENSES/LGPL-2.1.txt`
- Redistributed file: `winhttp.dll`
- Source code: https://github.com/NeighTools/UnityDoorstop

## HarmonyX 2.9.0

- Project: https://github.com/BepInEx/HarmonyX
- License: MIT — `LICENSES/HarmonyX-MIT.txt`
- Copyright (c) 2020 BepInEx
- Redistributed file: `BepInEx/core/0Harmony.dll` (self-contained package only)

## MonoMod (RuntimeDetour / Utils)

- Project: https://github.com/MonoMod/MonoMod
- License: MIT — `LICENSES/MonoMod-MIT.txt`
- Copyright (c) 2015 - 2020 0x0ade

## Mono.Cecil

- Project: https://github.com/jbevain/cecil
- License: MIT — `LICENSES/Mono.Cecil-MIT.txt`
- Copyright (c) 2008 - 2015 Jb Evain, 2008 - 2011 Novell, Inc.

---

## Not included

- **Hollow Knight: Silksong game assemblies** (`Assembly-CSharp.dll`,
  `UnityEngine.*.dll`, `TeamCherry.TK2D.dll`, …) are referenced at compile time
  only. They are never copied into the repository or the release archives.
- **Team Cherry assets** — no artwork, audio, fonts or level data are bundled.

Hollow Knight: Silksong is a trademark of Team Cherry. This is an unofficial
fan project, not affiliated with or endorsed by Team Cherry.
