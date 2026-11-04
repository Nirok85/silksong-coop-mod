# Migration guide: 2.x → 3.0.0

> **Patch 3.0.3-3.3.0:** no protocol change and no extra migration step. Install
> 3.3.0 instead of 3.0.0; Steam lobbies compare the full mod version, so both
> players should use the same release (UDP remains wire-compatible with 3.0.x,
> but stable pickup dedup needs 3.2.0 on both sides). 3.3.0 adds the optional
> `[Network] SteamMaxPlayers`; leave it at 2 unless every player is on 3.3.0 and
> wants the experimental 3+ Steam mesh.

## Breaking changes

- **Protocol version 8.** 2.2.x uses v7. The handshake (UDP and Steam) rejects a
  peer on a different revision, and the room-code HMAC session key is derived from
  the `SilksongCoop-Session-v8` label, so a 2.2.x peer cannot complete a session.
- **Packet layout.** `WorldEventPacket` and `PersistentStatePacket` gained
  `StateRevision` (uint) and `OriginPeerId` (ulong). See `docs/protocol-v8.md`.
- **Steam lobby compatibility.** The lobby stores the mod version and protocol;
  a lobby created by 2.2.x is refused instead of half-connecting.
- **Plugin version** is `3.0.0` (assembly version `3.0.0.0`).

Unchanged in this release: `PacketHeader` (12 bytes), channel numbering, room-code
format (28-character secret codes and legacy 20-character codes still decode).

## Upgrade steps

1. Update **both** peers to 3.0.0. A mixed session is rejected.
2. Room codes keep working (IP/port encoding is unchanged), but a peer running an
   older build will fail the handshake.
3. `INetworkTransport` gained `ConnectedPeers`. This only matters if you build a
   transport outside this repository; add the property.
4. Re-run your local test suite; the protocol/version tests were updated for v8.

## Compatibility table

| Client A | Client B | Result |
|---|---|---|
| 2.2.x | 2.2.x | OK (unchanged) |
| 2.2.x | 3.0.0 | Rejected (version/label mismatch) |
| 3.0.0 | 3.0.0 | OK |

## Not delivered in 3.0.0

Do not expect these from this version — they are not implemented:

- More than two players: the UDP and Steam transports still hold a single remote
  connection, and one remote clone is spawned per connection. The session state can
  track several peer ids, and transform broadcast iterates the transport's peer
  list, but the transports never report more than one peer.
- Host-authoritative arbitration and vote-based conflict resolution.
- `SessionId`/`ConnectionEpoch` reconnect protocol and delta resync.
- Cross-scene persistent state: values for unloaded scenes are cached and retried,
  not formally preserved across sessions.
