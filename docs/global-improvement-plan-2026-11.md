# Глобальный план улучшения и оптимизации (2026-11)

Этот документ дополняет `docs/improvement-plan-2026-11.md` и фиксирует
следующий крупный цикл развития проекта после аудита 2026-11-08. Принципы:

- не ломать протокол **v8**; любые изменения wire-format — только через
  feature flag и новый `NetworkProtocol.Version`;
- сначала измерения, потом оптимизации;
- инкрементальность: одна подсистема за итерацию + зелёные тесты + CI;
- UDP-клиенты 3.3.x остаются совместимыми; Steam требует одинаковой версии.

## Текущее состояние

| Показатель | Значение |
|---|---|
| Исходники | 124 файла, ~24 400 строк |
| Тесты | 89 файлов, ~19 000 строк, 934 теста |
| Протокол | v8, UDP + Steam P2P |
| Крупнейшие файлы | `EnemySyncController` 2080, `LiteNetLibTransport` 1855, `SilksongCoopPlugin` 1749, `CoopMenuUI` 1530, `SteamP2PTransport` 1520 |
| CI | GitHub Actions: сборка + тесты + preflight |

## Фаза 0 — Стабилизация и релиз 3.3.3 (1–2 недели)

- Закрыть оставшиеся UI-пункты: rich-text в тостах, полная локализация
  меток `BW`, `KB/s`, `HP`, `Silk`.
- Провести живой smoke UDP + Steam: подключение, бой, смерть/возрождение,
  скамейка, переходы сцен, reconnect, late join, quest-флаги.
- Поднять версию до **3.3.3**, обновить `CHANGELOG.md`, `README.md`,
  `README.ru.md`, `ROADMAP.md`.
- Собрать архивы, проверить SHA-256, прогнать `preflight-release.ps1`,
  поставить тег, опубликовать GitHub Release.
- **Метрика:** 0 блокеров, 934+ тестов, CI green, smoke pass.

## Фаза 1 — Производительность (2–4 недели)

| Задача | Приоритет | Метрика |
|---|---|---|
| Профилирование hot paths (`Update`, `FixedUpdate`, `OnGUI`, receive/send) | P0 | отчёт с точками аллокаций |
| Убрать аллокации в steady-state: пулы, `Span`, отказ от LINQ/foreach в hot path | P0 | 0 B/frame в Core hot path |
| Delta-сжатие snapshot'ов врагов и world-state | P1 | размер snapshot −40% |
| Батчинг/coalescing сетевых отправок | P1 | пакетов/с −20% |
| Оптимизация IMGUI: кэш стилей, отрисовка только на `Repaint`, минимум `GUILayout` | P1 | `OnGUI` < 1 ms |
| Настройка GC: incremental GC, избегать `new` в циклах | P2 | GC spike < 2 ms |
| Бенчмарки `BenchmarkDotNet` для Core | P2 | baseline + regression gate |

Инструменты: Unity Profiler, `dotnet-trace`, `BenchmarkDotNet`, `dotnet-counters`.

## Фаза 2 — Надёжность и тестирование (3–4 недели)

- Интеграционные тесты: UDP loopback, mock Steam, 2 клиента, реальные сокеты.
- Chaos-тесты: потеря 10–30% пакетов, задержка 50–300 ms, дубли, reorder,
  reconnect, late join.
- Fuzzing: `PacketReader`, `PacketValidator`, `RoomCodeCodec` (SharpFuzz).
- Property-based тесты (FsCheck): roundtrip сериализации, инварианты
  авторитета, монотонность HP.
- Stress: 8 пиров, 1000 пакетов/с, 60 секунд, без desync.
- Coverage gate: Core ≥ 80%, общий ≥ 70%.
- **Метрика:** 0 flaky, 0 crash в chaos, 1M fuzz-итераций без исключений.

## Фаза 3 — Архитектура и качество кода (4–6 недель)

| Монолит | Разбиение |
|---|---|
| `EnemySyncController` (2080) | orchestration, snapshot loop, dead-enemy replay, persistent-state replay, world-event replay |
| `LiteNetLibTransport` (1855) | transport core, handshake/auth, reliability, rate limiting, send/receive loop |
| `SilksongCoopPlugin` (1749) | bootstrap, transport coordinator, session lifecycle, hooks |
| `CoopMenuUI` (1530) | window shell, tabs, HUD renderer, onboarding |
| `SteamP2PTransport` (1520) | lobby, P2P peers, handshake, lifecycle |

- Ввести Roslyn analyzers: `Microsoft.CodeAnalysis.NetAnalyzers`, StyleCop,
  `TreatWarningsAsErrors`.
- Убрать статические синглтоны, перейти на явные зависимости/фабрики.
- Ввести coverage и mutation testing (Stryker.NET) для Core.
- **Метрика:** max файл ≤ 800 строк, 0 analyzer warnings, coverage gate в CI.

## Фаза 4 — UX/UI и локализация (2–3 недели)

- Полный аудит строк: все пользовательские тексты через `Loc.T`, RU/EN,
  плюрализация.
- Адаптивное окно и масштабирование UI (0.75×–2.0×), доступность
  (контраст AA, крупные шрифты).
- Улучшить onboarding, настройки HUD (позиция/масштаб), экспорт диагностики.
- **Метрика:** 100% строк локализовано, UI не выходит за экран при 1280×720,
  контраст ≥ 4.5:1.

## Фаза 5 — Масштабирование 3+ игроков (6–8 недель)

- AOI / interest management в `NetworkOrchestrator`.
- Per-peer vitals, death/revive, HUD.
- Steam full-mesh + relay, UDP host-relay.
- Feature-set в handshake.
- **Метрика:** 4 игрока стабильно, 8 — experimental, 0 desync за 30 минут.

## Фаза 6 — CI/CD и релизы (постоянно)

- Matrix Windows/Linux, кэш NuGet, артефакты сборки.
- Автоматическая упаковка архивов, SHA-256, manifest.
- Release notes из `CHANGELOG.md`, smoke-runbook.
- **Метрика:** релиз < 30 минут, 0 ручных шагов, откат за 1 команду.

## Ключевые метрики успеха

| Область | Метрика | Цель |
|---|---|---|
| Производительность | GC alloc/frame в hot path | 0 B |
| Производительность | CPU overhead мода | < 1% |
| Сеть | p99 RTT | < 100 ms (LAN) |
| Сеть | Потеря reliable-пакетов | 0 |
| Надёжность | Reconnect success | > 99% |
| Качество | Coverage Core | ≥ 80% |
| Качество | Flaky tests | 0 |
| UX | Локализация | 100% |
| Релиз | Time to release | < 30 мин |

## Риски и контроль

| Риск | Митигация |
|---|---|
| Рефакторинг ломает gameplay | инкрементально, по подсистемам, с интеграционными тестами |
| Оптимизация меняет поведение | бенчмарки + regression gate в CI |
| Изменение протокола ломает совместимость | feature flag + version bump + отдельная ветка |
| Steam 3+ сложен | experimental flag, не блокирует 2-player релиз |
| UI-переделка ломает читаемость | скриншот-тесты/ручной smoke на 1280×720 и 4K |
