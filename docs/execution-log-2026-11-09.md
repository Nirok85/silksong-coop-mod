# Журнал выполнения плана — 2026-11-09

Основание: `docs/global-improvement-plan-2026-11.md`. Протокол остаётся **v8**.
Все проверки выполнялись на Windows, Silksong 1.0.30000, Unity 6000.0.50f1.

## Итог

| Показатель | До | После |
|---|---:|---:|
| Тесты | 934 | **963** |
| Release build | 0 ошибок / 0 предупреждений | **0 / 0** |
| Debug build | 0 / 0 | **0 / 0** |
| Fuzz / chaos / allocation тесты | базовые | **+21 новых тестов** |
| Бенчмарки Core | нет | **7 сценариев, 5 классов** |
| CI | Linux Core + Mod | **Linux/Windows Core + nightly + benchmark smoke** |

## Этап A — стабильная база

- `docs/coop-invariants.md`: зафиксированы авторитет, HP/snapshot, смерть,
  сохранения, reconnect, транспорт и UI-инварианты.
- `docs/smoke-3.3.3.md`: одиночный smoke пройден на 1280×720 и 1024×600;
  двухклиентный сценарий описан как обязательный ручной гейт.
- `RealUdpLifecycleTests`: реальные UDP-сокеты, все каналы, reliable burst,
  client reconnect и host restart.
- Одиночный smoke: 3.3.3 загружается, F10 и F11 работают, локализация видна,
  ошибок после инициализации нет.

## Этап B — измерения

- `tools/SilksongCoop.Benchmarks` (BenchmarkDotNet 0.14.0):
  заголовок, transform-пакет, enemy snapshot, merge удара, snapshot-buffer,
  rate limiter и room code.
- `docs/perf-baseline-2026-11.md`: ShortRun baseline.
- `SerializationAllocationTests`: 0 B для header, transform roundtrip,
  enemy merge и snapshot-buffer после прогрева.
- Результат: steady-state Core hot path не аллоцирует; `RoomCodeCodec`
  выделяет 264 B только на подключении.

## Этап C — надёжность

- `ChaosSimulationTests`: 45% потерь unreliable-трафика, 120 ms jitter,
  200 reliable-пакетов в порядке; reorder sequenced-канала; дубли ударов;
  snapshot timestamps и запрет воскрешения.
- `ProtocolFuzzCampaignTests`: 20 000 итераций парсеров, 20 000 строк room
  code, 10 000 чтений `PacketReader`; ночной режим
  `SILKSONG_FUZZ_ITERATIONS=200000`.
- Проверены инварианты: нет двойного урона, нет stale snapshot, нет
  воскрешения, нет неожиданных исключений.

## Этап D — оптимизация

- Transform broadcast: ровно одна отправка на каждого реального получателя;
  убрана отдельная отправка в `RemotePeerId` с пропуском в цикле.
- Snapshot: одно чтение `transform.position` на отправляемого врага.
- `StateFlagReader` вынесен в Core; после прогрева 0 B на вызов.
- `EnemySnapshotSendPolicy` вынесена в Core; таблица истинности покрыта
  тестами.
- Baseline подтверждает отсутствие per-hit/per-packet аллокаций.

## Этап E — архитектура

- `NetworkOrchestrator` уменьшен: reflection/accessor-логика переехала в
  `Core/Networking/StateFlagReader.cs`.
- Snapshot-политика отделена от Unity-контроллера в
  `Core/State/EnemySnapshotSendPolicy.cs`.
- `EnemySyncController` и транспорты остаются крупными; их дальнейшее
  разбиение требует отдельной итерации с интеграционными тестами. Это
  зафиксировано как следующий шаг, а не выдано за завершённое.

## Этап F — UX/UI

- Адаптивный размер окна: 460×600 при наличии места, уменьшение до
  доступной области; drag/close используют фактическую ширину.
- Тосты и панель диагностики не уходят за экран на узких разрешениях.
- Локализованы `Auto`, `English`, `Russian`, placeholder адреса.
- Проверено на 1024×600: окно 460×520, F10/F11 доступны, контент
  прокручивается.

## Этап G — 3+ игрока

- `docs/multiplayer-3plus-analysis.md`: что уже готово (PeerRegistry,
  per-peer vitals/death, клоны, HUD, Steam full-mesh), какие ограничения
  остаются (UDP single-peer, relay, feature-set, host identity, HUD).
- Вывод: Steam 3+ можно доводить без protocol bump; UDP 3+ требует v9.
- В текущем релизе 3+ не включён по умолчанию и не заявлен как стабильный.

## Этап H — CI/CD

- CI: матрица Ubuntu/Windows, `--blame-hang`, таймауты, minimal permissions.
- Nightly: расширенный fuzz и benchmark dry run.
- `scripts/release-notes.ps1`: release body из CHANGELOG.
- `scripts/verify-artifacts.ps1`: SHA-256, состав архивов, запрет игровых
  сборок. Проверен на 3.3.3.
- `docs/release-checklist.md` и `docs/RELEASING.md` обновлены.

## Проверки

```text
dotnet build -c Release -p:TreatWarningsAsErrors=true  -> 0 warnings, 0 errors
dotnet build -c Debug   -p:TreatWarningsAsErrors=true  -> 0 warnings, 0 errors
dotnet test  -c Release                                 -> 963 passed, 0 skipped
preflight-release.ps1                                   -> passed
package-release.ps1                                     -> both archives produced
verify-artifacts.ps1 -Version 3.3.3                     -> passed
```

## Артефакты 3.3.3

| Файл | SHA-256 |
|---|---|
| `SilksongCoop_v3.3.3.zip` | `a3e91e5f1eb1958f1466f7ac78d8cea22c9bce93ff39c995fedf029e922d9ee5` |
| `SilksongCoop_v3.3.3_plugin-only.zip` | `05d9f035d4c9abdd2f11d8c8cdcfb847d2aba3a20fb0d024415f34d2eb98060e` |

## Незакрытые внешние гейты

- Живой smoke двумя клиентами (UDP и Steam): подключение, бой,
  смерть/revive, скамейка, переходы сцен, late join, reconnect, quest-флаги.
- Steam 3+ экспериментальный прогон.
- Тег `v3.3.3` и публикация GitHub Release — только после этих проверок.

Автоматические тесты проверяют протокол и сервисы, но не заменяют Unity-сцену
и Steam API.

