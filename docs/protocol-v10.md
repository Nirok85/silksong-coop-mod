# Co-op wire protocol v10

Historical specification for published v10 builds. See [protocol v11](protocol-v11.md) for the current development build.

Upgrade both peers together. Steam and UDP reject protocol v9 during setup.
To roll back, install the same v9 release on both peers. Game save formats
remain unchanged.

The 12-byte application header is unchanged. v10 adds a little-endian uint32
scene generation immediately after that header in transform and action payloads.
A departure carries the new generation in the header sequence. A transform
from that generation and the current scene establishes visibility. An older
departure, arrival signal or transform cannot restore visibility.

`PeerReady` (0x21) carries a 16-byte save-instance GUID. A snapshot sender
waits for readiness on both sides. Receivers defer durable packets while
loading. Game payloads must consume the entire application frame.

`CurrencyReceipt` (0x32) acknowledges an applied or previously seen currency
transaction by its 16-byte GUID. The sender keeps unacknowledged deltas in
memory across reconnects to the same room and save. A new participant receives
the signed host balance for both currencies in `WalletSeed` (0x33).

UDP binds its stateless challenge cookie to the source IPv4 endpoint,
client peer ID and a short validity window. Its response echoes the cookie
and client ID with a 32-byte room-code HMAC proof. UDP uses a 16-byte HMAC-SHA256
tag and directional keys derived from the v10 session key. Post-handshake control frames use monotonic sequence values.
Unreliable data tracks a 64-frame window per channel. Reliable retransmissions
can receive another acknowledgement without repeating application effects.

The host is the writer for world and persistent packets. The origin on the
wire is the authenticated host transport peer ID. The receiver rejects
mismatched origins and uses modular revision ordering within the host stream.
