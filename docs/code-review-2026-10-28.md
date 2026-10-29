# Полный аудит кода Silksong Co-op Mod v2.1.0

**Дата:** 2026-10-28
**Коммит:** `b6c4824` (master, рабочий тест — clean)
**Объём:** 124 `.cs`-файла; `src` — 17 603 строки, `tests` — 16 609 строк
**Артефакты:** `SilksongCoop_v2.1.0.zip` / `_plugin-only.zip` (собраны из этого коммита)

> **Статус после v2.2.1 (2026-10-28):** H-1, H-2, M-0…M-7 и L-1…L-16
> исправлены; протокол поднят до v5, room code — 80 бит. L-6 закрыт для
> высоконагруженного пути (`PersistentState`: индекс `key → item`), при этом
> `Breakable`/`Gate` по-прежнему ищутся сканированием на редкое событие — это
> сознательно оставлено; в v2.2.1 добавлен fallback по авторитетной позиции при
> несовпадении хэша.
> Тестовые двойники перенесены из Core DLL в тестовый проект.

---

## 1. Как проверялось

| Проверка | Результат |
|---|---|
| `dotnet build` Core, Release, `TreatWarningsAsErrors=true` | 0 ошибок / 0 предупреждений |
| `dotnet build` Mod, Release, `TreatWarningsAsErrors=true`, реальные `Managed/` | 0 ошибок / 0 предупреждений |
| Roslyn analyzers (`EnableNETAnalyzers`, `AnalysisMode=Recommended`) | ни одного предупреждения в Core и Mod |
| `dotnet test` | **682 / 682** (0 failed, 0 skipped) |
| Harmony-хуки | 28 / 28 целей резолвятся в реальных сборках игры |
| Ручной разбор | весь `src` (протокол, транспорты, состояние, смерть/возрождение, враги, UI, хуки), тесты, CI, упаковка |

Аудит **статический + сборка/тесты**. Запуск игры и Steam-сессии не выполнялись
(требуется GUI/Steam), поэтому пункты, помеченные «требует smoke-теста», проверены
только по коду.

---

## 2. Итог

Критических дефектов, ломающих обычную сессию «хост + гость по room code», не найдено.
Сборка, тесты, упаковка и лицензии в порядке. Но есть **два дефекта высокой важности**
в догоняющей синхронизации мира (обе фичи заявлены в v2.1.0) и несколько
security/hardening-замечаний, включая молчаливую потерю игровых пакетов в Steam-пути.

| # | Важность | Проблема | Где |
|---|---|---|---|
| H-1 | High | Catch-up мира обрезается на 512 reliable-пакетах без повторов | `LiteNetLibTransport.cs:533`, `EnemySyncController.cs:406` |
| H-2 | High | `PersistentState` для незагруженных сцен теряется безвозвратно | `EnemySyncController.cs:524-589` |
| M-0 | Medium | Steam: игровые пакеты на канале 1 молча выбрасываются | `SteamP2PTransport.cs:480`, `SilksongCoopPlugin.cs:923,1769` |
| M-1 | Medium | `heartPieces:+N` / `silkParts:+N` без ограничения → цикл на 25k+ итераций | `SilksongCoopPlugin.cs:1205,1221` |
| M-2 | Medium | Data/Ack/Disconnect не аутентифицированы после handshake | `LiteNetLibTransport.cs:983+` |
| M-3 | Medium | Секрет room code — 40 бит | `RoomCodeCodec.cs:28` |
| M-4 | Medium | Нет rate limit до аутентификации (handshake/challenge) | `LiteNetLibTransport.cs:805` |
| M-5 | Medium | Таймаут хоста переводит UI в Disconnected, хотя сокет слушает | `LiteNetLibTransport.cs:431`, `CoopSessionState.cs:285` |
| M-6 | Medium | Переключение транспорта не закрывает предыдущий | `SilksongCoopPlugin.cs:475`, `CoopMenuUI.cs:1080,1230` |
| M-7 | Medium | Неограниченный разбор входящих датаграмм за кадр | `LiteNetLibTransport.cs:614-655` |
| L-1 | Low | F9 «Spectator camera» документирован, но нигде не читается | `KeybindManager.cs:181`, `README.md:149` |
| L-2 | Low | `Enum.IsDefined` на каждом пакете (reflection/boxing) | `PacketHeader.cs:61`, `LiteNetLibTransport.cs:785` |
| L-3 | Low | Reflection на каждый опрос клавиш каждый кадр | `KeybindManager.cs:228-260` |
| L-4 | Low | `GetComponent`/хэш клипа каждый тик трансформа | `SilksongCoopPlugin.cs:1651,1677` |
| L-5 | Low | `GetComponent<Renderer>` каждый кадр у клона | `RemotePlayerController.cs:526-530` |
| L-6 | Low | `FindObjectsByType` на каждый пакет мира | `EnemySyncController.cs:561,578,612` |
| L-7 | Low | Призраки рывка не парентятся и не удаляются при despawn | `DashTrailController.cs:180,247` |
| L-8 | Low | `Renderer.materials`-инстансы не уничтожаются | `PaletteSwapController.cs` (нет `OnDestroy`) |
| L-9 | Low | Выход из spectator включает все коллайдеры/рендереры | `SilksongCoopPlugin.cs:560,838` |
| L-10 | Low | Clock-offset клона не пересчитывается при wrap | `RemotePlayerController.cs:229-245` |
| L-11 | Low | Инвайт в свой же лобби сбрасывает `IsHost` | `SteamP2PTransport.cs:692` |
| L-12 | Low | `GUI.skin` не восстанавливается; статические текстуры не уничтожаются | `CoopMenuUI.cs:167`, `SilksongSkin.cs:92` |
| L-13 | Low | В shipped-конфиге нет `[Quality] Preset` | `assets/com.silksong.coop.cfg` |
| L-14 | Low | Нет тестов Steam-транспорта и catch-up; тест читает исходник по пути | `tests/.../Challenger2...:569` |
| L-15 | Low | `MockNetworkTransport` поставляется в Core DLL | `Core/Networking/MockNetworkTransport.cs` |
| L-16 | Low | Ошибки Core уходят в `Debug.WriteLine` (в релизе невидимы) | `Core/**` |

---

## 3. Подробно

### H-1. Догоняющая синхронизация мира обрезается на 512 пакетах

**Код:**
- `EnemySyncController.ReplayWorldEvents` (`:406-432`) в одном кадре отправляет **все**
  `_worldEvents` (до 1024) и `_persistentStates` (до 2048, `:507`) по одному пакету.
- `LiteNetLibTransport.Send` (`:533-537`) при `_unacknowledged.Count >= 512` **молча
  выбрасывает новый reliable-пакет** (это правильное решение против «дыры» в
  последовательности, но для replay оно означает потерю).
- ACK-и приходят только в следующем `PollEvents`, т.е. за один цикл replay больше 512
  пакетов физически не отправить. Повтора/очереди нет.

**Последствия:** при позднем подключении к «долгому» хосту (много открытых дверей,
рычагов, сундуков, сломанных стен) часть `PersistentState`/`WorldEvent` не доедет и
**никогда не будет повторена**. Игрок увидит закрытую дверь, которую хост уже открыл,
или целую стену, которую хост сломал. Это ломает главную новую фичу v2.1.0.

Дополнительно: `ReplayWorldEvents` вызывается из `Update` в одном кадре, поэтому
2048 пакетов — ещё и hitch на кадр.

**Исправление:**
- Сделать очередь replay и отправлять, например, по 32 пакета за тик, пока
  `transport` не освободит backlog (или пока не придёт ACK на последний пакет).
- Плюс ограничить `_persistentStates` окном (например, 512 последних) или слать
  состояние сцены только для текущей сцены + список изменённых id.
- Добавить тест: 1000 persistent-состояний → все доходят (durable queue), нет потерь.

---

### H-2. `PersistentState` для сцен, которые сейчас не загружены, теряется

**Код:**
- Хост копит `_persistentStates` (до 2048, `EnemySyncController.cs:507`) и при
  подключении клиента отправляет их все (`ReplayWorldEvents:422-431`).
- Клиент применяет пакет через `ApplyRemotePersistentState` (`:524-559`),
  который ищет объект через `FindPersistentBool/Int` (`:561-589`), а те делают
  `UnityEngine.Object.FindObjectsByType<...>(FindObjectsSortMode.None)` — **только
  загруженные сцены**.
- Если объект не найден (дверь/рычаг/сундук в другой комнате), метод просто
  `return`; пакет нигде не сохраняется.
- При смене сцены клиент не запрашивает состояние у хоста: `SceneTransitionHooks`
  посылает только «я ушёл», а `DetectSceneChange` (`:686-700`) очищает локальные
  регистры и `_worldReplayPending` ставит в `false`.

**Последствия:** если игроки находятся в разных комнатах в момент подключения (или
клиент позже заходит в комнату, где хост уже открыл дверь/дёрнул рычаг/забрал сундук),
клиент увидит **своё** состояние объекта, а не состояние хоста. Повторной отправки
нет. Это особенно заметно в поздней игре, когда у хоста изменены десятки объектов в
разных сценах.

**Исправление:**
- Хранить состояние с привязкой к сцене (например,
  `Dictionary<int sceneHash, Dictionary<ulong itemKey, PersistentStatePacket>>`).
- На смене сцены клиент шлёт запрос `SceneStateRequest(sceneHash)` (новый пакет или
  расширение `SceneTransitionPacket`), хост отвечает только состояниями этой сцены.
- Как минимум, при смене сцены у **любой** стороны повторять `_persistentStates`,
  отфильтрованные по `sceneHash` (для этого в пакет нужно добавить `SceneHash` —
  wire-format v2.2).
- Тест: два клиента в разных сценах, хост меняет состояние в сцене B, клиент
  переходит в B → состояние применено.

---

### M-0. Steam: канал 1 зарезервирован под RTT, но по нему же идут игровые пакеты

**Код:**
- `SteamP2PTransport.PollEvents` (`:465-484`) опрашивает каналы 0–4; для `channel == 1`
  вызывает `HandleRttControl(...)` и делает `continue` — пакет **никогда** не попадает в
  `OnDataReceived`.
- При этом `BroadcastActionTrigger` (`SilksongCoopPlugin.cs:917-925`) и
  `BroadcastSceneTransitionDeparture` (`:1763-1771`) шлют именно на **канал 1**
  (`ReliableOrdered`).
- README (`README.md:37`, `README.ru.md:37`) утверждает, что канал «неиспользуемый».

**Последствия (Steam-хост/гость):**
- `ActionTrigger` целиком не доходит. Сейчас это влияет на trail **harpoon dash**
  (он триггерится только из `TriggerAction`, `RemotePlayerController.cs:296-304`);
  обычный/воздушный рывок остаётся видимым, потому что trail дополнительно
  включается по `FlagDashing` из transform-пакета (`:269-274`);
- сигнал «напарник ушёл из сцены» не доходит, клон висит до следующего transform-тика
  (до ~33 мс при 30 Гц) — визуальный артефакт, самовосстанавливается;
- любое будущее действие, добавленное только в `ActionTrigger`, на Steam работать не
  будет — и это не будет видно ни в логах, ни в тестах;
- RTT работает, поэтому баг не заметен в диагностике.

**Почему не поймали тесты:** Steam-транспорт не тестируется (нужен Steamworks);
UDP-путь канал 1 обрабатывает нормально.

**Исправление (низкий риск, без смены wire-format):**
1. Ввести общий класс констант каналов, например
   `Transform=0, Action=1, Vitals=2, Currency=3, Death=4, World=5, Rtt=6`.
2. Перевести `SendRttPing`/`HandleRttControl` на канал 6, `MaxChannel = 6`.
3. Либо оставить канал 1, но тогда в `PollEvents` сначала проверять
   `_readBuffer[0] is RttPing or RttPong`, а иначе отдавать пакет в `OnDataReceived`.

После правки — smoke-тест Steam: harpoon dash напарника даёт trail, смена комнаты
скрывает клон сразу.

---

### M-1. `heartPieces:+N` / `silkParts:+N`: неограниченный цикл (DoS)

`ApplyRemoteItemUnlock` (`SilksongCoopPlugin.cs:1198-1229`):

```csharp
bool isDelta = itemId[...] == '+' || itemId[...] == '-';
int next = isDelta ? playerData.heartPieces + value : Math.Max(...);
next = Math.Max(0, next);
while (next >= 4) { HeroController.instance?.AddToMaxHealth(1); next -= 4; }
```

`PacketValidator.ValidateItemUnlock` проверяет только длину/категорию/`TransactionId`,
но не значение. Модифицированный клиент может прислать `heartPieces:+100000` — это
25 000 вызовов `AddToMaxHealth` в одном кадре (аналогично `silkParts:+100000` →
33 000 вызовов `AddToMaxSilk`). Это зависание/фриз игры, а также неограниченный рост
максимального здоровья.

**Исправление:** принимать дельту только в разумном диапазоне
(`Math.Abs(value) <= 8`, для silk — `<= 6`), а результат клампить
(`heartPieces < 4`, `silkParts < 3`). Абсолютные значения — только `0..3` / `0..2`.
Добавить тест на `+100000` и `-100000`.

---

### M-2. После handshake трафик не аутентифицирован

`LiteNetLibTransport.ProcessIncomingDatagram` проверяет HMAC только в
`ChallengeResponse` (`:920-941`). Все остальные кадры (`Data`, `Ack`, `Disconnect`,
`Ping`) принимаются от любого пакета, у которого совпал source IP:port
(`:792-799`). На LAN (или при знании эфемерного порта) можно:
- поддельным `Disconnect` (8 байт) разорвать сессию;
- поддельным `Ack` «подтвердить» reliable-пакет, который не дошёл (тихая потеря
  bench-checkpoint/предмета);
- внедрить `Data`-пакет (например, урон врагу или смену кошелька).

Это не «дыра с интернета» (нужен spoof source IP:port), но для co-op по LAN —
реальный вектор.

**Исправление:** в v2.2 добавить в конверт 8-байтовый MAC (например,
`HMAC-SHA256(key, seq||payload)[0..7]`) либо случайный `SessionId` + MAC только для
управляющих кадров. Это осознанная смена wire-format → поднять `ProtocolVersion`
до 4 и указать в CHANGELOG, что обе стороны должны обновиться.

---

### M-3. 40-битный секрет room code

`RoomCodeCodec.SecretBytes = 5` → 40 бит. Секрет — единственное, что защищает
HMAC-handshake. Если атакующий перехватил один challenge/response, он может
восстановить секрет офлайн (2^40 на GPU — минуты/часы) и затем выдать себя за хоста
или гостя.

**Исправление:** `SecretBytes = 10` (80 бит) или 16 (128 бит) в следующей версии.
Формат кода станет длиннее (`SK-…-…-…` + 20/26 символов); старые 20-символьные
коды можно продолжать принимать для совместимости, но генерировать только новые.
Потребуется обновить `TryDecode`/`TryCanonicalize`, тесты и README.

---

### M-4. Нет rate limit до аутентификации

`if (_remotePeerId.HasValue && !_rateLimiter.TryConsume(...))` (`:805`) — пока пир не
подключён, `_remotePeerId == null`, значит handshake/challenge-response не
ограничиваются. Следствия:
- перебор 40-битного секрета по HMAC без ограничения скорости;
- CPU-нагрузка на `HMACSHA256` при флуде;
- хост отвечает `Challenge` (24 байта) на 19-байтовый handshake с произвольного
  spoofed-адреса — небольшой reflector/amplifier;
- `Pong` (16 байт) на `Ping` (8 байт) от неизвестного адреса — 2× amplification.

**Исправление:** rate-limit по `EndPoint` (IP:port), а не по `NetworkPeerId`;
например, 20 handshake/сек на адрес и глобальный лимит на число «новых» адресов
в секунду. Не генерировать новый nonce чаще, чем раз в N мс.

---

### M-5. Таймаут хоста: сокет слушает, а UI считает сессию завершённой

`DropPeerForTimeout` (`:431-451`) намеренно оставляет сокет открытым, чтобы гость мог
переподключиться. Но событие `PeerDisconnected` → `SetDisconnected` обнуляет `Role` и
`CurrentRoomCode` (`CoopSessionState.cs:285-300`), а `CoopMenuUI.DrawWatermark`
показывает код только при `Status == Hosting`. В результате хост видит «Disconnected»
и не видит код; если он нажмёт «Host» снова, `HostUdpSession` сгенерирует **новый**
код и перебиндит порт. Гость, который в этот момент автоматически переподключается
по старому коду, получит отказ HMAC.

**Исправление:** ввести статус `Hosting` (или `WaitingForPeer`) для хоста после
таймаута и сохранять `CurrentRoomCode`; либо, наоборот, явно закрывать сокет и
показывать «сессия завершена». Главное — согласовать поведение с auto-reconnect.

---

### M-6. Переключение транспорта не закрывает предыдущий

`SetActiveTransport` (`:475-483`) только присваивает поле. `HostSteamSession`
(`CoopMenuUI.cs:1230`) не вызывает `UdpTransport.Disconnect()`; `HostUdpSession`
(`:1080`) не закрывает Steam-лобби. Итог: после «Steam host → UDP host» лобби
остаётся открытым (друзья могут войти, их пакеты игнорируются), а после
«UDP host → Steam host» порт 7777 остаётся занятым и не опрашивается
(`Update` вызывает `PollEvents` только у Steam и активного транспорта).
Это не краш, но утечка ресурса и путаница в состоянии.

**Исправление:** в `SetActiveTransport` (или в обработчиках меню) явно
`oldTransport.Disconnect()` перед переключением; для UDP-хоста — `StopHost()`
с сохранением возможности re-host.

---

### M-7. Неограниченный разбор датаграмм за кадр

`PollEventsLocked` крутит `while (true) { if (socket.Available <= 0) break; Receive... }`
(`:614-655`). При большом backlog (флуд, несколько тысяч пакетов) весь разбор
происходит в одном кадре на main thread — фриз игры.

**Исправление:** ограничить, например, 256 пакетов за `PollEvents`; остальное
оставить в сокете до следующего кадра.

---

### Низкие замечания (кратко)

- **L-1.** `ToggleSpectatorCamera = F9` (`KeybindManager.cs:21,181`) нигде не
  используется; README (`:149`, `:221`) обещает «Spectator camera». Либо реализовать
  (переключение `cameraCtrl` между героем и напарником), либо убрать из README/конфига.
- **L-2.** `PacketHeader.IsValidPacketType` (`:61`) и `Enum.IsDefined` в
  `ProcessIncomingDatagram` (`:785-786`) вызываются на каждый пакет — reflection +
  boxing. Заменить на `switch`/диапазон (`packetId <= PacketType.ItemUnlockSync`).
- **L-3.** `KeybindManager.CheckNewInputSystemKey` делает
  `Type.GetProperty`/`GetValue` на каждый вызов `IsKeyDown` (3 клавиши × каждый кадр).
  Кэшировать `PropertyInfo`/делегаты по `KeyCode`.
- **L-4.** `BroadcastLocalTransform` каждый тик (20–60 Гц) вызывает
  `hero.GetComponent<Rigidbody2D>()` и `AnimationClipHasher.ComputeHash(clip.name)`.
  Кэшировать `Rigidbody2D` и хэш по имени клипа (`Dictionary<string,int>`).
- **L-5.** `RemotePlayerController.EnsureAnimationLibraryBound` каждый кадр делает
  `GetComponent<Renderer>()`/`GetComponentInChildren<Renderer>()`. Кэшировать.
- **L-6.** `FindPersistentBool/Int` и `FindObjectByKey<T>` делают
  `FindObjectsByType` на **каждый** входящий пакет мира (`:561-622`). При replay
  это O(пакеты × объекты сцены). Построить индекс `key → Component` при смене сцены.
- **L-7.** `DashGhostInstance` создаётся как root-GameObject без `SetParent`;
  при `DespawnRemotePlayer` активные (не в пуле) призраки не удаляются, а их
  `OnDestroy` чистит только пул (`:247-260`). Плюс `WaitForSeconds` не работает на
  паузе. Парентить к клону и/или хранить активные в списке.
- **L-8.** `PaletteSwapController` кэширует `ren.materials` (инстансы материалов),
  но не уничтожает их при despawn клона — утечка native-материалов на каждое
  подключение. Добавить `OnDestroy` с `Destroy(mat)` для созданных инстансов.
- **L-9.** `ExitSpectatorMode`/`OnBenchRespawnCompleted` включают **все**
  `Collider2D`/`Renderer` у героя, включая те, что игра отключила сама. Запоминать
  исходное состояние и восстанавливать только изменённое модом.
- **L-10.** `_clockOffsetMs` вычисляется один раз (`RemotePlayerController.cs:238-244`);
  при wrap `uint` (≈49.7 дней) или сбросе session-таймера клон «застревает».
  Пересчитывать offset, когда `diff < -10000`.
- **L-11.** `OnGameLobbyJoinRequested` безусловно ставит `IsHost=false`
  (`SteamP2PTransport.cs:692`); если хост получил инвайт в собственное лобби,
  роль ломается. Проверять `callback.m_steamIDLobby == _lobbyId`.
- **L-12.** `CoopMenuUI.OnGUI` ставит `GUI.skin = _skin` и не восстанавливает
  предыдущий — влияет на OnGUI других модов. `SilksongSkin` и `DiagnosticsOverlay`
  создают статические `Texture2D` и никогда их не `Destroy` (утечка при выгрузке
  плагина; для игровой сессии не критично).
- **L-13.** В `assets/com.silksong.coop.cfg` нет секции `[Quality] Preset`
  (создаётся BepInEx при первом запуске). Не баг, но «полный» дефолтный конфиг
  должен содержать все ключи, включая `Quality.Preset`.
- **L-14.** Тесты: 682 зелёных, но **нет ни одного теста Steam-транспорта** (именно
  поэтому M-0 не пойман), нет тестов на catch-up >512 (H-1) и на состояние из другой
  сцены (H-2), а
  `ControlRestoration_SourceCodeStaticAnalysis_...` читает `.cs` по относительному
  пути (`AppDomain.BaseDirectory/../../../../..`) — сломается при переносе дерева.
- **L-15.** Тестовые двойники `MockNetworkTransport` и `HeadlessPlayerCloneManager`
  лежат в `SilksongCoop.Core` и попадают в релизный DLL. Безвредно, но чище вынести
  в тестовый проект или `#if DEBUG`.
- **L-16.** В Core ошибки пишутся в `System.Diagnostics.Debug.WriteLine` и в релизе
  не видны. Для диагностики стоит добавить опциональный `Action<string>`-логгер,
  который плагин подключает к BepInEx.

---

## 4. Что проверено и работает корректно

- **Протокол:** magic `SK`, 12-байтовый заголовок, `Enum.IsDefined` для типа пакета,
  строгая проверка `ProtocolVersion` в handshake/ack, отказ 2.0.x.
- **Валидация:** NaN/Infinity и границы позиций/скоростей; `MaxHealth ≤ 64`,
  `MaxSilkSpools ≤ 16`, кошелёк ≤ 100 млн; `EnemySync`/`EnemySnapshot`/`WorldEvent`/
  `PersistentState`/`BenchCheckpoint` — все поля проверяются.
- **Handshake:** HMAC-SHA256, constant-time сравнение, nonce 10 с, слот не
  захватывается до успешного `ChallengeResponse`, повторный handshake переиспользует
  nonce, `RequireRoomCode` включён в UI.
- **Надёжность:** per-(channel, method) последовательности, reorder-буфер с
  отказом от ACK при переполнении, backpressure вместо вытеснения старого пакета,
  дедуп дублей, `Stopwatch`-таймеры.
- **Смерть/возрождение:** solo-death не убивает напарника (`SoloBenchRespawn`),
  epoch-синхронизация, сброс `_teamWipeInProgress` на reconnect, `ReadyForRespawn`
  без активной сессии, `ReviveHealthPercent`/`Invulnerability` реально применяются,
  порядок `dead=false` → `RegainControl`.
- **Прогресс:** baseline+delta для осколков масок/шёлка, max-merge, кошелёк с
  монотонной последовательностью и периодической сверкой, дедуп физических дропов.
- **Мир:** ключи объектов стабильны (FNV-1a по сцене+иерархии), HP врагов
  монотонно убывает, смерть от любых источников, breakable/gate/persistent
  синхронизируются **в пределах загруженной сцены** (см. H-2 про остальные сцены),
  `RemoteApplyScope` предотвращает эхо.
- **UI/настройки:** ключи, которые биндятся в `Awake` и `KeybindManager` (10 + 3),
  совпадают с `Config.Bind`; `Quality.Preset` создаётся при первом запуске (см. L-13),
  порт клампится 1024–65535, тик 20–60, quality preset применяется, история
  подключений отключается `SaveConnectionHistory=false`.
- **Сборка/релиз:** 0 warnings, 682 теста, 28 хуков, лицензии (MIT + LGPL-2.1/MIT
  третьих сторон) в обоих архивах, SHA-256 совпадают с опубликованными.

---

## 5. Рекомендуемый порядок работ

**v2.2.0 (hotfix, без смены протокола):**
1. H-1 — очередь replay с паузами/подтверждениями.
2. H-2 — scene-scoped `PersistentState` + запрос состояния при смене сцены.
3. M-0 — перенести RTT на отдельный канал (Steam).
4. M-1 — клампы счётчиков.
4. M-5/M-6 — согласовать состояние хоста при таймауте и закрывать старый транспорт.

**v2.2 (протокол 4, обе стороны обновляются):**
5. M-2 — MAC на кадры.
6. M-3 — 80/128-битный секрет.
7. M-4/M-7 — rate limit по endpoint, лимит пакетов на кадр.
8. L-1…L-16 — по мере возможности.

**Тесты, которые стоит добавить:**
- фейковый Steam-транспорт (интерфейс уже есть) с проверкой, что канал 1 не
  съедает `ActionTrigger`/`SceneTransition`;
- replay 1000+ persistent-состояний → все применяются;
- два игрока в разных сценах: состояние из сцены B применяется после перехода в B;
- `heartPieces:+100000` не вешает игру;
- host timeout → reconnect по тому же коду.

---

## 6. Ограничения аудита

- Не проверялось поведение в реальной игре: физика, анимации, смерть/возрождение,
  Steam P2P — только по коду и тестам.
- Оценка «High/Medium» — по влиянию на игрока, а не по CVSS.
- Строки указаны для коммита `b6c4824`.
