# Hollow Knight: Silksong Co-op

![Version](https://img.shields.io/badge/Version-3.3.9-success)
[![CI](https://github.com/Nirok85/silksong-coop-mod/actions/workflows/ci.yml/badge.svg)](https://github.com/Nirok85/silksong-coop-mod/actions/workflows/ci.yml)

[Русская версия](README.ru.md) · [Download v3.3.9 beta](https://github.com/Nirok85/silksong-coop-mod/releases/tag/v3.3.9-beta.1)

Explore Pharloom together in a two-player co-op mod for the Steam PC version of Silksong. Fight enemies together and share currency, upgrades and unlocks. Each player manages their own health, silk and equipped gear.

## Installation

**Beta:** the redesigned interface has not yet been visually verified in game. All players need matching game and mod versions. This beta uses protocol v11.

- **Windows x64.** Close the game and extract `SilksongCoop_v3.3.9.zip` into the game folder. The archive includes BepInEx 5.4.21. Keep `winhttp.dll` next to the game executable.
- **Existing BepInEx 5.4.x install.** Download `SilksongCoop_v3.3.9_plugin-only.zip` and place both mod DLLs in `BepInEx/plugins/SilksongCoop/`. On Linux or macOS, use the BepInEx loader for your platform.

Launch Silksong through Steam, load a save and press **F10**.

## Play together

- **Steam (recommended for internet play).** Open **Host** → **HOST Steam P2P Lobby** → **Invite Steam Friends…**. Your partner accepts the Steam invite.
- **UDP (LAN or VPN).** Open **Host** → **HOST UDP (Room Code)** and share the full code. Your partner pastes it in **Join** and selects **JOIN via IP / Code**. The default port is `7777/UDP`.

**F9** toggles spectator camera follow. **F11** opens network diagnostics. Change keybinds in `BepInEx/config/com.silksong.coop.cfg` and restart the game.

## Gameplay and limits

- A fallen player spectates and revives beside their partner in the same room after 20 seconds by default. If both players fall, they respawn at the last bench.
- Steam sessions with three or more players are experimental. UDP supports two players. Some scripted doors and boss phases can differ between players.

## Troubleshooting

- **F10 missing.** Check `BepInEx/LogOutput.log` for plugin startup and errors.
- **Steam invite fails.** Launch via Steam and enable its in-game overlay.
- **UDP connection fails.** Check the room code and the host's firewall. Use the same LAN or VPN, or use Steam.
- **Uninstall.** Close the game and remove `BepInEx/plugins/SilksongCoop/`.

## Project links

[Changelog](CHANGELOG.md) · [Architecture](docs/architecture.md) · [Testing](docs/testing.md) · [Build and release](docs/RELEASING.md)

Unofficial fan mod. [MIT license](https://github.com/Nirok85/silksong-coop-mod/blob/master/LICENSE) · [Third-party notices](https://github.com/Nirok85/silksong-coop-mod/blob/master/THIRD-PARTY-NOTICES.md)
