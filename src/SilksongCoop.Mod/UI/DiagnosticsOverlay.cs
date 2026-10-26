using System.Collections.Generic;
using UnityEngine;

namespace SilksongCoop.Mod.UI
{
    /// <summary>
    /// Overlay for rendering live network diagnostics graphs (ping, packet loss, bandwidth).
    /// Toggled with F11. Renders in the top-right corner of the screen.
    /// </summary>
    public class DiagnosticsOverlay
    {
        private const float GraphWidth = 280f;
        private const float GraphHeight = 70f;
        private const float Padding = 10f;
        private const float RowSpacing = 8f;
        private const float TotalWidth = GraphWidth + Padding * 2;
        private const float TotalHeight = 20f + (GraphHeight + RowSpacing) * 3 + 24f + Padding * 2;

        private readonly NetworkDiagnostics _diagnostics;
        private bool _isVisible;

        // Cached 1x1 textures for drawing solid-color backgrounds/lines.
        // Created once on first use to avoid per-frame Texture2D allocation.
        private static Texture2D? _whiteTex;
        private static Texture2D? _bgTex;
        private static Texture2D? _gridTex;

        public bool IsVisible
        {
            get => _isVisible;
            set => _isVisible = value;
        }

        public DiagnosticsOverlay(NetworkDiagnostics diagnostics)
        {
            _diagnostics = diagnostics;
        }

        private static Texture2D GetSolidTexture(Color color)
        {
            var tex = new Texture2D(1, 1, TextureFormat.ARGB32, false);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            tex.hideFlags = HideFlags.DontSave;
            return tex;
        }

        private static void EnsureTextures()
        {
            if (_whiteTex == null) _whiteTex = GetSolidTexture(Color.white);
            if (_bgTex == null) _bgTex = GetSolidTexture(new Color(0f, 0f, 0f, 0.75f));
            if (_gridTex == null) _gridTex = GetSolidTexture(new Color(1f, 1f, 1f, 0.15f));
        }

        public void OnGUI()
        {
            if (!_isVisible) return;

            EnsureTextures();
            if (_bgTex == null || _whiteTex == null) return;

            // Save GUI state so we don't corrupt other rendering
            var savedColor = GUI.color;
            var savedBgColor = GUI.backgroundColor;
            var savedMatrix = GUI.matrix;

            try
            {
                float x = Screen.width - TotalWidth - Padding;
                float y = Padding;

                // Dark semi-transparent background panel
                GUI.DrawTexture(new Rect(x, y, TotalWidth, TotalHeight), _bgTex);

                // Title
                var titleStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 13,
                    fontStyle = FontStyle.Bold,
                    normal = { textColor = new Color(0.95f, 0.85f, 0.55f) }
                };
                GUI.Label(new Rect(x + Padding, y + 6, TotalWidth - Padding * 2, 18),
                    "Network Diagnostics [F11]", titleStyle);

                float cursorY = y + 24f;

                // Ping graph (yellow)
                DrawGraph("Ping", cursorY, x + Padding,
                    _diagnostics.GetPingHistory(),
                    _diagnostics.CurrentPing, "ms",
                    0f, 200f, new Color(1f, 0.9f, 0.3f));
                cursorY += GraphHeight + RowSpacing;

                // Packet loss graph (red)
                DrawGraph("Loss", cursorY, x + Padding,
                    _diagnostics.GetPacketLossHistory(),
                    _diagnostics.PacketLossPercent, "%",
                    0f, 20f, new Color(1f, 0.4f, 0.4f));
                cursorY += GraphHeight + RowSpacing;

                // Bandwidth graph (cyan) — convert bytes to KB
                DrawGraph("BW", cursorY, x + Padding,
                    ScaleToKB(_diagnostics.GetBandwidthHistory()),
                    (_diagnostics.BytesOutPerSecond + _diagnostics.BytesInPerSecond) / 1024f, "KB/s",
                    0f, 100f, new Color(0.4f, 0.9f, 1f));
                cursorY += GraphHeight + RowSpacing;

                // Summary line
                var summaryStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 11,
                    normal = { textColor = Color.white }
                };
                GUI.Label(new Rect(x + Padding, cursorY, TotalWidth - Padding * 2, 20),
                    $"Session: {_diagnostics.SessionDuration:F0}s  |  Pkt out: {_diagnostics.PacketsOutPerSecond}/s  in: {_diagnostics.PacketsInPerSecond}/s",
                    summaryStyle);
            }
            finally
            {
                // Restore GUI state
                GUI.color = savedColor;
                GUI.backgroundColor = savedBgColor;
                GUI.matrix = savedMatrix;
            }
        }

        private void DrawGraph(string label, float y, float x,
            IEnumerable<float> data, float currentValue, string unit,
            float minValue, float maxValue, Color lineColor)
        {
            if (_whiteTex == null || _gridTex == null) return;

            // Row label + current value
            var labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                normal = { textColor = lineColor }
            };
            GUI.Label(new Rect(x, y - 2, 40, 14), label, labelStyle);

            var valueStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                alignment = TextAnchor.UpperRight,
                normal = { textColor = Color.white }
            };
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
            if (data != null)
            {
                var savedMatrix = GUI.matrix;
                var points = new List<Vector2>();
                int index = 0;
                foreach (float value in data)
                {
                    float clamped = Mathf.Clamp(value, minValue, maxValue);
                    float normalized = Mathf.InverseLerp(minValue, maxValue, clamped);
                    float px = x + (index / 60f) * GraphWidth;
                    float py = graphY + graphHeight - normalized * graphHeight;
                    points.Add(new Vector2(px, py));
                    index++;
                    if (index > 60) break;
                }

                if (points.Count > 1)
                {
                    // Draw connected line segments using DrawTexture with small rectangles.
                    // Avoids GUIUtility.RotateAroundPivot which corrupts GUI state.
                    GUI.color = lineColor;
                    for (int i = 0; i < points.Count - 1; i++)
                    {
                        DrawLineSegment(points[i], points[i + 1], 2f);
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

        private static IEnumerable<float> ScaleToKB(IEnumerable<float> bytesPerSecond)
        {
            foreach (float bps in bytesPerSecond)
                yield return bps / 1024f;
        }
    }
}
