# Smoke-проверка 3.3.3

Дата: 2026-11-09
Сборка: `3.3.3` (protocol v8)
Платформа: Windows, Silksong 1.0.30000, Unity 6000.0.50f1-uum-100966

## Артефакты

SHA-256 и состав архивов фиксируются в
`dist/3.3.3/SilksongCoop_v3.3.3_SHA256SUMS.txt`. Проверка перед публикацией:

```powershell
pwsh ./scripts/verify-artifacts.ps1 -Version 3.3.3
```

Для smoke в игру развёрнуты `SilksongCoop.Mod.dll` и `SilksongCoop.Core.dll` из
Release-сборки 3.3.3. Предыдущая версия сохранена вне `BepInEx/plugins`, чтобы
BepInEx не загружал две копии плагина.

## Одиночный клиент — пройдено

| Проверка | Результат |
|---|---|
| Игра запускается через Steam | Да |
| BepInEx загружает плагин 3.3.3 | Да |
| Версия/протокол в логе | `mod 3.3.3, protocol v8` |
| Harmony hooks | 7 классов, 0 ошибок |
| Водяной знак `[F10] Silksong Co-op` | Виден |
| F10 открывает меню | Да |
| Вкладки HOST/JOIN/STATUS/SETTINGS | Отображаются |
| Onboarding | Отображается на русском |
| F11 открывает диагностику | Да |
| Локализация диагностики | `Пинг`, `Потери`, `Трафик` |
| Адаптивное окно при 1024×600 | Да, окно 460×520, контент прокручивается |
| Ошибки в `LogOutput.log` после инициализации | Нет |

Фрагмент лога:

```text
[Info   :   BepInEx] Loading [Silksong Co-op Multiplayer 3.3.3]
[Info   :Silksong Co-op Multiplayer] Initializing Silksong Co-op Multiplayer v3.3.3...
[Info   :Silksong Co-op Multiplayer] [Coop] Compatibility: game 1.0.30000, Unity 6000.0.50f1-uum-100966-branch1, mod 3.3.3, protocol v8, runtime 4.0.30319.42000
[Info   :Silksong Co-op Multiplayer] [Coop] UI language: Russian (configured: Auto)
[Info   :Silksong Co-op Multiplayer] Harmony hooks applied: 7 class(es), 0 failed.
[Info   :Silksong Co-op Multiplayer] [QuestSync] Tracking 38 bool and 0 int quest flag(s)
[Info   :Silksong Co-op Multiplayer] Silksong Co-op initialization complete. Press F10 in game for Co-op menu.
```

Скриншоты: `artifacts/game-menu.png`, `artifacts/game-f10.png`,
`artifacts/game-f11.png`, а также проверка адаптивного окна
`artifacts/game-1024x600-f10.png` и `artifacts/game-1024x600-f11.png`.

Примечание: в логе присутствует `Steam initialization error: Steamworks is not
initialized.` при старте. Это ожидаемо для запуска без инициализированного
Steamworks-контекста в момент инициализации плагина; UDP-путь и меню работают.
Steam-транспорт требует отдельной проверки двумя игроками.

## Два клиента на уровне протокола — пройдено

Реальный хост в игре + headless-клиент `tools/SilksongCoop.Probe` (не второй
игрок, но настоящий UDP-сокет и настоящий handshake):

```powershell
# Хост: F10 -> HOST -> СОЗДАТЬ UDP; код комнаты копируется в буфер обмена.
dotnet run --project tools/SilksongCoop.Probe -c Release -- "SK-XXXX-..." 20
```

Результат:

| Проверка | Результат |
|---|---|
| Хост создаёт UDP-сессию с room code | Да |
| Требование room code | Да, прямой IP без кода отклонён |
| Handshake клиента | `CONNECTED peer=1 name=Host` |
| Vitals клиента приняты игрой | Да, HUD показал HP/Silk |
| Catch-up от игры | `VitalsSync = 1`, `ItemUnlock = 3` |
| RTT / loss | 16 ms / 0% |
| Скриншот | `artifacts/game-connected-probe.png` |

Это подтверждает handshake, HMAC room code, реальный сокет, catch-up и HUD.
Не покрыты: бой, смерть/revive, скамейка, переходы сцен, late join и
квесты — для них нужен второй экземпляр игры.

## Два клиента — обязательный ручной гейт

Автоматический протокольный тест на реальных UDP-сокетах выполняется в
`tests/SilksongCoop.Tests/Integration/RealUdpLifecycleTests.cs` и покрывает
handshake, все каналы, reconnect и очистку. Он не проверяет Unity-сцену.

Перед публикацией релиза необходимо пройти сценарий на двух машинах/аккаунтах.

### UDP (LAN или VPN)

1. Оба игрока ставят одну и ту же сборку 3.3.3.
2. Хост: F10 → HOST → `СОЗДАТЬ UDP`.
3. Клиент: F10 → JOIN → адрес `IP:port` или room code.
4. Проверить: подключение, появление клона, движение, анимацию.
5. Бой: одновременные удары по одному врагу, отсутствие двойного урона.
6. Смерть клиента → revive напарником; смерть хоста → revive клиентом.
7. Скамейка: оба игрока садятся, checkpoint синхронизируется.
8. Переход сцены каждым игроком, возврат в одну сцену.
9. Late join: подключить второго игрока после убийства босса.
10. Reconnect: разорвать связь на 5–10 секунд, восстановить.
11. Квесты: диалог/флаг, проверить отсутствие отката и дублей.
12. Смена сохранения: выйти в меню, загрузить другой слот, подключиться снова.
13. F11: ping/loss/bandwidth обновляются, нет роста очередей.
14. Логи обоих клиентов: нет исключений, нет `Desync`.

### Steam

1. Хост: F10 → HOST → `СОЗДАТЬ Steam P2P лобби`.
2. Клиент: F10 → JOIN → Steam lobby.
3. Повторить шаги 3–14 из UDP-сценария.
4. Проверить, что версии совпадают: Steam отклоняет несовпадающую версию.

### Критерий прохождения

- Нет рассинхронизации HP, позиции и checkpoint.
- Нет двойного урона/двойной смерти.
- Reconnect и late join не теряют прогресс и не применяют события чужой сцены.
- Нет исключений и зависаний в меню/диагностике.
- Оба клиента остаются играбельными после смены сохранения.

## Текущий статус

- Одиночный клиент: **пройден**.
- Автоматический реальный UDP loopback: **пройден**.
- Хост в реальной игре + headless-клиент: **пройден** (handshake, room code,
  catch-up, HUD, RTT).
- Два живых клиента UDP/Steam (бой, смерть/revive, сцены): **не выполнено** —
  требуется второй игрок/машина.
- Тег `v3.3.3` и GitHub Release: **опубликованы** 2026-11-09
  ([release](https://github.com/Nirok85/silksong-coop-mod/releases/tag/v3.3.3)).
  Публикация выполнена по решению владельца проекта; полный двухклиентный
  gameplay-smoke остаётся незакрытым и обязателен для следующего патча.

