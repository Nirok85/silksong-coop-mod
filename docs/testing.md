# Testing guide

## Running tests

```bash
dotnet test tests/SilksongCoop.Tests/SilksongCoop.Tests.csproj -c Release
dotnet build tests/SilksongCoop.Tests/SilksongCoop.Tests.csproj -c Release -p:TreatWarningsAsErrors=true
```

The test project references `SilksongCoop.Core` only — it must not reference
`SilksongCoop.Mod` (the Mod needs the game's managed assemblies).

## Test harness

- `MockNetworkTransport` — in-memory transport with configurable latency, jitter,
  loss and virtual time; a real UDP loopback transport is used by the integration
  tests.
- `FakeSteamTransport` — in-memory `INetworkTransport` with a paired host/client,
  channel recording and disconnect events. It does **not** exercise
  `SteamP2PTransport` itself; Steam handshake, lobby and P2P behaviour is only
  verified manually in game.
- `DualClientTestHarness`, `HeadlessPlayerCloneManager` — engine-agnostic helpers
  for multi-side scenarios.

## Categories

Protocol serialization/validation, session state, vitals/currency, death lifecycle,
enemy/pickup sync, interpolation, room codes, safe ground, adversarial/stress,
property-based invariants, integration/E2E over UDP loopback.

## Coverage boundaries

| Area | Automated | Manual in game |
|---|---|---|
| Core protocol/state/pure logic | Yes | - |
| Real UDP connect/reconnect/retransmit | Yes (loopback) | Yes (LAN/VPN) |
| Unity Harmony hooks and patch targets | No | Yes |
| Steam lobby/P2P/full mesh | No (fake transport only) | Yes, including 3+ |
| Scene transitions, saves, boss phases | No | Yes |
| Release archives | Packaging script checks | Install smoke |

A green Core test run does not prove that the Mod compiled against the current game
build or that Steam/Unity behaviour is correct. Record the game version, mod
version, OS, transport and result for every manual run.

## Adding tests

- Core tests only (no Unity dependency).
- Use `MockNetworkTransport.CreatePair()` for network scenarios.
- Use `RemoteApplyScope` to simulate remote application.
- Static-analysis tests must account for partial classes (check every
  `SilksongCoopPlugin*.cs` file).
