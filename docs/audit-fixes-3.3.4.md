# Исправления аудита 3.3.4

Дата: 2026-11-09
Протокол: **v8** (без новых пакетов и полей)
Базовый релиз: 3.3.3 (`80a681e`), исправления — `master`

## Что исправлено

| № | Проблема | Файлы | Как исправлено |
|---|---|---|---|
| 1 | Встречная трата и получение валюты расходились навсегда | `Core/State/VitalsSyncService.cs` | Верхняя сверка с `TotalWalletAmount` разрешена только пока после предыдущего пакета пира не было локального изменения валюты |
| 2 | Разные близкие монеты считались дубликатом | `Core/State/VitalsSyncService.cs` | Проверка по радиусу только между локальной и удалённой симуляцией одного дропа; две локальные или две удалённые заявки с разными ключами независимы |
| 3 | Игровой кошелёк отставал от общего баланса | `Mod/Services/VitalsSyncManager.cs` | После удалённого изменения кошелёк `PlayerData` выравнивается по Core с задержкой 0.15 с (до 4 попыток) |
| 4 | После неодновременного возрождения терялась настоящая смерть | `Core/State/DeathLifecycleCoordinator.cs` | Флаг «эпоха принята во время собственного respawn»; `ResetAfterBenchRespawn` не увеличивает эпоху повторно |
| 5 | Ручное отключение пропускало очистку | `Mod/SilksongCoopPlugin.cs`, `Mod/UI/CoopMenuUI.cs` | Новый `DisconnectFromSession` выполняет полный teardown синхронно до отложенного transport-события |
| 6 | Отложенная смерть врага обходила HP хоста | `Mod/State/EnemySyncController.cs` | Хост не кеширует клиентскую смерть без урона; проверка повторяется при применении |
| 7 | Повторяемые боссы оставались мёртвыми | `Core/State/DeadEnemyRegistry.cs`, `Core/State/EnemySyncService.cs`, `Mod/State/EnemySyncController.cs` | Отдельный флаг sequence-boss, очистка записей и per-enemy состояния по окончании последовательности |
| 8 | Водяной знак ревизии перезаписывался другим отправителем | `Core/State/RevisionLedger.cs` | Watermark хранится для пары (объект, отправитель) |
| 9 | Steam-тайм-аут оставлял P2P-сессию и peer id | `Mod/Networking/SteamP2PTransport.cs` | Тайм-аут закрывает P2P-сессию, очищает primary/additional peers и `_remoteSteamId` |
| 10 | Поздний `LobbyCreated` принимался за новый запрос | `Mod/Networking/SteamP2PTransport.cs` | Очередь поколений запросов; просроченные помечаются sentinel-значением и их лобби закрывается |
| 11 | `Kicked`/`Banned` не удаляли участника | `Mod/Networking/SteamP2PTransport.cs` | Флаги добавлены к обработке выхода из лобби |
| 12 | Выгрузка плагина оставляла героя и объекты | `Mod/SilksongCoopPlugin.cs` | Восстановление spectator/invulnerability и уничтожение spawner/enemy-sync/menu |
| — | 3+ action triggers получал один партнёр | `Mod/SilksongCoopPlugin.cs` | `PacketSender.SendToAll` вместо отправки только `RemotePeerId` |

## Регрессионные тесты

`tests/SilksongCoop.Tests/FullFixRegressionTests.cs` (новый файл; существующие
тесты не изменялись):

- встречная трата/получение сходится к одному балансу;
- устаревший delta-0 resync не поднимает кошелёк после локальной траты;
- две близкие локальные монеты и две близкие удалённые заявки кредитуются обе;
- неодновременный respawn не скрывает смерть партнёра;
- watermark ревизии выживает после смены отправителя;
- sequence-boss удаляется, persistent-boss остаётся;
- `EnemySyncService.Forget` разрешает повторное появление босса.

## Проверки

- `dotnet test -c Release`: **972/972**, 0 failed, 0 skipped.
- Release и Debug: **0 ошибок, 0 предупреждений**.
- Протокол не менялся: v8.

## Что остаётся внешним гейтом

- Полный двухклиентный gameplay-smoke 3.3.4: бой, death/revive, скамейка,
  переходы сцен, late join, reconnect, смена сохранения, quest-флаги.
- Steam two-client и experimental 3+ (late join, per-peer revive, promotion,
  длинная сессия).
- Реальный packet loss/latency за пределами loopback и симулированных тестов.

## Публикация

- Релиз: [v3.3.4](https://github.com/Nirok85/silksong-coop-mod/releases/tag/v3.3.4), published, не draft/prerelease.
- Тег `v3.3.4` (annotated) указывает на `2fb3bc8`.
- `SilksongCoop_v3.3.4.zip`: `21fe8b63ac4715264de27b067e018548ab2a9da66278707780f91e102207efad`.
- `SilksongCoop_v3.3.4_plugin-only.zip`: `2a14d66147b1976e75573e0d0ed6fd6fe94ed65cebfdebac1adaa21a0e449133`.
- Обе суммы совпали после независимого скачивания ассетов с GitHub.
