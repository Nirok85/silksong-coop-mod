# Performance baseline — 2026-11-09

Сборка: Release, `BenchmarkDotNet 0.14.0`, .NET 8.0.24, RyuJIT AVX2.
Процессор в отчёте BDN определён как `Unknown processor`; это не мешает
относительному сравнению, но абсолютные числа нельзя переносить на другую
машину без повторного прогона.

Команда:

```powershell
dotnet run --project tools/SilksongCoop.Benchmarks -c Release -- --filter "*" --job short
```

Для быстрой проверки API: `--job dry`.

## Результаты ShortRun

| Benchmark | Mean | Allocated |
|---|---:|---:|
| `PacketHeader.WriteAndRead` | 7.5 ns | 0 B |
| `TransformSyncPacket.SerializeDeserialize` | 65.2 ns | 0 B |
| `EnemySnapshotPacket` batch serialize | 7.2 ns | 0 B |
| `EnemySyncService.EnemyHitMerge` | 24.0 ns | 0 B |
| `SnapshotBuffer.AddAndInterpolate` | 386 ns | 0 B |
| `RateLimiter.TryConsume` | 42.7 ns | 0 B |
| `RoomCodeCodec.TryDecode` | 188 ns | 264 B |

`ShortRun` использует 3 итерации; значения с большим `StdErr`
(`SnapshotBuffer`, `RoomCodeDecode`) следует перепроверять полным job перед
принятием решения. Для сравнения изменений достаточно одного и того же job.

## Выводы

- Заголовок, transform-пакет, enemy snapshot, merge удара, snapshot-buffer и
  rate limiter не выделяют память в steady state. Это подтверждает
  allocation-тесты в `tests/SilksongCoop.Tests/Performance`.
- `EnemySyncService` обрабатывает удар примерно за 24 ns; при 60 Hz и даже
  сотнях ударов в секунду это не является узким местом.
- `RoomCodeCodec.TryDecode` выделяет `IPAddress`/строку. Это путь подключения
  (не горячий); оптимизация не приоритетна.
- Сетевой send/receive и Unity-хуки нельзя измерить этим проектом: для них
  нужен Unity Profiler / `dotnet-trace` на реальной игре. BDN-проект покрывает
  только Core.

## Следующие шаги

1. Повторить baseline после каждого изменения hot path и сравнивать
   `Allocated` и `Mean` при одинаковом job.
2. Добавить benchmark для `PacketReader.ReadString`/`PacketWriter.WriteString`
   только если строки появятся в частом пути.
3. Для реальной игры снять Unity Profiler на сценах: обычное перемещение,
   насыщенный бой, переход сцены, открытые F10/F11.
4. Зафиксировать в CI только smoke-запуск `--job dry`; полноценные замеры —
   локально/ночью, чтобы не платить за шумных соседей на runner.
