using System;
using SilksongCoop.Core.Localization;
using SilksongCoop.Core.Networking;
using SilksongCoop.Core.State;
using SilksongCoop.Mod;
using SilksongCoop.Mod.Diagnostics;
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
        public bool IsVisible { get; set; }

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
        private Rect _windowRect = new Rect(40, 50, 460, 600);
        // Current adaptive window size. The content uses a scroll view, so shrinking the
        // window on small resolutions keeps every control reachable instead of drawing the
        // close button off-screen.
        private float _layoutWidth = 460f;
        private float _layoutHeight = 600f;
        private string _inputAddressOrCode = "";
        private string _currentHostAddress = "";
        private string _currentRoomCode = "";
        private string _udpPortInput = "";
        private bool _udpPortInputInitialized;
        private int _windowId;

        // Copy feedback
        private float _copyFlashUntil = 0f;
        private string _copyFlashText = "";

        // Watermark cache: the text/color are rebuilt in Update at most four times per
        // second (or when the connection identity changes), never on every OnGUI event.
        private const float WatermarkRefreshInterval = 0.25f;
        private string _watermarkText = "[F10] Silksong Co-op";
        private Color _watermarkColor = SilksongSkin.TextCream;
        private float _nextWatermarkRefreshTime;
        private bool _watermarkInitialized;
        private bool _watermarkConnected;
        private SessionStatus _watermarkStatus;
        private NetworkPeerId? _watermarkPeerId;

        // Disconnect confirmation
        private bool _showDisconnectConfirm = false;

        // First-run guide; persisted through the [UI] OnboardingCompleted config.
        private bool _showOnboarding = true;

        // Independent scroll positions so a long Settings tab does not push the
        // Disconnect button out of the fixed-size window.
        private Vector2 _hostScroll;
        private Vector2 _joinScroll;
        private Vector2 _statusScroll;
        private Vector2 _settingsScroll;

        // Cached style refs (looked up once per OnGUI frame)
        private GUIStyle? _styleTitle;
        private GUIStyle? _styleSubtitle;
        private GUIStyle? _styleTab;
        private GUIStyle? _styleTabActive;
        private GUIStyle? _styleGood;
        private GUIStyle? _styleWarn;
        private GUIStyle? _styleBad;

        // BUG-10: dedicated derived styles for the HUD and overlays. Drawing code used to
        // mutate the shared cached styles (_styleTitle / Skin.label) in place, permanently
        // changing font sizes and alignment for every other element using them.
        private GUIStyle? _hudNameStyle;
        private GUIStyle? _barLabelStyle;
        private GUIStyle? _spectTitleStyle;
        private GUIStyle? _spectSubStyle;
        private GUIStyle? _wipeTitleStyle;
        private GUIStyle? _wipeSubStyle;
        private GUIStyle? _placeholderStyle;

        public ToastNotifier Toasts => _toasts;
        public NetworkDiagnostics Diagnostics => _diagnostics;
        public ConnectionHistory History => _history;

        /// <summary>
        /// Initializes a new instance of the CoopMenuUI class.
        /// </summary>
        public CoopMenuUI()
        {
            _windowId = 9842;
        }

        #region Unity Lifecycle

        private void Awake()
        {
            _windowId = 9842 + UnityEngine.Random.Range(0, 1000);
        }

        /// <summary>
        /// Initializes the menu UI with session state and transport references.
        /// </summary>
        /// <param name="sessionState">The co-op session state manager.</param>
        /// <param name="steamTransport">Steam P2P transport (can be null).</param>
        /// <param name="udpTransport">UDP transport (can be null).</param>
        /// <param name="keybinds">Keybind manager (optional).</param>
        /// <param name="qualityPresets">Quality preset manager (optional).</param>
        public void Initialize(CoopSessionState sessionState, SteamP2PTransport? steamTransport, LiteNetLibTransport? udpTransport, KeybindManager? keybinds = null, QualityPresetManager? qualityPresets = null)
        {
            _sessionState = sessionState;
            _steamTransport = steamTransport;
            _udpTransport = udpTransport;
            _keybinds = keybinds;
            _qualityPresets = qualityPresets;
            _diagnosticsOverlay = new DiagnosticsOverlay(
                _diagnostics,
                () => SilksongCoopPlugin.Instance?.CurrentTickHz ?? 0f,
                BuildPeerDiagnostics);

            // Issue #2.6: connection targets used to be written to the Windows registry
            // (PlayerPrefs) unconditionally, with no way to opt out.
            _history.PersistEnabled = SilksongCoopPlugin.Instance?.SaveConnectionHistoryConfig?.Value ?? false;

            // Only show the first-run card until the player dismisses it once.
            _showOnboarding = !(SilksongCoopPlugin.Instance?.OnboardingCompletedConfig?.Value ?? true);
        }

        /// <summary>
        /// Snapshot of the partner's scene presence and interpolation delay for the F11
        /// overlay. Kept here because the Unity-side clone spawner owns the live values.
        /// </summary>
        private static PeerDiagnosticsSnapshot BuildPeerDiagnostics()
        {
            var snapshot = new PeerDiagnosticsSnapshot();
            var plugin = SilksongCoopPlugin.Instance;
            var session = plugin?.SessionState;
            if (plugin == null || session == null || !session.IsConnected) return snapshot;

            snapshot.Connected = true;
            snapshot.PartnerAlive = plugin.DeathCoordinator?.IsPartnerAlive ?? true;

            var peerId = session.RemotePeerId;
            if (!peerId.HasValue) return snapshot;

            snapshot.SameScene = plugin.CloneSpawner?.IsRemoteInSameScene(peerId.Value) == true;
            if (plugin.CloneSpawner != null &&
                plugin.CloneSpawner.TryGetInterpolationDelayMs(peerId.Value, out float delayMs))
            {
                snapshot.HasInterpolationDelay = true;
                snapshot.InterpolationDelayMs = delayMs;
            }

            return snapshot;
        }

        /// <summary>
        /// Sets a status message to be displayed as an info toast.
        /// </summary>
        /// <param name="message">The message to display.</param>
        public void SetStatusMessage(string message)
        {
            _toasts.ShowInfo(message);
        }

        private void Update()
        {
            // Toggle menu using configurable keybind
            if (_keybinds != null && _keybinds.IsKeyDown(KeybindManager.ToggleMenu))
            {
                IsVisible = !IsVisible;
            }

            // Toggle diagnostics overlay with F11 (BUG-09: single keybinds-driven path —
            // the former `_keybinds == null` fallback branch was dead code, since Keybinds
            // is always initialized in Awake before CoopMenuUI ever updates)
            if (_keybinds != null && _keybinds.IsKeyDown(KeybindManager.ToggleDiagnostics))
            {
                if (_diagnosticsOverlay != null)
                {
                    _diagnosticsOverlay.IsVisible = !_diagnosticsOverlay.IsVisible;
                    _toasts.ShowInfo(_diagnosticsOverlay.IsVisible
                        ? Loc.T("Network diagnostics: ON (F10 - co-op menu)")
                        : Loc.T("Network diagnostics: OFF"));
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

            if (_sessionState?.Status == SessionStatus.Disconnected)
            {
                _currentRoomCode = string.Empty;
                _currentHostAddress = string.Empty;
                _copyFlashText = string.Empty;
            }

            // Update diagnostics
            _diagnostics.Update(Time.unscaledDeltaTime);
            RefreshWatermarkIfNeeded();

            // Auto-switch to Status tab when connection state changes
            if (_sessionState != null && _sessionState.IsConnected && _activeTab == Tab.Host)
            {
                _activeTab = Tab.Status;
            }
        }

        private void OnGUI()
        {
            bool isRepaint = Event.current.type == EventType.Repaint;

            // GUI.skin is process-global; restore the previous skin so other mods' OnGUI
            // passes are not rendered with the co-op skin.
            var previousSkin = GUI.skin;
            var previousColor = GUI.color;
            var previousContentColor = GUI.contentColor;
            var previousBackgroundColor = GUI.backgroundColor;
            try
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
            EnsureHudStyles();

            // Fixed-rect HUD elements, overlays and toasts only need to draw on Repaint.
            // The main window below still runs on Layout so GUILayout can measure it.
            if (isRepaint)
            {
                // ── Watermark ──────────────────────────────────────────────────
                DrawWatermark();

                // ── Partner vitals HUD ─────────────────────────────────────────
                if (_sessionState != null && _sessionState.IsConnected)
                {
                    DrawPartnerVitalsHud();
                }

                // ── Spectator overlay (death state) ────────────────────────────
                if (_sessionState != null && _sessionState.IsConnected)
                {
                    DrawSpectatorOverlay();
                }

                // ── Diagnostics overlay (F11 toggle) ───────────────────────────
                if (_diagnosticsOverlay != null)
                {
                    _diagnosticsOverlay.OnGUI();
                }

                // ── Toast notifications (always on top) ────────────────────────
                _toasts.Draw();
            }

            // ── Main window ────────────────────────────────────────────────────
            if (!IsVisible) return;

            UpdateWindowLayout();
            _windowRect = GUI.Window(_windowId, _windowRect, DrawWindowContents, "");

            // Keep the window fully on-screen so the close button stays reachable.
            if (_windowRect.x < 0) _windowRect.x = 0;
            if (_windowRect.y < 0) _windowRect.y = 0;
            if (_windowRect.x + _windowRect.width > Screen.width)
                _windowRect.x = Mathf.Max(0, Screen.width - _windowRect.width);
            if (_windowRect.y + _windowRect.height > Screen.height)
                _windowRect.y = Mathf.Max(0, Screen.height - _windowRect.height);
            }
            finally
            {
                GUI.skin = previousSkin;
                GUI.color = previousColor;
                GUI.contentColor = previousContentColor;
                GUI.backgroundColor = previousBackgroundColor;
            }
        }

        #endregion // Unity Lifecycle

        #region HUD and Overlay Drawing

        // ─── Watermark ────────────────────────────────────────────────────────
        private void DrawWatermark()
        {
            GUI.color = _watermarkColor;
            GUI.Label(new Rect(10, 10, 400, 25), _watermarkText, _styleTitle ?? Skin.label);
            GUI.color = Color.white;
        }

        /// <summary>
        /// Rebuilds the cached watermark at most four times per second, or immediately
        /// when the connection state/peer/room changes. RTT changes are picked up by the
        /// timer so a jittering value cannot rebuild the string on every frame.
        /// </summary>
        private void RefreshWatermarkIfNeeded()
        {
            float now = Time.unscaledTime;
            bool connected = _sessionState != null && _sessionState.IsConnected;
            SessionStatus status = _sessionState?.Status ?? SessionStatus.Disconnected;
            NetworkPeerId? peerId = _sessionState?.RemotePeerId;

            bool identityChanged = !_watermarkInitialized ||
                                   connected != _watermarkConnected ||
                                   status != _watermarkStatus ||
                                   peerId != _watermarkPeerId;

            if (!identityChanged && now < _nextWatermarkRefreshTime) return;

            _watermarkInitialized = true;
            _watermarkConnected = connected;
            _watermarkStatus = status;
            _watermarkPeerId = peerId;
            _nextWatermarkRefreshTime = now + WatermarkRefreshInterval;

            if (connected)
            {
                string peerName = peerId.HasValue
                    ? peerId.Value.DisplayName
                    : Loc.T("Partner");
                int rtt = _sessionState?.RoundTripTimeMs ?? 0;
                string quality = rtt <= 0 ? "–" : rtt < 50 ? "●" : rtt < 150 ? "◐" : "○";
                _watermarkText = $"[F10] Co-op  {quality} {peerName}  {(rtt > 0 ? $"{rtt}ms" : Loc.T("n/a"))}";
                _watermarkColor = rtt <= 0 ? SilksongSkin.TextCream : rtt < 50 ? SilksongSkin.GreenOk : rtt < 150 ? SilksongSkin.YellowWarn : SilksongSkin.RedError;
            }
            else if (status == SessionStatus.Hosting)
            {
                _watermarkText = Loc.T("Hosting (waiting for partner...)");
                _watermarkColor = SilksongSkin.Gold;
            }
            else if (status == SessionStatus.Connecting)
            {
                _watermarkText = $"[F10] {Loc.T("Connecting...")}";
                _watermarkColor = SilksongSkin.BlueInfo;
            }
            else
            {
                _watermarkText = "[F10] Silksong Co-op";
                _watermarkColor = SilksongSkin.TextCream;
            }
        }

        // ─── Partner vitals HUD (top-right) ───────────────────────────────────
        private void DrawPartnerVitalsHud()
        {
            var vitals = SilksongCoopPlugin.Instance?.VitalsSync;
            if (vitals == null) return;

            var peers = vitals.RemotePeers;
            if (peers.Count == 0) return;

            const float hudW = 180f;
            const float hudH = 68f;
            const float gap = 6f;
            float x = _diagnosticsOverlay?.IsVisible == true ? 16f : Mathf.Max(0f, Screen.width - hudW - 16f);
            float y = 40f;

            int shown = 0;
            for (int i = 0; i < peers.Count; i++)
            {
                var peer = peers[i];
                if (peer.MaxHealth <= 0 && peer.MaxSilkSpools <= 0) continue;

                DrawPeerVitalsBlock(peer, x, y + shown * (hudH + gap), hudW, hudH);
                shown++;
            }
        }

        private void DrawPeerVitalsBlock(SilksongCoop.Core.State.RemotePeerVitals peer, float x, float y, float hudW, float hudH)
        {
            string peerName = string.IsNullOrEmpty(peer.Peer.DisplayName)
                ? Loc.T("Partner")
                : peer.Peer.DisplayName;

            bool alive = SilksongCoopPlugin.Instance?.DeathCoordinator?.IsRemotePeerAlive(peer.Peer) ?? true;

            // Background (dimmed while the partner is dead)
            GUI.color = alive
                ? new Color(SilksongSkin.BgPanel.r, SilksongSkin.BgPanel.g, SilksongSkin.BgPanel.b, 0.88f)
                : new Color(SilksongSkin.BgPanel.r, SilksongSkin.BgPanel.g, SilksongSkin.BgPanel.b, 0.55f);
            GUI.DrawTexture(new Rect(x, y, hudW, hudH), SilksongSkin.WhitePixel);
            SilksongSkin.DrawFrame(new Rect(x, y, hudW, hudH), SilksongSkin.GoldDim);

            // Left accent bar identifies the partner and signals death.
            GUI.color = alive
                ? SilksongSkin.BlueInfo
                : SilksongSkin.RedError;
            GUI.DrawTexture(new Rect(x, y, 3f, hudH), SilksongSkin.WhitePixel);

            // Partner name
            GUI.color = SilksongSkin.BlueInfo;
            GUI.Label(new Rect(x + 8f, y + 2f, hudW - 64f, 16f), peerName, _hudNameStyle ?? Skin.label);

            if (!alive)
            {
                GUI.color = new Color(0.95f, 0.35f, 0.35f);
                GUI.Label(new Rect(x + hudW - 56f, y + 2f, 50f, 16f), Loc.T("DEAD"), _hudNameStyle ?? Skin.label);
            }

            // HP bar
            float barX = x + 8f;
            float barW = hudW - 16f;
            float barH = 8f;
            float barY = y + 22f;

            float hpFrac = peer.MaxHealth > 0 ? (float)peer.Health / peer.MaxHealth : 0f;
            Color hpColor = alive ? SilksongSkin.RedError : new Color(0.45f, 0.30f, 0.30f);
            DrawStatusBar(barX, barY, barW, barH, hpFrac, hpColor,
                Loc.T("HP {0}/{1}", peer.Health, peer.MaxHealth));

            // Silk bar
            float silkY = y + 42f;
            float silkFrac = peer.MaxSilkSpools > 0 ? (float)peer.Silk / (peer.MaxSilkSpools * 9f) : 0f;
            Color silkColor = alive ? SilksongSkin.BlueInfo : new Color(0.30f, 0.36f, 0.39f);
            DrawStatusBar(barX, silkY, barW, barH, silkFrac, silkColor,
                Loc.T("Silk {0}/{1}", peer.Silk, peer.MaxSilkSpools * 9));

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
            GUI.Label(new Rect(x, y, w, h), label, _barLabelStyle ?? Skin.label);
        }

        // ─── Spectator overlay (death state) ──────────────────────────────────
        /// <summary>
        /// BUG-10: creates the HUD-derived styles exactly once, copying the base styles so
        /// the shared cached styles are never mutated in place.
        /// </summary>
        private void EnsureHudStyles()
        {
            if (_hudNameStyle != null && _barLabelStyle != null && _placeholderStyle != null) return;
            var title = _styleTitle ?? Skin.label;
            var label = Skin.label;
            _hudNameStyle = new GUIStyle(title) { fontSize = 11 };
            _barLabelStyle = new GUIStyle(label) { fontSize = 9, alignment = TextAnchor.MiddleCenter };
            _spectTitleStyle = new GUIStyle(title) { fontSize = 16, alignment = TextAnchor.MiddleCenter };
            _spectSubStyle = new GUIStyle(label) { fontSize = 13, alignment = TextAnchor.MiddleCenter };
            _wipeTitleStyle = new GUIStyle(title) { fontSize = 15, alignment = TextAnchor.MiddleCenter };
            _wipeSubStyle = new GUIStyle(label) { fontSize = 12, alignment = TextAnchor.MiddleCenter };
            _placeholderStyle = new GUIStyle(Skin.textField)
            {
                normal = { textColor = SilksongSkin.TextMuted }
            };
        }

        private void DrawSpectatorOverlay()
        {
            var coordinator = SilksongCoopPlugin.Instance?.DeathCoordinator;
            if (coordinator == null) return;

            if (coordinator.CurrentState == DeathState.Spectating)
            {
                float bannerWidth = Mathf.Min(420f, Mathf.Max(1f, Screen.width - 24f));
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
                    : Loc.T("Partner");

                GUI.color = SilksongSkin.GoldBright;
                GUI.Label(new Rect(x, y + 8, bannerWidth, 24), Loc.T("SPECTATING {0}", partnerName.ToUpperInvariant()), _spectTitleStyle ?? Skin.label);

                int secondsLeft = Mathf.CeilToInt(coordinator.RemainingSpectatorTime);
                GUI.color = SilksongSkin.TextCream;

                // The revive timer still expires when the partner walked into another
                // room; spell out that the fallback is a local bench respawn instead of
                // silently teleporting (which is impossible across scenes).
                var spectatorPeer = _sessionState?.RemotePeerId;
                bool partnerInScene = spectatorPeer.HasValue &&
                                      SilksongCoopPlugin.Instance?.CloneSpawner?.IsRemoteInSameScene(spectatorPeer.Value) == true;
                string reviveText = partnerInScene
                    ? Loc.T("Reviving beside partner in <color=#D4A843><b>{0}s</b></color>", secondsLeft)
                    : Loc.T("Partner is in another room — reviving at the last bench in <color=#D4A843><b>{0}s</b></color>", secondsLeft);
                GUI.Label(new Rect(x, y + 32, bannerWidth, 22), reviveText, _spectSubStyle ?? Skin.label);

                // Progress bar
                float total = Math.Clamp(coordinator.ReviveCooldownDuration, 15f, 30f);
                float progress = Mathf.Clamp01(1f - (coordinator.RemainingSpectatorTime / total));
                float barWidth = bannerWidth - 40f;
                float barY = y + 58f;
                DrawStatusBar(x + 20f, barY, barWidth, 8f, progress, SilksongSkin.Gold, "");
            }
            else if (coordinator.CurrentState == DeathState.TeamWipe)
            {
                float bannerWidth = Mathf.Min(420f, Mathf.Max(1f, Screen.width - 24f));
                float bannerHeight = 50f;
                float x = (Screen.width - bannerWidth) * 0.5f;
                float y = Screen.height * 0.15f;

                GUI.color = SilksongSkin.BgDeepest;
                GUI.DrawTexture(new Rect(x, y, bannerWidth, bannerHeight), SilksongSkin.WhitePixel);
                GUI.color = SilksongSkin.RedError;
                GUI.DrawTexture(new Rect(x, y, bannerWidth, 2), SilksongSkin.WhitePixel);

                GUI.color = SilksongSkin.RedError;
                GUI.Label(new Rect(x, y + 6, bannerWidth, 22), Loc.T("TEAM WIPE"), _wipeTitleStyle ?? Skin.label);

                GUI.color = SilksongSkin.TextCream;
                GUI.Label(new Rect(x, y + 28, bannerWidth, 18), Loc.T("Both heroes have fallen — Reloading at last bench..."), _wipeSubStyle ?? Skin.label);
            }

            GUI.color = Color.white;
        }

        #endregion // HUD and Overlay Drawing

        #region Main Window and Tab Drawing

        /// <summary>
        /// Fits the window inside the current screen. The preferred 460x600 layout is kept
        /// when there is room; on smaller resolutions the window shrinks and the content
        /// scroll view still provides access to every tab.
        /// </summary>
        private void UpdateWindowLayout()
        {
            float width = Mathf.Min(460f, Mathf.Max(1f, Screen.width - 24f));
            float height = Mathf.Min(600f, Mathf.Max(1f, Screen.height - 24f));

            if (!Mathf.Approximately(width, _layoutWidth) ||
                !Mathf.Approximately(height, _layoutHeight))
            {
                _layoutWidth = width;
                _layoutHeight = height;
                _windowRect.width = width;
                _windowRect.height = height;
            }
        }

        // ─── Main window ──────────────────────────────────────────────────────
        private void DrawWindowContents(int windowId)
        {
            GUI.DragWindow(new Rect(0, 0, _layoutWidth, 24));

            // A fine border gives the panel a quiet frame without obscuring the game.
            if (Event.current.type == EventType.Repaint)
            {
                SilksongSkin.DrawFrame(new Rect(0, 0, _layoutWidth, _layoutHeight), SilksongSkin.GoldDim);
                SilksongSkin.DrawFrame(new Rect(4, 4, _layoutWidth - 8, _layoutHeight - 8),
                    new Color(SilksongSkin.GoldDim.r, SilksongSkin.GoldDim.g, SilksongSkin.GoldDim.b, 0.35f));
            }

            // Title bar
            GUI.color = SilksongSkin.Gold;
            var titleStyle = _styleTitle ?? Skin.label;
            GUI.Label(new Rect(12, 4, Mathf.Max(120f, _layoutWidth - 48f), 20),
                "SILKSONG CO-OP", titleStyle);

            // Close button
            GUI.color = Color.white;
            if (GUI.Button(new Rect(_layoutWidth - 28, 3, 24, 18), "×"))
            {
                IsVisible = false;
            }

            GUILayout.Space(26);

            // ── Tab bar ────────────────────────────────────────────────────────
            DrawTabBar();

            SilksongSkin.DrawDivider(1f, 4f, 6f);

            // Always-visible connection strip: the player can see hosting/connected
            // state without switching to the Status tab.
            DrawConnectionStrip();
            SilksongSkin.DrawDivider(1f, 2f, 4f);

            // ── Tab content ────────────────────────────────────────────────────
            Vector2 scroll = GetActiveScroll();
            scroll = GUILayout.BeginScrollView(scroll, false, false);
            DrawOnboardingPanel();
            switch (_activeTab)
            {
                case Tab.Host: DrawHostTab(); break;
                case Tab.Join: DrawJoinTab(); break;
                case Tab.Status: DrawStatusTab(); break;
                case Tab.Settings: DrawSettingsTab(); break;
            }
            GUILayout.EndScrollView();
            SetActiveScroll(scroll);
        }

        /// <summary>
        /// Compact status line shown on every tab: connection state plus, while hosting,
        /// a reminder that the partner has not joined yet.
        /// </summary>
        private void DrawConnectionStrip()
        {
            if (_sessionState == null) return;

            GUILayout.BeginHorizontal();
            GUILayout.Space(8);
            DrawStatusDot();
            GUILayout.Label(GetStatusDisplay(), GUILayout.ExpandWidth(true));

            if (_sessionState.IsConnected)
            {
                GUILayout.Label(Loc.T("Partner online"), _styleSubtitle ?? Skin.label);
            }
            else if (_sessionState.Status == SessionStatus.Hosting)
            {
                GUILayout.Label(Loc.T("Waiting for partner..."), _styleSubtitle ?? Skin.label);
            }

            GUILayout.Space(8);
            GUILayout.EndHorizontal();
        }

        private Vector2 GetActiveScroll()
        {
            return _activeTab switch
            {
                Tab.Host => _hostScroll,
                Tab.Join => _joinScroll,
                Tab.Status => _statusScroll,
                _ => _settingsScroll
            };
        }

        private void SetActiveScroll(Vector2 value)
        {
            switch (_activeTab)
            {
                case Tab.Host: _hostScroll = value; break;
                case Tab.Join: _joinScroll = value; break;
                case Tab.Status: _statusScroll = value; break;
                default: _settingsScroll = value; break;
            }
        }

        /// <summary>
        /// First-run card: explains the two hotkeys and the shortest host/join path.
        /// Dismissing it writes [UI] OnboardingCompleted=true.
        /// </summary>
        private void DrawOnboardingPanel()
        {
            if (!_showOnboarding) return;

            GUILayout.BeginVertical(Skin.box);
            GUILayout.Label(Loc.T("GETTING STARTED"), _styleTitle ?? Skin.label);
            GUILayout.Label(Loc.T("1. One player hosts a game: UDP for LAN/VPN, Steam for friends."));
            GUILayout.Label(Loc.T("2. The other player joins with the room code, IP:port or Steam lobby."));
            GUILayout.Label(Loc.T("3. F11 shows live network diagnostics; Settings can save a diagnostics file."));
            GUILayout.Space(4);
            if (GUILayout.Button(Loc.T("Got it"), GUILayout.Height(24)))
            {
                _showOnboarding = false;
                var config = SilksongCoopPlugin.Instance?.OnboardingCompletedConfig;
                if (config != null) config.Value = true;
            }
            GUILayout.EndVertical();
            GUILayout.Space(8);
        }

        private void DrawTabBar()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(8);
            DrawTabButton(Loc.T("HOST"), Tab.Host);
            DrawTabButton(Loc.T("JOIN"), Tab.Join);
            DrawTabButton(Loc.T("STATUS"), Tab.Status);
            DrawTabButton(Loc.T("SETTINGS"), Tab.Settings);
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
                GUILayout.Label(
                    _sessionState != null && _sessionState.Role == SessionRole.Host
                        ? Loc.T("YOUR SESSION")
                        : Loc.T("SESSION"),
                    _styleTitle ?? Skin.label);
                GUILayout.Space(4);

                // The room code is the only thing the partner needs on the UDP path;
                // keep it in a large, one-click copy block instead of a small row.
                if (!string.IsNullOrEmpty(_currentRoomCode))
                {
                    GUILayout.Label(Loc.T("SHARE THIS CODE"), _styleSubtitle ?? Skin.label);
                    GUILayout.BeginVertical(Skin.box);
                    GUILayout.Label($"<color=#D4A843><b>{_currentRoomCode}</b></color>");
                    if (GUILayout.Button(Loc.T("COPY CODE"), GUILayout.Height(30)))
                    {
                        CopyToClipboard(_currentRoomCode);
                    }
                    GUILayout.EndVertical();
                    GUILayout.Label($"<color=#ffaa44><i>{Loc.T("Share the room code (with secret): it authenticates the session. Direct IP:port has no shared-secret barrier and should only be used with trusted peers on a private network.")}</i></color>");
                }

                if (!string.IsNullOrEmpty(_currentHostAddress))
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(Loc.T("<b>Address:</b>"), GUILayout.Width(70));
                    GUILayout.Label($"<color=#D4A843><b>{_currentHostAddress}</b></color>");
                    DrawCopyButton(_currentHostAddress, Loc.T("Copy"));
                    GUILayout.EndHorizontal();
                }

                if (_sessionState != null && _sessionState.SteamLobbyId.HasValue)
                {
                    GUILayout.Space(4);
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(Loc.T("<b>Steam Lobby:</b>"), GUILayout.Width(90));
                    GUILayout.Label($"{_sessionState.SteamLobbyId.Value}");
                    DrawCopyButton(_sessionState.SteamLobbyId.Value.ToString(), Loc.T("Copy"));
                    GUILayout.EndHorizontal();
                }

                if (_steamTransport != null && _sessionState != null &&
                    _sessionState.Role == SessionRole.Host && _sessionState.SteamLobbyId.HasValue)
                {
                    GUILayout.Space(8);
                    if (GUILayout.Button(Loc.T("Invite Steam Friends..."), GUILayout.Height(30)))
                    {
                        _steamTransport.OpenInviteDialog();
                        _toasts.ShowInfo(Loc.T("Steam invite dialog opened"));
                    }
                }

                GUILayout.Space(8);
                DrawDisconnectButton();
            }
            else
            {
                GUILayout.Label(Loc.T("START A SESSION"), _styleTitle ?? Skin.label);
                GUILayout.Space(4);
                GUILayout.Label(Loc.T("Host a game for your partner to join"), _styleSubtitle ?? Skin.label);
                GUILayout.Space(12);

                if (GUILayout.Button(Loc.T("HOST  Steam  P2P  Lobby"), GUILayout.Height(38)))
                {
                    HostSteamSession();
                }

                GUILayout.Label(Loc.T("Invite friends directly through Steam. Requires Steam running."), _styleSubtitle ?? Skin.label);
                GUILayout.Space(10);

                if (GUILayout.Button(Loc.T("HOST  UDP  (Room Code)"), GUILayout.Height(34)))
                {
                    HostUdpSession();
                }

                GUILayout.Label(Loc.T("Best with Radmin VPN or LAN. Share the full room code with your partner."), _styleSubtitle ?? Skin.label);
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

            GUILayout.Label(Loc.T("JOIN A SESSION"), _styleTitle ?? Skin.label);
            GUILayout.Space(4);

            // Recent targets are the fastest path back into a session: one click per
            // row, before any text input.
            var recent = _history.GetAll();
            if (recent.Count > 0)
            {
                GUILayout.Label(Loc.T("RECENT"), _styleSubtitle ?? Skin.label);
                int roomNumber = 0;
                foreach (var entry in recent)
                {
                    bool steamEntry = IsSteamLobbyTarget(entry);
                    bool roomEntry = !steamEntry && RoomCodeCodec.TryDecode(entry, out _, out _);
                    string label = roomEntry
                        ? $"{Loc.T("Room Code")} #{++roomNumber}"
                        : steamEntry ? $"[Steam] {entry}" : entry;
                    if (GUILayout.Button(label, GUILayout.Height(24)))
                    {
                        _inputAddressOrCode = roomEntry ? string.Empty : entry;
                        if (steamEntry)
                            JoinSteamSession(entry);
                        else
                            JoinUdpSession(entry);
                    }
                }

                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(Loc.T("Clear History"), GUILayout.Width(130), GUILayout.Height(18)))
                {
                    _history.Clear();
                    _toasts.ShowInfo(Loc.T("History cleared"));
                }
                GUILayout.EndHorizontal();

                SilksongSkin.DrawDivider(1f, 6f, 6f);
                GUILayout.Label(Loc.T("OR ENTER AN ADDRESS"), _styleSubtitle ?? Skin.label);
            }
            else
            {
                GUILayout.Label(Loc.T("Enter the host's IP:port or Room Code"), _styleSubtitle ?? Skin.label);
            }

            GUILayout.Space(8);

            // Input field with placeholder
            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("Address:"), GUILayout.Width(60));

            // BUG-U1: single named TextField for BOTH empty and non-empty states. The old
            // code switched to an unnamed GUILayout.TextField as soon as the value became
            // non-empty, which dropped keyboard focus after the first typed character.
            var fieldRect = GUILayoutUtility.GetRect(0f, 10000f, 24f, 24f);
            GUI.SetNextControlName("CoopAddrField");
            _inputAddressOrCode = GUI.TextField(fieldRect, _inputAddressOrCode, Skin.textField);

            if (string.IsNullOrEmpty(_inputAddressOrCode) && GUI.GetNameOfFocusedControl() != "CoopAddrField")
            {
                // Draw placeholder text on top of the empty, unfocused field
                GUI.color = SilksongSkin.TextMuted;
                GUI.Label(fieldRect, Loc.T("192.168.1.5:7777 or SK-..."), _placeholderStyle ?? Skin.textField);
                GUI.color = Color.white;
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(10);

            // Connect buttons
            bool canConnect = !isConnecting && !string.IsNullOrWhiteSpace(_inputAddressOrCode);

            GUI.enabled = canConnect;
            if (GUILayout.Button(Loc.T("JOIN  via  IP / Code"), GUILayout.Height(32)))
            {
                JoinUdpSession(_inputAddressOrCode);
            }
            if (GUILayout.Button(Loc.T("JOIN  via  Steam  ID"), GUILayout.Height(32)))
            {
                JoinSteamSession(_inputAddressOrCode);
            }

            GUI.enabled = true;

            // Paste from clipboard button
            GUILayout.Space(4);
            if (GUILayout.Button(Loc.T("Paste from Clipboard"), GUILayout.Height(22)))
            {
                _inputAddressOrCode = GUIUtility.systemCopyBuffer;
                _toasts.ShowInfo(Loc.T("Pasted from clipboard"));
            }
        }

        // ─── STATUS TAB ───────────────────────────────────────────────────────
        private void DrawStatusTab()
        {
            bool isConnected = _sessionState != null && _sessionState.IsConnected;

            if (!isConnected)
            {
                GUILayout.Label(Loc.T("NOT CONNECTED"), _styleTitle ?? Skin.label);
                GUILayout.Space(6);
                GUILayout.Label(Loc.T("Start or join a session to see connection details."), _styleSubtitle ?? Skin.label);
                return;
            }

            // Connection status
            GUILayout.Label(Loc.T("SESSION STATUS"), _styleTitle ?? Skin.label);
            GUILayout.Space(4);

            // Status indicator
            GUILayout.BeginHorizontal();
            DrawStatusDot();
            GUILayout.Label(GetStatusDisplay(), GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            // Role
            if (_sessionState == null) return;
            string role = _sessionState.Role == SessionRole.Host ? Loc.T("Player 1 (Host)") : Loc.T("Player 2 (Client)");
            GUILayout.Label(Loc.T("<b>Role:</b>  {0}", role));

            // Peer
            string peerName = _sessionState.RemotePeerId?.ToString() ?? Loc.T("Unknown");
            GUILayout.Label(Loc.T("<b>Partner:</b>  {0}", peerName));

            // Scene presence is the gate for every position-based sync path; showing it
            // here makes "partner is invisible / hits do nothing" immediately diagnosable.
            var peerId = _sessionState.RemotePeerId;
            bool sameScene = peerId.HasValue &&
                             SilksongCoopPlugin.Instance?.CloneSpawner?.IsRemoteInSameScene(peerId.Value) == true;
            GUILayout.Label(sameScene
                ? Loc.T("<b>Partner scene:</b>  same room")
                : Loc.T("<b>Partner scene:</b>  other room / loading"));

            bool partnerAlive = SilksongCoopPlugin.Instance?.DeathCoordinator?.IsPartnerAlive ?? true;
            GUILayout.Label(partnerAlive
                ? Loc.T("<b>Partner state:</b>  alive")
                : Loc.T("<b>Partner state:</b>  dead / spectating"));

            // The peer registry becomes meaningful when a multi-peer transport lands.
            // Keep the single-player Status tab unchanged and only list extra peers.
            var peerRecords = _sessionState.Peers.Peers;
            if (peerRecords.Count > 1)
            {
                GUILayout.Space(4);
                GUILayout.Label(Loc.T("<b>Connected peers:</b>  {0}", peerRecords.Count));
                for (int i = 0; i < peerRecords.Count; i++)
                {
                    var peerRecord = peerRecords[i];
                    string state = peerRecord.State switch
                    {
                        PeerState.Connecting => Loc.T("Connecting"),
                        PeerState.Authenticated => Loc.T("Authenticated"),
                        _ => Loc.T("Ready")
                    };
                    bool peerAlive =
                        SilksongCoopPlugin.Instance?.DeathCoordinator?.IsRemotePeerAlive(peerRecord.Peer) ?? true;
                    string suffix = peerAlive ? string.Empty : $" — {Loc.T("DEAD")}";
                    GUILayout.Label($"  {peerRecord.Peer.DisplayName} — {state}{suffix}");
                }
            }

            if (peerId.HasValue &&
                SilksongCoopPlugin.Instance?.CloneSpawner != null &&
                SilksongCoopPlugin.Instance.CloneSpawner.TryGetInterpolationDelayMs(peerId.Value, out float interpDelay))
            {
                GUILayout.Label(Loc.T("<b>Interpolation:</b>  {0:F0} ms behind", interpDelay));
            }

            // Session duration
            float sessionTime = _diagnostics.SessionDuration;
            if (sessionTime > 0)
            {
                GUILayout.Label(Loc.T("<b>Session:</b>  {0}", FormatSessionTime(sessionTime)));
            }

            GUILayout.Space(8);
            SilksongSkin.DrawDivider(1f, 2f, 4f);

            // Network quality
            GUILayout.Label(Loc.T("NETWORK"), _styleTitle ?? Skin.label);
            GUILayout.Space(4);

            int rtt = _sessionState.RoundTripTimeMs;
            string rttColor = rtt <= 0 ? "#998877" : rtt < 50 ? "#66DD66" : rtt < 150 ? "#DDDD66" : "#DD6666";
            string rttText = rtt > 0 ? $"{rtt} ms" : Loc.T("n/a");

            GUILayout.BeginHorizontal();
            GUILayout.Label(Loc.T("<b>Ping:</b>  <color={0}>{1}</color>", rttColor, rttText), GUILayout.Width(180));
            DrawPingBars(rtt);
            GUILayout.EndHorizontal();

            float tickHz = SilksongCoopPlugin.Instance?.CurrentTickHz ?? 0f;
            if (tickHz > 0f)
            {
                GUILayout.Label(Loc.T("<b>Tick rate:</b>  {0:F0} Hz (adaptive)", tickHz));
            }

            // Packet stats
            long sent = _sessionState.SentPackets;
            long recv = _sessionState.ReceivedPackets;
            GUILayout.Label(Loc.T("<b>Packets:</b>  ↑ {0}  ↓ {1}", sent, recv));
            if (_diagnostics.RejectedPackets > 0)
            {
                GUILayout.Label(Loc.T("<b>Rejected:</b>  <color=#DD6666>{0}</color> (malformed or wrong peer)", _diagnostics.RejectedPackets));
            }

            // Bandwidth
            long bwOut = _diagnostics.BytesOutPerSecond;
            long bwIn = _diagnostics.BytesInPerSecond;
            GUILayout.Label(Loc.T("<b>Bandwidth:</b>  ↑ {0}/s  ↓ {1}/s", FormatBandwidth(bwOut), FormatBandwidth(bwIn)));

            // Packet loss
            float loss = _diagnostics.PacketLossPercent;
            if (loss > 0.1f)
            {
                string lossColor = loss < 2f ? "#DDDD66" : "#DD6666";
                GUILayout.Label(Loc.T("<b>Packet Loss:</b>  <color={0}>{1:F1}%</color>", lossColor, loss));
            }

            GUILayout.Space(8);
            SilksongSkin.DrawDivider(1f, 2f, 4f);

            // Host address (if hosting)
            if (!string.IsNullOrEmpty(_currentRoomCode))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("<b>Room Code:</b>  {0}", _currentRoomCode), GUILayout.ExpandWidth(true));
                DrawCopyButton(_currentRoomCode, Loc.T("Copy"));
                GUILayout.EndHorizontal();
            }

            if (!string.IsNullOrEmpty(_currentHostAddress))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("<b>Your Address:</b>  {0}", _currentHostAddress), GUILayout.ExpandWidth(true));
                DrawCopyButton(_currentHostAddress, Loc.T("Copy"));
                GUILayout.EndHorizontal();
            }

            // BUG-N11: hosting switches the active tab to STATUS, so the Steam invite button
            // (only drawn on the HOST tab) became unreachable right after the lobby was
            // created — the toast told the user to "use Invite Friends" but there was no
            // button on screen. Surface lobby sharing on the status tab too.
            if (_sessionState.SteamLobbyId.HasValue && _steamTransport != null)
            {
                GUILayout.Space(4);
                GUILayout.BeginHorizontal();
                GUILayout.Label(Loc.T("<b>Steam Lobby:</b>  {0}", _sessionState.SteamLobbyId.Value), GUILayout.ExpandWidth(true));
                DrawCopyButton(_sessionState.SteamLobbyId.Value.ToString(), Loc.T("Copy"));
                GUILayout.EndHorizontal();

                if (_sessionState.Role == SessionRole.Host)
                {
                    if (GUILayout.Button(Loc.T("Invite Steam Friends..."), GUILayout.Height(24)))
                    {
                        _steamTransport.OpenInviteDialog();
                        _toasts.ShowInfo(Loc.T("Steam invite dialog opened"));
                    }
                }
            }

            GUILayout.Space(12);
            DrawDisconnectButton();
        }

        // ─── SETTINGS TAB ─────────────────────────────────────────────────────
        private void DrawSettingsTab()
        {
            GUILayout.Label(Loc.T("SETTINGS"), _styleTitle ?? Skin.label);
            GUILayout.Space(6);

            var plugin = SilksongCoopPlugin.Instance;
            if (plugin == null)
            {
                GUILayout.Label(Loc.T("Plugin not initialized."), _styleSubtitle ?? Skin.label);
                return;
            }

            // Language
            GUILayout.Label(Loc.T("<b>Language</b>"));
            GUILayout.BeginHorizontal();
            DrawLanguageButton(Loc.T("Auto"), "Auto");
            DrawLanguageButton(Loc.T("English"), "English");
            DrawLanguageButton(Loc.T("Russian"), "Russian");
            GUILayout.EndHorizontal();
            GUILayout.Label(Loc.T("Language changes apply immediately."), _styleSubtitle ?? Skin.label);
            GUILayout.Label(
                Loc.T("Active language: {0}", Loc.Current == CoopLanguage.Russian ? "Русский" : "English"),
                _styleSubtitle ?? Skin.label);

            GUILayout.Space(10);

            // Tick rate
            GUILayout.Label(Loc.T("<b>Network Tick Rate</b>"));
            GUILayout.BeginHorizontal();
            int tickRate = plugin.TickRateHzConfig?.Value ?? 30;
            int newTickRate = (int)GUILayout.HorizontalSlider(tickRate, 20, 60, GUILayout.ExpandWidth(true));
            GUILayout.Label(Loc.T("{0} Hz", newTickRate), GUILayout.Width(50));
            GUILayout.EndHorizontal();
            if (newTickRate != tickRate && plugin.TickRateHzConfig != null)
            {
                plugin.TickRateHzConfig.Value = newTickRate;
                _qualityPresets?.MarkAsCustom();
            }
            GUILayout.Label(Loc.T("Higher = smoother but more bandwidth (20-60 Hz)"), _styleSubtitle ?? Skin.label);

            GUILayout.Space(10);

            // UDP port
            GUILayout.Label(Loc.T("<b>Default UDP Port</b>"));
            GUILayout.BeginHorizontal();
            int port = plugin.DefaultUdpPortConfig?.Value ?? 7777;
            if (!_udpPortInputInitialized)
            {
                _udpPortInput = port.ToString();
                _udpPortInputInitialized = true;
            }

            // Keep an editable buffer. Re-reading the config into the TextField every
            // frame made the field impossible to type into (issue #1 / #2).
            GUI.SetNextControlName("CoopUdpPort");
            string editedPort = GUILayout.TextField(_udpPortInput, GUILayout.Width(80));
            bool portFocused = GUI.GetNameOfFocusedControl() == "CoopUdpPort";
            if (portFocused)
            {
                _udpPortInput = editedPort;
            }

            if (int.TryParse(editedPort, out int newPort) && newPort >= 1024 && newPort <= 65535)
            {
                if (newPort != port && plugin.DefaultUdpPortConfig != null)
                {
                    plugin.DefaultUdpPortConfig.Value = newPort;
                }
            }
            else if (!portFocused)
            {
                _udpPortInput = port.ToString();
            }

            GUILayout.Label("(1024–65535)", _styleSubtitle ?? Skin.label);
            GUILayout.EndHorizontal();

            GUILayout.Space(10);

            // Player 2 hue shift
            GUILayout.Label(Loc.T("<b>Player 2 Color</b>"));
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
            GUILayout.Label(Loc.T("Shift the hue of Player 2's cloak color"), _styleSubtitle ?? Skin.label);

            GUILayout.Space(10);

            // Dash trail toggle
            GUILayout.Label(Loc.T("<b>Player 2 Dash Trail</b>"));
            bool trailEnabled = plugin.EnablePlayer2DashTrailConfig?.Value ?? true;
            bool newTrail = GUILayout.Toggle(trailEnabled, Loc.T("Enable cyan dash trail for Player 2"));
            if (newTrail != trailEnabled && plugin.EnablePlayer2DashTrailConfig != null)
            {
                plugin.EnablePlayer2DashTrailConfig.Value = newTrail;
                _qualityPresets?.MarkAsCustom();
            }

            GUILayout.Space(10);

            // Revive cooldown
            GUILayout.Label(Loc.T("<b>Revive Cooldown</b>"));
            GUILayout.BeginHorizontal();
            float reviveCd = plugin.ReviveCooldownConfig?.Value ?? 20f;
            float newReviveCd = GUILayout.HorizontalSlider(reviveCd, 15f, 30f, GUILayout.ExpandWidth(true));
            GUILayout.Label(Loc.T("{0:F0}s", newReviveCd), GUILayout.Width(40));
            GUILayout.EndHorizontal();
            if (Math.Abs(newReviveCd - reviveCd) > 0.1f && plugin.ReviveCooldownConfig != null)
            {
                plugin.ReviveCooldownConfig.Value = newReviveCd;
                if (plugin.DeathCoordinator != null)
                {
                    plugin.DeathCoordinator.ReviveCooldownDuration = Math.Clamp(newReviveCd, 15f, 30f);
                }
            }
            GUILayout.Label(Loc.T("Time before solo-death revive (15–30 seconds)"), _styleSubtitle ?? Skin.label);

            GUILayout.Space(16);
            SilksongSkin.DrawDivider(1f, 4f, 4f);

            // Diagnostics bundle: log tail + session/network/config snapshot in one file.
            GUILayout.Label(Loc.T("<b>Diagnostics</b>"));
            if (GUILayout.Button(Loc.T("Save Diagnostics File"), GUILayout.Height(24)))
            {
                try
                {
                    string path = DiagnosticsBundle.Save(plugin);
                    _toasts.ShowSuccess(Loc.T("Diagnostics saved: {0}", path), 4f);
                }
                catch (Exception ex)
                {
                    SilksongCoopPlugin.LogWarn($"[Diagnostics] Failed to save bundle: {ex.Message}");
                    _toasts.ShowError(Loc.T("Could not save diagnostics: {0}", ex.Message), 4f);
                }
            }
            GUILayout.Label(Loc.T("Writes BepInEx/SilksongCoop-diagnostics/ with the log tail, session state and config."),
                _styleSubtitle ?? Skin.label);

            GUILayout.Space(8);
            if (GUILayout.Button(Loc.T("Show the first-run guide again"), GUILayout.Height(22)))
            {
                _showOnboarding = true;
                if (plugin.OnboardingCompletedConfig != null) plugin.OnboardingCompletedConfig.Value = false;
                _toasts.ShowInfo(Loc.T("First-run guide enabled."));
            }

            GUILayout.Space(12);

            // Info
            GUILayout.Label($"<color=#998877>Silksong Co-op Mod v{SilksongCoopPlugin.PluginVersion}</color>", _styleSubtitle ?? Skin.label);
        }

        private void DrawLanguageButton(string label, string configValue)
        {
            var plugin = SilksongCoopPlugin.Instance;
            bool active = string.Equals(plugin?.LanguageConfig?.Value, configValue, StringComparison.OrdinalIgnoreCase);
            var style = (active ? _styleTabActive : _styleTab) ?? Skin.button;
            if (GUILayout.Button(label, style, GUILayout.Height(24)) && plugin?.LanguageConfig != null)
            {
                plugin.LanguageConfig.Value = configValue;
                plugin.ApplyUiLanguage();
            }
        }

        #endregion // Main Window and Tab Drawing

        #region UI Helper Methods

        // ─── Disconnect ───────────────────────────────────────────────────────
        private void DrawDisconnectButton()
        {
            if (_showDisconnectConfirm)
            {
                GUILayout.BeginVertical(Skin.box);
                GUILayout.Label(Loc.T("Are you sure you want to disconnect?"));
                GUILayout.Space(4);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(Loc.T("Disconnect"), GUILayout.Height(26)))
                {
                    _showDisconnectConfirm = false;
                    DisconnectSession();
                }
                if (GUILayout.Button(Loc.T("Cancel"), GUILayout.Height(26)))
                {
                    _showDisconnectConfirm = false;
                }
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }
            else
            {
                GUI.color = new Color(0.9f, 0.35f, 0.35f, 1f);
                if (GUILayout.Button(Loc.T("DISCONNECT"), GUILayout.Height(30)))
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
            int bars = rtt <= 0 ? 0 : rtt < 30 ? 5 : rtt < 60 ? 4 : rtt < 100 ? 3 : rtt < 150 ? 2 : 1;
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
            if (GUILayout.Button(isFlashing ? Loc.T("✓ Copied") : label, GUILayout.Width(90)))
            {
                CopyToClipboard(text);
            }
            if (isFlashing)
            {
                GUI.backgroundColor = Color.white;
            }
        }

        /// <summary>
        /// Copies <paramref name="text"/> and shows the shared success toast. Used by both
        /// the compact copy button and the large room-code block.
        /// </summary>
        private void CopyToClipboard(string text)
        {
            GUIUtility.systemCopyBuffer = text;
            _copyFlashUntil = Time.unscaledTime + 1.5f;
            _copyFlashText = text;
            _toasts.ShowSuccess(Loc.T("Copied to clipboard!"));
        }

        private string GetStatusDisplay()
        {
            if (_sessionState == null) return $"<color=#998877>{Loc.T("Uninitialized")}</color>";
            return _sessionState.Status switch
            {
                SessionStatus.Disconnected => $"<color=#DD5555>{Loc.T("Disconnected")}</color>",
                SessionStatus.Hosting => $"<color=#D4A843>{Loc.T("Hosting (Waiting...)")}</color>",
                SessionStatus.Connecting => $"<color=#66AAEE>{Loc.T("Connecting...")}</color>",
                SessionStatus.Connected => $"<color=#66DD66>{Loc.T("Connected")}</color>",
                SessionStatus.Spectating => $"<color=#DDAA33>{Loc.T("Spectating")}</color>",
                SessionStatus.TeamWipe => $"<color=#DD3333>{Loc.T("Team Wipe")}</color>",
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

        #endregion // UI Helper Methods

        #region Session Connection Actions

        // ─── Session actions ──────────────────────────────────────────────────

        private void HostUdpSession()
        {
            try
            {
                var plugin = SilksongCoopPlugin.Instance;
                if (plugin == null)
                {
                    _toasts.ShowError(Loc.T("Plugin not initialized"));
                    return;
                }

                plugin.CancelReconnect();
                _udpTransport = plugin.UdpTransport;
                if (_udpTransport == null)
                {
                    _toasts.ShowError(Loc.T("UDP transport not available"));
                    return;
                }

                // A manually edited config can contain 0/privileged/out-of-range values.
                // StartHost(0) would bind an ephemeral port while the code advertised port 0.
                int udpPort = Math.Clamp(plugin.DefaultUdpPortConfig?.Value ?? 7777, 1024, 65535);
                // Issue #1.1: the 12-character code is a deterministic encoding of IP:port,
                // so it cannot serve as a shared secret. Append 80 random bits; the host
                // HMACs the challenge against the full string and direct IP joins fail.
                string code = RoomCodeCodec.EncodeWithSecret(GetLocalIpAddress(), udpPort);

                // BUG-B2: arm the HMAC challenge-response — without SetRoomCode the host
                // silently ran in unauthenticated mode and the room code was never verified.
                // Issue #1.1: also REQUIRE the code. The code is the shared secret; direct
                // IP joins that do not know it are rejected instead of silently accepted.
                _udpTransport.SetRoomCode(code);
                _udpTransport.RequireRoomCode = true;
                // Host and client must not share the same peer id: OriginPeerId in world
                // packets is compared per origin, and two sides using id 1 made every
                // revision look like it came from the same source.
                _udpTransport.SetLocalPeerId(1, "Host");
                plugin.SetActiveTransport(_udpTransport);
                plugin.PrepareForNewSession();
                _udpTransport.StartHost(udpPort);

                _currentRoomCode = code;
                _currentHostAddress = $"{GetLocalIpAddress()}:{udpPort}";

                _sessionState?.SetHosting(_udpTransport.LocalPeerId, code);

                // Copy the room code (not just IP:port) — it is what the guest needs and
                // it was previously never shown anywhere in the UI.
                _diagnostics.OnSessionStarted();
                _toasts.ShowSuccess(Loc.T("UDP room is ready."));
                _activeTab = Tab.Status;
            }
            catch (Exception ex)
            {
                _toasts.ShowError(Loc.T("Failed to host UDP: {0}", ex.Message));
                SilksongCoopPlugin.LogWarn($"[Coop] HostUdpSession error: {ex.Message}");
            }
        }

        private string GetLocalIpAddress()
        {
            // 1. Ask the OS which source address it would use for an outbound packet.
            //    A UDP "connect" sends nothing; it only performs a route lookup. This
            //    picks the adapter that actually has a route, instead of whichever
            //    address Dns.GetHostEntry happens to list first (Docker/WSL/Hyper-V
            //    virtual adapters used to win and produced an unreachable room code).
            try
            {
                using var probe = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.InterNetwork,
                    System.Net.Sockets.SocketType.Dgram,
                    System.Net.Sockets.ProtocolType.Udp);
                probe.Connect(System.Net.IPAddress.Parse("8.8.8.8"), 65530);
                if (probe.LocalEndPoint is System.Net.IPEndPoint localEp &&
                    localEp.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                    !System.Net.IPAddress.IsLoopback(localEp.Address) &&
                    !localEp.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                {
                    return localEp.Address.ToString();
                }
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"Route-based local IP detection failed: {ex.Message}");
            }

            // 2. Fallback: enumerate interfaces, prefer a private LAN address on an
            //    operational interface that has a gateway.
            try
            {
                string? fallback = null;
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                        continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback ||
                        ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel)
                        continue;

                    var props = ni.GetIPProperties();
                    bool hasGateway = false;
                    foreach (var gateway in props.GatewayAddresses)
                    {
                        if (gateway.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                            !gateway.Address.Equals(System.Net.IPAddress.Any))
                        {
                            hasGateway = true;
                            break;
                        }
                    }

                    foreach (var unicast in props.UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                            continue;

                        string s = unicast.Address.ToString();
                        if (s == "127.0.0.1") continue;

                        if (hasGateway && IsPrivateLanIp(s))
                            return s;

                        fallback ??= s;
                    }
                }

                if (fallback != null) return fallback;
            }
            catch (Exception ex)
            {
                SilksongCoopPlugin.LogWarn($"Interface-based local IP detection failed: {ex.Message}");
            }

            // 3. Last resort: the old DNS path.
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
                SilksongCoopPlugin.LogWarn($"Failed to detect local IP: {ex.Message}");
            }
            return "127.0.0.1";
        }

        /// <summary>
        /// Steam lobby IDs are 64-bit integers. Room codes always start with "SK-" (and
        /// contain letters when pasted without the prefix), while IP:port targets contain
        /// a dot or colon, so a numeric target of realistic lobby length is a Steam ID.
        /// </summary>
        private static bool IsSteamLobbyTarget(string target)
        {
            return !string.IsNullOrWhiteSpace(target) &&
                   target.Length >= 10 &&
                   ulong.TryParse(target, out ulong lobbyId) &&
                   lobbyId != 0;
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
            // CGNAT range 100.64.0.0/10 (RFC 6598) is commonly used by carriers and VPNs.
            if (ip.StartsWith("100."))
            {
                var parts = ip.Split('.');
                if (parts.Length >= 2 && int.TryParse(parts[1], out int octet))
                    return octet >= 64 && octet <= 127;
            }
            return false;
        }

        private void HostSteamSession()
        {
            try
            {
                var plugin = SilksongCoopPlugin.Instance;
                if (plugin == null) { _toasts.ShowError(Loc.T("Plugin not initialized")); return; }

                plugin.CancelReconnect();
                _steamTransport = plugin.SteamTransport;
                if (_steamTransport == null)
                {
                    _toasts.ShowError(Loc.T("Steam transport not available. Ensure Steam is running."));
                    return;
                }
                if (!SteamP2PTransport.IsSteamAvailable)
                {
                    // Check before switching transports: a failed StartHost would
                    // otherwise have already torn down the current UDP session.
                    _toasts.ShowError(Loc.T("Steam is offline. Start Steam and try again."));
                    return;
                }

                plugin.SetActiveTransport(_steamTransport);
                plugin.PrepareForNewSession();
                _steamTransport.StartHost();
                _sessionState?.SetHosting(_steamTransport.LocalPeerId, "Steam-Lobby");
                _currentRoomCode = "";
                _currentHostAddress = "";

                _diagnostics.OnSessionStarted();
                _toasts.ShowSuccess(Loc.T("Steam lobby created — use Invite Friends"));
                _activeTab = Tab.Status;
            }
            catch (Exception ex)
            {
                _toasts.ShowError(Loc.T("Steam host failed: {0}", ex.Message));
                SilksongCoopPlugin.LogWarn($"[Coop] HostSteamSession error: {ex.Message}");
            }
        }

        private void JoinUdpSession(string addressOrCode)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(addressOrCode))
                {
                    _toasts.ShowWarning(Loc.T("Please enter an IP:port or Room Code"));
                    return;
                }

                var plugin = SilksongCoopPlugin.Instance;
                if (plugin == null) { _toasts.ShowError(Loc.T("Plugin not initialized")); return; }

                plugin.CancelReconnect();
                _udpTransport = plugin.UdpTransport;
                if (_udpTransport == null) { _toasts.ShowError(Loc.T("UDP transport not available")); return; }

                string connectAddress = addressOrCode.Trim();
                // BUG-U2: a valid room code may be pasted WITHOUT its "SK-" prefix —
                // the codec normalizes and decodes it anyway. Only fall back to appending
                // the default port when the input is NOT a decodable room code; appending
                // a ":7777" suffix to a bare 12-char code broke Base32 decoding.
                if (!RoomCodeCodec.TryDecode(connectAddress, out _, out _))
                {
                    if (!connectAddress.Contains(":"))
                    {
                        int defaultPort = Math.Clamp(plugin.DefaultUdpPortConfig?.Value ?? 7777, 1024, 65535);
                        connectAddress = $"{connectAddress}:{defaultPort}";
                    }
                }

                _udpTransport.SetLocalPeerId(2, "Client");
                plugin.SetActiveTransport(_udpTransport);
                plugin.PrepareForNewSession();
                _udpTransport.Connect(connectAddress);
                _history.Add(connectAddress);
                _currentRoomCode = "";
                _currentHostAddress = "";

                _sessionState?.SetConnecting(_udpTransport.LocalPeerId, connectAddress);
                _toasts.ShowInfo(Loc.T("Connecting..."));
                _activeTab = Tab.Status;
            }
            catch (Exception ex)
            {
                _toasts.ShowError(Loc.T("Join error: {0}", ex.Message));
                SilksongCoopPlugin.LogWarn($"[Coop] JoinUdpSession error: {ex.Message}");
            }
        }

        private void JoinSteamSession(string lobbyIdString)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(lobbyIdString))
                {
                    _toasts.ShowWarning(Loc.T("Please enter a Steam Lobby ID"));
                    return;
                }

                var plugin = SilksongCoopPlugin.Instance;
                if (plugin == null) { _toasts.ShowError(Loc.T("Plugin not initialized")); return; }

                plugin.CancelReconnect();
                _steamTransport = plugin.SteamTransport;
                if (_steamTransport == null) { _toasts.ShowError(Loc.T("Steam transport not available")); return; }
                if (!SteamP2PTransport.IsSteamAvailable)
                {
                    _toasts.ShowError(Loc.T("Steam is offline. Start Steam and try again."));
                    return;
                }

                string trimmed = lobbyIdString.Trim();
                plugin.SetActiveTransport(_steamTransport);
                plugin.PrepareForNewSession();
                _steamTransport.Connect(trimmed);
                _history.Add(trimmed);

                if (ulong.TryParse(trimmed, out ulong parsedLobby) && _sessionState != null)
                {
                    _sessionState.SteamLobbyId = parsedLobby;
                }

                _sessionState?.SetConnecting(_steamTransport.LocalPeerId, trimmed);
                _toasts.ShowInfo(Loc.T("Joining Steam Lobby {0}...", trimmed));
                _activeTab = Tab.Status;
            }
            catch (Exception ex)
            {
                _toasts.ShowError(Loc.T("Steam join error: {0}", ex.Message));
                SilksongCoopPlugin.LogWarn($"[Coop] JoinSteamSession error: {ex.Message}");
            }
        }

        private void DisconnectSession()
        {
            try
            {
                var plugin = SilksongCoopPlugin.Instance;
                if (plugin != null)
                {
                    // Full gameplay teardown: the UDP disconnect event is delivered on a
                    // later frame and would be ignored after SetDisconnected, leaving the
                    // clone, death state and spectator mode behind.
                    plugin.DisconnectFromSession(DisconnectReason.GracefulDisconnect);
                    plugin.CancelReconnect();
                    if (plugin.UdpTransport != null)
                    {
                        plugin.SetActiveTransport(plugin.UdpTransport);
                    }
                }
                else
                {
                    _currentTransport?.Disconnect();
                    _sessionState?.SetDisconnected(DisconnectReason.GracefulDisconnect);
                }

                _currentHostAddress = "";
                _currentRoomCode = "";
                _diagnostics.OnSessionEnded();
                _toasts.ShowInfo(Loc.T("Disconnected"));
                _activeTab = Tab.Host;
            }
            catch (Exception ex)
            {
                _toasts.ShowError(Loc.T("Disconnect error: {0}", ex.Message));
                SilksongCoopPlugin.LogWarn($"[Coop] DisconnectSession error: {ex.Message}");
            }
        }

        #endregion // Session Connection Actions

        // Backing field for disconnect convenience
        private INetworkTransport? _currentTransport
        {
            get => SilksongCoopPlugin.Instance?.ActiveTransport;
        }
    }
}
