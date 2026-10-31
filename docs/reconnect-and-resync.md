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
  cached and retried; the applied revision is recorded when the retry succeeds.

## What does not exist

- There is no `SessionId` / `ConnectionEpoch` handshake and no delta resync:
  catch-up is a full replay of the registered state.
- There is no "reconnect rejected because a different session" check beyond the
  protocol/mod version agreement.
- Disconnect during a scene change does not preserve per-scene state in a formal
  way; only the pending caches described above survive.

## Related limits

The region/replay registries are capped (1024 world events, bounded pending
caches). A very large world can therefore drop the oldest catch-up entries.
