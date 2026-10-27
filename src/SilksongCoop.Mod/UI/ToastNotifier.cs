using System;
using System.Collections.Generic;
using UnityEngine;

namespace SilksongCoop.Mod.UI
{
    /// <summary>
    /// Non- MonoBehaviour toast notification system that displays small ephemeral messages
    /// in the upper-right corner of the screen. Drawn from any OnGUI pass — call <see cref="Draw"/>
    /// from the owning MonoBehaviour's OnGUI. Toasts slide in, hold, and fade out automatically.
    /// </summary>
    public class ToastNotifier
    {
        #region Nested Types

        /// <summary>
        /// Types of toast notifications.
        /// </summary>
        public enum ToastKind
        {
            Info,
            Success,
            Warning,
            Error,
            Copy
        }

        private struct Toast
        {
            public string Message;
            public ToastKind Kind;
            public float CreatedAt;
            public float HoldDuration;
            public float FadeInDuration;
            public float FadeOutDuration;
        }

        #endregion

        #region Constants and Fields

        private const int MaxVisible = 4;
        private const float ToastWidth = 340f;
        private const float ToastHeight = 34f;
        private const float ToastSpacing = 6f;
        private const float RightMargin = 16f;
        private const float TopMargin = 40f; // Below the watermark

        private readonly LinkedList<Toast> _pending = new LinkedList<Toast>();
        private readonly List<Toast> _active = new List<Toast>(MaxVisible);

        #endregion

        #region Public Methods

        /// <summary>
        /// Enqueue a new toast message. If too many toasts are queued, oldest are dropped.
        /// </summary>
        public void Show(string message, ToastKind kind = ToastKind.Info, float holdSeconds = 2.5f)
        {
            if (string.IsNullOrEmpty(message)) return;

            var toast = new Toast
            {
                Message = message,
                Kind = kind,
                CreatedAt = Time.unscaledTime,
                HoldDuration = holdSeconds,
                FadeInDuration = 0.25f,
                FadeOutDuration = 0.45f,
            };

            _pending.AddLast(toast);

            // Limit queue depth
            while (_pending.Count > 12)
            {
                _pending.RemoveFirst();
            }
        }

        /// <summary>
        /// Shows a success toast message.
        /// </summary>
        /// <param name="message">The message to display.</param>
        /// <param name="holdSeconds">How long to hold the toast (default 2.5s).</param>
        public void ShowSuccess(string message, float holdSeconds = 2.5f) => Show(message, ToastKind.Success, holdSeconds);
        /// <summary>
        /// Shows a warning toast message.
        /// </summary>
        /// <param name="message">The message to display.</param>
        /// <param name="holdSeconds">How long to hold the toast (default 3.5s).</param>
        public void ShowWarning(string message, float holdSeconds = 3.5f) => Show(message, ToastKind.Warning, holdSeconds);
        /// <summary>
        /// Shows an error toast message.
        /// </summary>
        /// <param name="message">The message to display.</param>
        /// <param name="holdSeconds">How long to hold the toast (default 4.0s).</param>
        public void ShowError(string message, float holdSeconds = 4.0f)   => Show(message, ToastKind.Error, holdSeconds);
        /// <summary>
        /// Shows an info toast message.
        /// </summary>
        /// <param name="message">The message to display.</param>
        /// <param name="holdSeconds">How long to hold the toast (default 2.5s).</param>
        public void ShowInfo(string message, float holdSeconds = 2.5f)    => Show(message, ToastKind.Info, holdSeconds);

        /// <summary>
        /// Call this from OnGUI every frame. Draws all active toasts and manages lifecycle.
        /// </summary>
        public void Draw()
        {
            float now = Time.unscaledTime;

            // Promote pending toasts into active slots
            while (_active.Count < MaxVisible && _pending.Count > 0)
            {
                _active.Add(_pending.First!.Value);
                _pending.RemoveFirst();
            }

            // Cull finished toasts
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                var t = _active[i];
                float age = now - t.CreatedAt;
                float total = t.FadeInDuration + t.HoldDuration + t.FadeOutDuration;
                if (age >= total)
                {
                    _active.RemoveAt(i);
                }
            }

            if (_active.Count == 0) return;

            var skin = SilksongSkin.GetSkin();

            // Draw bottom-to-top (newest at the top)
            for (int i = 0; i < _active.Count; i++)
            {
                var t = _active[i];
                float age = now - t.CreatedAt;
                float total = t.FadeInDuration + t.HoldDuration + t.FadeOutDuration;
                float alpha = 1f;

                if (age < t.FadeInDuration)
                    alpha = Mathf.Clamp01(age / t.FadeInDuration);
                else if (age > t.FadeInDuration + t.HoldDuration)
                {
                    float fadeOutProgress = (age - t.FadeInDuration - t.HoldDuration) / t.FadeOutDuration;
                    alpha = 1f - Mathf.Clamp01(fadeOutProgress);
                }

                // Horizontal slide-in
                float slide = 0f;
                if (age < t.FadeInDuration)
                {
                    float progress = age / t.FadeInDuration;
                    slide = (1f - Mathf.SmoothStep(0f, 1f, progress)) * 60f;
                }

                (Color bg, Color border, Color text, string icon) = GetKindColors(t.Kind);
                bg.a *= alpha;
                border.a *= alpha;
                text.a *= alpha;

                // Position: right side, stacked from top
                float x = Screen.width - ToastWidth - RightMargin + slide;
                float y = TopMargin + i * (ToastHeight + ToastSpacing);

                // Background
                var bgRect = new Rect(x, y, ToastWidth, ToastHeight);
                GUI.color = bg;
                GUI.DrawTexture(bgRect, SilksongSkin.WhitePixel);

                // Left accent bar (4px wide)
                var accentRect = new Rect(x, y, 4f, ToastHeight);
                GUI.color = border;
                GUI.DrawTexture(accentRect, SilksongSkin.WhitePixel);

                // Text
                GUI.color = text;
                var style = new GUIStyle(skin.label)
                {
                    fontSize = 12,
                    fontStyle = FontStyle.Normal,
                    alignment = TextAnchor.MiddleLeft,
                    padding = new RectOffset(14, 8, 0, 0),
                    richText = true,
                };
                var textRect = new Rect(x + 4f, y, ToastWidth - 8f, ToastHeight);
                GUI.Label(textRect, $"<b>{icon}</b>  {EscapeRichText(t.Message)}", style);
            }

            GUI.color = Color.white;
        }

        #endregion

        #region Private Methods

        private static (Color bg, Color border, Color text, string icon) GetKindColors(ToastKind kind)
        {
            return kind switch
            {
                ToastKind.Success => (
                    new Color(0.08f, 0.22f, 0.12f, 0.92f),
                    SilksongSkin.GreenOk,
                    new Color(0.80f, 1.00f, 0.85f, 1f),
                    "✓"
                ),
                ToastKind.Warning => (
                    new Color(0.25f, 0.20f, 0.05f, 0.92f),
                    SilksongSkin.YellowWarn,
                    new Color(1.00f, 0.95f, 0.75f, 1f),
                    "⚠"
                ),
                ToastKind.Error => (
                    new Color(0.25f, 0.07f, 0.07f, 0.92f),
                    SilksongSkin.RedError,
                    new Color(1.00f, 0.80f, 0.80f, 1f),
                    "✕"
                ),
                ToastKind.Copy => (
                    new Color(0.10f, 0.15f, 0.25f, 0.92f),
                    SilksongSkin.Gold,
                    SilksongSkin.TextCream,
                    "📋"
                ),
                _ => ( // Info
                    new Color(0.08f, 0.10f, 0.18f, 0.92f),
                    SilksongSkin.BlueInfo,
                    new Color(0.85f, 0.90f, 1.00f, 1f),
                    "ℹ"
                ),
            };
        }

        /// <summary>
        /// Escape rich-text angle brackets in user-supplied message text so they render literally.
        /// </summary>
        private static string EscapeRichText(string s)
        {
            return s.Replace("<", "<\u200B").Replace(">", "\u200B>");
        }

        #endregion
    }
}
