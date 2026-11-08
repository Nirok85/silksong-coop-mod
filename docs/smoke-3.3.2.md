# Smoke test 3.3.2

Release: [v3.3.2](https://github.com/Nirok85/silksong-coop-mod/releases/tag/v3.3.2), commit `37871f9` (version 3.3.2, protocol v8)
Archives:

- `SilksongCoop_v3.3.2.zip` — SHA-256 `D8094CF946F48252B7DFFD15CF25BCD5921DE18E6DCCB8D09F6C7091711FFF75`
- `SilksongCoop_v3.3.2_plugin-only.zip` — SHA-256 `AD639015D4CC289B88AD1582330CCB2E1BB1D0F82458D0BDF5AF195C36EAB20B`

Use the same archive and the same full mod version on both players. Steam
compares the full version; UDP is wire-compatible but the corrected damage
semantics expect 3.3.2 on both sides.

## Preconditions

- Back up both saves.
- Close other mods that bind F10/F11.
- Keep `BepInEx/LogOutput.log` after each run.
- Record the game version, OS, transport, mod build and result.

## 1. UDP, two players

1. Host: F10 → Host Session (Direct UDP) → copy the room code.
2. Client: F10 → Join → paste the code.
3. Confirm the status strip shows Connected and both peers share the room.
4. Walk, dash, attack; confirm the remote clone moves smoothly.
5. Check the partner HUD (HP/silk) and F11 diagnostics.
6. Hit the same enemy simultaneously; confirm no double kill or stuck HP.
7. Kill the partner; confirm death, spectator timer and revive.
8. Use a bench; confirm both players' respawn state.
9. Transition scenes in both directions; confirm the clone hides/reappears.
10. Disconnect the client and reconnect; confirm the session recovers.
11. Join late with a second client after progress; confirm world/enemy catch-up.
12. Complete one NPC dialogue/quest flag change; confirm it appears on the peer.

Result: `PASS / FAIL` — notes:

## 2. Steam, two players

Repeat the same checks with F10 → Host Session (Steam) and a Steam invite.

Result: `PASS / FAIL` — notes:

## 3. Steam, 3+ (experimental)

Set `[Network] SteamMaxPlayers = 3` on all clients, restart, host, invite two
players. Check late join, per-peer HUD, revive target selection, host promotion
after the host leaves, and a 10+ minute session without a false timeout.

Result: `PASS / FAIL / NOT RUN` — notes:

## Failure evidence

Attach:

- `BepInEx/LogOutput.log` tail (remove room codes/tokens),
- F11 screenshot,
- exact steps and which player failed,
- game version and mod SHA/version.
