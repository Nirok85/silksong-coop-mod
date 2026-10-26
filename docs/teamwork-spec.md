# Network Stability Campaign - Specification

## Goal
Improve the network stability of the Hollow Knight: Silksong co-op mod by identifying and fixing connection reliability issues, packet handling bugs, and disconnection recovery problems in both transport implementations (LiteNetLib UDP and Steam P2P).

## Boundaries
- **In scope**: Network transport layer (`LiteNetLibTransport`, `SteamP2PTransport`), connection lifecycle, packet handling, retransmission, timeout, disconnection recovery, thread safety of network state.
- **Out of scope**: Game logic, UI rendering, visual effects, asset pipeline, non-network gameplay code.
- **Must not be touched**: Test files (existing tests must pass without modification), core protocol serialization (PacketReader/Writer), game hooks.

## Acceptance Criteria
1. All existing tests pass (603/603) after changes.
2. No new compilation errors or warnings introduced (beyond pre-existing CS8602).
3. Connection timeout works correctly for both host and client.
4. Graceful disconnect properly cleans up resources on both sides.
5. Unexpected disconnect (packet loss, timeout) is detected within 15 seconds.
6. Reliable ordered delivery handles out-of-order, duplicate, and lost packets correctly.
7. No unbounded memory growth in packet buffers under sustained load.
8. Thread safety: all shared state accessed under appropriate locks.
9. Steam transport handles lobby join failures and P2P session rejection gracefully.

## Identified Risk Areas (from code audit)
1. **LiteNetLibTransport.ProcessIncomingDatagram**: No bounds checking on `datagram[0..7]` — IndexOutOfRangeException on malformed/tiny packets (caught by try-catch but silently logged).
2. **LiteNetLibTransport.HandleIncomingData (ReliableOrdered)**: `_pendingReliable` SortedDictionary grows unbounded under packet loss — no stale entry cleanup.
3. **LiteNetLibTransport (Sequenced)**: `sequence >= latest` allows duplicate packets to be processed and dispatched.
4. **LiteNetLibTransport.Send**: Allocates new byte array for payload copy on every call (hot path GC pressure).
5. **LiteNetLibTransport (Host)**: No connection timeout or heartbeat loss detection — host waits indefinitely if client disappears.
6. **LiteNetLibTransport (Client)**: Connection timeout only checked in PollEvents, but if PollEvents is not called, timeout never fires.
7. **SteamP2PTransport.OnLobbyEntered (non-host)**: Calls `NotifyPeerConnected` immediately before handshake completes — peer may be marked connected before P2P session is established.
8. **SteamP2PTransport.OnP2PSessionRequest**: Accepts session from any peer if `_remoteSteamId == CSteamID.Nil` — potential for unauthorized connection before lobby join.
9. **SteamP2PTransport**: No connection timeout — if P2P session never establishes, stays in "connecting" forever.
10. **SteamP2PTransport.Send**: Allocates new buffer when offset != 0 (common case for transform packets).
11. **SteamP2PTransport.PollEvents**: `_readBuffer` expansion under concurrent access (Steam callbacks are single-threaded via RunCallbacks, but buffer resize could race with OnDataReceived dispatch).
12. **CoopSessionState**: `SentPackets`, `ReceivedPackets`, `SentBytes`, `ReceivedBytes` are not protected by lock — concurrent access from multiple threads possible.
13. **LiteNetLibTransport.PollEvents**: `senderEp` (IPEndPoint) is reused across receive calls — if not reset, could process stale sender info.
14. **LiteNetLibTransport**: `GetTimestampMs()` uses `DateTime.UtcNow` with ~15ms resolution on Windows — affects RTT accuracy and retransmission timing.
15. **LiteNetLibTransport (retransmission)**: Fixed 100ms timeout with no exponential backoff — under high latency, excessive retransmissions.
16. **LiteNetLibTransport (Ack processing)**: Iterates `_unacknowledged` LinkedList linearly for each ACK — O(n) per ACK, could be slow under high packet rates.

## Risks
- Changes to network transport could break existing connection tests.
- Steam transport changes require Steam runtime to test (not available in CI).
- Thread safety changes could introduce deadlocks if lock ordering is not consistent.
- Performance changes (buffer reuse) could introduce aliasing bugs if buffers are shared incorrectly.

## Verification Strategy
- Run full test suite (603 tests) after each milestone.
- Add targeted tests for identified edge cases where feasible.
- Static analysis of all network code paths.
- Build verification (0 errors, 0 warnings target).
