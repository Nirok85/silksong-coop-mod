# Architecture

## Projects

- `SilksongCoop.Core` — engine-agnostic domain, networking, state, interpolation. Unit-testable, no Unity dependency.
- `SilksongCoop.Mod` — BepInEx plugin, Unity/Harmony integration, Steam transport, UI.
- `SilksongCoop.Tests` — xUnit tests against Core only.

## Key abstractions

- `INetworkTransport` — transport contract (UDP, Steam, mock). Exposes
  `ConnectedPeers`; single-peer transports report one entry.
- `ISessionService` / `ITransportService` — session and transport lifecycle.
- `ISyncOrchestrator` — per-subsystem sync managers.
- `IPlayerCloneManager` — remote clone management.
- `IGameplayActions` — narrow gameplay surface for sync managers (no god object).
- `INetworkDiagnostics` — narrow diagnostics surface.
- `ICoopLogger` (via `CoopLog`) — level-aware logging hook.
- `StateFlagReader` (Core) — resolves and caches the game's controller-state flags
  without reflection on the transform hot path.
- `EnemySnapshotSendPolicy` (Core) — pure rule for first-seen/damaged/changed/dead
  snapshot entries, testable without Unity.
- `PeerRegistry` / `CoopSessionState.RemotePeerIds` — N-player peer foundation;
  `VitalsSyncService` and clone storage are already per-peer.

## Service composition

Composition happens in `SilksongCoopPlugin.Awake()`:

1. Core services: `CoopSessionState`, `VitalsSyncService`, `DeathLifecycleCoordinator`, `InventorySyncService`.
2. Transports: `LiteNetLibTransport`, optional `SteamP2PTransport`, wrapped by `TransportService`.
3. Sync managers: `NetworkOrchestrator`, `VitalsSyncManager`, `InventorySyncManager`, `DeathSyncManager`, `DesyncDetector`.
4. UI: `CoopMenuUI`.

All managers depend on interfaces, not on `SilksongCoopPlugin`. Gameplay actions are exposed via `IGameplayActions`.

## Authority model (implemented in 3.0.x)

- Player position: each player owns and replicates their own; remote values are validated.
- Death/revive: `DeathLifecycleCoordinator`; a dead partner is never revived by transform traffic.
- Shared progression (max HP, silk, needle): monotonic upgrades only.
- Enemy HP: the host sends an authoritative snapshot roughly once per second and
  merges client hits against its own simulated HP. A client death claim is only a
  damage claim until the combined damage is lethal. Snapshots cannot resurrect a
  dead enemy and cannot raise HP before a newer local hit is acknowledged.
- Pickups: claimed idempotently per item key.
- World events / persistent values: `StateRevision` + `OriginPeerId`, idempotent per key (see `docs/protocol-v8.md`).

There is **no** vote-based conflict resolution. Enemy HP and death use the host
as the authority described in `docs/state-authority.md`; world objects use
revision ordering. UDP still accepts exactly one remote peer in v8. Steam has an
experimental full-mesh path; see `docs/multiplayer-3plus-analysis.md`.

## Reconnect / resync

- Catch-up is a full replay of the registered world state (`PumpWorldReplay`), throttled and bounded.
- `EnemySyncController.ResetSession()` clears revision bookkeeping and pending caches for the new session.
- There is **no** `SessionId` / `ConnectionEpoch` handshake and no delta resync.
  See `docs/reconnect-and-resync.md`.

## Logging

`CoopLog` (Core) exposes `Debug/Info/Warn/Error`. The plugin implements `ICoopLogger`
and installs itself as `CoopLog.Logger`, so every Core log line reaches BepInEx
`LogOutput.log` with its severity.
