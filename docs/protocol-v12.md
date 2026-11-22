# Co-op wire protocol v12

Current wire protocol for mod v4.0.1. Protocol v12 is shared by the v4.0.0 and v4.0.1 releases. Every participant must use the same protocol revision. UDP handshake and Steam datagram framing reject other revisions. No save-format migration is required. To roll back, install the same older build on every peer.

The application header remains the v11 header. The session-key domain is `SilksongCoop-Session-v12`.

## Wallet snapshot

`WalletSeedPacket` includes signed rosary and shard balances, up to eight origin watermarks, and up to 1024 credited pickup keys. A watermark contains the authenticated peer ID, one sequence per currency, and presence flags. Sequence comparisons use signed modular subtraction.

The snapshot is transmitted in up to eight reliable ordered parts on the Currency channel. Each part has a snapshot GUID, index/count, identical balances and watermarks, and up to 128 currency/key pairs. Maximum serialized part size is 1328 bytes. The receiver validates and assembles the entire snapshot before applying it. Duplicate parts, mixed metadata and invalid bounds are rejected.

Only transactions outside the snapshot's origin boundaries and physical pickup set are added from the pending journal. Watermarks also reject already-included transactions arriving after the snapshot. A 4096-transaction pre-seed buffer fails closed on exhaustion. An already-applied seed cannot overwrite later transactions.

Currency claims and nonzero deltas share the per-peer ordered journal. Sending stops at the first refused entry. Accepted entries are retained until receipt; reconnect clears the sent markers so pending entries can be retransmitted.

## Lifecycle handshake

`DeathEventType.LifecycleSnapshot = 6` reports actual local life state during readiness. `FallenPlayerIndex` is zero for alive and one for dead. Position, remaining countdown and epoch retain their existing fields. It establishes a per-peer epoch boundary. Subsequent events from that peer are checked against that boundary.

`DeathEventType.EpochSync = 7` aligns the epoch without asserting that the sender is alive. Stale bench notifications cannot resurrect a dead peer. Snapshots and subsequent death events use the same reliable ordered Death channel.

## Silk and fragments

The legacy-named `MaxSilkSpools` field carries raw `PlayerData.silkMax` capacity in v12. Initial capacity is nine. One native `AddToMaxSilk(1)` increases capacity by one. Permanent partial progress is `silkSpoolParts`; `silkParts` is transient resource state and must not be replicated as progression. A silk upgrade requires two permanent parts; a health upgrade requires four heart pieces.

Native counter resets do not represent negative pickups. Applying remote fragments waits while a native local conversion is in flight. Earned maxima account for both local upgrades and remote completed fragments, preventing an independent Vitals message from awarding the same upgrade twice.

## Validation limits

Counter transaction sequences remain protected after GUID FIFO eviction. Legacy zero-sequence callers use a bounded retained GUID guard; exhaustion refuses new work. Reliable unordered delivery uses a contiguous receive watermark with bounded future acceptance, preserving retransmission of missing old packets.

Gameplay validation on two real game instances remains required before publication. API-shim checks exercise production adapter code but do not substitute for Unity physics or complete pickup FSM coverage.