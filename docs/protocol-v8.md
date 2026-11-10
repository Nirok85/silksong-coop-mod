# Protocol v8

Historical specification. See [protocol v11](protocol-v11.md) for the current contract.

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

## Optional packet added in 3.2.0

`QuestStatePacket` (`PacketId = 0x56`) carries the bool/int `PlayerData` flags that
changed during an NPC dialogue:

| Size | Field | Meaning |
|---|---|---|
| 1 | `Count` | Number of entries (max 64) |
| 1 | `Kind` | `0` = bool, `1` = int |
| 2+N | `Key` | Length-prefixed UTF-8 `PlayerData` field name (max 64 chars) |
| 1 or 4 | `Value` | Bool byte or Int32 |

This packet is **optional and backward compatible**: a build that does not know
`0x56` rejects the packet type and ignores the datagram. No existing v8 payload
changed, so the handshake and session label stay at v8. Quest sync only works when
both peers run 3.2.0 or newer; the receiver applies only names accepted by the
quest allowlist, never arbitrary `PlayerData` fields.

**3.0.1-3.2.0 do not change the existing wire format.** They are still v8; the patch
releases only fix the peer identity used in `OriginPeerId` (menu host = 1, menu
client = 2), boss-death catch-up, handshake ordering, transport sequence handling,
validation and the guarded object-matching rules. 3.1.0 is a UI/localization
release; 3.2.0 adds boss-phase signals, persistent-object polling and quest-state sync. A 3.0.0 UDP
peer can still complete the handshake. Steam lobbies additionally compare the full
mod version and therefore require both sides on the same release.

**3.3.0 does not change the wire format either.** It adds per-peer runtime state
(peer registry, broadcast, per-peer vitals/HUD and death/revive) but no new packet
types or payload fields, so the protocol label stays v8. Steam lobbies still
compare the full mod version, so 3.2.0 and 3.3.0 cannot mix over Steam; UDP
remains wire-compatible. The experimental Steam 3+ mode only works when every
player sets the same `[Network] SteamMaxPlayers` value.

**Post-3.3.0 reliability builds (3.3.1).** The existing `EnemySyncPacket.Damage`
field is used to combine simultaneous hits, and `TickSequence` is used only as a
per-sender dedup key. 3.3.1 refines the merge rules without changing the format:
a hit from the authoritative peer replaces HP with its absolute value (so a hit
already present in a snapshot is not subtracted twice), while hits and deaths from
other clients are deferred while that authority is fresh. Sequence dedup now runs
before the death branch. `PersistentStatePacket` / `WorldEventPacket` revision
handling is unchanged, but queued stale revisions are discarded before they reach
`SaveState`. `QuestStatePacket` is now split by encoded size; the wire layout and
`MaxEntries` are unchanged. No field was added or reordered. A 3.3.0 peer still
understands every packet, but may double-count a hit contained in a snapshot and
does not defer unconfirmed client deaths; update every player for correct damage
semantics.

**Pickup-key semantics in 3.2.0.** `CurrencySyncPacket.PickupKey` now prefers the
drop's serialized persistent id instead of its rounded position. The field layout
is unchanged, but two peers on different releases derive different keys for the
same drop, so cross-version pickup deduplication is not guaranteed. Use 3.2.0 on
both sides when stable drop dedup matters.

## Idempotency semantics

Receiver side (`EnemySyncController`):

- Revisions are compared only for the same key **and** the same `OriginPeerId`.
  An event from a different origin is always applied, because revisions from two
  senders are not comparable.
- `StateRevision == 0` disables the check for that packet (legacy/unversioned senders).
- A revision equal to or lower than the last applied one for that key is dropped.
- A revision is recorded when the change is actually applied — including when a
  cached event is applied later after a scene load.
- A cached event is re-checked against the applied ledger immediately before it is
  applied. If a newer revision for the same key/origin was applied while the event
  waited for its scene, the cached event is discarded instead of rolling state back.
- Pending (scene-not-loaded) caches keep the newest revision per key.
- Applied revisions are cleared on `ResetSession()`.

## Not implemented in v8

The following are **not** part of this protocol revision and must not be relied on:

- `SessionId` / `ConnectionEpoch` negotiation.
- Delta resync after reconnect.
- Host authority over enemy/vitals/currency packets.
- More than two players at the transport level. The experimental Steam full mesh
  (`[Network] SteamMaxPlayers`) reuses the same v8 framing, but UDP remains
  single-peer and 3+ sessions are not verified in live play.
