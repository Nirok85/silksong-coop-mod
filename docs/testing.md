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

## Adding tests

- Core tests only (no Unity dependency).
- Use `MockNetworkTransport.CreatePair()` for network scenarios.
- Use `RemoteApplyScope` to simulate remote application.
- Static-analysis tests must account for partial classes (check every
  `SilksongCoopPlugin*.cs` file).
