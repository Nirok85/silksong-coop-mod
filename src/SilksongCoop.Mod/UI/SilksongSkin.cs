using System;
using UnityEngine;

namespace SilksongCoop.Mod.UI
{
    /// <summary>
    /// Silksong-themed GUISkin factory producing a dark-indigo + gold aesthetic matching
    /// the Hollow Knight: Silksong in-game UI. All textures are generated procedurally
    /// (1x1 pixel fills) so no external assets are required.
    /// </summary>
    public static class SilksongSkin
    {
        // ── Palette ────────────────────────────────────────────────────────────
        // Backgrounds (darkest → lightest)
        public static readonly Color BgDeepest   = new Color(0.035f, 0.030f, 0.065f, 0.96f); // #09081A window bg
        public static readonly Color BgPanel     = new Color(0.055f, 0.050f, 0.095f, 0.92f); // #0E0D18 section
        public static readonly Color BgField     = new Color(0.080f, 0.070f, 0.130f, 0.95f); // input field bg
        public static readonly Color BgSubtle    = new Color(0.110f, 0.100f, 0.170f, 0.70f); // hover highlight

        // Accents
        public static readonly Color Gold        = new Color(0.831f, 0.659f, 0.263f, 1.00f); // #D4A843 primary accent
        public static readonly Color GoldBright  = new Color(0.950f, 0.800f, 0.350f, 1.00f); // hover accent
        public static readonly Color GoldDim     = new Color(0.550f, 0.430f, 0.180f, 1.00f); // pressed / disabled

        // Text
        public static readonly Color TextCream   = new Color(0.910f, 0.863f, 0.784f, 1.00f); // #E8DCC8 body text
        public static readonly Color TextMuted   = new Color(0.620f, 0.600f, 0.560f, 1.00f); // captions
        public static readonly Color TextTitle   = new Color(0.970f, 0.830f, 0.400f, 1.00f); // section headers

        // Semantic
        public static readonly Color GreenOk     = new Color(0.400f, 0.850f, 0.450f, 1.00f);
        public static readonly Color YellowWarn  = new Color(0.950f, 0.800f, 0.300f, 1.00f);
        public static readonly Color RedError    = new Color(0.900f, 0.350f, 0.350f, 1.00f);
        public static readonly Color BlueInfo    = new Color(0.400f, 0.700f, 0.950f, 1.00f);

        // ── Cached skin (built once) ───────────────────────────────────────────
        private static GUISkin? _cached;
        private static Texture2D? _texBgDeepest, _texBgPanel, _texBgField, _texBgSubtle;
        private static Texture2D? _texGold, _texGoldBright, _texGoldDim;
        private static Texture2D? _texWhite; // 1x1 pure white for DrawTexture bars

        public static Texture2D WhitePixel => _texWhite ??= MakeTex(Color.white);

        /// <summary>Returns the cached Silksong skin (created on first call).</summary>
        public static GUISkin GetSkin()
        {
            if (_cached != null) return _cached;
            return BuildSkin();
        }

        /// <summary>
        /// Force rebuild the skin (call if DPI / resolution changes dramatically).
        /// </summary>
        public static void Invalidate()
        {
            _cached = null;
        }

        // ── Textures ───────────────────────────────────────────────────────────
        private static Texture2D MakeTex(Color c)
        {
            var t = new Texture2D(1, 1, TextureFormat.ARGB32, false) { hideFlags = HideFlags.DontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        // ── Builder ────────────────────────────────────────────────────────────
        private static GUISkin BuildSkin()
        {
            // Start from default skin as base so missing styles still work.
            var skin = UnityEngine.Object.Instantiate(GUI.skin);
            skin.name = "SilksongCoopSkin";

            // Allocate palette textures
            _texBgDeepest = MakeTex(BgDeepest);
            _texBgPanel   = MakeTex(BgPanel);
            _texBgField   = MakeTex(BgField);
            _texBgSubtle  = MakeTex(BgSubtle);
            _texGold      = MakeTex(Gold);
            _texGoldBright = MakeTex(GoldBright);
            _texGoldDim   = MakeTex(GoldDim);
            _texWhite     = MakeTex(Color.white);

            // ── Font setup (use Unity's default font at various sizes) ─────────
            var font = Font.CreateDynamicFontFromOSFont("Segoe UI", 14);

            // ── WINDOW ──────────────────────────────────────────────────────────
            skin.window = new GUIStyle(skin.window)
            {
                normal = { background = _texBgDeepest, textColor = TextCream },
                hover  = { background = _texBgDeepest, textColor = TextCream },
                active = { background = _texBgDeepest, textColor = TextCream },
                focused = { background = _texBgDeepest, textColor = TextCream },
                font = font,
                fontSize = 14,
                fontStyle = FontStyle.Normal,
                border = new RectOffset(2, 2, 2, 2),
                padding = new RectOffset(12, 12, 28, 12),
                margin = new RectOffset(0, 0, 0, 0),
                alignment = TextAnchor.UpperLeft,
            };

            // ── BOX (section panel) ────────────────────────────────────────────
            skin.box = new GUIStyle(skin.box)
            {
                normal = { background = _texBgPanel, textColor = TextCream },
                font = font,
                fontSize = 13,
                padding = new RectOffset(10, 10, 8, 8),
                margin = new RectOffset(0, 0, 4, 4),
            };

            // ── LABEL ──────────────────────────────────────────────────────────
            skin.label = new GUIStyle(skin.label)
            {
                normal = { textColor = TextCream },
                font = font,
                fontSize = 13,
                richText = true,
                wordWrap = true,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(2, 2, 1, 1),
            };

            // ── BUTTON ─────────────────────────────────────────────────────────
            skin.button = new GUIStyle(skin.button)
            {
                normal      = { background = _texBgField,   textColor = TextCream },
                hover       = { background = _texBgSubtle,  textColor = GoldBright },
                active      = { background = _texGoldDim,   textColor = TextCream },
                focused     = { background = _texBgSubtle,  textColor = GoldBright },
                font = font,
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                border = new RectOffset(3, 3, 3, 3),
                padding = new RectOffset(10, 10, 6, 6),
                contentOffset = new Vector2(0, 0),
                imagePosition = ImagePosition.TextOnly,
            };

            // ── TEXT FIELD ─────────────────────────────────────────────────────
            skin.textField = new GUIStyle(skin.textField)
            {
                normal = { background = _texBgField, textColor = TextCream },
                hover  = { background = _texBgField, textColor = TextCream },
                active = { background = _texBgField, textColor = TextCream },
                focused = { background = _texBgField, textColor = TextCream },
                font = font,
                fontSize = 13,
                fontStyle = FontStyle.Normal,
                padding = new RectOffset(8, 8, 4, 4),
                border = new RectOffset(2, 2, 2, 2),
                richText = false,
            };

            // ── TOGGLE ─────────────────────────────────────────────────────────
            skin.toggle = new GUIStyle(skin.toggle)
            {
                normal = { textColor = TextCream },
                hover  = { textColor = GoldBright },
                active = { textColor = GoldBright },
                focused = { textColor = TextCream },
                font = font,
                fontSize = 13,
                padding = new RectOffset(22, 4, 4, 4),
            };

            // ── HORIZONTAL SLIDER ──────────────────────────────────────────────
            skin.horizontalSlider = new GUIStyle(skin.horizontalSlider)
            {
                normal = { background = _texBgField },
                border = new RectOffset(2, 2, 2, 2),
                fixedHeight = 8,
            };
            skin.horizontalSliderThumb = new GUIStyle(skin.horizontalSliderThumb)
            {
                normal = { background = _texGold },
                hover  = { background = _texGoldBright },
                active = { background = _texGoldBright },
                fixedWidth = 14,
                fixedHeight = 14,
            };

            // ── VERTICAL SCROLLBAR ─────────────────────────────────────────────
            skin.verticalScrollbar = new GUIStyle(skin.verticalScrollbar)
            {
                normal = { background = _texBgField },
                border = new RectOffset(2, 2, 2, 2),
                fixedWidth = 10,
            };
            skin.verticalScrollbarThumb = new GUIStyle(skin.verticalScrollbarThumb)
            {
                normal = { background = _texGoldDim },
                hover  = { background = _texGold },
                active = { background = _texGoldBright },
                fixedWidth = 10,
            };

            // ── Custom named styles ────────────────────────────────────────────

            // "Title" — golden section header
            var titleStyle = new GUIStyle(skin.label)
            {
                normal = { textColor = TextTitle },
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(2, 2, 2, 6),
            };
            titleStyle.name = "SilksongTitle";

            // "Subtitle" — muted caption
            var subtitleStyle = new GUIStyle(skin.label)
            {
                normal = { textColor = TextMuted },
                fontSize = 11,
                fontStyle = FontStyle.Italic,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(4, 2, 0, 4),
            };
            subtitleStyle.name = "SilksongSubtitle";

            // "TabButton" — used for the top tab bar
            var tabStyle = new GUIStyle(skin.button)
            {
                normal      = { background = _texBgPanel,   textColor = TextMuted },
                hover       = { background = _texBgSubtle,  textColor = GoldBright },
                active      = { background = _texGoldDim,   textColor = TextCream },
                focused     = { background = _texBgSubtle,  textColor = GoldBright },
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(12, 12, 5, 5),
                margin = new RectOffset(0, 2, 0, 0),
            };
            tabStyle.name = "SilksongTab";

            // "TabActive" — currently-selected tab
            var tabActiveStyle = new GUIStyle(tabStyle)
            {
                normal      = { background = _texGoldDim,   textColor = TextCream },
                hover       = { background = _texGold,      textColor = TextCream },
            };
            tabActiveStyle.name = "SilksongTabActive";

            // "StatusGood" / "StatusBad" / "StatusWarn"
            var goodStyle = new GUIStyle(skin.label)
            {
                normal = { textColor = GreenOk },
                fontSize = 13,
                fontStyle = FontStyle.Bold,
            };
            goodStyle.name = "SilksongGood";

            var warnStyle = new GUIStyle(skin.label)
            {
                normal = { textColor = YellowWarn },
                fontSize = 13,
                fontStyle = FontStyle.Bold,
            };
            warnStyle.name = "SilksongWarn";

            var badStyle = new GUIStyle(skin.label)
            {
                normal = { textColor = RedError },
                fontSize = 13,
                fontStyle = FontStyle.Bold,
            };
            badStyle.name = "SilksongBad";

            // "Divider" — a 1-pixel high golden line (via a stretched box)
            var dividerStyle = new GUIStyle(skin.box)
            {
                normal = { background = _texGoldDim },
                fixedHeight = 1,
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 6, 6),
            };
            dividerStyle.name = "SilksongDivider";

            // Append custom styles
            var customList = new System.Collections.Generic.List<GUIStyle>(skin.customStyles ?? Array.Empty<GUIStyle>());
            customList.Add(titleStyle);
            customList.Add(subtitleStyle);
            customList.Add(tabStyle);
            customList.Add(tabActiveStyle);
            customList.Add(goodStyle);
            customList.Add(warnStyle);
            customList.Add(badStyle);
            customList.Add(dividerStyle);
            skin.customStyles = customList.ToArray();

            _cached = skin;
            return skin;
        }

        /// <summary>
        /// Retrieve a custom named style from the skin (e.g. "SilksongTitle").
        /// Returns null if the style is not found.
        /// </summary>
        public static GUIStyle? GetCustomStyle(GUISkin skin, string name)
        {
            if (skin?.customStyles == null) return null;
            foreach (var s in skin.customStyles)
            {
                if (s.name == name) return s;
            }
            return null;
        }

        /// <summary>
        /// Draw a 1px-high golden horizontal divider at the current GUILayout position.
        /// </summary>
        public static void DrawDivider(float height = 1f, float marginTop = 6f, float marginBottom = 6f)
        {
            GUILayout.Space(marginTop);
            var rect = GUILayoutUtility.GetRect(0f, 10000f, height, height);
            if (Event.current.type == EventType.Repaint && _texGoldDim != null)
            {
                GUI.DrawTexture(rect, _texGoldDim);
            }
            GUILayout.Space(marginBottom);
        }
    }
}
