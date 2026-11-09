# Reconnect and resync

## What works today

- **Transport reconnect.** Auto-reconnect attempts are driven by the plugin; on a
  successful connection `OnPeerConnected` runs a fresh session setup.
- **Session reset on connect.** `EnemySyncController.ResetSession()` clears
  applied-revision bookkeeping, pending (not-yet-loaded) world events and the
  replay queue, so a new session is not rejected by revisions from the old host.
- **World catch-up.** `PumpWorldReplay()` replays the registered world events and
  persistent values in throttled batches to the newly connected peer, preserving
  the original `StateRevision`/`OriginPeerId`.
- **Deferred application.** Events whose objects are not in the loaded scene are
  cached and retried; the applied revision is recorded when the retry succeeds and
  re-checked immediately before applying, so a newer revision applied in the
  meantime is not rolled back by the older cached entry.
- **Reliable-drop recovery.** If transport backpressure rejects reliable packets,
  the host replays world/persistent state and a full snapshot after the backlog
  drains, and quest sync re-sends its full allowlisted snapshot. Currency queues
  behind the backlog and is sent in order as space becomes available. The queue
  belongs to the connection that produced the transactions.
- **Save changes.** Leaving or replacing the loaded save ends the co-op session
  and discards pending wallet reconciliation for that save.
- **Boss-death catch-up.** The host keeps boss death records across scene changes
  (`DeadEnemyRegistry`), and a client that receives a dead-boss snapshot while the
  boss room is unloaded caches it until the scene loads. Ordinary-enemy deaths are
  still dropped on a scene change because those enemies respawn.

## What does not exist

- There is no `SessionId` / `ConnectionEpoch` handshake and no delta resync:
  catch-up is a full replay of the registered state.
- Compatibility checks include the game build, but reconnect has no separate
  identifier for the previous game session.
- Disconnect during a scene change does not preserve per-scene state in a formal
  way; only the pending caches and the boss-death registry described above survive.

## Related limits

The region/replay registries are capped (1024 world events, bounded pending
caches). A very large world can therefore drop the oldest catch-up entries.
