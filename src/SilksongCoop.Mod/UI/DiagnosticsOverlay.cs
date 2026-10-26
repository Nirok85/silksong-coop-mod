using System.Collections.Generic;
using UnityEngine;

namespace SilksongCoop.Mod.UI
{
    /// <summary>
    /// Overlay component for rendering network diagnostics graphs.
    /// Displays 4 live graphs: ping, packet loss, bandwidth (in/out).
    /// </summary>
    public class DiagnosticsOverlay
    {
        private const float GraphWidth = 300f;
        private const float GraphHeight = 80f;
        private const float Padding = 10f;

        private readonly NetworkDiagnostics _diagnostics;
        private bool _isVisible;

        public bool IsVisible
        {
            get => _isVisible;
            set => _isVisible = value;
        }

        public DiagnosticsOverlay(NetworkDiagnostics diagnostics)
        {
            _diagnostics = diagnostics;
        }

        public void OnGUI()
        {
            if (!_isVisible) return;

            float x = Screen.width - GraphWidth - Padding * 2;
            float y = Padding;

            GUI.Box(new Rect(x, y, GraphWidth + Padding * 2, GraphHeight * 4 + Padding * 5), "");

            // Ping graph
            y += Padding;
            DrawGraph("Ping (ms)", x + Padding, y, GraphWidth, GraphHeight, 
                     _diagnostics.GetPingHistory(), 0f, 200f, Color.yellow);

            // Packet loss graph
            y += GraphHeight + Padding;
            DrawGraph("Packet Loss (%)", x + Padding, y, GraphWidth, GraphHeight,
                     _diagnostics.GetPacketLossHistory(), 0f, 10f, Color.red);

            // Bandwidth graph
            y += GraphHeight + Padding;
            DrawGraph("Bandwidth (KB/s)", x + Padding, y, GraphWidth, GraphHeight,
                     ScaleToKB(_diagnostics.GetBandwidthHistory()), 0f, 100f, Color.cyan);

            // Current metrics
            y += GraphHeight + Padding;
            GUIStyle labelStyle = new GUIStyle(GUI.skin.label);
            labelStyle.fontSize = 12;
            labelStyle.normal.textColor = Color.white;

            GUI.Label(new Rect(x + Padding, y, GraphWidth, 20), 
                     $"Ping: {_diagnostics.CurrentPing:F1}ms | Loss: {_diagnostics.PacketLossPercent:F1}%", 
                     labelStyle);
        }

        private void DrawGraph(string title, float x, float y, float width, float height,
                               IEnumerable<float> data, float minValue, float maxValue, Color color)
        {
            // Title
            GUIStyle titleStyle = new GUIStyle(GUI.skin.label);
            titleStyle.fontSize = 11;
            titleStyle.normal.textColor = Color.white;
            GUI.Label(new Rect(x, y - 15, width, 15), title, titleStyle);

            // Background
            GUI.Box(new Rect(x, y, width, height), "");

            // Draw data points
            if (data != null)
            {
                var points = new List<Vector2>();
                int index = 0;
                foreach (float value in data)
                {
                    float normalizedValue = Mathf.InverseLerp(minValue, maxValue, value);
                    float px = x + (index / 60f) * width;
                    float py = y + height - (normalizedValue * height);
                    points.Add(new Vector2(px, py));
                    index++;
                }

                // Draw lines
                if (points.Count > 1)
                {
                    Texture2D lineTex = new Texture2D(1, 1);
                    lineTex.SetPixel(0, 0, color);
                    lineTex.Apply();

                    for (int i = 0; i < points.Count - 1; i++)
                    {
                        DrawLine(points[i], points[i + 1], lineTex, 2f);
                    }

                    Object.Destroy(lineTex);
                }
            }

            // Border
            GUI.color = Color.gray;
            GUI.Box(new Rect(x, y, width, height), "");
            GUI.color = Color.white;
        }

        private void DrawLine(Vector2 start, Vector2 end, Texture2D texture, float thickness)
        {
            Vector2 direction = end - start;
            float length = direction.magnitude;
            float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;

            GUIUtility.RotateAroundPivot(angle, start);
            GUI.DrawTexture(new Rect(start.x, start.y, length, thickness), texture);
            GUIUtility.RotateAroundPivot(-angle, start);
        }

        private IEnumerable<float> ScaleToKB(IEnumerable<float> bytesPerSecond)
        {
            foreach (float bps in bytesPerSecond)
            {
                yield return bps / 1024f;
            }
        }
    }
}
