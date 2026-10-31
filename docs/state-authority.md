# State authority model

## Current state (3.0.0)

| State | Behaviour today |
|---|---|
| Player transform | Each player replicates their own; remote values are validated (NaN/bounds) |
| Player death/revive | `DeathLifecycleCoordinator`; a dead partner is never revived by transform traffic |
| Shared vitals (max HP, silk, needle) | Monotonic upgrades only; applied under `RemoteApplyScope` |
| Currency | Deltas applied under `RemoteApplyScope` |
| Inventory unlocks | Idempotent per item key |
| World events (breakables, gates) | `WorldEventPacket` with `StateRevision`/`OriginPeerId`, idempotent per key |
| Persistent world values | `PersistentStatePacket`, idempotent per key |

## Not implemented

There is **no** host-authoritative arbitration: the mod does not vote, does not
compare peers, and does not reject a peer's state because the local peer is the
host. Any peer can send vitals, currency, world events and deaths, and the
receiver applies them after field validation. Do not describe 3.0.0 as
"host-authoritative".

## Revision rules

See `docs/protocol-v8.md` for the exact idempotency rules (origin-aware
comparison, revision `0` opt-out, cache behaviour, reset on new session).
