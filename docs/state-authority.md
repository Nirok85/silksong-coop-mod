# State authority model

This document is normative: it describes who writes each piece of state, how a
receiver resolves conflicts, and which failures are accepted by design. When code
and this document disagree, the code is wrong unless the document is updated in
the same change.

## Current state (3.0.4)

| State | Writer(s) | Receiver rule | Conflict resolution |
|---|---|---|---|
| Player transform | Each player writes their own | Field-validated (NaN/Infinity, world bounds, velocity cap) | Newer tick wins; interpolation smooths the rest |
| Player death/revive | Each player writes their own lifecycle | `DeathLifecycleCoordinator` with epoch + event type | Higher epoch is adopted; stale epochs are ignored |
| Shared vitals (max HP, silk spools, needle) | Either peer | Monotonic upgrades only, applied under `RemoteApplyScope` | A higher value wins; a lower value never downgrades |
| Currency | Either peer | Transaction GUID dedup + per-sender sequence, applied under `RemoteApplyScope` | Duplicate transaction IDs are dropped; wallet is clamped to a sane ceiling |
| Inventory unlocks | Either peer | Idempotent per item key | First unlock wins; repeats are no-ops |
| Enemy/boss HP and deaths | The attacker's local hit is replicated to the partner | `EnemySyncController` + `EnemySyncService` | Exact `scene + hierarchy` key; host snapshot corrects drift |
| World events (breakables, gates) | Either peer | `WorldEventPacket` with `StateRevision`/`OriginPeerId` | Per-key revision/ origin comparison; replay of stored events on join |
| Persistent world values | Either peer | `PersistentStatePacket` keyed by scene + persistent id + type | Per-key revision/origin comparison |
| Physical pickups | Either peer | Stable pickup key + one-time claim tracking | Exact key first; coordinates only as a guarded fallback |

## What "authoritative" means here

There is **no** host-authoritative arbitration for arbitrary state: the mod does
not vote, does not compare peers, and does not reject a peer's vitals, currency,
world events or deaths merely because the local peer is the host.

The host is authoritative for exactly one thing: the **periodic enemy HP
snapshot**. `EnemySnapshotPacket` is sent by the host and applied by clients as a
drift correction after the fields have been validated. It is not a general
consensus mechanism and it does not roll back player, wallet or world-progress
state.

Do not describe this mod as "host-authoritative". If a future release adds
arbitration, this section must be rewritten together with the protocol revision.

## Transport trust boundary

`SilksongCoopPlugin.ProcessPacket` drops any datagram whose sender is not the
current `SessionState.RemotePeerId`, before desync frames or gameplay packets are
parsed. This keeps a queued packet from a previous connection (or a second
endpoint) from mutating the new session. Rejected packets are counted in the
diagnostics rather than logged one by one.

Within that boundary, packets are still untrusted: every payload is range-checked
by `PacketValidator` and then applied under the rules in the table above.

## Object identity

`WorldObjectKey` builds a 64-bit FNV-1a hash from:

- the Unity scene name,
- the full transform hierarchy (`name#siblingIndex` at every level).

Physical pickups add the currency type and position rounded to 0.1 units.
Persistent world items use scene + persistent id + value type instead of a
hierarchy path.

The same object therefore hashes identically on both machines without exchanging
Unity instance IDs. A key is only comparable when both peers run the same game
build; a game update that renames objects or changes sibling order changes the
hash. This is why the guarded proximity fallback exists at all.

## Matching rules (enforced)

1. **Exact key always wins.** Every receive path tries the exact
   `scene + hierarchy` key first. A match is applied immediately and no
   positional search is performed.
2. **Position fallback requires proof of scene presence.** Coordinates are used
   only when `CloneSpawner.IsRemoteInSameScene(sender)` is true, i.e. the sender
   is known to be in the scene the receiver currently has loaded. Unknown,
   disconnected or cross-scene senders get the exact-key path only.
3. **Exactly one candidate.** The fallback applies only when exactly one live
   object of the expected type is inside the 2.5-unit radius. Two or more
   candidates means the update is dropped and logged (throttled); guessing is
   worse than a missed correction because the host snapshot / world replay will
   converge later.
4. **Deferred updates are exact-key only.** Enemies, world events and pickups
   cached because their scene was not loaded are replayed by key. For cached
   world events the original sender is remembered, so the fallback can be used
   after the scene loads only if that same peer is still in it.
5. **Ambiguous matches are observable.** A dropped fallback produces one warning
   per five seconds with the number of suppressed repeats, so a real key drift
   can be diagnosed from `LogOutput.log` without flooding it.

These rules currently cover:

- `EnemySyncController.ApplyRemoteHit` (hits; deaths are exact-key only),
- `EnemySyncController.ApplyRemoteSnapshot` (live entries),
- `EnemySyncController.ApplyRemoteWorldEvent` (breakables, gates),
- `EnemySyncController.OnRemotePickupClaimed` (physical drops).

## Revision rules

See `docs/protocol-v8.md` for the exact idempotency rules (origin-aware
comparison, revision `0` opt-out, cache behaviour, reset on new session).

## Known accepted gaps

- Boss phases driven only by a `TookDamage` event (rather than polling HP) can
  lag by one hit on the partner's machine.
- Scripted doors whose state lives only in a PlayMaker FSM or a `PlayerData` flag
  are not replicated.
- A physical drop can still be credited twice if its position diverges by more
  than the dedup window; wallet reconciliation converges to the higher value.
- Maximum stats never decrease by design.
