using System;
using System.Collections.Generic;
using UnityEngine;

namespace SchaleMcWorld
{
    /// <summary>
    /// Minecraft's E screen, rebuilt with IMGUI because the character has no inventory of its
    /// own. It is a real 9-slot hotbar model: every slot holds an item string
    /// ("block:MCBlock_Grass" or "tool:shield"), clicking a block or tool drops it into the
    /// selected hotbar slot, and clicking a hotbar slot selects it.
    ///
    /// Clicks are read from Win32, not from Event.current: the window procedure swallows the
    /// mouse while the panel is open, so IMGUI never sees a click of its own.
    /// </summary>
    public partial class McWorldBehaviour
    {
        internal const float InvSlot = 42f;
        internal const float InvGap = 4f;
        internal const int InvCols = 9;

        internal static readonly string[] InventoryBlocks =
        {
            "MCBlock_Grass", "MCBlock_Stone", "MCBlock_Dirt", "MCBlock_Cobble",
            "MCBlock_Planks", "MCBlock_Bricks", "MCBlock_Glass", "MCBlock_Sand",
            "MCBlock_Obsidian", "MCBlock_TNT", "MCBlock_Netherrack", "MCBlock_Iron",
            "MCBlock_Gold", "MCBlock_Diamond",
        };

        internal static readonly string[] InventoryTools =
        {
            "tool:pickaxe", "tool:sword", "tool:shield", "tool:pearl",
        };

        internal struct InvCell
        {
            public Rect Rect;
            public int Kind;      // 0 = inventory block, 1 = inventory tool, 2 = hotbar slot
            public int Index;
            public string Item;
        }

        private static readonly List<InvCell> Cells = new List<InvCell>(32);

        // Same reason as the hotbar: 27 ItemIcon() lookups per frame all hit the throttled
        // texture loader. Cache by item string and refresh at most twice a second.
        private static readonly Dictionary<string, Texture2D> InvIcons =
            new Dictionary<string, Texture2D>(32);
        private static float _nextInvIconRefresh;
        private static bool _invIconRefreshNow;

        private static Texture2D InvIcon(string item)
        {
            if (item == null)
            {
                return null;
            }
            if (!_invIconRefreshNow && InvIcons.TryGetValue(item, out var hit) && hit != null)
            {
                return hit;
            }
            var t = ItemIcon(item);
            if (t != null)
            {
                InvIcons[item] = t;
                return t;
            }
            return InvIcons.TryGetValue(item, out var old) ? old : null;
        }

        // ---- hotbar helpers --------------------------------------------------

        internal static string ItemAt(int slot)
        {
            return W.Hotbar[Mathf.Clamp(slot, 0, W.Hotbar.Length - 1)];
        }

        internal static string CurrentItem => ItemAt(W.SelectedBlock);

        internal static bool IsTool(string item)
        {
            return item != null && item.StartsWith("tool:", StringComparison.Ordinal);
        }

        internal static bool IsBlockItem(string item)
        {
            return item != null && item.StartsWith("block:", StringComparison.Ordinal);
        }

        internal static string BlockKeyOf(string item)
        {
            return IsBlockItem(item) ? item.Substring(6) : null;
        }

        internal static Texture2D ItemIcon(string item)
        {
            if (item == null)
            {
                return null;
            }
            if (IsBlockItem(item))
            {
                return BlockIconTexture(item.Substring(6).ToLowerInvariant());
            }
            switch (item)
            {
                case "tool:pickaxe": return LoadDiskTexture("iron_pickaxe");
                case "tool:sword": return LoadDiskTexture("diamond_sword");
                case "tool:shield": return LoadDiskTexture("shield");
                case "tool:pearl": return LoadDiskTexture("ender_pearl");
            }
            return null;
        }

        internal static string ItemLabel(string item)
        {
            if (item == null)
            {
                return "空";
            }
            if (IsBlockItem(item))
            {
                string key = item.Substring(6).Replace("MCBlock_", "").ToLowerInvariant();
                switch (key)
                {
                    case "grass": return "草方块";
                    case "stone": return "石头";
                    case "dirt": return "泥土";
                    case "cobble": return "圆石";
                    case "planks": return "木板";
                    case "bricks": return "砖块";
                    case "glass": return "玻璃";
                    case "sand": return "沙子";
                    case "obsidian": return "黑曜石";
                    case "tnt": return "TNT";
                    case "netherrack": return "地狱岩";
                    case "iron": return "铁块";
                    case "gold": return "金块";
                    case "diamond": return "钻石块";
                }
                return key;
            }
            switch (item)
            {
                case "tool:pickaxe": return "铁镐";
                case "tool:sword": return "钻石剑";
                case "tool:shield": return "盾牌";
                case "tool:pearl": return "末影珍珠";
            }
            return item;
        }

        // ---- open / close ----------------------------------------------------

        private static void ToggleInventory()
        {
            if (W.InventoryOpen)
            {
                CloseInventory();
                return;
            }
            W.InventoryOpen = true;
            NativeInput.Install();
            NativeInput.SetProfile(true, false, true, true, false);
            NativeInput.ClearHeld();
            NativeInput.CenterCursor();
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            Say("[MC] 背包：点击方块或工具放入第 " + (W.SelectedBlock + 1) + " 格，E / Esc 关闭");
        }

        private static void CloseInventory()
        {
            if (!W.InventoryOpen)
            {
                return;
            }
            W.InventoryOpen = false;
            NativeInput.ClearHeld();
            if (W.UiMode)
            {
                ApplyFpProfile();
            }
            else
            {
                ApplyModeInputProfile();
            }
        }

        private static void HandleInventoryInput()
        {
            if (NativeInput.Pressed(NativeInput.VkE) || NativeInput.Pressed(NativeInput.VkEscape))
            {
                CloseInventory();
                return;
            }
            BuildInventoryLayout();
            if (!NativeInput.Pressed(NativeInput.VkLButton))
            {
                return;
            }
            // CursorScreen is bottom-up (Unity), the layout rectangles are top-down (GUI).
            Vector2 cursor = NativeInput.CursorScreen();
            int hit = HitCell(new Vector2(cursor.x, Screen.height - cursor.y));
            if (hit < 0)
            {
                return;
            }
            var cell = Cells[hit];
            switch (cell.Kind)
            {
                case 0:
                    W.Hotbar[W.SelectedBlock] = cell.Item;
                    RefreshHeldItems();
                    UpdateFpHand();
                    Say("[MC] 第 " + (W.SelectedBlock + 1) + " 格 ← " + ItemLabel(cell.Item));
                    break;
                case 1:
                    W.Hotbar[W.SelectedBlock] = cell.Item;
                    RefreshHeldItems();
                    UpdateFpHand();
                    Say("[MC] 第 " + (W.SelectedBlock + 1) + " 格 ← " + ItemLabel(cell.Item));
                    break;
                case 2:
                    W.SelectedBlock = cell.Index;
                    W.HeldSword = false;
                    W.Blocking = false;
                    RefreshHeldItems();
                    UpdateFpHand();
                    Say("[MC] 选中第 " + (cell.Index + 1) + " 格：" + ItemLabel(cell.Item));
                    break;
            }
        }

        // ---- layout ----------------------------------------------------------

        private static float InvPanelW()
        {
            return InvCols * InvSlot + (InvCols - 1) * InvGap + 36f;
        }

        private static Rect BuildInventoryLayout()
        {
            Cells.Clear();

            float rowH = InvSlot + InvGap;
            float panelW = InvPanelW();
            float panelH = 52f + 18f + rowH * 2f + 10f + 18f + rowH + 10f + 18f + rowH + 34f;

            float x0 = (Screen.width - panelW) * 0.5f;
            float y0 = (Screen.height - panelH) * 0.5f;

            float gx = x0 + 18f;
            float y = y0 + 52f;

            // blocks: 14 of them, two rows of nine (the second row is short)
            y += 18f;
            for (int i = 0; i < InventoryBlocks.Length; i++)
            {
                int col = i % InvCols;
                int row = i / InvCols;
                Cells.Add(new InvCell
                {
                    Rect = new Rect(gx + col * (InvSlot + InvGap), y + row * rowH, InvSlot, InvSlot),
                    Kind = 0,
                    Index = i,
                    // The item string needs its "block:" prefix, otherwise ItemIcon cannot
                    // recognise it and every block slot draws an empty square.
                    Item = "block:" + InventoryBlocks[i],
                });
            }
            y += rowH * 2f + 10f;

            // tools
            y += 18f;
            for (int i = 0; i < InventoryTools.Length; i++)
            {
                Cells.Add(new InvCell
                {
                    Rect = new Rect(gx + i * (InvSlot + InvGap), y, InvSlot, InvSlot),
                    Kind = 1,
                    Index = i,
                    Item = InventoryTools[i],
                });
            }
            y += rowH + 10f;

            // hotbar
            y += 18f;
            for (int i = 0; i < InvCols; i++)
            {
                Cells.Add(new InvCell
                {
                    Rect = new Rect(gx + i * (InvSlot + InvGap), y, InvSlot, InvSlot),
                    Kind = 2,
                    Index = i,
                    Item = ItemAt(i),
                });
            }

            return new Rect(x0, y0, panelW, panelH);
        }

        private static int HitCell(Vector2 cursor)
        {
            for (int i = 0; i < Cells.Count; i++)
            {
                if (Cells[i].Rect.Contains(cursor))
                {
                    return i;
                }
            }
            return -1;
        }

        // ---- drawing ---------------------------------------------------------

        private static void DrawInventory()
        {
            var panel = BuildInventoryLayout();
            _invIconRefreshNow = Time.unscaledTime >= _nextInvIconRefresh;
            if (_invIconRefreshNow)
            {
                _nextInvIconRefresh = Time.unscaledTime + 0.5f;
            }

            // Dim the battlefield so the grid reads clearly.
            UiKit.Rect(new Rect(0f, 0f, Screen.width, Screen.height), new Color(0.02f, 0.03f, 0.05f, 0.55f));
            UiKit.Card(panel, true);

            GUI.Label(new Rect(panel.x + 18f, panel.y + 12f, 220f, 24f), "物品栏", _titleStyle);
            var tag = GUI.skin.label;
            GUI.Label(new Rect(panel.xMax - 130f, panel.y + 14f, 112f, 20f), "INVENTORY", _smallStyle);

            float gx = panel.x + 18f;
            float y = panel.y + 52f;
            float rowH = InvSlot + InvGap;

            SectionLabel(new Rect(gx, y, 200f, 18f), "方块");
            y += 18f + rowH * 2f + 10f;
            SectionLabel(new Rect(gx, y, 200f, 18f), "工具与物品");
            y += 18f + rowH + 10f;
            SectionLabel(new Rect(gx, y, 260f, 18f), "快捷栏（点击切换）");

            Vector2 cursor = NativeInput.CursorScreen();
            int hover = HitCell(new Vector2(cursor.x, Screen.height - cursor.y));
            string hoverText = "";

            for (int i = 0; i < Cells.Count; i++)
            {
                var c = Cells[i];
                bool selected = c.Kind == 2 && c.Index == W.SelectedBlock;
                bool isHover = i == hover;
                if (isHover)
                {
                    hoverText = ItemLabel(c.Item);
                }

                UiKit.Slot(c.Rect, selected, isHover);
                UiKit.Icon(new Rect(c.Rect.x + 6f, c.Rect.y + 6f, InvSlot - 12f, InvSlot - 12f), InvIcon(c.Item));
                if (c.Kind == 2)
                {
                    GUI.Label(new Rect(c.Rect.x + 3f, c.Rect.y - 2f, 18f, 13f), (c.Index + 1).ToString(), _tinyStyle);
                }
            }

            // footer
            float fy = panel.yMax - 30f;
            UiKit.Rect(new Rect(panel.x + 16f, fy - 4f, panel.width - 32f, 1f), UiKit.Edge);
            string tip = hoverText.Length > 0 ? hoverText : ItemLabel(CurrentItem);
            GUI.Label(new Rect(panel.x + 18f, fy + 2f, panel.width - 36f, 20f),
                "当前：" + tip, _bodyStyle);
            GUI.Label(new Rect(panel.x + panel.width - 250f, fy + 2f, 232f, 20f),
                "左键放入第 " + (W.SelectedBlock + 1) + " 格 · E 关闭", _smallStyle);
        }

        private static void SectionLabel(Rect r, string text)
        {
            UiKit.Rect(new Rect(r.x, r.y + r.height * 0.5f - 3.5f, 2f, 7f), UiKit.Accent);
            GUI.Label(new Rect(r.x + 8f, r.y, r.width, r.height), text, _smallStyle);
        }
    }
}
