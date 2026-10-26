# Silksong Co-op Mod — Large-Scale Improvement Campaign Spec

## Goal
Transform the mod from a "working prototype" into a robust, production-grade co-op experience by executing the 15-step improvement plan across 5 phases: Architecture, Network Layer, Game Sync, Security, and UX/Observability.

## Boundaries
- **In scope**: All 15 steps from `docs/improvement-plan.md`.
- **Out of scope**: New game content, new characters, visual art changes.
- **Must not be touched**: Test files (existing 603 tests must pass without modification); core protocol serialization binary format (PacketReader/Writer wire format stays compatible).
- **Compatibility**: Must remain backward-compatible within v2.x (new packet types allowed, existing packet formats unchanged).

## Acceptance Criteria
1. Build: 0 errors, 0 warnings (Debug + Release).
2. Tests: 603/603 existing tests pass (no test file modifications).
3. Each milestone produces a verifiable, committed result.
4. No regressions in connection stability (loopback handshake test passes).
5. New features (multi-peer, scene sync, auto-reconnect) demonstrably functional.
6. Security: malformed packet fuzzing produces 0 crashes.

## Risks
- Multi-peer architecture is a large refactor touching transport, plugin, clone spawner, and UI.
- Scene/boss sync requires deep game API knowledge (Silksong internals).
- Lock reentrancy refactor could change event ordering and break timing-sensitive tests.
- Performance regressions possible from compression/fragmentation overhead.
- Steam transport changes cannot be tested without Steam runtime.

## Milestones
See section below. Each milestone = verifiable result with checks.
