# Co-op wire protocol v11

Upgrade all peers together. Steam and UDP reject v10 during setup.
To roll back, install the same v10 build on every peer. Game saves retain their format.

v11 keeps the [v10 application header, handshake and state rules](protocol-v10.md).
The HMAC session label is `SilksongCoop-Session-v11`.

`CurrencySync` encodes `DeltaAmount` as a signed little-endian 32-bit integer,
immediately after `TotalWalletAmount`. One transaction still carries one GUID
and, for physical pickups, one pickup key. The allowed delta range is
−100,000,000 to 100,000,000. The receiver applies the delta to its own balance.

UDP data frames have a 2,048-byte datagram limit. The payload limit is 2,024
bytes for authenticated frames and 2,040 bytes for legacy direct-IP frames
without a MAC. Oversize sends are rejected before consuming a sequence number.
A datagram with a failed MAC is discarded individually.

Currency journals retain pending transactions for reconnects to the same room
and save. If a peer exhausts its journal, resuming that journal fails explicitly.
Other connected Steam peers keep their own journals and reliable queues.

The initial catch-up burst ends with a new `CatchUpComplete` packet (type 0x22):
header plus a 16-byte GUID echoing the receiver's `PeerReady` save instance.
It is informational — it logs that the burst was dispatched. Peers on older
3.3.9 beta builds reject the unknown type and keep working; a marker with an
unexpected or empty GUID is discarded as rejected.

## Quest flag replication (0x56)

`QuestStatePacket` is header, one entry-count byte (0–64), then that many
entries. Each entry is the kind byte (bool or int), a length-prefixed UTF-8
`PlayerData` key, and a 4-byte int or 1-byte bool value. The packet header is
followed by no padding; trailing bytes are rejected.

One packet must also stay within 1,500 encoded bytes, which is why senders split
a diff by encoded size and not only by entry count. The format is unchanged in
v11; stricter validation is a receiver-side rule:

- a count above 64 raises `FormatException` instead of being clamped to 64;
- a truncated body raises the reader's underflow exception;
- the receive site drops malformed packets and counts them as rejected in the
  diagnostics, so a malformed peer cannot desynchronise quest flags or inflate
  the rejection-free statistics.

## Inventory transaction deduplication

`ItemUnlockSync` carries a transaction GUID. Regular unlock GUIDs are kept in a
bounded FIFO. Counter pseudo-items (`heartPieces:<n>`, silk parts) use the same
packet but recur across upgrade cycles, so their GUIDs are retained for the whole
session instead of being evicted. A replayed counter packet is therefore always
a no-op, even after hundreds of unrelated unlocks.

