using System;
using System.Collections.Generic;
using System.Globalization;

namespace SilksongCoop.Core.Localization
{
    /// <summary>
    /// UI language selected by the user. <see cref="Auto"/> follows the operating
    /// system language at startup.
    /// </summary>
    public enum CoopLanguage
    {
        Auto,
        English,
        Russian
    }

    /// <summary>
    /// Small English-to-Russian lookup used by the in-game UI. The English source
    /// string is the key, so a missing translation falls back to readable English
    /// instead of a placeholder or an exception. Lives in Core so the translation
    /// rules can be unit-tested without Unity or BepInEx.
    /// </summary>
    public static class Loc
    {
        /// <summary>
        /// The language currently used by <see cref="T(string)"/>. Set once at startup
        /// and when the user changes it from the settings tab.
        /// </summary>
        public static CoopLanguage Current { get; private set; } = CoopLanguage.English;

        /// <summary>
        /// Resolves <see cref="CoopLanguage.Auto"/> against the OS language.
        /// </summary>
        public static CoopLanguage Resolve(CoopLanguage requested, bool systemIsRussian)
        {
            if (requested != CoopLanguage.Auto) return requested;
            return systemIsRussian ? CoopLanguage.Russian : CoopLanguage.English;
        }

        /// <summary>
        /// Applies the selected language to the UI.
        /// </summary>
        public static void SetLanguage(CoopLanguage requested, bool systemIsRussian)
        {
            Current = Resolve(requested, systemIsRussian);
        }

        /// <summary>
        /// Returns the translation for <paramref name="english"/> in the current language.
        /// </summary>
        public static string T(string english) => Translate(Current, english);

        /// <summary>
        /// Returns the formatted translation for <paramref name="english"/>. The
        /// translation keeps the same <c>{0}</c>-style placeholders as the source text.
        /// </summary>
        public static string T(string english, params object[] args) => Translate(Current, english, args);

        /// <summary>
        /// Pure translation helper. Unknown keys and unsupported languages return the
        /// English source, so partially translated builds remain usable.
        /// </summary>
        public static string Translate(CoopLanguage language, string? english, params object[]? args)
        {
            if (string.IsNullOrEmpty(english)) return string.Empty;

            string template = english!;
            if (language == CoopLanguage.Russian && Russian.TryGetValue(template, out string? translated))
            {
                template = translated;
            }

            if (args == null || args.Length == 0) return template;

            try
            {
                return string.Format(CultureInfo.InvariantCulture, template, args);
            }
            catch (FormatException)
            {
                // A malformed translation must never break the UI; show the English
                // source (unformatted) instead of throwing from OnGUI.
                return english!;
            }
        }

        private static readonly Dictionary<string, string> Russian = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // ── Notifications and HUD ─────────────────────────────────────────
            ["Network diagnostics: ON (F10 - co-op menu)"] = "Диагностика сети: ВКЛ (F10 — меню коопа)",
            ["Network diagnostics: OFF"] = "Диагностика сети: ВЫКЛ",
            ["Press F10 for the co-op menu. F11 shows network diagnostics."] =
                "F10 — меню коопа, F11 — диагностика сети.",
            ["Partner"] = "Напарник",
            ["Partner online"] = "Напарник в сети",
            ["Waiting for partner..."] = "Ожидание напарника...",
            ["Hosting: {0}"] = "Хостинг: {0}",
            ["Hosting (waiting for partner...)"] = "Хостинг (ожидание напарника...)",
            ["Connecting..."] = "Подключение...",
            ["SPECTATING {0}"] = "НАБЛЮДЕНИЕ: {0}",
            ["Reviving beside partner in <color=#D4A843><b>{0}s</b></color>"] = "Возрождение рядом с напарником через <color=#D4A843><b>{0} с</b></color>",
            ["Partner is in another room — reviving at the last bench in <color=#D4A843><b>{0}s</b></color>"] =
                "Напарник в другой комнате — возрождение у последней скамейки через <color=#D4A843><b>{0} с</b></color>",
            ["Partner revived at the last bench."] = "Напарник возродился у последней скамейки.",
            ["Partner is in another room — reviving at the last bench."] =
                "Напарник в другой комнате — возрождение у последней скамейки.",
            ["TEAM WIPE"] = "КОМАНДНЫЙ ВАЙП",
            ["Both heroes have fallen — Reloading at last bench..."] =
                "Оба героя пали — загрузка у последней скамейки...",

            // ── Main window and tabs ──────────────────────────────────────────
            ["HOST"] = "ХОСТ",
            ["JOIN"] = "ВОЙТИ",
            ["STATUS"] = "СТАТУС",
            ["SETTINGS"] = "НАСТРОЙКИ",
            ["GETTING STARTED"] = "С ЧЕГО НАЧАТЬ",
            ["1. One player hosts a game: UDP for LAN/VPN, Steam for friends."] =
                "1. Один игрок создаёт игру: UDP для LAN/VPN, Steam для друзей.",
            ["2. The other player joins with the room code, IP:port or Steam lobby."] =
                "2. Второй игрок подключается по коду комнаты, IP:port или лобби Steam.",
            ["3. F11 shows live network diagnostics; Settings can save a diagnostics file."] =
                "3. F11 показывает сетевую диагностику; в настройках можно сохранить файл диагностики.",
            ["Got it"] = "Понятно",

            // ── Host tab ──────────────────────────────────────────────────────
            ["YOUR SESSION"] = "ВАША СЕССИЯ",
            ["SESSION"] = "СЕССИЯ",
            ["SHARE THIS CODE"] = "ПЕРЕДАЙТЕ ЭТОТ КОД",
            ["COPY CODE"] = "КОПИРОВАТЬ КОД",
            ["<b>Address:</b>"] = "<b>Адрес:</b>",
            ["Share the room code (with secret): it authenticates the session. Direct IP:port has no shared-secret barrier and should only be used with trusted peers on a private network."] =
                "Передайте код комнаты (вместе с секретом): он аутентифицирует сессию. У прямого IP:port нет общего секрета, поэтому используйте его только с доверенными игроками в частной сети.",
            ["<b>Steam Lobby:</b>"] = "<b>Лобби Steam:</b>",
            ["Invite Steam Friends..."] = "Пригласить друзей в Steam...",
            ["Steam invite dialog opened"] = "Окно приглашения Steam открыто",
            ["START A SESSION"] = "НАЧАТЬ СЕССИЮ",
            ["Host a game for your partner to join"] = "Создайте игру, к которой подключится напарник",
            ["HOST  UDP  (Direct IP)"] = "СОЗДАТЬ  UDP  (прямой IP)",
            ["Best with Radmin VPN or LAN. Share your IP:port with your partner."] =
                "Лучше всего работает с Radmin VPN или в локальной сети. Передайте напарнику IP:port.",
            ["HOST  Steam  P2P  Lobby"] = "СОЗДАТЬ  Steam  P2P  лобби",
            ["Invite friends directly through Steam. Requires Steam running."] =
                "Приглашайте друзей прямо через Steam. Требуется запущенный Steam.",

            // ── Join tab ──────────────────────────────────────────────────────
            ["JOIN A SESSION"] = "ПОДКЛЮЧЕНИЕ",
            ["Enter the host's IP:port or Room Code"] = "Введите IP:port хоста или код комнаты",
            ["Address:"] = "Адрес:",
            ["JOIN  via  IP / Code"] = "ВОЙТИ  по  IP / коду",
            ["JOIN  via  Steam  ID"] = "ВОЙТИ  по  Steam ID",
            ["Paste from Clipboard"] = "Вставить из буфера",
            ["Pasted from clipboard"] = "Вставлено из буфера обмена",
            ["RECENT"] = "НЕДАВНИЕ",
            ["OR ENTER AN ADDRESS"] = "ИЛИ ВВЕДИТЕ АДРЕС",
            ["Clear History"] = "Очистить историю",
            ["History cleared"] = "История очищена",

            // ── Status tab ────────────────────────────────────────────────────
            ["NOT CONNECTED"] = "НЕТ ПОДКЛЮЧЕНИЯ",
            ["Start or join a session to see connection details."] =
                "Создайте сессию или подключитесь, чтобы увидеть детали соединения.",
            ["SESSION STATUS"] = "СТАТУС СЕССИИ",
            ["Player 1 (Host)"] = "Игрок 1 (хост)",
            ["Player 2 (Client)"] = "Игрок 2 (клиент)",
            ["<b>Role:</b>  {0}"] = "<b>Роль:</b>  {0}",
            ["Unknown"] = "Неизвестно",
            ["<b>Partner:</b>  {0}"] = "<b>Напарник:</b>  {0}",
            ["<b>Partner scene:</b>  same room"] = "<b>Сцена напарника:</b>  та же комната",
            ["<b>Partner scene:</b>  other room / loading"] = "<b>Сцена напарника:</b>  другая комната / загрузка",
            ["<b>Partner state:</b>  alive"] = "<b>Состояние напарника:</b>  жив",
            ["<b>Partner state:</b>  dead / spectating"] = "<b>Состояние напарника:</b>  мёртв / наблюдает",
            ["<b>Connected peers:</b>  {0}"] = "<b>Подключённые игроки:</b>  {0}",
            ["Connecting"] = "Подключение",
            ["Authenticated"] = "Аутентифицирован",
            ["Ready"] = "Готов",
            ["<b>Interpolation:</b>  {0:F0} ms behind"] = "<b>Интерполяция:</b>  отставание {0:F0} мс",
            ["<b>Session:</b>  {0}"] = "<b>Сессия:</b>  {0}",
            ["NETWORK"] = "СЕТЬ",
            ["<b>Ping:</b>  <color={0}>{1}</color>"] = "<b>Пинг:</b>  <color={0}>{1}</color>",
            ["n/a"] = "н/д",
            ["<b>Tick rate:</b>  {0:F0} Hz (adaptive)"] = "<b>Частота тиков:</b>  {0:F0} Гц (адаптивная)",
            ["<b>Packets:</b>  ↑ {0}  ↓ {1}"] = "<b>Пакеты:</b>  ↑ {0}  ↓ {1}",
            ["<b>Rejected:</b>  <color=#DD6666>{0}</color> (malformed or wrong peer)"] =
                "<b>Отклонено:</b>  <color=#DD6666>{0}</color> (повреждённые или от чужого пира)",
            ["<b>Bandwidth:</b>  ↑ {0}/s  ↓ {1}/s"] = "<b>Трафик:</b>  ↑ {0}/с  ↓ {1}/с",
            ["<b>Packet Loss:</b>  <color={0}>{1:F1}%</color>"] = "<b>Потери пакетов:</b>  <color={0}>{1:F1}%</color>",
            ["<b>Room Code:</b>  {0}"] = "<b>Код комнаты:</b>  {0}",
            ["<b>Your Address:</b>  {0}"] = "<b>Ваш адрес:</b>  {0}",
            ["<b>Steam Lobby:</b>  {0}"] = "<b>Лобби Steam:</b>  {0}",

            // ── Settings tab ──────────────────────────────────────────────────
            ["Plugin not initialized."] = "Плагин не инициализирован.",
            ["<b>Language</b>"] = "<b>Язык</b>",
            ["Language changes apply immediately."] = "Смена языка применяется сразу.",
            ["Active language: {0}"] = "Активный язык: {0}",
            ["Show the first-run guide again"] = "Показать вводную подсказку снова",
            ["First-run guide enabled."] = "Вводная подсказка включена.",
            ["<b>Network Tick Rate</b>"] = "<b>Частота сетевых тиков</b>",
            ["{0} Hz"] = "{0} Гц",
            ["Higher = smoother but more bandwidth (20-60 Hz)"] =
                "Выше — плавнее, но больше трафика (20–60 Гц)",
            ["<b>Default UDP Port</b>"] = "<b>Порт UDP по умолчанию</b>",
            ["<b>Player 2 Color</b>"] = "<b>Цвет игрока 2</b>",
            ["Shift the hue of Player 2's cloak color"] = "Сдвиг оттенка плаща игрока 2",
            ["<b>Player 2 Dash Trail</b>"] = "<b>След рывка игрока 2</b>",
            ["Enable cyan dash trail for Player 2"] = "Включить голубой след рывка игрока 2",
            ["<b>Revive Cooldown</b>"] = "<b>Задержка возрождения</b>",
            ["{0:F0}s"] = "{0:F0} с",
            ["Time before solo-death revive (15–30 seconds)"] =
                "Время до возрождения после одиночной смерти (15–30 секунд)",
            ["<b>Diagnostics</b>"] = "<b>Диагностика</b>",
            ["Save Diagnostics File"] = "Сохранить файл диагностики",
            ["Diagnostics saved: {0}"] = "Диагностика сохранена: {0}",
            ["Could not save diagnostics: {0}"] = "Не удалось сохранить диагностику: {0}",
            ["Writes BepInEx/SilksongCoop-diagnostics/ with the log tail, session state and config."] =
                "Записывает в BepInEx/SilksongCoop-diagnostics/ хвост лога, состояние сессии и конфиг.",

            // ── Disconnect and helpers ────────────────────────────────────────
            ["Are you sure you want to disconnect?"] = "Вы уверены, что хотите отключиться?",
            ["Disconnect"] = "Отключиться",
            ["Cancel"] = "Отмена",
            ["DISCONNECT"] = "ОТКЛЮЧИТЬСЯ",
            ["Uninitialized"] = "Не инициализировано",
            ["Disconnected"] = "Отключено",
            ["Hosting (Waiting...)"] = "Хостинг (ожидание...)",
            ["Connected"] = "Подключено",
            ["Spectating"] = "Наблюдение",
            ["Team Wipe"] = "Командный вайп",
            ["Copy"] = "Копировать",
            ["✓ Copied"] = "✓ Скопировано",
            ["Copied to clipboard!"] = "Скопировано в буфер обмена!",

            // ── Session actions ───────────────────────────────────────────────
            ["Plugin not initialized"] = "Плагин не инициализирован",
            ["UDP transport not available"] = "UDP-транспорт недоступен",
            ["Steam transport not available"] = "Steam-транспорт недоступен",
            ["Hosting on {0} — room code {1} copied!"] = "Хостинг на {0} — код комнаты {1} скопирован!",
            ["Failed to host UDP: {0}"] = "Не удалось создать UDP-сессию: {0}",
            ["Steam transport not available. Ensure Steam is running."] =
                "Steam-транспорт недоступен. Убедитесь, что Steam запущен.",
            ["Steam is offline. Start Steam and try again."] =
                "Steam не в сети. Запустите Steam и повторите попытку.",
            ["Steam lobby created — use Invite Friends"] =
                "Лобби Steam создано — используйте «Пригласить друзей»",
            ["Steam host failed: {0}"] = "Не удалось создать Steam-лобби: {0}",
            ["Please enter an IP:port or Room Code"] = "Введите IP:port или код комнаты",
            ["Connecting to {0}..."] = "Подключение к {0}...",
            ["Join error: {0}"] = "Ошибка подключения: {0}",
            ["Please enter a Steam Lobby ID"] = "Введите ID лобби Steam",
            ["Joining Steam Lobby {0}..."] = "Подключение к лобби Steam {0}...",
            ["Steam join error: {0}"] = "Ошибка подключения к Steam: {0}",
            ["Disconnect error: {0}"] = "Ошибка отключения: {0}",

            // ── Connection status messages ────────────────────────────────────
            ["Connected to {0}"] = "Подключено к {0}",
            ["Partner timed out — still hosting. Share the same room code to reconnect."] =
                "Напарник не отвечает — хостинг продолжается. Передайте тот же код комнаты для переподключения.",
            ["Partner left — still hosting. Share the same room code to reconnect."] =
                "Напарник вышел — хостинг продолжается. Передайте тот же код комнаты для переподключения.",
            ["Version mismatch — update both players to the same version."] =
                "Несовпадение версий — обновите мод у обоих игроков.",
            ["Disconnected. Reconnecting in {0}s..."] =
                "Соединение потеряно. Переподключение через {0:0.#} с...",
            ["Connection lost. Max reconnect attempts reached."] =
                "Соединение потеряно. Достигнут лимит попыток переподключения.",
            ["Spectator camera: follow partner"] = "Камера наблюдателя: следовать за напарником",
            ["Spectator camera: hold position"] = "Камера наблюдателя: оставаться на месте",
            ["Reconnecting (attempt {0})..."] = "Переподключение (попытка {0})...",

            // ── Diagnostics overlay ───────────────────────────────────────────
            ["Network Diagnostics [F11]"] = "Диагностика сети [F11]",
            ["Ping"] = "Пинг",
            ["Loss"] = "Потери",
            ["Session: {0:F0}s  |  Pkt out: {1}/s  in: {2}/s{3}{4}"] =
                "Сессия: {0:F0} с  |  Пакеты исх.: {1}/с  вх.: {2}/с{3}{4}",
            ["  |  Rejected: {0}"] = "  |  Отклонено: {0}",
            ["  |  Tick: {0:F0} Hz"] = "  |  Тики: {0:F0} Гц",
            ["same room"] = "та же комната",
            ["other room"] = "другая комната",
            ["alive"] = "жив",
            ["dead"] = "мёртв",
            ["  |  interp {0:F0} ms"] = "  |  интерп. {0:F0} мс"
        };
    }
}
