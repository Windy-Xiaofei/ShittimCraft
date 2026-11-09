using System;
using UnityEngine;

namespace SchaleMcWorld
{
    /// <summary>
    /// Blue Archive flavoured IMGUI skin (dark navy cards, cyan rules, gold selection frame)
    /// with Minecraft's chunky square slots.
    ///
    /// Drawn ONLY with Unity's built-in whiteTexture - no generated Texture2D at all.
    /// An earlier version built three rounded sheets at runtime and 9-sliced them. Blue
    /// Archive sweeps unowned runtime textures constantly, so those sheets were destroyed
    /// and rebuilt over and over: every rebuild registered new il2cpp GC handles (the pin
    /// table filled up at 2048), threw mid-draw, and cost ~250 IMGUI draw calls per frame.
    /// That is what made the MC UI stutter. Layered solid rectangles look the same and cost
    /// a fraction of that, with nothing to collect.
    /// </summary>
    internal static class UiKit
    {
        // Blue Archive palette
        public static readonly Color Panel = new Color(0.078f, 0.086f, 0.114f, 0.93f);
        public static readonly Color PanelSolid = new Color(0.078f, 0.086f, 0.114f, 1f);
        public static readonly Color Edge = new Color(1f, 1f, 1f, 0.10f);
        public static readonly Color Accent = new Color(0.36f, 0.79f, 1f, 1f);
        public static readonly Color AccentSoft = new Color(0.36f, 0.79f, 1f, 0.35f);
        public static readonly Color Gold = new Color(1f, 0.85f, 0.38f, 1f);
        public static readonly Color Text = new Color(0.96f, 0.97f, 1f, 1f);
        public static readonly Color Muted = new Color(0.60f, 0.66f, 0.75f, 1f);
        public static readonly Color SlotFill = new Color(0.13f, 0.145f, 0.185f, 1f);
        public static readonly Color SlotEdge = new Color(1f, 1f, 1f, 0.07f);
        public static readonly Color Danger = new Color(1f, 0.45f, 0.42f, 1f);

        private static readonly Color ChipFill = new Color(0.10f, 0.11f, 0.15f, 0.94f);
        private static readonly Color Shadow = new Color(0f, 0f, 0f, 0.38f);

        // Kept for the self test / callers. Nothing is generated any more, so these are
        // always ready and there is nothing to invalidate.
        public static bool HasTextures => true;
        public static void EnsureTextures() { }
        public static void Invalidate() { }

        /// <summary>Cheap liveness probe, still used by the GC survival self test.</summary>
        public static bool Alive(Texture2D tex)
        {
            if (tex == null)
            {
                return false;
            }
            try
            {
                return tex.width > 0;
            }
            catch
            {
                return false;
            }
        }

        internal static bool Collected(Exception e)
        {
            if (e == null)
            {
                return false;
            }
            string t = e.GetType().Name ?? "";
            if (t.IndexOf("ObjectCollected", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            string m = e.Message ?? "";
            return m.IndexOf("garbage collected", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ---- primitives ------------------------------------------------------

        /// <summary>One solid rectangle. whiteTexture is engine-owned and never swept.</summary>
        public static void Rect(Rect r, Color c)
        {
            if (r.width <= 0f || r.height <= 0f)
            {
                return;
            }
            var prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = prev;
        }

        /// <summary>A card: shadow, thin edge, dark body, optional cyan top rule. 4 draws.</summary>
        public static void Card(Rect r, bool accentRule)
        {
            Rect(new Rect(r.x - 3f, r.y - 2f, r.width + 6f, r.height + 5f), Shadow);
            Rect(r, Edge);
            Rect(new Rect(r.x + 1f, r.y + 1f, r.width - 2f, r.height - 2f), Panel);
            if (accentRule)
            {
                Rect(new Rect(r.x + 12f, r.y + 2f, Mathf.Min(72f, r.width - 24f), 2f), Accent);
            }
        }

        /// <summary>Item slot: 2 draws normally, 4 when selected (gold frame + inner glow).</summary>
        public static void Slot(Rect r, bool selected, bool hover)
        {
            Color edge = selected ? Gold : hover ? new Color(1f, 1f, 1f, 0.55f) : SlotEdge;
            float t = selected ? 2f : 1f;
            Rect(r, edge);
            Rect(new Rect(r.x + t, r.y + t, r.width - 2f * t, r.height - 2f * t), SlotFill);
            if (selected)
            {
                Rect(new Rect(r.x + 3f, r.y + 3f, r.width - 6f, r.height - 6f),
                    new Color(1f, 0.85f, 0.38f, 0.16f));
                Rect(new Rect(r.x + 3f, r.y + 3f, r.width - 6f, 1f),
                    new Color(1f, 0.85f, 0.38f, 0.55f));
            }
        }

        public static void Icon(Rect r, Texture2D tex)
        {
            if (tex == null)
            {
                return;
            }
            try
            {
                var prev = GUI.color;
                GUI.color = Color.white;
                GUI.DrawTexture(r, tex, ScaleMode.ScaleToFit, true);
                GUI.color = prev;
            }
            catch
            {
                // A swept icon simply misses one frame; the disk cache rebuilds it on the
                // next lookup. Never let a dead texture abort the whole hotbar.
            }
        }

        /// <summary>Small tag: 3 draws + one label.</summary>
        public static void Chip(Rect r, string text, Color accent, GUIStyle style)
        {
            Rect(r, new Color(accent.r, accent.g, accent.b, 0.42f));
            Rect(new Rect(r.x + 1f, r.y + 1f, r.width - 2f, r.height - 2f), ChipFill);
            Rect(new Rect(r.x + 3f, r.y + r.height * 0.5f - 3f, 4f, 6f), accent);
            if (style == null)
            {
                return;
            }
            var prev = style.normal.textColor;
            style.normal.textColor = Text;
            GUI.Label(new Rect(r.x + 12f, r.y, r.width - 16f, r.height), text, style);
            style.normal.textColor = prev;
        }

        public static void Divider(Rect r, Color c)
        {
            Rect(r, c);
        }
    }
}
