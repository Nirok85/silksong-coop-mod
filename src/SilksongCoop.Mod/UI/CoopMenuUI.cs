using System;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.State;
using SilksongCoop.Mod.Networking;
using SilksongCoop.Mod.Settings;
using UnityEngine;

namespace SilksongCoop.Mod.UI
{
    /// <summary>
    /// Redesigned in-game co-op menu (F10 toggle) with a tab-based layout, Silksong-themed
    /// visuals, toast notifications, partner vitals HUD, connection history, and network diagnostics.
    /// </summary>
    public class CoopMenuUI : MonoBehaviour
    {
        public bool IsVisible { get; set; } = false;

        private enum Tab { Host, Join, Status, Settings }

        // ── Dependencies ───────────────────────────────────────────────────────
        private CoopSessionState? _sessionState;
        private SteamP2PTransport? _steamTransport;
        private LiteNetLibTransport? _udpTransport;
        private KeybindManager? _keybinds;
        private QualityPresetManager? _qualityPresets;
        private GUISkin? _skin;

        private GUISkin Skin => _skin ?? throw new InvalidOperationException("Skin not initialized");

        // ── Subsystems ─────────────────────────────────────────────────────────
        private readonly ToastNotifier _toasts = new ToastNotifier();
        private readonly ConnectionHistory _history = new ConnectionHistory();
        private readonly NetworkDiagnostics _diagnostics = new NetworkDiagnostics();
        private DiagnosticsOverlay? _diagnosticsOverlay;

        // ── UI state ───────────────────────────────────────────────────────────
        private Tab _activeTab = Tab.Host;
        private Rect _windowRect = new Rect(40, 50, 460, 540);
        private string _inputAddressOrCode = "";
        private string _lastConnectionTarget = "";
        private string _currentHostAddress = "";
        private int _windowId;

        // Copy feedback
        private float _copyFlashUntil = 0f;
        private string _copyFlashText = "";

        // Disconnect confirmation
        private bool _showDisconnectConfirm = false;

        // Cached style refs (looked up once per OnGUI frame)
        private GUIStyle? _styleTitle;
        private GUIStyle? _styleSubtitle;
        private GUIStyle? _styleTab;
        private GUIStyle? _styleTabActive;
        private GUIStyle? _styleGood;
        private GUIStyle? _styleWarn;
        private GUIStyle? _styleBad;

        public ToastNotifier Toasts => _toasts;
        public NetworkDiagnostics Diagnostics => _diagnostics;
        public ConnectionHistory History => _history;

        public CoopMenuUI()
        {
            _windowId = 9842;
        }

        private void Awake()
        {
            _windowId = 9842 + UnityEngine.Random.Range(0, 1000);
        }

        public void Initialize(CoopSessionState sessionState, SteamP2PTransport? steamTransport, LiteNetLibTransport? udpTransport, KeybindManager? keybinds = null, QualityPresetManager? qualityPresets = null)
        {
            _sessionState = sessionState;
            _steamTransport = steamTransport;
            _udpTransport = udpTransport;
            _keybinds = keybinds;
            _qualityPresets = qualityPresets;
            _diagnosticsOverlay = new DiagnosticsOverlay(_diagnostics);
        }

        public void SetStatusMessage(string message)
        {
            _toasts.ShowInfo(message);
        }

        private bool _diagnosticLogged = false;
        
        private void Update()
        {
            // ONE-TIME diagnostic log to verify Update() runs and which input path works
            if (!_diagnosticLogged && Time.frameCount > 10)
            {
                _diagnosticLogged = true;
                Debug.Log($"[Coop] Update() is running. _keybinds={_keybinds != null}");
                
                // Test which input work
                bool legacyWorks = false;
                try
                {
                    var test = Input.GetKeyDown(KeyCode.F10);
                    legacyWorks = true;
                    Debug.Log($"[Coop] Legacy Input.GetKeyDown() works: {legacyWorks}");
                }
                catch (System.Exception ex)
                {
                    Debug.Log($"[Coop] Legacy Input.GetKeyDown() throws: {ex.GetType().Name}: {ex.Message}");
                }
                
                bool newInputWorks = KeybindManager.IsKeyTriggered(KeyCode.F12); // Test with F12
                Debug.Log($"[Coop] New Input System fallback works: {newInputWorks}");
            }
            
            // Toggle menu using configurable keybind
            if (_keybinds != null && _keybinds.IsKeyDown(KeybindManager.ToggleMenu))
            {
                IsVisible = !IsVisible;
            }
            // Fallback to F10 if keybinds not initialized
            else if (_keybinds == null && Input.GetKeyDown(KeyCode.F10))
            {
                IsVisible = !IsVisible;
            }

            // Toggle diagnostics overlay with F11
            if (_keybinds != null && _keybinds.IsKeyDown(KeybindManager.ToggleDiagnostics))
            {
                if (_diagnosticsOverlay != null)
                {
                    _diagnosticsOverlay.IsVisible = !_diagnosticsOverlay.IsVisible;
                    _toasts.ShowInfo(_diagnosticsOverlay.IsVisible 
                        ? "Диагностика сети: ВКЛ (F10 - главное меню коопа)" 
                        : "Диагностика сети: ВЫКЛ");
                }
            }
            else if (_keybinds == null && Input.GetKeyDown(KeyCode.F11))
            {
                if (_diagnosticsOverlay != null)
                {
                    _diagnosticsOverlay.IsVisible = !_diagnosticsOverlay.IsVisible;
                    _toasts.ShowInfo(_diagnosticsOverlay.IsVisible 
                        ? "Диагностика сети: ВКЛ (F10 - главное меню коопа)" 
                        : "Диагностика сети: ВЫКЛ");
                }
            }

            // Keep Steam lobby id in sync with the transport (lobby created asynchronously)
            if (_steamTransport != null && _sessionState != null)
            {
                ulong lobbyVal = _steamTransport.CurrentLobbyId.m_SteamID;
                if (lobbyVal != 0 && _sessionState.SteamLobbyId != lobbyVal)
                {
                    _sessionState.SteamLobbyId = lobbyVal;
                }
            }

            // Update diagnostics
            _diagnostics.Update(Time.deltaTime);

            // Auto-switch to Status tab when connection state changes
            if (_sessionState != null && _sessionState.IsConnected && _activeTab == Tab.Host)
            {
                _activeTab = Tab.Status;
            }
        }

        private void OnGUI()
        {
            // Lazy-load skin + styles
            if (_skin == null)
            {
                _skin = SilksongSkin.GetSkin();
                GUI.skin = _skin;
                _styleTitle = SilksongSkin.GetCustomStyle(_skin, "SilksongTitle");
                _styleSubtitle = SilksongSkin.GetCustomStyle(_skin, "SilksongSubtitle");
                _styleTab = SilksongSkin.GetCustomStyle(_skin, "SilksongTab");
                _styleTabActive = SilksongSkin.GetCustomStyle(_skin, "SilksongTabActive");
                _styleGood = SilksongSkin.GetCustomStyle(_skin, "SilksongGood");
                _styleWarn = SilksongSkin.GetCustomStyle(_skin, "SilksongWarn");
                _styleBad = SilksongSkin.GetCustomStyle(_skin, "SilksongBad");
            }

            GUI.skin = _skin;

            // ── Watermark ──────────────────────────────────────────────────────
            DrawWatermark();

            // ── Partner vitals HUD ─────────────────────────────────────────────
            if (_sessionState != null && _sessionState.IsConnected)
            {
                DrawPartnerVitalsHud();
            }

            // ── Spectator overlay (death state) ────────────────────────────────
            if (_sessionState != null && _sessionState.IsConnected)
            {
                DrawSpectatorOverlay();
            }

            // ── Diagnostics overlay (F11 toggle) ────────────────────────────────
            if (_diagnosticsOverlay != null)
            {
                _diagnosticsOverlay.OnGUI();
            }

            // ── Toast notifications (always on top) ────────────────────────────
            _toasts.Draw();

            // ── Main window ────────────────────────────────────────────────────
            if (!IsVisible) return;

            _windowRect = GUI.Window(_windowId, _windowRect, DrawWindowContents, "");
        }

        // ─── Watermark ────────────────────────────────────────────────────────
        private void DrawWatermark()
        {
            string watermark;
            Color watermarkColor;

            if (_sessionState != null && _sessionState.IsConnected)
            {
                string peerName = _sessionState.RemotePeerId.HasValue
                    ? _sessionState.RemotePeerId.Value.DisplayName
                    : "Partner";
                int rtt = _sessionState.RoundTripTimeMs;
                string quality = rtt < 50 ? "●" : rtt < 150 ? "◐" : "○";
                watermark = $"[F10] Co-op  {quality} {peerName}  ({rtt}ms)";
                watermarkColor = rtt < 50 ? SilksongSkin.GreenOk : rtt < 150 ? SilksongSkin.YellowWarn : SilksongSkin.RedError;
            }
            else if (_sessionState != null && _sessionState.Status == SessionStatus.Hosting)
            {
                watermark = !string.IsNullOrEmpty(_currentHostAddress)
                    ? $"[F10] Hosting: {_currentHostAddress}"
                    : "[F10] Hosting (waiting for partner...)";
                watermarkColor = SilksongSkin.Gold;
            }
            else if (_sessionState != null && _sessionState.Status == SessionStatus.Connecting)
            {
                watermark = "[F10] Connecting...";
                watermarkColor = SilksongSkin.BlueInfo;
            }
            else
            {
                watermark = "[F10] Silksong Co-op";
                watermarkColor = SilksongSkin.TextCream;
            }

            GUI.color = watermarkColor;
            GUI.Label(new Rect(10, 10, 400, 25), watermark, _styleTitle ?? Skin.label);
            GUI.color = Color.white;
        }

        // ─── Partner vitals HUD (top-right) ───────────────────────────────────
        private void DrawPartnerVitalsHud()
        {
            var vitals = SilksongCoopPlugin.Instance?.VitalsSync;
            if (vitals == null) return;

            int remoteHp = vitals.RemoteHealth;
            int remoteMaxHp = vitals.RemoteMaxHealth;
            int remoteSilk = vitals.RemoteSilk;
            int remoteMaxSilk = vitals.RemoteMaxSilk;

            // Don't show if we have no data yet
            if (remoteMaxHp <= 0 && remoteMaxSilk <= 0) return;

            string peerName = _sessionState?.RemotePeerId?.DisplayName ?? "Partner";

            float hudW = 180f;
            float hudH = 68f;
            float x = Screen.width - hudW - 16f;
            float y = 40f;

            // Background
            GUI.color = new Color(SilksongSkin.BgPanel.r, SilksongSkin.BgPanel.g, SilksongSkin.BgPanel.b, 0.88f);
            GUI.DrawTexture(new Rect(x, y, hudW, hudH), SilksongSkin.WhitePixel);

            // Left accent bar (gold)
            GUI.color = SilksongSkin.Gold;
            GUI.DrawTexture(new Rect(x, y, 3f, hudH), SilksongSkin.WhitePixel);

            // Partner name
            GUI.color = SilksongSkin.TextTitle;
            var nameStyle = _styleTitle ?? Skin.label;
            nameStyle.fontSize = 11;
            GUI.Label(new Rect(x + 8f, y + 2f, hudW - 12f, 16f), peerName, nameStyle);

            // HP bar
            float barX = x + 8f;
            float barW = hudW - 16f;
            float barH = 8f;
            float barY = y + 22f;

            float hpFrac = remoteMaxHp > 0 ? (float)remoteHp / remoteMaxHp : 0f;
            DrawStatusBar(barX, barY, barW, barH, hpFrac, new Color(0.85f, 0.25f, 0.25f), $"HP {remoteHp}/{remoteMaxHp}");

            // Silk bar
            float silkY = y + 42f;
            float silkFrac = remoteMaxSilk > 0 ? (float)remoteSilk / (remoteMaxSilk * 9f) : 0f;
            DrawStatusBar(barX, silkY, barW, barH, silkFrac, new Color(0.30f, 0.55f, 0.90f), $"Silk {remoteSilk}/{remoteMaxSilk * 9}");

            GUI.color = Color.white;
        }

        private void DrawStatusBar(float x, float y, float w, float h, float fraction, Color fillColor, string label)
        {
            fraction = Mathf.Clamp01(fraction);

            // Background
            GUI.color = new Color(0.12f, 0.10f, 0.18f, 0.9f);
            GUI.DrawTexture(new Rect(x, y, w, h), SilksongSkin.WhitePixel);

            // Fill
            if (fraction > 0f)
            {
                GUI.color = fillColor;
                GUI.DrawTexture(new Rect(x, y, w * fraction, h), SilksongSkin.WhitePixel);
            }

            // Border
            GUI.color = new Color(0.3f, 0.28f, 0.4f, 0.5f);
            GUI.DrawTexture(new Rect(x, y, w, 1), SilksongSkin.WhitePixel);
            GUI.DrawTexture(new Rect(x, y + h - 1, w, 1), SilksongSkin.WhitePixel);
            GUI.DrawTexture(new Rect(x, y, 1, h), SilksongSkin.WhitePixel);
            GUI.DrawTexture(new Rect(x + w - 1, y, 1, h), SilksongSkin.WhitePixel);

            // Label
            GUI.color = SilksongSkin.TextCream;
            var style = Skin.label;
            style.fontSize = 9;
            style.alignment = TextAnchor.MiddleCenter;
            GUI.Label(new Rect(x, y, w, h), label, style);
            style.alignment = TextAnchor.MiddleLeft; // restore
        }

        // ─── Spectator overlay (death state) ──────────────────────────────────
        private void DrawSpectatorOverlay()
        {
            var coordinator = SilksongCoopPlugin.Instance?.DeathCoordinator;
            if (coordinator == null) return;

            if (coordinator.CurrentState == DeathState.Spectating)
            {
                float bannerWidth = 420f;
                float bannerHeight = 75f;
                float x = (Screen.width - bannerWidth) * 0.5f;
                float y = Screen.height * 0.15f;

                GUI.color = new Color(SilksongSkin.BgDeepest.r, SilksongSkin.BgDeepest.g, SilksongSkin.BgDeepest.b, 0.92f);
                GUI.DrawTexture(new Rect(x, y, bannerWidth, bannerHeight), SilksongSkin.WhitePixel);
                GUI.color = SilksongSkin.Gold;
                GUI.DrawTexture(new Rect(x, y, bannerWidth, 2), SilksongSkin.WhitePixel);
                GUI.DrawTexture(new Rect(x, y + bannerHeight - 2, bannerWidth, 2), SilksongSkin.WhitePixel);

                string partnerName = (_sessionState != null && _sessionState.RemotePeerId.HasValue)
                    ? _sessionState.RemotePeerId.Value.DisplayName
                    : "Partner";

                GUI.color = SilksongSkin.GoldBright;
                var titleStyle = _styleTitle ?? Skin.label;
                titleStyle.fontSize = 16;
                titleStyle.alignment = TextAnchor.MiddleCenter;
                GUI.Label(new Rect(x, y + 8, bannerWidth, 24), $"SPECTATING {partnerName.ToUpperInvariant()}", titleStyle);

                int secondsLeft = Mathf.CeilToInt(coordinator.RemainingSpectatorTime);
                GUI.color = SilksongSkin.TextCream;
                var subStyle = Skin.label;
                subStyle.fontSize = 13;
                subStyle.alignment = TextAnchor.MiddleCenter;
                GUI.Label(new Rect(x, y + 32, bannerWidth, 22), $"Reviving beside partner in <color=#D4A843><b>{secondsLeft}s</b></color>", subStyle);

                // Progress bar
                float total = Math.Max(15f, coordinator.ReviveCooldownDuration);
                float progress = Mathf.Clamp01(1f - (coordinator.RemainingSpectatorTime / total));
                float barWidth = bannerWidth - 40f;
                float barY = y + 58f;
                DrawStatusBar(x + 20f, barY, barWidth, 8f, progress, SilksongSkin.Gold, "");

                titleStyle.alignment = TextAnchor.MiddleLeft; // restore
                subStyle.alignment = TextAnchor.MiddleLeft;
            }
            else if (coordinator.CurrentState == DeathState.TeamWipe)
            {
                float bannerWidth = 420f;
                float bannerHeight = 50f;
                float x = (Screen.width - bannerWidth) * 0.5f;
                float y = Screen.height * 0.15f;

                GUI.color = new Color(0.15f, 0.03f, 0.03f, 0.92f);
                GUI.DrawTexture(new Rect(x, y, bannerWidth, bannerHeight), SilksongSkin.WhitePixel);
                GUI.color = SilksongSkin.RedError;
                GUI.DrawTexture(new Rect(x, y, bannerWidth, 2), SilksongSkin.WhitePixel);

                GUI.color = SilksongSkin.RedError;
                var titleStyle = _styleTitle ?? Skin.label;
                titleStyle.fontSize = 15;
                titleStyle.alignment = TextAnchor.MiddleCenter;
                GUI.Label(new Rect(x, y + 6, bannerWidth, 22), "TEAM WIPE", titleStyle);

                GUI.color = SilksongSkin.TextCream;
                var subStyle = Skin.label;
                subStyle.fontSize = 12;
                subStyle.alignment = TextAnchor.MiddleCenter;
                GUI.Label(new Rect(x, y + 28, bannerWidth, 18), "Both heroes have fallen — Reloading at last bench...", subStyle);

                titleStyle.alignment = TextAnchor.MiddleLeft;
                subStyle.alignment = TextAnchor.MiddleLeft;
            }

            GUI.color = Color.white;
        }

        // ─── Main window ──────────────────────────────────────────────────────
        private void DrawWindowContents(int windowId)
        {
            GUI.DragWindow(new Rect(0, 0, 460, 24));

            // Title bar
            GUI.color = SilksongSkin.Gold;
            var titleStyle = _styleTitle ?? Skin.label;
            int origSize = titleStyle.fontSize;
            titleStyle.fontSize = 14;
            GUI.Label(new Rect(12, 4, 300, 20), "SILKSONG CO-OP", titleStyle);
            titleStyle.fontSize = origSize;

            // Close button
            GUI.color = Color.white;
            if (GUI.Button(new Rect(460 - 28, 3, 24, 18), "×"))
            {
                IsVisible = false;
            }

            GUILayout.Space(26);

            // ── Tab bar ────────────────────────────────────────────────────────
            DrawTabBar();

            SilksongSkin.DrawDivider(1f, 4f, 6f);

            // ── Tab content ────────────────────────────────────────────────────
            switch (_activeTab)
            {
                case Tab.Host:    DrawHostTab(); break;
                case Tab.Join:    DrawJoinTab(); break;
                case Tab.Status:  DrawStatusTab(); break;
                case Tab.Settings: DrawSettingsTab(); break;
            }
        }

        private void DrawTabBar()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(8);
            DrawTabButton("HOST", Tab.Host);
            DrawTabButton("JOIN", Tab.Join);
            DrawTabButton("STATUS", Tab.Status);
            DrawTabButton("SETTINGS", Tab.Settings);
            GUILayout.Space(8);
            GUILayout.EndHorizontal();
        }

        private void DrawTabButton(string label, Tab tab)
        {
            var style = (_activeTab == tab ? _styleTabActive : _styleTab) ?? Skin.button;
            if (GUILayout.Button(label, style))
            {
                _activeTab = tab;
            }
        }

        // ─── HOST TAB ─────────────────────────────────────────────────────────
        private void DrawHostTab()
        {
            bool isHosting = _sessionState != null && _sessionState.Status == SessionStatus.Hosting;
            bool isConnected = _sessionState != null && _sessionState.IsConnected;

            if (isConnected || isHosting)
            {
                // Already hosting — show address with copy button
                GUILayout.Label("YOUR SESSION", _styleTitle ?? Skin.label);
                GUILayout.Space(4);

                if (!string.IsNullOrEmpty(_currentHostAddress))
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("<b>Address:</b>", GUILayout.Width(70));
                    GUILayout.Label($"<color=#D4A843><b>{_currentHostAddress}</b></color>");
                    DrawCopyButton(_currentHostAddress, "Copy");
                    GUILayout.EndHorizontal();
                    GUILayout.Label("<color=#999999><i>Note: Direct UDP works via LAN or VPN (Radmin/Hamachi). For Internet, use Steam Host.</i></color>");
                }

                if (_sessionState!.SteamLobbyId.HasValue)
                {
                    GUILayout.Space(4);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("<b>Steam Lobby:</b>", GUILayout.Width(70));
                    GUILayout.Label($"{_sessionState.SteamLobbyId.Value}");
                    DrawCopyButton(_sessionState.SteamLobbyId.Value.ToString(), "Copy");
                    GUILayout.EndHorizontal();
                }

                if (_steamTransport != null && _sessionState.SteamLobbyId.HasValue)
                {
                    GUILayout.Space(8);
                    if (GUILayout.Button("Invite Steam Friends...", GUILayout.Height(28)))
                    {
                        _steamTransport.OpenInviteDialog();
                        _toasts.ShowInfo("Steam invite dialog opened");
                    }
                }

                GUILayout.Space(8);
                DrawDisconnectButton();
            }
            else
            {
                GUILayout.Label("START A SESSION", _styleTitle ?? Skin.label);
                GUILayout.Space(4);
                GUILayout.Label("Host a game for your partner to join", _styleSubtitle ?? Skin.label);
                GUILayout.Space(12);

                // UDP button (large, prominent)
                GUI.color = SilksongSkin.Gold;
                if (GUILayout.Button("HOST  UDP  (Direct IP)", GUILayout.Height(38)))
                {
                    GUI.color = Color.white;
                    HostUdpSession();
                }
                GUI.color = Color.white;

                GUILayout.Label("Best with Radmin VPN or LAN. Share your IP:port with your partner.", _styleSubtitle ?? Skin.label);
                GUILayout.Space(10);

                // Steam button
                if (GUILayout.Button("HOST  Steam  P2P  Lobby", GUILayout.Height(32)))
                {
                    HostSteamSession();
                }

                GUILayout.Label("Invite friends directly through Steam. Requires Steam running.", _styleSubtitle ?? Skin.label);
            }
        }

        // ─── JOIN TAB ─────────────────────────────────────────────────────────
        private void DrawJoinTab()
        {
            bool isConnected = _sessionState != null && _sessionState.IsConnected;
            bool isConnecting = _sessionState != null && _sessionState.Status == SessionStatus.Connecting;

            if (isConnected)
            {
                DrawStatusTab(); // Redirect to status if already connected
                return;
            }

            GUILayout.Label("JOIN A SESSION", _styleTitle ?? Skin.label);
            GUILayout.Space(4);
            GUILayout.Label("Enter the host's IP:port or Room Code", _styleSubtitle ?? Skin.label);
            GUILayout.Space(10);

            // Input field with placeholder
            GUILayout.BeginHorizontal();
            GUILayout.Label("Address:", GUILayout.Width(60));

            if (string.IsNullOrEmpty(_inputAddressOrCode))
            {
                // Draw placeholder overlay
                var fieldRect = GUILayoutUtility.GetRect(0f, 10000f, 24f, 24f);
                int controlId = GUIUtility.GetControlID(FocusType.Keyboard);
                _inputAddressOrCode = GUI.TextField(fieldRect, _inputAddressOrCode, Skin.textField);

                if (GUI.GetNameOfFocusedControl() != "CoopAddrField")
                {
                    // Draw placeholder text
                    GUI.color = SilksongSkin.TextMuted;
                    var phStyle = new GUIStyle(Skin.textField) { normal = { textColor = SilksongSkin.TextMuted } };
                    GUI.Label(fieldRect, "192.168.1.5:7777 or SK-...", phStyle);
                    GUI.color = Color.white;
                }
                GUI.SetNextControlName("CoopAddrField");
            }
            else
            {
                _inputAddressOrCode = GUILayout.TextField(_inputAddressOrCode, GUILayout.Height(24));
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(10);

            // Connect buttons
            bool canConnect = !isConnecting && !string.IsNullOrWhiteSpace(_inputAddressOrCode);

            GUI.enabled = canConnect;
            GUILayout.BeginHorizontal();

            if (GUILayout.Button("JOIN  via  IP / Code", GUILayout.Height(32)))
            {
                JoinUdpSession(_inputAddressOrCode);
            }
            if (GUILayout.Button("JOIN  via  Steam  ID", GUILayout.Height(32)))
            {
                JoinSteamSession(_inputAddressOrCode);
            }

            GUILayout.EndHorizontal();
            GUI.enabled = true;

            // Paste from clipboard button
            GUILayout.Space(4);
            if (GUILayout.Button("Paste from Clipboard", GUILayout.Height(22)))
            {
                _inputAddressOrCode = GUIUtility.systemCopyBuffer;
                _toasts.ShowInfo("Pasted from clipboard");
            }

            // Recent connections
            var recent = _history.GetAll();
            if (recent.Count > 0)
            {
                GUILayout.Space(12);
                SilksongSkin.DrawDivider(1f, 2f, 4f);
                GUILayout.Label("RECENT", _styleSubtitle ?? Skin.label);

                foreach (var entry in recent)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"  {entry}", GUILayout.ExpandWidth(true));
                    if (GUILayout.Button("Join", GUILayout.Width(50), GUILayout.Height(20)))
                    {
                        _inputAddressOrCode = entry;
                        JoinUdpSession(entry);
                    }
                    GUILayout.EndHorizontal();
                }

                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Clear History", GUILayout.Width(100), GUILayout.Height(18)))
                {
                    _history.Clear();
                    _toasts.ShowInfo("History cleared");
                }
                GUILayout.EndHorizontal();
            }

            // Reconnect button
            if (!string.IsNullOrEmpty(_lastConnectionTarget))
            {
                GUILayout.Space(8);
                if (GUILayout.Button($"Reconnect to {_lastConnectionTarget}", GUILayout.Height(26)))
                {
                    if (ulong.TryParse(_lastConnectionTarget, out _))
                        JoinSteamSession(_lastConnectionTarget);
                    else
                        JoinUdpSession(_lastConnectionTarget);
                }
            }
        }

        // ─── STATUS TAB ───────────────────────────────────────────────────────
        private void DrawStatusTab()
        {
            bool isConnected = _sessionState != null && _sessionState.IsConnected;

            if (!isConnected)
            {
                GUILayout.Label("NOT CONNECTED", _styleTitle ?? Skin.label);
                GUILayout.Space(6);
                GUILayout.Label("Start or join a session to see connection details.", _styleSubtitle ?? Skin.label);
                return;
            }

            // Connection status
            GUILayout.Label("SESSION STATUS", _styleTitle ?? Skin.label);
            GUILayout.Space(4);

            // Status indicator
            GUILayout.BeginHorizontal();
            DrawStatusDot();
            GUILayout.Label(GetStatusDisplay(), GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            // Role
            string role = _sessionState!.Role == SessionRole.Host ? "Player 1 (Host)" : "Player 2 (Client)";
            GUILayout.Label($"<b>Role:</b>  {role}");

            // Peer
            string peerName = _sessionState.RemotePeerId?.ToString() ?? "Unknown";
            GUILayout.Label($"<b>Partner:</b>  {peerName}");

            // Session duration
            float sessionTime = _diagnostics.SessionDuration;
            if (sessionTime > 0)
            {
                GUILayout.Label($"<b>Session:</b>  {FormatSessionTime(sessionTime)}");
            }

            GUILayout.Space(8);
            SilksongSkin.DrawDivider(1f, 2f, 4f);

            // Network quality
            GUILayout.Label("NETWORK", _styleTitle ?? Skin.label);
            GUILayout.Space(4);

            int rtt = _sessionState.RoundTripTimeMs;
            string rttColor = rtt < 50 ? "#66DD66" : rtt < 150 ? "#DDDD66" : "#DD6666";

            GUILayout.BeginHorizontal();
            GUILayout.Label($"<b>Ping:</b>  <color={rttColor}>{rtt} ms</color>", GUILayout.Width(180));
            DrawPingBars(rtt);
            GUILayout.EndHorizontal();

            // Packet stats
            long sent = _sessionState.SentPackets;
            long recv = _sessionState.ReceivedPackets;
            GUILayout.Label($"<b>Packets:</b>  ↑ {sent}  ↓ {recv}");

            // Bandwidth
            long bwOut = _diagnostics.BytesOutPerSecond;
            long bwIn = _diagnostics.BytesInPerSecond;
            GUILayout.Label($"<b>Bandwidth:</b>  ↑ {FormatBandwidth(bwOut)}/s  ↓ {FormatBandwidth(bwIn)}/s");

            // Packet loss
            float loss = _diagnostics.PacketLossPercent;
            if (loss > 0.1f)
            {
                string lossColor = loss < 2f ? "#DDDD66" : "#DD6666";
                GUILayout.Label($"<b>Packet Loss:</b>  <color={lossColor}>{loss:F1}%</color>");
            }

            GUILayout.Space(8);
            SilksongSkin.DrawDivider(1f, 2f, 4f);

            // Host address (if hosting)
            if (!string.IsNullOrEmpty(_currentHostAddress))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"<b>Your Address:</b>  {_currentHostAddress}", GUILayout.ExpandWidth(true));
                DrawCopyButton(_currentHostAddress, "Copy");
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(12);
            DrawDisconnectButton();
        }

        // ─── SETTINGS TAB ─────────────────────────────────────────────────────
        private void DrawSettingsTab()
        {
            GUILayout.Label("SETTINGS", _styleTitle ?? Skin.label);
            GUILayout.Space(6);

            var plugin = SilksongCoopPlugin.Instance;
            if (plugin == null)
            {
                GUILayout.Label("Plugin not initialized.", _styleSubtitle ?? Skin.label);
                return;
            }

            // Tick rate
            GUILayout.Label("<b>Network Tick Rate</b>");
            GUILayout.BeginHorizontal();
            int tickRate = plugin.TickRateHzConfig?.Value ?? 30;
            int newTickRate = (int)GUILayout.HorizontalSlider(tickRate, 20, 60, GUILayout.ExpandWidth(true));
            GUILayout.Label($"{newTickRate} Hz", GUILayout.Width(50));
            GUILayout.EndHorizontal();
            if (newTickRate != tickRate && plugin.TickRateHzConfig != null)
            {
                plugin.TickRateHzConfig.Value = newTickRate;
            }
            GUILayout.Label("Higher = smoother but more bandwidth (20-60 Hz)", _styleSubtitle ?? Skin.label);

            GUILayout.Space(10);

            // UDP port
            GUILayout.Label("<b>Default UDP Port</b>");
            GUILayout.BeginHorizontal();
            int port = plugin.DefaultUdpPortConfig?.Value ?? 7777;
            string portStr = GUILayout.TextField(port.ToString(), GUILayout.Width(80));
            if (int.TryParse(portStr, out int newPort) && newPort >= 1024 && newPort <= 65535)
            {
                if (newPort != port && plugin.DefaultUdpPortConfig != null)
                {
                    plugin.DefaultUdpPortConfig.Value = newPort;
                }
            }
            GUILayout.Label("(1024–65535)", _styleSubtitle ?? Skin.label);
            GUILayout.EndHorizontal();

            GUILayout.Space(10);

            // Player 2 hue shift
            GUILayout.Label("<b>Player 2 Color</b>");
            GUILayout.BeginHorizontal();
            float hue = plugin.Player2HueShiftConfig?.Value ?? 0.58f;
            float newHue = GUILayout.HorizontalSlider(hue, 0f, 1f, GUILayout.ExpandWidth(true));
            // Preview color swatch
            var previewRect = GUILayoutUtility.GetRect(24, 24);
            if (Event.current.type == EventType.Repaint)
            {
                Color swatch = Color.HSVToRGB(newHue, 0.65f, 0.85f);
                GUI.DrawTexture(previewRect, SilksongSkin.WhitePixel);
                GUI.color = swatch;
                GUI.DrawTexture(new Rect(previewRect.x + 2, previewRect.y + 2, previewRect.width - 4, previewRect.height - 4), SilksongSkin.WhitePixel);
                GUI.color = Color.white;
            }
            GUILayout.EndHorizontal();
            if (Math.Abs(newHue - hue) > 0.001f && plugin.Player2HueShiftConfig != null)
            {
                plugin.Player2HueShiftConfig.Value = newHue;
                plugin.RefreshLocalVisuals(_sessionState != null && _sessionState.Role == SessionRole.Client);
            }
            GUILayout.Label("Shift the hue of Player 2's cloak color", _styleSubtitle ?? Skin.label);

            GUILayout.Space(10);

            // Dash trail toggle
            GUILayout.Label("<b>Player 2 Dash Trail</b>");
            bool trailEnabled = plugin.EnablePlayer2DashTrailConfig?.Value ?? true;
            bool newTrail = GUILayout.Toggle(trailEnabled, "Enable cyan dash trail for Player 2");
            if (newTrail != trailEnabled && plugin.EnablePlayer2DashTrailConfig != null)
            {
                plugin.EnablePlayer2DashTrailConfig.Value = newTrail;
            }

            GUILayout.Space(10);

            // Revive cooldown
            GUILayout.Label("<b>Revive Cooldown</b>");
            GUILayout.BeginHorizontal();
            float reviveCd = plugin.ReviveCooldownConfig?.Value ?? 20f;
            float newReviveCd = GUILayout.HorizontalSlider(reviveCd, 15f, 30f, GUILayout.ExpandWidth(true));
            GUILayout.Label($"{newReviveCd:F0}s", GUILayout.Width(40));
            GUILayout.EndHorizontal();
            if (Math.Abs(newReviveCd - reviveCd) > 0.1f && plugin.ReviveCooldownConfig != null)
            {
                plugin.ReviveCooldownConfig.Value = newReviveCd;
                if (plugin.DeathCoordinator != null)
                {
                    plugin.DeathCoordinator.ReviveCooldownDuration = Math.Clamp(newReviveCd, 15f, 30f);
                }
            }
            GUILayout.Label("Time before solo-death revive (15–30 seconds)", _styleSubtitle ?? Skin.label);

            GUILayout.Space(16);
            SilksongSkin.DrawDivider(1f, 4f, 4f);

            // Info
            GUILayout.Label($"<color=#998877>Silksong Co-op Mod v{SilksongCoopPlugin.PluginVersion}</color>", _styleSubtitle ?? Skin.label);
        }

        // ─── Disconnect ───────────────────────────────────────────────────────
        private void DrawDisconnectButton()
        {
            if (_showDisconnectConfirm)
            {
                GUILayout.BeginVertical(Skin.box);
                GUILayout.Label("Are you sure you want to disconnect?");
                GUILayout.Space(4);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Disconnect", GUILayout.Height(26)))
                {
                    _showDisconnectConfirm = false;
                    DisconnectSession();
                }
                if (GUILayout.Button("Cancel", GUILayout.Height(26)))
                {
                    _showDisconnectConfirm = false;
                }
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }
            else
            {
                GUI.color = new Color(0.9f, 0.35f, 0.35f, 1f);
                if (GUILayout.Button("DISCONNECT", GUILayout.Height(30)))
                {
                    _showDisconnectConfirm = true;
                }
                GUI.color = Color.white;
            }
        }

        // ─── Helpers ──────────────────────────────────────────────────────────

        private void DrawStatusDot()
        {
            if (_sessionState == null) return;
            float dotSize = 12f;
            var rect = GUILayoutUtility.GetRect(dotSize, dotSize);
            if (Event.current.type == EventType.Repaint)
            {
                Color dotColor = _sessionState.IsConnected
                    ? SilksongSkin.GreenOk
                    : _sessionState.Status == SessionStatus.Connecting
                        ? SilksongSkin.BlueInfo
                        : _sessionState.Status == SessionStatus.Hosting
                            ? SilksongSkin.Gold
                            : SilksongSkin.RedError;
                GUI.color = dotColor;
                GUI.DrawTexture(rect, SilksongSkin.WhitePixel);
                GUI.color = Color.white;
            }
        }

        private void DrawPingBars(int rtt)
        {
            int bars = rtt < 30 ? 5 : rtt < 60 ? 4 : rtt < 100 ? 3 : rtt < 150 ? 2 : 1;
            Color barColor = rtt < 60 ? SilksongSkin.GreenOk : rtt < 150 ? SilksongSkin.YellowWarn : SilksongSkin.RedError;

            for (int i = 0; i < 5; i++)
            {
                var rect = GUILayoutUtility.GetRect(6, 14);
                if (Event.current.type == EventType.Repaint)
                {
                    float h = 4f + i * 2f;
                    var barRect = new Rect(rect.x, rect.y + (14f - h), 4f, h);
                    GUI.color = i < bars ? barColor : new Color(0.2f, 0.18f, 0.25f, 0.5f);
                    GUI.DrawTexture(barRect, SilksongSkin.WhitePixel);
                    GUI.color = Color.white;
                }
            }
        }

        private void DrawCopyButton(string text, string label)
        {
            bool isFlashing = _copyFlashText == text && Time.unscaledTime < _copyFlashUntil;

            if (isFlashing)
            {
                GUI.backgroundColor = SilksongSkin.GreenOk;
            }
            if (GUILayout.Button(isFlashing ? "✓ Copied" : label, GUILayout.Width(70)))
            {
                GUIUtility.systemCopyBuffer = text;
                _copyFlashUntil = Time.unscaledTime + 1.5f;
                _copyFlashText = text;
                _toasts.ShowSuccess("Copied to clipboard!");
            }
            if (isFlashing)
            {
                GUI.backgroundColor = Color.white;
            }
        }

        private string GetStatusDisplay()
        {
            if (_sessionState == null) return "<color=#998877>Uninitialized</color>";
            return _sessionState.Status switch
            {
                SessionStatus.Disconnected => "<color=#DD5555>Disconnected</color>",
                SessionStatus.Hosting => "<color=#D4A843>Hosting (Waiting...)</color>",
                SessionStatus.Connecting => "<color=#66AAEE>Connecting...</color>",
                SessionStatus.Connected => "<color=#66DD66>Connected</color>",
                SessionStatus.Spectating => "<color=#DDAA33>Spectating</color>",
                SessionStatus.TeamWipe => "<color=#DD3333>Team Wipe</color>",
                _ => _sessionState.Status.ToString()
            };
        }

        private static string FormatBandwidth(long bytesPerSec)
        {
            if (bytesPerSec < 1024) return $"{bytesPerSec} B";
            if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024.0:F1} KB";
            return $"{bytesPerSec / (1024.0 * 1024.0):F1} MB";
        }

        private static string FormatSessionTime(float seconds)
        {
            if (seconds < 0) return "—";
            int s = (int)seconds;
            int h = s / 3600;
            int m = (s % 3600) / 60;
            int sec = s % 60;
            if (h > 0) return $"{h}h {m:D2}m {sec:D2}s";
            if (m > 0) return $"{m}m {sec:D2}s";
            return $"{sec}s";
        }

        // ─── Session actions ──────────────────────────────────────────────────

        private void HostUdpSession()
        {
            try
            {
                var plugin = SilksongCoopPlugin.Instance;
                if (plugin == null)
                {
                    _toasts.ShowError("Plugin not initialized");
                    return;
                }

                plugin.CancelReconnect();
                _udpTransport = plugin.UdpTransport;
                if (_udpTransport == null)
                {
                    _toasts.ShowError("UDP transport not available");
                    return;
                }

                int udpPort = plugin.DefaultUdpPortConfig?.Value ?? 7777;
                _udpTransport.StartHost(udpPort);

                _currentHostAddress = $"{GetLocalIpAddress()}:{udpPort}";
                string code = RoomCodeCodec.Encode(GetLocalIpAddress(), udpPort);

                _sessionState?.SetHosting(_udpTransport.LocalPeerId, code);
                plugin.SetActiveTransport(_udpTransport);

                // Auto-copy address to clipboard
                GUIUtility.systemCopyBuffer = _currentHostAddress;
                _copyFlashUntil = Time.unscaledTime + 2f;
                _copyFlashText = _currentHostAddress;

                _diagnostics.OnSessionStarted();
                _toasts.ShowSuccess($"Hosting on {_currentHostAddress} — address copied!");
                _activeTab = Tab.Status;
            }
            catch (Exception ex)
            {
                _toasts.ShowError($"Failed to host UDP: {ex.Message}");
                SilksongCoopPlugin.LogWarn($"[Coop] HostUdpSession error: {ex.Message}");
            }
        }

        private string GetLocalIpAddress()
        {
            try
            {
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                string? fallback = null;
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                        continue;

                    string s = ip.ToString();
                    if (s == "127.0.0.1")
                    {
                        fallback ??= s;
                        continue;
                    }

                    if (IsPrivateLanIp(s)) return s;
                    fallback ??= s;
                }
                return fallback ?? "127.0.0.1";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to detect local IP: {ex.Message}");
            }
            return "127.0.0.1";
        }

        private static bool IsPrivateLanIp(string ip)
        {
            if (ip.StartsWith("10.")) return true;
            if (ip.StartsWith("192.168.")) return true;
            if (ip.StartsWith("172."))
            {
                var parts = ip.Split('.');
                if (parts.Length >= 2 && int.TryParse(parts[1], out int octet))
                    return octet >= 16 && octet <= 31;
            }
            return false;
        }

        private void HostSteamSession()
        {
            try
            {
                var plugin = SilksongCoopPlugin.Instance;
                if (plugin == null) { _toasts.ShowError("Plugin not initialized"); return; }

                plugin.CancelReconnect();
                _steamTransport = plugin.SteamTransport;
                if (_steamTransport == null)
                {
                    _toasts.ShowError("Steam transport not available. Ensure Steam is running.");
                    return;
                }

                _steamTransport.StartHost();
                plugin.SetActiveTransport(_steamTransport);
                _sessionState?.SetHosting(_steamTransport.LocalPeerId, "Steam-Lobby");

                _diagnostics.OnSessionStarted();
                _toasts.ShowSuccess("Steam lobby created — use Invite Friends");
                _activeTab = Tab.Status;
            }
            catch (Exception ex)
            {
                _toasts.ShowError($"Steam host failed: {ex.Message}");
                SilksongCoopPlugin.LogWarn($"[Coop] HostSteamSession error: {ex.Message}");
            }
        }

        private void JoinUdpSession(string addressOrCode)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(addressOrCode))
                {
                    _toasts.ShowWarning("Please enter an IP:port or Room Code");
                    return;
                }

                var plugin = SilksongCoopPlugin.Instance;
                if (plugin == null) { _toasts.ShowError("Plugin not initialized"); return; }

                plugin.CancelReconnect();
                _udpTransport = plugin.UdpTransport;
                if (_udpTransport == null) { _toasts.ShowError("UDP transport not available"); return; }

                string connectAddress = addressOrCode.Trim();
                if (!connectAddress.Contains(":") && !connectAddress.StartsWith("SK-"))
                {
                    int defaultPort = plugin.DefaultUdpPortConfig?.Value ?? 7777;
                    connectAddress = $"{connectAddress}:{defaultPort}";
                }

                _udpTransport.Connect(connectAddress);
                plugin.SetActiveTransport(_udpTransport);
                _lastConnectionTarget = connectAddress;
                _history.Add(connectAddress);

                _sessionState?.SetConnecting(_udpTransport.LocalPeerId, connectAddress);
                _toasts.ShowInfo($"Connecting to {connectAddress}...");
                _activeTab = Tab.Status;
            }
            catch (Exception ex)
            {
                _toasts.ShowError($"Join error: {ex.Message}");
                SilksongCoopPlugin.LogWarn($"[Coop] JoinUdpSession error: {ex.Message}");
            }
        }

        private void JoinSteamSession(string lobbyIdString)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(lobbyIdString))
                {
                    _toasts.ShowWarning("Please enter a Steam Lobby ID");
                    return;
                }

                var plugin = SilksongCoopPlugin.Instance;
                if (plugin == null) { _toasts.ShowError("Plugin not initialized"); return; }

                plugin.CancelReconnect();
                _steamTransport = plugin.SteamTransport;
                if (_steamTransport == null) { _toasts.ShowError("Steam transport not available"); return; }

                string trimmed = lobbyIdString.Trim();
                _steamTransport.Connect(trimmed);
                plugin.SetActiveTransport(_steamTransport);
                _lastConnectionTarget = trimmed;
                _history.Add(trimmed);

                if (ulong.TryParse(trimmed, out ulong parsedLobby) && _sessionState != null)
                {
                    _sessionState.SteamLobbyId = parsedLobby;
                }

                _sessionState?.SetConnecting(_steamTransport.LocalPeerId, trimmed);
                _toasts.ShowInfo($"Joining Steam Lobby {trimmed}...");
                _activeTab = Tab.Status;
            }
            catch (Exception ex)
            {
                _toasts.ShowError($"Steam join error: {ex.Message}");
                SilksongCoopPlugin.LogWarn($"[Coop] JoinSteamSession error: {ex.Message}");
            }
        }

        private void DisconnectSession()
        {
            try
            {
                _currentTransport?.Disconnect();
                _sessionState?.SetDisconnected(DisconnectReason.GracefulDisconnect);

                var plugin = SilksongCoopPlugin.Instance;
                plugin?.CancelReconnect();
                if (plugin != null && plugin.UdpTransport != null)
                {
                    plugin.SetActiveTransport(plugin.UdpTransport);
                }

                _currentHostAddress = "";
                _diagnostics.OnSessionEnded();
                _toasts.ShowInfo("Disconnected");
                _activeTab = Tab.Host;
            }
            catch (Exception ex)
            {
                _toasts.ShowError($"Disconnect error: {ex.Message}");
                SilksongCoopPlugin.LogWarn($"[Coop] DisconnectSession error: {ex.Message}");
            }
        }

        // Backing field for disconnect convenience
        private INetworkTransport? _currentTransport
        {
            get => SilksongCoopPlugin.Instance?.ActiveTransport;
        }
    }
}
