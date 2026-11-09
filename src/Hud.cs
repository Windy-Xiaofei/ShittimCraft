using System;
using UnityEngine;

namespace SchaleMcWorld
{
    public partial class McWorldBehaviour
    {
        private static GUIStyle _titleStyle;
        private static GUIStyle _bodyStyle;
        private static GUIStyle _smallStyle;
        private static GUIStyle _tinyStyle;
        private static GUIStyle _chatStyle;
        private static GUIStyle _inputStyle;

        private const float SlotSize = 32f;
        private const float SlotGap = 3f;
        private const int HotbarSlots = 9;
        private const float CardPad = 8f;
        private const float HotbarCardH = SlotSize + CardPad * 2f;

        // Icon lookups go through LoadDiskTexture(), which is throttled. Calling it nine
        // times every frame only produced nulls (and churn) - cache the nine slot icons and
        // refresh them twice a second or immediately when the selection changes.
        private static readonly Texture2D[] _hotbarIcons = new Texture2D[HotbarSlots];
        private static float _nextIconRefresh;
        private static int _lastIconSelection = -1;

        private static void RefreshHotbarIcons()
        {
            if (W.SelectedBlock != _lastIconSelection)
            {
                _lastIconSelection = W.SelectedBlock;
                _nextIconRefresh = 0f;
            }
            if (Time.unscaledTime < _nextIconRefresh)
            {
                return;
            }
            _nextIconRefresh = Time.unscaledTime + 0.5f;
            for (int i = 0; i < HotbarSlots; i++)
            {
                var t = ItemIcon(ItemAt(i));
                if (t != null)
                {
                    _hotbarIcons[i] = t;
                }
            }
        }

        private static void DrawHud()
        {
            // No generated skin sheets any more: UiKit draws entirely with Unity's built-in
            // whiteTexture. Building a 1x1 pixel here used to be the first thing the game's
            // asset sweep destroyed, which restarted the whole rebuild-every-frame loop.
            UiKit.EnsureTextures();
            InitStyles();

            try { DrawNightOverlay(); } catch (Exception e) { HudError("night", e); }
            try { DrawCrosshair(); } catch (Exception e) { HudError("crosshair", e); }
            try { DrawHotbar(); } catch (Exception e) { HudError("hotbar", e); }
            try { DrawModeChips(); } catch (Exception e) { HudError("chips", e); }
            try { DrawChat(); } catch (Exception e) { HudError("chat", e); }
            if (W.InventoryOpen)
            {
                try { DrawInventory(); } catch (Exception e) { HudError("inventory", e); }
            }
        }

        private static void HudError(string part, Exception e)
        {
            // A collected wrapper means a cached GUIStyle died. Rebuild at most once a
            // second - rebuilding every frame was the old stutter/GC-handle leak.
            string type = e.GetType().Name ?? "";
            if ((type.IndexOf("ObjectCollected", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (e.Message != null && e.Message.IndexOf("garbage collected", StringComparison.OrdinalIgnoreCase) >= 0)) &&
                Time.unscaledTime - _lastSkinReset > 1f)
            {
                _lastSkinReset = Time.unscaledTime;
                ResetSkin();
            }

            if (Time.unscaledTime - _lastHudError < 3f)
            {
                return;
            }
            _lastHudError = Time.unscaledTime;
            string where = "";
            try
            {
                string st = e.StackTrace ?? "";
                string[] lines = st.Split('\n');
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < lines.Length && i < 4; i++)
                {
                    sb.Append(' ').Append(lines[i].Trim());
                }
                where = " @" + sb;
            }
            catch { }
            Plugin.L.LogError("[McWorld] hud/" + part + ": " + e.GetType().Name + ": " + e.Message + where);
        }

        private static void InitStyles()
        {
            // A GUIStyle held in a static field is not collected on its own. Probing it with
            // CalcSize() every frame used to be an IL2CPP call per frame and, when it did
            // fail, restarted a rebuild loop. Now we build once and let HudError rebuild
            // (throttled) only if a draw actually throws.
            if (_titleStyle != null)
            {
                return;
            }
            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleLeft,
                richText = true,
            };
            _titleStyle.normal.textColor = UiKit.Text;

            _bodyStyle = new GUIStyle(_titleStyle) { fontSize = 12 };
            _bodyStyle.normal.textColor = UiKit.Text;

            _smallStyle = new GUIStyle(_titleStyle) { fontSize = 11 };
            _smallStyle.normal.textColor = UiKit.Muted;

            _tinyStyle = new GUIStyle(_titleStyle) { fontSize = 9, alignment = TextAnchor.MiddleCenter };
            _tinyStyle.normal.textColor = new Color(1f, 1f, 1f, 0.42f);

            _chatStyle = new GUIStyle(_titleStyle) { fontSize = 12, wordWrap = false };
            _chatStyle.normal.textColor = UiKit.Text;

            _inputStyle = new GUIStyle(_chatStyle) { fontSize = 13 };
            _inputStyle.normal.textColor = new Color(1f, 0.92f, 0.66f, 1f);

            // Deliberately NOT registered with NativeRoot: registering six fresh handles
            // every rebuild is what filled the pin table at 2048 in the previous build.
            _styleBuilds++;
            if (_styleBuilds <= 3)
            {
                Plugin.L.LogInfo("[McWorld] hud styles built (" + _styleBuilds + ")");
            }
        }

        /// <summary>True while CalcSize still works - a collected GUIStyle throws on first use.</summary>
        private static bool StyleAlive(GUIStyle style)
        {
            if (style == null)
            {
                return false;
            }
            try
            {
                style.CalcSize(new GUIContent("A"));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static int _styleBuilds;
        private static float _lastSkinReset;

        /// <summary>Drops the cached styles so they are rebuilt on the next frame.</summary>
        private static void ResetSkin()
        {
            _titleStyle = null;
            _bodyStyle = null;
            _smallStyle = null;
            _tinyStyle = null;
            _chatStyle = null;
            _inputStyle = null;
            UiKit.Invalidate();
        }

        // ---- overlays --------------------------------------------------------

        private static void DrawNightOverlay()
        {
            if (W.NightAlpha <= 0.01f)
            {
                return;
            }
            UiKit.Rect(new Rect(0f, 0f, Screen.width, Screen.height),
                new Color(0.03f, 0.05f, 0.16f, 0.62f * Mathf.Clamp01(W.NightAlpha)));
        }

        private static void DrawCrosshair()
        {
            float ax, ay;
            if (W.UiMode)
            {
                if (!W.FpLook)
                {
                    return; // free pointer: the OS cursor is the pointer
                }
                ax = Screen.width * 0.5f;
                ay = Screen.height * 0.5f;
            }
            else
            {
                ax = Mathf.Clamp(W.AimX, 0f, 1f) * Screen.width;
                ay = (1f - Mathf.Clamp(W.AimY, 0f, 1f)) * Screen.height;
            }

            var shadow = new Color(0f, 0f, 0f, 0.55f);
            var white = new Color(1f, 1f, 1f, 0.92f);
            UiKit.Rect(new Rect(ax - 9f, ay - 1.5f, 18f, 3f), shadow);
            UiKit.Rect(new Rect(ax - 1.5f, ay - 9f, 3f, 18f), shadow);
            UiKit.Rect(new Rect(ax - 8f, ay - 1f, 16f, 2f), white);
            UiKit.Rect(new Rect(ax - 1f, ay - 8f, 2f, 16f), white);
            UiKit.Rect(new Rect(ax - 1f, ay - 1f, 2f, 2f), UiKit.Accent);
        }

        /// <summary>Mode badges, stacked above the hotbar so Blue Archive's own HUD stays clear.</summary>
        private static void DrawModeChips()
        {
            if (!W.Active && !W.UiMode)
            {
                return;
            }
            float y = Screen.height - HotbarCardH - 62f;
            float x = 14f;

            string mode = W.UiMode ? "第一人称" : "斜俯视";
            float w = _smallStyle.CalcSize(new GUIContent(mode)).x + 40f;
            UiKit.Chip(new Rect(x, y, w, 20f), mode, W.UiMode ? UiKit.Accent : UiKit.Gold, _smallStyle);
            x += w + 6f;

            if (W.UiMode)
            {
                string look = W.FpLook ? "视角锁定" : "光标可用";
                float lw = _smallStyle.CalcSize(new GUIContent(look)).x + 40f;
                UiKit.Chip(new Rect(x, y, lw, 20f), look, W.FpLook ? UiKit.Accent : UiKit.Muted, _smallStyle);
                x += lw + 6f;

                if (W.MineMode)
                {
                    float mw = _smallStyle.CalcSize(new GUIContent("破坏")).x + 40f;
                    UiKit.Chip(new Rect(x, y, mw, 20f), "破坏", UiKit.Danger, _smallStyle);
                }
            }
            else if (W.Blocking)
            {
                UiKit.Chip(new Rect(x, y, 66f, 20f), "举盾", UiKit.Accent, _smallStyle);
            }
        }

        // ---- hotbar ----------------------------------------------------------

        /// <summary>
        /// Minecraft's hotbar (nine square slots, gold selection frame) rebuilt with Blue
        /// Archive's card styling, pinned to the bottom-left corner so it never sits on the
        /// students' skill cards or the S / AUTO buttons.
        /// </summary>
        private static void DrawHotbar()
        {
            float hotW = HotbarSlots * SlotSize + (HotbarSlots - 1) * SlotGap;
            float cardW = hotW + CardPad * 2f;
            float cardX = 14f;
            float cardY = Screen.height - HotbarCardH - 14f;
            RefreshHotbarIcons();

            try { UiKit.Card(new Rect(cardX, cardY, cardW, HotbarCardH), true); }
            catch (Exception e) { HudError("hotbar/card", e); return; }

            for (int i = 0; i < HotbarSlots; i++)
            {
                var r = new Rect(cardX + CardPad + i * (SlotSize + SlotGap), cardY + CardPad, SlotSize, SlotSize);
                bool sel = i == Mathf.Clamp(W.SelectedBlock, 0, HotbarSlots - 1);
                try
                {
                    UiKit.Slot(r, sel, false);
                    UiKit.Icon(new Rect(r.x + 5f, r.y + 5f, r.width - 10f, r.height - 10f),
                        i < _hotbarIcons.Length ? _hotbarIcons[i] : null);
                }
                catch (Exception e)
                {
                    HudError("hotbar/slot" + i, e);
                    return;
                }
                if (sel)
                {
                    try
                    {
                        GUI.Label(new Rect(r.x - 1f, r.y - 9f, 16f, 12f), (i + 1).ToString(), _tinyStyle);
                    }
                    catch (Exception e)
                    {
                        HudError("hotbar/number", e);
                        return;
                    }
                }
            }

            // Item name, like the vanilla "held item" line above the bar.
            try
            {
                string label = ItemLabel(CurrentItem);
                float lw = _bodyStyle.CalcSize(new GUIContent(label)).x + 30f;
                UiKit.Chip(new Rect(cardX, cardY - 26f, lw, 20f), label, UiKit.Gold, _smallStyle);
            }
            catch (Exception e)
            {
                HudError("hotbar/label", e);
            }
        }

        // ---- chat ------------------------------------------------------------

        private static void DrawChat()
        {
            if (!W.Active && !W.UiMode && !W.ChatOpen && !HasRecentChat())
            {
                return;
            }

            float lineH = 19f;
            float bottom = Screen.height - HotbarCardH - 96f;
            if (W.ChatOpen)
            {
                bottom -= lineH + 4f;
            }

            int shown = 0;
            for (int i = W.ChatLog.Count - 1; i >= 0 && shown < 8; i--)
            {
                var entry = W.ChatLog[i];
                float age = Time.unscaledTime - entry.Time;
                if (!W.ChatOpen && age > W.ChatStay)
                {
                    continue;
                }
                float alpha = Mathf.Clamp01((W.ChatStay - age) / 2f);
                if (W.ChatOpen)
                {
                    alpha = 1f;
                }

                float y = bottom - shown * lineH;
                float width = Mathf.Min(Screen.width - 40f,
                    _chatStyle.CalcSize(new GUIContent(entry.Text)).x + 18f);
                UiKit.Rect(new Rect(14f, y, width, lineH), new Color(0f, 0f, 0f, 0.40f * alpha));
                UiKit.Rect(new Rect(14f, y, 2f, lineH), new Color(UiKit.Accent.r, UiKit.Accent.g, UiKit.Accent.b, 0.75f * alpha));
                var prev = _chatStyle.normal.textColor;
                _chatStyle.normal.textColor = new Color(1f, 1f, 1f, alpha);
                GUI.Label(new Rect(22f, y, width, lineH), entry.Text, _chatStyle);
                _chatStyle.normal.textColor = prev;
                shown++;
            }

            if (!W.ChatOpen)
            {
                return;
            }

            float iy = bottom + 2f;
            float iw = Mathf.Min(Screen.width - 40f, 620f);
            UiKit.Rect(new Rect(14f, iy, iw, lineH + 6f), new Color(0f, 0f, 0f, 0.72f));
            UiKit.Rect(new Rect(14f, iy, 2f, lineH + 6f), UiKit.Accent);
            string caret = (Mathf.FloorToInt(Time.unscaledTime * 2f) % 2 == 0) ? "_" : " ";
            GUI.Label(new Rect(24f, iy, iw - 12f, lineH + 6f), W.ChatInput + caret, _inputStyle);
        }
    }
}
