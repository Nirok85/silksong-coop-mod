# Protocol v8

## Version negotiation

`NetworkProtocol.Version` is 8 and `NetworkProtocol.SessionLabel` is
`SilksongCoop-Session-v8`. Both are enforced by every transport:

- UDP: client and host announce the version in the handshake (`Handshake`,
  `HandshakeAck`); a mismatch drops the connection.
- Steam: every datagram is prefixed with the version, the lobby stores it in
  lobby data, and a mismatch disconnects with `DisconnectReason.VersionMismatch`.
- Room-code sessions also derive their HMAC session key from the version label,
  so builds on different packet layouts cannot share a key at all.

## Packet header

Unchanged since v7: 12 bytes, little-endian.

| Offset | Size | Field |
|---|---|---|
| 0 | 2 | Magic (`0x4B53`) |
| 2 | 1 | PacketId |
| 3 | 1 | ChannelId |
| 4 | 4 | TickSequence |
| 8 | 4 | Timestamp (ms) |

## Changed packet layouts in v8

`WorldEventPacket` (after `PositionY`):

| Size | Field | Meaning |
|---|---|---|
| 4 | `StateRevision` (uint) | Sender-monotonic revision, `0` = unversioned |
| 8 | `OriginPeerId` (ulong) | Transport id of the peer that produced the event |

`PersistentStatePacket` (after `Value`): the same two fields.

Older builds cannot parse these payloads; that is why the version was bumped.

## Idempotency semantics

Receiver side (`EnemySyncController`):

- Revisions are compared only for the same key **and** the same `OriginPeerId`.
  An event from a different origin is always applied, because revisions from two
  senders are not comparable.
- `StateRevision == 0` disables the check for that packet (legacy/unversioned senders).
- A revision equal to or lower than the last applied one for that key is dropped.
- A revision is recorded when the change is actually applied — including when a
  cached event is applied later after a scene load.
- Pending (scene-not-loaded) caches keep the newest revision per key.
- Applied revisions are cleared on `ResetSession()`.

## Not implemented in v8

The following are **not** part of this protocol revision and must not be relied on:

- `SessionId` / `ConnectionEpoch` negotiation.
- Delta resync after reconnect.
- Host authority over enemy/vitals/currency packets.
- More than two players at the transport level.
