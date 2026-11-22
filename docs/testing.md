# Testing guide

## Running tests

```bash
dotnet test tests/SilksongCoop.Tests/SilksongCoop.Tests.csproj -c Release
dotnet build tests/SilksongCoop.Tests/SilksongCoop.Tests.csproj -c Release -p:TreatWarningsAsErrors=true
```

The test project references `SilksongCoop.Core` only — it must not reference
`SilksongCoop.Mod` (the Mod needs the game's managed assemblies).

## Production adapter checks

Production adapter checks compile production code against small shims:

- `tests/SilksongCoop.RuntimeChecks` compiles `UnityGroundProber.cs` and checks body bounds, trigger filtering, blocked clearance and bounded query buffers.
- `tests/SilksongCoop.ModSourceChecks` compiles production `InventoryHooks.cs` and `RemoteApplyScope.cs` and checks the save-bool whitelist, hook guards and category forwarding.
- `tests/SilksongCoop.WalletPumpChecks` compiles the production `PeerHandshakeCoordinator` and `PacketSender`. Its boundary shims exercise marker retry, interrupted and duplicate snapshots, local stages, and per-peer isolation on timeout.

CI and release preflight run all three separately from the Core suite. With `-NoRestore`, restore these projects beforehand because they are outside the solution.

The `E2ERemediationTests` simulation helpers are model tests, not actual Unity end-to-end tests. Production Core regressions cover wallet snapshot capture/serialization/assembly/application, counter replay, lifecycle handshakes and deferred damage. Real Unity physics, pickup FSM interleaving and two-instance gameplay still require in-game verification.

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

## Reliability soak

From the repository root, use the same runner for the 15-second smoke and the 45-minute run. Record the full commit and a source manifest for a dirty candidate.

~~~powershell
dotnet restore tools/SilksongCoop.Soak/SilksongCoop.Soak.csproj --locked-mode
dotnet run --project tools/SilksongCoop.Soak -c Release --no-restore -- 1337 15 "$env:TEMP/soak-smoke.json" '<candidate-id>'
dotnet run --project tools/SilksongCoop.Soak -c Release --no-restore -- 1337 2700 "$env:TEMP/soak-long.json" '<candidate-id>'
~~~

The long run uses three 15-minute phases. The first runs real UDP on loopback with reconnect and backpressure. The other two use virtual time and seeded loss, delay, and send refusals on two and then three to four nodes. Use `Replay` from a failed report to repeat the failing scenario. The optional fifth argument selects a single scenario in `udp`, `two` or `group`.

UDP reports the unacknowledged backlog. The virtual profiles report per-link receive and reorder queues. Timing percentiles and allocation totals describe the runner and its driving thread. They do not measure frame time in Unity. Hosted CI runs the smoke. Invoke **Nightly reliability** with `run_soak=true` for the long profile.

## Categories

Protocol serialization/validation, session state, vitals/currency, death lifecycle,
enemy/pickup sync, interpolation, room codes, safe ground, adversarial/stress,
property-based invariants, integration/E2E over UDP loopback.

## Coverage boundaries

| Area | Automated | Manual in game |
|---|---|---|
| Core protocol/state/pure logic | Yes | - |
| Real UDP connect/reconnect/retransmit | Yes (loopback) | Yes (LAN/VPN) |
| Reliable backlog cap and drop diagnostics | Yes (loopback) | - |
| Unity Harmony hooks and patch targets | No | Yes |
| Steam lobby/P2P/full mesh | No (fake transport only; the create-callback sequencing is code-reviewed) | Yes, including 3+ |
| Scene transitions, saves, boss phases | No | Yes |
| Pending world/quest state across scene loads | Core rules only (`RevisionLedger`, packet size) | Yes |
| Release archives | Packaging script checks | Install smoke |

A green Core test run does not prove that the Mod compiled against the current game
build or that Steam/Unity behaviour is correct. Record the game version, mod
version, release commit SHA, OS, transport and result for every manual run.

## Adding tests

- Core tests only (no Unity dependency).
- Use `MockNetworkTransport.CreatePair()` for network scenarios.
- Remote application (`RemoteApplyScope`) lives in the Mod assembly and cannot be
  used from this project; model the receive rule directly in Core instead, or
  verify it manually in game.
- Static-analysis tests must account for partial classes (check every
  `SilksongCoopPlugin*.cs` file).