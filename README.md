# Hollow Knight: Silksong — Co-op Multiplayer Mod

![Platform](https://img.shields.io/badge/Platform-PC%20%7C%20Steam-blue)
![Engine](https://img.shields.io/badge/Engine-Unity%206000.0.50f1%20(Mono%20x64)-black)
![Loader](https://img.shields.io/badge/ModLoader-BepInEx%205.4.x-purple)
![Tests](https://github.com/Nirok85/silksong-coop-mod/actions/workflows/ci.yml/badge.svg)
![Version](https://img.shields.io/badge/Version-2.1.0-success)

**[🇷🇺 Русская версия](README.ru.md)**

A fully-featured cooperative mod for **Hollow Knight: Silksong**, allowing two players to explore the world of Pharloom together in real time. The release package is **fully self-contained** — BepInEx 5.4.21 and HarmonyX are bundled; no separate downloads are required.

---

## Table of Contents

- [Features & Mechanics](#features--mechanics)
- [System Requirements](#system-requirements)
- [Installation](#installation)
- [How to Play (Connecting)](#how-to-play-connecting)
- [Controls & Status](#controls--status)
- [Shared vs Individual Mechanics](#shared-vs-individual-mechanics)
- [Death Rules](#death-rules)
- [Mod Configuration](#mod-configuration)
- [Project Architecture](#project-architecture)
- [Testing & Verification](#testing--verification)
- [FAQ & Troubleshooting](#faq--troubleshooting)
- [Uninstall](#uninstall)
- [Development & Building](#development--building)

---

## Features & Mechanics

### 1. Serverless Networking
* **Steam P2P Matchmaking**: the host opens the world for network play and sends a direct invitation via the Steam overlay. Uses the game's built-in `com.rlabrecque.steamworks.net.dll`.
* **Steam telemetry**: app-level ping/pong probes on an unused channel feed real RTT and loss into the F11 overlay and adaptive interpolation (the legacy Steam P2P API exposes no counters).
* **Fallback (custom UDP transport)**: if the Steam overlay is unavailable, the host generates a short room code (e.g. `SK-A8B3-K9Q2-W1Z4-M4T7`) or connects directly by IP:Port. The class is named `LiteNetLibTransport` for historical reasons, but this is a purpose-built UDP socket implementation — the LiteNetLib package is not used or bundled.
* **Movement Smoothing (Hermite Spline)**: position is replicated at 30 Hz with cubic Hermite spline interpolation (C¹ continuity) and a 64-frame snapshot buffer. No teleporting or jitter at latencies up to 150 ms.
* **Secure sessions**: challenge-response authentication, packet validation, rate limiting and room-code codec protection against unauthorized joins. The menu-generated code carries 40 random bits that cannot be derived from the address; a host started from the menu requires that code and rejects a bare IP:Port join.
* **Shared world state**: enemy/boss HP is replicated from the attacker's authoritative hit, breakable walls and gates are mirrored, and physical currency drops are deduplicated so the shared wallet is credited once per drop.

### 2. Visual Differentiation (Blue Hornet Cloak)
* **Player 1**: original crimson Hornet cloak (`_HueShift = 0.0`).
* **Player 2**: azure-blue cloak via Team Cherry's native shader (`CAN_HUESHIFT` with `_HueShift = 0.58`).
* **Mathematical purity (zero-drift)**: the white mask, obsidian horns and steel needle are achromatic and unaffected by hue rotation.
* **Combat readability**: Player 2 gets an azure dash trail (ghost after-images) and adaptive rim lighting for instant silhouette recognition in fast combat.

### 3. Separate Resources, Shared Progression
* **Current health and silk are separate**: taking damage or spending silk only affects the acting player.
* **Maximum stats are shared**: mask shards, silk spools and needle upgrades strengthen both players automatically.
* **Shared wallet with deduplication**: Rosaries and Shell Shards are credited to both players. Protected by a FIFO transaction-deduplication buffer (500 entries by GUID) plus a monotonic per-sender sequence, preventing double-crediting from network packet replays.

### 4. Cooperative Death & Revival
* **Solo Death**: if one player dies while the other is alive:
  * The standard bench reload is blocked by the `GameManager.PlayerDead` hook.
  * The fallen player enters spectator mode (camera follows the partner).
  * A timer of **15–30 seconds** (default 20 s) starts.
  * When the timer expires, the player revives beside the partner (spawn surface resolved by `SafeGroundSolver` raycasts — no spawning mid-air or over pits) with 50% HP and temporary invulnerability.
* **Team Wipe**: if both players die simultaneously — or the second dies during the first's revival timer — both respawn at the last saved bench.

### 5. Gear, Crests & Item Discovery
* **Equipped slots are separate**: each player freely configures their own crest (Hunter, Architect, Weaver…) and active tools.
* **Item discovery is shared**: as soon as one player finds, crafts or buys a new crest, tool or ability, it becomes instantly available to the partner at any bench.

---

## System Requirements

| Requirement | Details |
|-------------|---------|
| Game | Hollow Knight: Silksong (Steam, PC, 64-bit) — Unity 6000.0.50f1 Mono x64 |
| Mod loader | BepInEx 5.4.21+ (Mono x64) — **bundled in the release package** |
| OS | Windows 10/11 (64-bit) |

No other downloads required.

---

## Installation

### Method A — Release Package (Recommended)

The `SilksongCoop_v2.1.0.zip` from the [Releases](https://github.com/Nirok85/silksong-coop-mod/releases) page includes everything: BepInEx 5.4.21, HarmonyX and the mod DLLs.

1. Close the game if it is running.
2. Open the Silksong install folder in Steam (Manage → Browse local files). Typical path:
   ```text
   D:\SteamLibrary\steamapps\common\Hollow Knight Silksong\
   ```
3. **Copy everything from the archive** (all files + `BepInEx\`) into the game folder, **merging** directories:
   * `winhttp.dll` and `doorstop_config.ini` must sit next to `Hollow Knight Silksong.exe`.
   * The `BepInEx\` folder merges with (or creates) the existing one.
4. Launch the game once, then close it (BepInEx generates cache and logs).
5. Launch again and load a save. Look for the **`[F10] Silksong Co-op`** label in the top-left corner. **Done.**

> **Important:** Both players must run the same game version and the same mod version.

### Method B — Manual Install (from source build)

If you already have BepInEx 5.4.x installed (any OS, including Linux/macOS), grab
`SilksongCoop_v2.1.0_plugin-only.zip` from the release page — it contains just the two DLLs,
a default config and these instructions.

1. Install [BepInEx 5.4.x (x64)](https://github.com/BepInEx/BepInEx/releases) into the game root (so that `BepInEx\` and `winhttp.dll` appear next to the game exe).
2. Build the mod from source (see [Development & Building](#development--building)) or take the DLLs from the release archive.
3. Place the two DLLs into the plugins folder (create it if missing):
   ```text
   BepInEx\plugins\SilksongCoop\
   ├── SilksongCoop.Core.dll
   └── SilksongCoop.Mod.dll
   ```
4. Launch the game.

---

## How to Play (Connecting)

In the main menu or in game, press **F10** to open the co-op interface.

### Method 1: Steam Invite (Recommended)
1. **Player 1 (Host)**:
   * Load a save or start a new game.
   * Press **F10** → **"Host Session (Steam)"**.
   * Press **"Invite Friend"** — the Steam friends overlay opens; send the invitation.
2. **Player 2 (Client)**:
   * Accept the invitation in the Steam chat/overlay.
   * The game connects automatically and Player 2 appears in the azure-blue cloak.

### Method 2: Direct P2P / Room Code (Fallback)
If the Steam overlay is disabled or unavailable:
1. **Player 1 (Host)**:
   * Press **F10** → **"Host Session (Direct UDP)"**.
   * A room code (e.g. `SK-A8B3-K9Q2-W1Z4-M4T7`) and the local port (`7777`) are shown; the code is auto-copied to the clipboard. Share it with the partner.
2. **Player 2 (Client)**:
   * Press **F10**, switch to the **"Join"** tab.
   * Paste the room code and press **"Connect"**. A bare `IP:Port` only works for hosts that do not require a code (the menu always creates one, so prefer the code).

> Direct UDP works via LAN or VPN (Radmin/Hamachi). For Internet play, Steam Host is the reliable path.

---

## Controls & Status

| Action | How |
|--------|-----|
| Open/close co-op menu | **F10** (configurable, `[Keybinds] ToggleMenu`) |
| Toggle network diagnostics overlay | **F11** (configurable, `[Keybinds] ToggleDiagnostics`) |
| Spectator camera | **F9** (configurable, `[Keybinds] ToggleSpectatorCamera`) |
| Invite Steam friends | Menu → "Invite Steam Friends…" (host only) |
| Copy room code / lobby ID | Menu → "Copy" (auto-copied when hosting) |
| Disconnect | Menu → "Disconnect Session" |
| Spectator countdown | Shown automatically after solo death |

The diagnostics overlay (**F11**) shows ping (RTT), packet loss and bandwidth graphs for the last 60 seconds.

---

## Shared vs Individual Mechanics

| Shared | Individual |
|--------|------------|
| Max health, max silk, needle upgrades | Current health & silk |
| Currency (Rosaries, Shell Shards) | Equipped crest / tools loadout |
| Enemy & boss HP, breakable walls, opened gates | Enemy AI timing / animation phase |
| Physical pickups (each drop is credited once) | Position, movement, camera |
| Unlocked items (auto-unlock for partner) | Death / revival state |
| Benches & checkpoints | |

> **Enemy/world sync:** hero damage is applied to the attacker's simulation and then replicated as authoritative HP, so both players fight the same boss HP pool. `Breakable` walls and `Gate.Open()` are mirrored, and physical drops are deduplicated by a stable scene/type/position key plus a 0.5-unit drift window. Scripted doors driven only by `PlayerData` flags and boss phases that listen exclusively to `TookDamage` (rather than polling HP) may still differ. Progression packets are range-validated, but co-op still assumes invited friends — a modified client can raise its own progression within those limits.

---

## Death Rules

* **One player dies** → spectator mode; revives beside the surviving partner after 15–30 s (default 20 s) with 50% HP + temporary invulnerability.
* **Both players die** → team wipe → both respawn at the last bench.

---

## Mod Configuration

After the first launch, the config file is created at `BepInEx\config\com.silksong.coop.cfg`:

```ini
[Gameplay]
## Seconds of delay before reviving beside the surviving partner (15–30)
ReviveCooldownSeconds = 20.0

## Percent of max HP restored on revive beside partner (0.1–1.0)
ReviveHealthPercent = 0.5

## Duration of post-revive invulnerability (seconds)
ReviveInvulnerabilitySeconds = 2.5

[Visuals]
## Enable azure dash trail for Player 2
EnablePlayer2DashTrail = true

## Hue shift for Player 2 palette (0.58 = azure blue)
Player2HueShift = 0.58

[Network]
## Default port for direct UDP connection
DefaultUdpPort = 7777

## Position replication rate (Hz, 20–60)
TickRateHz = 30

## Auto-reconnect after unexpected disconnects
AutoReconnect = true

## Max auto-reconnect attempts (1-10)
MaxReconnectAttempts = 5

## Persist the last five connection targets in PlayerPrefs (Windows registry)
SaveConnectionHistory = true

[Keybinds]
ToggleMenu = F10
ToggleSpectatorCamera = F9
ToggleDiagnostics = F11
```

Restart the game after editing.

---

## Project Architecture

The solution is split into isolated layers — the platform-independent core has zero references to Unity or BepInEx:

```text
silksong_coop_mod/
├── SilksongCoop.sln
├── src/
│   ├── SilksongCoop.Core/            # Platform-independent core (netstandard2.1)
│   │   ├── Networking/               # INetworkTransport abstraction, LiteNetLib UDP, RoomCodeCodec
│   │   │   ├── ConnectionAuthenticator.cs   # Challenge-response authentication
│   │   │   └── RateLimiter.cs               # Token-bucket rate limiting
│   │   ├── Protocol/                 # 12-byte binary protocol, PacketReader/Writer
│   │   │   └── Packets/              # Transform, vitals, currency, death, scene packets
│   │   ├── Interpolation/            # Hermite splines, snapshot buffer, Dead Reckoning
│   │   ├── State/                    # Session, death, vitals & wallet state machines
│   │   └── Visuals/                  # HSV/Rodrigues color-space rotation math
│   │
│   └── SilksongCoop.Mod/             # BepInEx / HarmonyX plugin for Silksong
│       ├── Clones/                   # RemoteHornetSpawner + RemotePlayerController
│       ├── Visuals/                  # PaletteSwapController (CAN_HUESHIFT), DashTrailController
│       ├── Hooks/                    # Harmony hooks (HeroVitals, Currency, Death, Inventory)
│       ├── Networking/               # SteamP2PTransport (Steamworks.NET P2P)
│       ├── Settings/                 # KeybindManager, QualityPresetManager
│       └── UI/                       # CoopMenuUI (F10), DiagnosticsOverlay (F11), ToastNotifier
│
└── tests/
    └── SilksongCoop.Tests/           # xUnit suite (net8.0) — 682 tests
        ├── TestHarness/              # DualClientTestHarness (2-client simulation, no Unity)
        ├── Tier1_FeatureTests/       # Requirement coverage tests (F01–F28)
        ├── Tier2_BoundaryTests/      # Boundary value tests
        ├── Tier3_PairwiseTests/      # Mechanic interaction tests
        ├── Tier4_ApplicationScenarios/ # Realistic gameplay scenarios (boss fights, wipes)
        └── Adversarial stress tests  # 100 threads, 40% packet loss, chaos testing
```

---

## Testing & Verification

The project is fully covered by an automated regression suite:

```bash
dotnet test SilksongCoop.sln -c Release
```

Current result:

```text
Total tests:   682
Passed:        682 (100.0%)
Failed:        0
Skipped:       0
Duration:      ~3 s
Build:         0 errors, 0 warnings (Debug + Release)
```

---

## Known Limitations

The mod is in active development. The following limitations are by design or are tracked for future releases:

- **Two players only.** Multi-peer (3+) is on the roadmap (v3.0).
- **Same game and mod version on both sides** is required; Steam lobbies verify the full mod version and the UDP handshake carries a protocol revision, so mismatched builds are refused.
- **Boss phase edge cases.** Enemy/boss HP is now shared and host snapshots correct drift, but a phase that is driven exclusively by a `TookDamage` event (rather than polling HP) may lag by one hit on the partner's machine.
- **Scripted doors.** `Breakable` walls, `Gate.Open()` and `PersistentBoolItem`/`PersistentIntItem` state (levers, chests, caches and most scripted doors) are mirrored while both players are in the same scene. A door whose state exists only in a PlayMaker FSM or `PlayerData` flag without a persistent component, or one changed while the partner is in another scene, is still local.
- **Maximum stats only grow.** `maxHealth`, `silkMax` and `nailUpgrades` are monotonic; a higher value on one peer never downgrades the other.
- **Internet play requires Steam.** Direct UDP works over LAN or VPN (Radmin/Hamachi); the host's detected LAN IP may be wrong behind VPNs — use the room code or paste a direct `IP:Port`.
- **Revive requires the partner to be in the same scene.** Cross-scene death falls back to a bench respawn.
- **Pickup deduplication is positional.** A drop that lands more than 0.5 units away on the two machines (very high frame-time divergence) can still be credited twice; the periodic wallet reconciliation converges the total to the higher value.

## FAQ & Troubleshooting

<details>
<summary><b>What hotkeys does the mod use? The menu/overlay doesn't open</b></summary>

* **F10** — main co-op menu (hosting, joining by code, Steam invites, partner status, settings).
* **F11** — network diagnostics overlay (ping/loss/bandwidth graphs, top-right corner).
* Keys are remappable in `BepInEx\config\com.silksong.coop.cfg`, section `[Keybinds]`.

If keys don't respond: check that `BepInEx\LogOutput.log` shows the plugin loading (look for `Initializing Silksong Co-op Multiplayer v2.1.0...`). Ensure no other mod or OS utility (e.g. screen recorders, media players) binds F10/F11 globally.
</details>

<details>
<summary><b>Steam overlay doesn't open on "Invite Friend"</b></summary>

Enable the overlay in Steam settings (Game Properties → General → "Enable the Steam Overlay while in-game"). If it still fails, use Method 2 (room code).
</details>

<details>
<summary><b>Is port forwarding required for direct room-code connection?</b></summary>

Usually not — LiteNetLib punches through NAT automatically (UDP hole punching). If the connection fails, the host can forward UDP port `7777` on their router, or both players can use a virtual LAN (Radmin VPN / Hamachi).
</details>

<details>
<summary><b>Does a single death affect Steel Soul mode?</b></summary>

In normal mode a solo death triggers the spectator timer and revive-at-partner. A full world reset (Steel Soul rules) only happens when both players die simultaneously (team wipe).
</details>

<details>
<summary><b>Can the mod be used alongside other mods?</b></summary>

Yes — BepInEx 5.4.x and targeted Harmony patches ensure compatibility with most cosmetic and informational mods. Avoid mods that modify `HeroController` movement or health logic in incompatible ways.
</details>

### Troubleshooting Table

| Symptom | Cause | Fix |
|---------|-------|-----|
| No `[F10]` label | BepInEx didn't load | Check `BepInEx\LogOutput.log`; ensure `winhttp.dll` is next to the game exe |
| "Steam is not initialized or offline" | Game launched outside Steam | Launch via Steam, log in; or use UDP fallback |
| Invite button does nothing | Not hosting a Steam lobby | Host the lobby first; partner must own Silksong |
| Can't connect via UDP | NAT/firewall blocking | Forward port 7777/UDP or use VPN (Radmin/Hamachi) |
| Desync / rubber-banding | High latency (>200 ms) | Interpolation degrades; prefer Steam P2P |
| Crash on load | Corrupt cache / mod conflict | Delete `BepInEx\cache`, re-extract, check conflicts |
| Remote player invisible | Older mod version | Both players must run the same mod version |

---

## Uninstall

1. Close the game.
2. Delete `BepInEx\plugins\SilksongCoop\`.
3. (Optional) Delete `BepInEx\config\com.silksong.coop.cfg`.
4. To remove BepInEx entirely: delete `winhttp.dll`, `doorstop_config.ini` and the `BepInEx\` folder.

---

## Development & Building

Requirements: .NET SDK 8.0+ (tests), any SDK capable of `netstandard2.1`.

```bash
# Restore + build (0 warnings enforced)
dotnet build SilksongCoop.sln -c Release

# Run the full test suite
dotnet test SilksongCoop.sln -c Release
```

The `ManagedDir` in `src/SilksongCoop.Mod/SilksongCoop.Mod.csproj` points to your local Silksong install (overridable via `-p:ManagedDir=...`).

To reproduce the release archives (self-contained Windows zip + plugin-only zip):

```bash
ManagedDir="/path/to/Hollow Knight Silksong_Data/Managed" bash scripts/package-release.sh
```

The script builds both projects in Release with warnings-as-errors, bundles BepInEx 5.4.21 x64 and
writes the packages to `dist/`. The version is taken from `SilksongCoop.Mod.csproj`.

See [CHANGELOG.md](CHANGELOG.md) for version history and [ROADMAP.md](ROADMAP.md) for the improvement plan.

---

## License

Developed for the Hollow Knight: Silksong community. Unofficial fan mod — contains no original game files or closed Team Cherry assets.

Hollow Knight: Silksong © Team Cherry.
