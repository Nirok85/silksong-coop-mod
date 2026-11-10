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
