using System;
using SilksongCoop.Core.Localization;
using UnityEngine;

namespace SilksongCoop.Mod.UI
{
    /// <summary>
    /// Partner state sampled for the diagnostics overlay. A struct instead of a tuple
    /// keeps the overlay free of Unity/BepInEx type dependencies and avoids allocating
    /// on every OnGUI pass.
    /// </summary>
    public struct PeerDiagnosticsSnapshot
    {
        public bool Connected;
        public bool SameScene;
        public bool PartnerAlive;
        public bool HasInterpolationDelay;
        public float InterpolationDelayMs;
    }

    /// <summary>
    /// Overlay for rendering live network diagnostics graphs (ping, packet loss, bandwidth).
    /// Toggled with F11. Renders in the top-right corner of the screen.
    /// </summary>
    public class DiagnosticsOverlay
    {
        #region Constants

        private const float GraphWidth = 280f;
        private const float GraphHeight = 70f;
        private const float Padding = 10f;
        private const float RowSpacing = 8f;
        private const float TotalWidth = GraphWidth + Padding * 2;

        // Three graphs, a summary line and a partner/scene line.
        private const float TotalHeight = 20f + (GraphHeight + RowSpacing) * 3 + 44f + Padding * 2;

        // One point per position across the 60-sample history.
        private const int MaxGraphSamples = 61;

        #endregion

        #region Fields

        private readonly NetworkDiagnostics _diagnostics;
        private readonly Func<float>? _tickRateProvider;
        private readonly Func<PeerDiagnosticsSnapshot>? _peerProvider;
        private bool _isVisible;

        // Cached 1x1 textures for drawing solid-color backgrounds/lines.
        // Created once on first use to avoid per-frame Texture2D allocation.
        private static Texture2D? _whiteTex;
        private static Texture2D? _bgTex;
        private static Texture2D? _gridTex;

        // P2: cached GUIStyles (created once — OnGUI runs several times per frame)
        private GUIStyle? _titleStyle;
        private GUIStyle? _summaryStyle;
        private GUIStyle? _labelStyle;
        private GUIStyle? _valueStyle;

        // Reusable graph buffers; no List<Vector2> or iterator allocations per pass.
        private readonly Vector2[] _graphPoints = new Vector2[MaxGraphSamples];
        private readonly float[] _historyBuffer = new float[MaxGraphSamples];

        private GUIStyle CreateOverlayStyle(int fontSize, FontStyle style, Color textColor)
        {
            return new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize,
                fontStyle = style,
                normal = { textColor = textColor }
            };
        }

        #endregion

        #region Properties

        public bool IsVisible
        {
            get => _isVisible;
            set => _isVisible = value;
        }

        #endregion

        #region Constructors

        /// <summary>
        /// Initializes a new instance of the DiagnosticsOverlay class.
        /// </summary>
        /// <param name="diagnostics">The network diagnostics data source.</param>
        /// <param name="tickRateProvider">Optional current tick-rate source (Hz).</param>
        /// <param name="peerProvider">Optional partner scene/interpolation source.</param>
        public DiagnosticsOverlay(
            NetworkDiagnostics diagnostics,
            Func<float>? tickRateProvider = null,
            Func<PeerDiagnosticsSnapshot>? peerProvider = null)
        {
            _diagnostics = diagnostics;
            _tickRateProvider = tickRateProvider;
            _peerProvider = peerProvider;
        }

        #endregion

        #region Static Methods

        private static Texture2D GetSolidTexture(Color color)
        {
            var tex = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            tex.hideFlags = HideFlags.DontSave;
            tex.SetPixel(0, 0, color);
            tex.Apply(false);
            return tex;
        }

        private static void EnsureTextures()
        {
            if (_whiteTex == null) _whiteTex = GetSolidTexture(Color.white);
            if (_bgTex == null) _bgTex = GetSolidTexture(new Color(0f, 0f, 0f, 0.75f));
            if (_gridTex == null) _gridTex = GetSolidTexture(new Color(1f, 1f, 1f, 0.15f));
        }

        /// <summary>
        /// Destroys the static textures; call from the plugin's OnDestroy.
        /// </summary>
        public static void Release()
        {
            DestroyTexture(ref _whiteTex);
            DestroyTexture(ref _bgTex);
            DestroyTexture(ref _gridTex);
        }

        // Instance-level release of cached GUI styles. Called when the overlay is disposed.
        public void ReleaseStyles()
        {
            _titleStyle = null;
            _summaryStyle = null;
            _labelStyle = null;
            _valueStyle = null;
        }

        private static void DestroyTexture(ref Texture2D? texture)
        {
            if (texture != null)
            {
                UnityEngine.Object.Destroy(texture);
                texture = null;
            }
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Renders the diagnostics overlay GUI. Call from OnGUI when visible.
        /// </summary>
        public void OnGUI()
        {
            if (!_isVisible) return;
            if (Event.current.type != EventType.Repaint) return;

            EnsureTextures();
            if (_bgTex == null || _whiteTex == null) return;

            // Save GUI state so we don't corrupt other rendering
            var savedColor = GUI.color;
            var savedBgColor = GUI.backgroundColor;
            var savedMatrix = GUI.matrix;

            try
            {
                // Keep the panel on-screen on unusually narrow displays; prefer the
                // left edge over drawing the title and graphs off-screen.
                float x = Mathf.Max(Padding, Screen.width - TotalWidth - Padding);
                float y = Padding;

                // Dark semi-transparent background panel
                GUI.DrawTexture(new Rect(x, y, TotalWidth, TotalHeight), _bgTex);

                // Title
                // P2: cached styles — the old code allocated 8 new GUIStyle objects on
                // EVERY OnGUI call (2-4 per frame) inside the overlay's draw path.
                var titleStyle = _titleStyle ??= CreateOverlayStyle(13, FontStyle.Bold, new Color(0.95f, 0.85f, 0.55f));
                GUI.Label(new Rect(x + Padding, y + 6, TotalWidth - Padding * 2, 18),
                    Loc.T("Network Diagnostics [F11]"), titleStyle);

                float cursorY = y + 24f;

                // Ping graph (yellow)
                int pingCount = _diagnostics.CopyPingHistory(_historyBuffer);
                DrawGraph(Loc.T("Ping"), cursorY, x + Padding,
                    _historyBuffer, pingCount,
                    _diagnostics.CurrentPing, "ms",
                    0f, 200f, new Color(1f, 0.9f, 0.3f));
                cursorY += GraphHeight + RowSpacing;

                // Packet loss graph (red)
                int lossCount = _diagnostics.CopyPacketLossHistory(_historyBuffer);
                DrawGraph(Loc.T("Loss"), cursorY, x + Padding,
                    _historyBuffer, lossCount,
                    _diagnostics.PacketLossPercent, "%",
                    0f, 20f, new Color(1f, 0.4f, 0.4f));
                cursorY += GraphHeight + RowSpacing;

                // Bandwidth graph (cyan) — convert bytes to KB inside DrawGraph
                int bandwidthCount = _diagnostics.CopyBandwidthHistory(_historyBuffer);
                DrawGraph(Loc.T("BW"), cursorY, x + Padding,
                    _historyBuffer, bandwidthCount,
                    (_diagnostics.BytesOutPerSecond + _diagnostics.BytesInPerSecond) / 1024f, Loc.T("KB/s"),
                    0f, 100f, new Color(0.4f, 0.9f, 1f), 1f / 1024f);
                cursorY += GraphHeight + RowSpacing;

                // Summary line
                var summaryStyle = _summaryStyle ??= CreateOverlayStyle(11, FontStyle.Normal, Color.white);
                string rejected = _diagnostics.RejectedPackets > 0
                    ? Loc.T("  |  Rejected: {0}", _diagnostics.RejectedPackets)
                    : string.Empty;
                string tick = _tickRateProvider != null
                    ? Loc.T("  |  Tick: {0:F0} Hz", _tickRateProvider())
                    : string.Empty;
                GUI.Label(new Rect(x + Padding, cursorY, TotalWidth - Padding * 2, 20),
                    Loc.T("Session: {0:F0}s  |  Pkt out: {1}/s  in: {2}/s{3}{4}",
                        _diagnostics.SessionDuration,
                        _diagnostics.PacketsOutPerSecond,
                        _diagnostics.PacketsInPerSecond,
                        rejected,
                        tick),
                    summaryStyle);

                // Partner state line: scene presence is the gate for position-based sync,
                // so seeing "other room" here explains missing hits without opening F10.
                cursorY += 18f;
                if (_peerProvider != null)
                {
                    var peer = _peerProvider();
                    if (peer.Connected)
                    {
                        string scene = peer.SameScene ? Loc.T("same room") : Loc.T("other room");
                        string state = peer.PartnerAlive ? Loc.T("alive") : Loc.T("dead");
                        string interp = peer.HasInterpolationDelay
                            ? Loc.T("  |  interp {0:F0} ms", peer.InterpolationDelayMs)
                            : string.Empty;

                        GUI.Label(new Rect(x + Padding, cursorY, TotalWidth - Padding * 2, 20),
                            $"{Loc.T("Partner")}: {scene}, {state}{interp}", summaryStyle);
                    }
                }
            }
            finally
            {
                // Restore GUI state
                GUI.color = savedColor;
                GUI.backgroundColor = savedBgColor;
                GUI.matrix = savedMatrix;
            }
        }

        #endregion

        #region Private Methods

        private void DrawGraph(string label, float y, float x,
            float[] data, int count, float currentValue, string unit,
            float minValue, float maxValue, Color lineColor, float dataScale = 1f)
        {
            if (_whiteTex == null || _gridTex == null) return;

            // Row label + current value
            // P2: cached styles; the per-graph color is applied via GUI.color (multiplied
            // into the label tint) instead of a fresh GUIStyle per call.
            var labelStyle = _labelStyle ??= CreateOverlayStyle(11, FontStyle.Normal, Color.white);
            if (_valueStyle == null)
            {
                _valueStyle = CreateOverlayStyle(11, FontStyle.Normal, Color.white);
                _valueStyle.alignment = TextAnchor.UpperRight;
            }
            var valueStyle = _valueStyle;

            GUI.color = lineColor;
            GUI.Label(new Rect(x, y - 2, 60, 14), label, labelStyle);
            GUI.color = Color.white;

            GUI.Label(new Rect(x + GraphWidth - 60, y - 2, 60, 14),
                $"{currentValue:F1}{unit}", valueStyle);

            float graphY = y + 12f;
            float graphHeight = GraphHeight - 12f;

            // Graph background (dark)
            var savedColor = GUI.color;
            GUI.color = new Color(0.05f, 0.05f, 0.1f, 0.9f);
            GUI.DrawTexture(new Rect(x, graphY, GraphWidth, graphHeight), _whiteTex);
            GUI.color = savedColor;

            // Horizontal grid lines
            GUI.DrawTexture(new Rect(x, graphY + graphHeight * 0.5f, GraphWidth, 1), _gridTex);

            // Border
            DrawRectOutline(x, graphY, GraphWidth, graphHeight, new Color(0.6f, 0.6f, 0.6f, 0.8f));

            // Data line
            if (data != null && count > 0)
            {
                var savedMatrix = GUI.matrix;
                int pointCount = Mathf.Min(count, _graphPoints.Length);
                for (int index = 0; index < pointCount; index++)
                {
                    float clamped = Mathf.Clamp(data[index] * dataScale, minValue, maxValue);
                    float normalized = Mathf.InverseLerp(minValue, maxValue, clamped);
                    float px = x + (index / 60f) * GraphWidth;
                    float py = graphY + graphHeight - normalized * graphHeight;
                    _graphPoints[index] = new Vector2(px, py);
                }

                if (pointCount > 1)
                {
                    // Draw connected line segments using DrawTexture with small rectangles.
                    // Avoids GUIUtility.RotateAroundPivot which corrupts GUI state.
                    GUI.color = lineColor;
                    for (int i = 0; i < pointCount - 1; i++)
                    {
                        DrawLineSegment(_graphPoints[i], _graphPoints[i + 1], 2f);
                    }
                    GUI.color = savedColor;
                }

                GUI.matrix = savedMatrix;
            }
        }

        private static void DrawLineSegment(Vector2 a, Vector2 b, float thickness)
        {
            if (_whiteTex == null) return;
            // Use simple axis-aligned rectangles — robust across Unity versions.
            // For steep segments, draw a vertical slice; for shallow, a horizontal slice.
            float dx = b.x - a.x;
            float dy = b.y - a.y;
            if (Mathf.Abs(dx) >= Mathf.Abs(dy))
            {
                // Mostly horizontal
                float minX = Mathf.Min(a.x, b.x);
                float maxX = Mathf.Max(a.x, b.x);
                float midY = (a.y + b.y) * 0.5f;
                GUI.DrawTexture(new Rect(minX, midY - thickness * 0.5f, maxX - minX + 1, thickness), _whiteTex);
            }
            else
            {
                // Mostly vertical
                float minY = Mathf.Min(a.y, b.y);
                float maxY = Mathf.Max(a.y, b.y);
                float midX = (a.x + b.x) * 0.5f;
                GUI.DrawTexture(new Rect(midX - thickness * 0.5f, minY, thickness, maxY - minY + 1), _whiteTex);
            }
        }

        private static void DrawRectOutline(float x, float y, float w, float h, Color color)
        {
            if (_whiteTex == null) return;
            var saved = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(x, y, w, 1), _whiteTex);             // top
            GUI.DrawTexture(new Rect(x, y + h - 1, w, 1), _whiteTex);     // bottom
            GUI.DrawTexture(new Rect(x, y, 1, h), _whiteTex);             // left
            GUI.DrawTexture(new Rect(x + w - 1, y, 1, h), _whiteTex);     // right
            GUI.color = saved;
        }

        #endregion
    }
}
