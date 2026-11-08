# SilksongCoop.Probe

Headless UDP client used to verify a real game host without a second player.

It performs the same handshake as the mod, prints every packet type received, and
sends a valid `VitalsSync` (7/9 HP, full silk) once per second. The game should show
the partner HUD and report `Connected`.

## Usage

1. Start the game and open the co-op menu (F10).
2. `HOST` -> `СОЗДАТЬ UDP`.
3. Copy the room code from the toast or the status tab.
4. Run:

```powershell
dotnet run --project tools/SilksongCoop.Probe -c Release -- "SK-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX" 20
```

The second argument is the duration in seconds (5–300, default 20).

Expected output on a healthy host:

```text
CONNECTED peer=1 name=Host
SENT_VITALS hp=7/9 silk=9/9 tick=1
...
FINAL connected=True rtt=16 loss=0
RX VitalsSync = 1
RX ItemUnlock = 3
```

A direct `IP:port` target works only when the host did not require a room code.
The probe does not simulate movement, combat, death or scene transitions; those
still need a real second game client.
