# State authority model

This document is normative: it describes who writes each piece of state, how a
receiver resolves conflicts, and which failures are accepted by design. When code
and this document disagree, the code is wrong unless the document is updated in
the same change.

## Current state (3.3.3)

| State | Writer(s) | Receiver rule | Conflict resolution |
|---|---|---|---|
| Player transform | Each player writes their own | Field-validated (NaN/Infinity, world bounds, velocity cap) | Newer tick wins; interpolation smooths the rest |
| Player death/revive | Each player writes their own lifecycle | `DeathLifecycleCoordinator` with epoch + event type; per-peer alive/position state when several peers are registered | Higher epoch is adopted; stale epochs are ignored; the nearest living peer is selected and re-validated (alive, connected, same scene) immediately before the revive; a fallback goes to the local bench with an explicit banner/toast; `PartnerReviveTrigger` is sent only after `ConfirmReviveAtPartner` |
| Shared vitals (max HP, silk spools, needle) | Either peer | Monotonic upgrades only, applied under `RemoteApplyScope` | A higher value wins; a lower value never downgrades |
| Currency | Either peer | Transaction GUID dedup + per-sender sequence, applied under `RemoteApplyScope` | Duplicate transaction IDs are dropped; wallet is clamped to a sane ceiling |
| Inventory unlocks | Either peer | Idempotent per item key | First unlock wins; repeats are no-ops |
| Enemy/boss HP, deaths and phases | Attackers replicate hits; the host simulates its scene | `EnemySyncController` + `EnemySyncService`; a replicated hit re-emits `TookDamage`/`TOOK DAMAGE` without reapplying damage | Exact `scene + hierarchy` key; see the authority rules below (absolute host HP, additive client damage only without host authority, per-sender sequence dedup) |
| World events (breakables, gates) | Either peer | `WorldEventPacket` with `StateRevision`/`OriginPeerId` | Per-key revision/ origin comparison in `RevisionLedger`; replay of stored events on join; queued stale revisions are discarded before applying |
| Persistent world values | Either peer | `PersistentStatePacket` keyed by scene + persistent id + type; `SaveState` hook plus a 1 Hz poll of `PersistentBoolItem`/`PersistentIntItem` | Same `RevisionLedger` rule; the retry queue re-checks staleness before `SaveState`; polling seeds initial scene state without broadcasting it |
| Quest flags from NPC dialogue | Either peer | `QuestStatePacket` (0x56) after `NPCControlBase` dialogue plus a 1 Hz poll; only allowlisted `PlayerData` bool/int names | Diff of before/after snapshots; only changed values are sent and applied under `RemoteApplyScope`; batches are split by encoded size; overlapping dialogue flushes keep separate baselines |
| Physical pickups | Either peer | Stable pickup key + one-time claim tracking | Exact key first; coordinates only as a guarded fallback |

## What "authoritative" means here

There is **no** host-authoritative arbitration for arbitrary state: the mod does
not vote, does not compare peers, and does not reject a peer's vitals, currency,
world events or deaths merely because the local peer is the host.

The host is authoritative for **enemy HP in its current scene**, and the primary
peer (`SessionState.RemotePeerId`) is the source of that authority for a client —
the same peer whose `EnemySnapshotPacket` is accepted. The rules are:

1. **Host sender, fresh authority: absolute HP.** A hit packet from that peer
   replaces the local HP with the packet's absolute value. It is never subtracted
   from the local value, because the host snapshot may already contain the same
   hit. Equal or higher HP is treated as a duplicate/stale packet and ignored.
   The one exception is a local hit made after the last applied snapshot *and* a
   host HP that is still at or above the local value: only then the host cannot
   have incorporated that hit, so the two simultaneous hits are combined. If the
   host HP is already lower it supersedes the local hit; subtracting again would
   double-count the same damage.
2. **Other peer, fresh authority: deferred.** While the host has sent a snapshot
   for that enemy in the last 3 seconds, hits and deaths reported by other clients
   are ignored. The host already received those hits; applying them again would
   double-count the damage or let a client declare an unconfirmed death. The next
   host snapshot/death packet converges the state.
3. **No fresh authority: additive.** When the host is in another room (no recent
   snapshot for that enemy), clients combine simultaneous damage additively and
   may kill the enemy themselves.
4. **Sequence dedup first.** A packet whose sender+sequence was already applied is
   dropped before the death branch, so a delayed death cannot override a newer hit.
5. **The host confirms kills on its own state.** The local host treats a client's
   `Death` event as a damage claim, not as proof. It seeds the merge with the HP it
   is currently simulating, subtracts the claimed damage, and only dies when that
   combined value is zero or below. A divergent client therefore cannot kill an
   enemy the host still has alive. The host broadcasts the confirmed death; clients
   apply the host death packet/snapshot. A client's claim that is not lethal is
   corrected by the next host snapshot.
6. **Snapshots never resurrect a newer local hit.** A snapshot generated before the
   local player's last hit may lower HP or confirm a death, but may not raise HP.
   This avoids the visible "enemy heals" window while the host is still receiving
   the hit. A later snapshot (after host authority has caught up) may correct the
   value normally.

The host is not authoritative for player position, death lifecycle, wallet,
inventory or world-progress state, and there is no vote-based arbitration. Do not
describe this mod as generally "host-authoritative": the authority is scoped to
enemy damage and only while the host simulates that scene.

## Save scope

World-event, persistent-state and dead-boss replay registries are scoped to one
save, not merely to the process. They deliberately survive a reconnect within the
same playthrough, but a change of the game's profile id or a reload/new game that
replaces `PlayerData.instance` clears them. A partner can therefore never receive
doors, gates or boss deaths from a different save.

## Transport trust boundary

`SilksongCoopPlugin.ProcessPacket` drops any datagram whose sender is not a
registered session peer (the first `RemotePeerId` or an entry in
`SessionState.RemotePeerIds`), before desync frames or gameplay packets are
parsed. This keeps a queued packet from a previous connection (or a second
endpoint) from mutating the new session. Rejected packets are counted in the
diagnostics rather than logged one by one.

Inside the UDP transport, source-endpoint validation runs before authentication.
Once a room-code session key exists, every post-handshake frame must carry a
valid MAC; an invalid MAC is rejected before the peer's token bucket is charged,
so spoofed invalid-MAC traffic cannot exhaust the real peer's budget. An unsigned
duplicate `Handshake` remains accepted only for the lost-ack retry path, is
charged to a separate per-endpoint pre-auth budget, and never refreshes the
heartbeat. A duplicate handshake cannot change the peer id that owns an
unanswered challenge.

Within that boundary, packets are still untrusted: every payload is range-checked
by `PacketValidator` and then applied under the rules in the table above.

## Object identity

`WorldObjectKey` builds a 64-bit FNV-1a hash from:

- the Unity scene name,
- the full transform hierarchy (`name#siblingIndex` at every level).

Physical pickups use the drop's serialized `PersistentBoolItem` id (scene + persistent
id + pickup salt) when available, so two copies that physics moved apart still share
one identity. Dynamic drops without a persistent id fall back to scene + currency
type + position rounded to 0.1 units. Persistent world items use scene + persistent
id + value type instead of a hierarchy path.

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

- Boss phases driven by custom code that listens neither to `HealthManager.TookDamage`
  nor to the PlayMaker `TOOK DAMAGE` event can still diverge. Phases driven by the
  standard damage signals are kept in sync by `NotifyBossTookDamage`.
- Scripted doors whose state lives only in a PlayMaker FSM or a `PlayerData` flag
  are not replicated. Doors, chests and lifts backed by `PersistentBoolItem` /
  `PersistentIntItem` are covered by the event hook and the 1 Hz poll.
- Quest sync is best-effort and limited to bool/int `PlayerData` names containing
  `Quest` (UI flags excluded). Strings, lists, enum fields and off-list story flags
  are not replicated; pure visual dialogue text is not mirrored. Only changes made
  during the co-op session are synced; joining does not overwrite the partner's
  existing quest progress. A live quest playthrough is still needed.
- Quest sync trusts every connected peer to write allowlisted `PlayerData` flags,
  and those flags are persisted by the game. This is a deliberate co-op trust
  boundary (Steam lobbies are friends-only; UDP room codes should only be shared
  with people you trust). Set `[Gameplay] SyncQuestFlags = false` to disable it and
  back up saves before testing long sessions.
- Reliable packets rejected by the UDP backlog are counted and trigger an
  idempotent host replay / full quest snapshot after the backlog drains. A packet
  lost by the OS without a backlog drop is recovered only when a later snapshot,
  poll or replay covers that state.
- A physical drop without a serialized persistent id can still be credited twice if
  its position diverges beyond the 0.5-unit dedup window; wallet reconciliation
  converges to the higher value. Drops with a `PersistentBoolItem` use its stable id
  and are not affected.
- Maximum stats never decrease by design.
