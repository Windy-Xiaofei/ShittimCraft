using System;
using System.Collections.Generic;
using System.Globalization;
using Il2CppInterop.Runtime;
using MX.Logic.BattleEntities;
using UnityEngine;

namespace SchaleMcWorld
{
    /// <summary>
    /// A Minecraft style command bar. Type T or "/" to open it, Enter runs the command,
    /// Escape closes it. The text is captured straight out of the window procedure, so the
    /// game never receives a single keystroke while the bar is open.
    ///
    /// Coordinates are grid coordinates: the grid origin is the point where the world was
    /// summoned (the centre of the students), so 0 0 0 is always "where it all started".
    /// "~" is relative to Steve, exactly like in Minecraft.
    /// </summary>
    public partial class McWorldBehaviour
    {
        internal class ChatLine
        {
            public string Text;
            public float Time;
        }

        private static readonly Dictionary<string, string> BlockAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static void BuildBlockAliases()
        {
            if (BlockAliases.Count > 0)
            {
                return;
            }
            for (int i = 0; i < InventoryBlocks.Length; i++)
            {
                string key = InventoryBlocks[i];
                string shortName = key.ToLowerInvariant().Replace("mcblock_", "");
                BlockAliases[shortName] = key;
                BlockAliases[key] = key;
                BlockAliases["minecraft:" + shortName] = key;
            }
            BlockAliases["cobblestone"] = "MCBlock_Cobble";
            BlockAliases["wood"] = "MCBlock_Planks";
            BlockAliases["plank"] = "MCBlock_Planks";
            BlockAliases["brick"] = "MCBlock_Bricks";
            BlockAliases["cobble"] = "MCBlock_Cobble";
        }

        // ---- chat log -------------------------------------------------------

        internal static void Say(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            W.ChatLog.Add(new ChatLine { Text = text, Time = Time.unscaledTime });
            while (W.ChatLog.Count > 80)
            {
                W.ChatLog.RemoveAt(0);
            }
            Plugin.L.LogInfo("[McWorld] " + text);
        }

        private static void EchoChat(string line)
        {
            W.ChatLog.Add(new ChatLine { Text = "> " + line, Time = Time.unscaledTime + 4f });
            while (W.ChatLog.Count > 80)
            {
                W.ChatLog.RemoveAt(0);
            }
        }

        private static bool HasRecentChat()
        {
            if (W.ChatLog.Count == 0)
            {
                return false;
            }
            return Time.unscaledTime - W.ChatLog[W.ChatLog.Count - 1].Time < W.ChatStay;
        }

        // ---- open / close ---------------------------------------------------

        private static void OpenChat()
        {
            if (W.ChatOpen)
            {
                return;
            }
            W.ChatOpen = true;
            W.ChatInput = "/";
            // The key that opened the bar also produces a WM_CHAR; ignore a few milliseconds
            // so the command never starts with a stray "t" or "/".
            W.ChatIgnoreUntil = Time.unscaledTime + 0.16f;
            // Free cursor while typing: pinning it to the window centre would yank the mouse
            // away from whatever menu the player was reading.
            NativeInput.SetProfile(true, false, true, true, false);
            NativeInput.SetCaptureText(true);
            NativeInput.ClearHeld();
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }

        private static void CloseChat(bool submit)
        {
            if (!W.ChatOpen)
            {
                return;
            }
            string line = W.ChatInput;
            W.ChatOpen = false;
            W.ChatInput = "";
            NativeInput.SetCaptureText(false);
            NativeInput.ClearHeld();

            // Hand the input back to whichever mode is now on top.
            ApplyModeInputProfile();

            if (submit && line != null && line.Trim().Length > 1)
            {
                EchoChat(line);
                RunCommand(line);
            }
        }

        /// <summary>Puts the input hook back the way the current mode wants it.</summary>
        private static void ApplyModeInputProfile()
        {
            if (W.InventoryOpen || W.ChatOpen)
            {
                return; // those two own the keyboard already
            }
            if (W.UiMode)
            {
                NativeInput.Install();
                ApplyFpProfile();
            }
            else if (W.Active)
            {
                NativeInput.Install();
                NativeInput.SetProfile(false, true, true, true);
                Cursor.visible = false;
                Cursor.lockState = CursorLockMode.None;
            }
            else
            {
                NativeInput.Uninstall();
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }
        }

        private static void PumpChat()
        {
            // Safety net: if the window procedure could not be installed at all, the game is
            // still receiving the keys, so read the legacy input queue instead. It is empty
            // whenever the hook is in place, so the two paths never double up.
            if (!NativeInput.Hooked)
            {
                string legacy = UnityEngine.Input.inputString ?? "";
                bool submit = false;
                for (int i = 0; i < legacy.Length; i++)
                {
                    char c = legacy[i];
                    if (c == '\b')
                    {
                        if (W.ChatInput.Length > 1)
                        {
                            W.ChatInput = W.ChatInput.Substring(0, W.ChatInput.Length - 1);
                        }
                    }
                    else if (c == '\n' || c == '\r')
                    {
                        submit = true;
                    }
                    else if (c >= 32 && Time.unscaledTime >= W.ChatIgnoreUntil)
                    {
                        W.ChatInput += c;
                    }
                }
                if (submit)
                {
                    CloseChat(true);
                    return;
                }
                if (UnityEngine.Input.GetKeyDown(KeyCode.Escape))
                {
                    CloseChat(false);
                    return;
                }
            }

            int back = NativeInput.TakeBackspaces();
            for (int i = 0; i < back; i++)
            {
                if (W.ChatInput.Length > 1)
                {
                    W.ChatInput = W.ChatInput.Substring(0, W.ChatInput.Length - 1);
                }
            }

            if (Time.unscaledTime >= W.ChatIgnoreUntil)
            {
                string typed = NativeInput.TakeChars();
                if (typed.Length > 0)
                {
                    W.ChatInput += typed;
                    if (W.ChatInput.Length > 160)
                    {
                        W.ChatInput = W.ChatInput.Substring(0, 160);
                    }
                }
            }
            else
            {
                NativeInput.TakeChars();
            }

            if (NativeInput.TakeEscapes() > 0)
            {
                CloseChat(false);
                return;
            }
            if (NativeInput.TakeSubmits() > 0)
            {
                CloseChat(true);
            }
        }

        // ---- parser ---------------------------------------------------------

        private static void RunCommand(string raw)
        {
            try
            {
                BuildBlockAliases();
                string body = raw.Trim();
                if (body.StartsWith("/"))
                {
                    body = body.Substring(1);
                }
                string[] a = body.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (a.Length == 0)
                {
                    return;
                }

                string cmd = a[0].ToLowerInvariant();
                switch (cmd)
                {
                    case "help":
                    case "?":
                        CmdHelp();
                        break;
                    case "give":
                        CmdGive(a);
                        break;
                    case "inv":
                    case "inventory":
                        ToggleInventory();
                        break;
                    case "setblock":
                        CmdSetBlock(a);
                        break;
                    case "fill":
                        CmdFill(a);
                        break;
                    case "tp":
                    case "teleport":
                        CmdTp(a);
                        break;
                    case "pos":
                        CmdPos();
                        break;
                    case "clear":
                        {
                            int had = W.Blocks.Count;
                            ClearBlocks();
                            Say("[MC] 已清空 " + had + " 个方块");
                            break;
                        }
                    case "kill":
                        CmdKill(a);
                        break;
                    case "hurt":
                        CmdHurt(a);
                        break;
                    case "heal":
                        CmdHeal();
                        break;
                    case "time":
                        CmdTime(a);
                        break;
                    case "speed":
                        CmdSpeed(a);
                        break;
                    case "fly":
                        W.Fly = !W.Fly;
                        Say("[MC] 飞行：" + (W.Fly ? "开" : "关"));
                        break;
                    case "fov":
                        CmdFov(a);
                        break;
                    case "cam":
                        CmdCam(a);
                        break;
                    case "fp":
                    case "firstperson":
                        SetUiMode(true);
                        break;
                    case "3d":
                    case "third":
                        SetUiMode(false);
                        break;
                    case "mine":
                        ToggleMineMode();
                        break;
                    case "restore":
                    case "undo":
                        RestoreAllUi();
                        break;
                    case "break":
                        BreakUiUnderCursor();
                        break;
                    case "ui":
                        Say("[MC] 当前可见界面元素：" + CountUiWidgets() + " 个，已破坏：" + W.Broken.Count + " 个");
                        break;
                    case "selftest":
                        RunSelfTest("command");
                        BattleSelfTest();
                        Say("[MC] 自检完成：通过 " + W.SelfTestPass + " 项，失败 " + W.SelfTestFail +
                            " 项（详情见 plugins/McWorld/selftest.log）");
                        break;
                    case "look":
                        if (W.UiMode)
                        {
                            ToggleLookMode();
                        }
                        else
                        {
                            Say("[MC] 只有第一人称（F8）才有鼠标锁定");
                        }
                        break;
                    case "mc":
                        if (W.Active)
                        {
                            LeaveWorld();
                        }
                        else
                        {
                            EnterWorld();
                        }
                        break;
                    default:
                        Say("[MC] 未知指令：" + cmd + "（输入 /help）");
                        break;
                }
            }
            catch (Exception e)
            {
                Say("[MC] 指令出错：" + e.GetType().Name + ": " + e.Message);
            }
        }

        // ---- individual commands --------------------------------------------

        private static void CmdHelp()
        {
            Say("──── MC 指令 ────");
            Say("/give <方块|pickaxe|sword|shield|pearl>   放入当前格");
            Say("/inv                    打开背包（也可以按 E）");
            Say("/setblock <x y z> <方块|air>   放置/移除方块");
            Say("/fill <x1 y1 z1> <x2 y2 z2> <方块|air>   区域填充(≤2048)");
            Say("/tp <x y z> | /tp here  传送史蒂夫 / 传到最近学员");
            Say("/pos                    当前坐标");
            Say("/clear                  清空所有方块");
            Say("/kill [all]             秒杀最近/all 学员");
            Say("/hurt <0-1>             剑的伤害比例（默认 0.25）");
            Say("/heal                   学员回满血");
            Say("/time set <day|night>   昼夜（画面滤镜）");
            Say("/speed <倍率> /fly       移动速度 / 飞行");
            Say("/fp | /3d               第一人称界面 / 斜俯视");
            Say("/mine                   切换 互动/破坏");
            Say("/break | /restore       破坏鼠标下的界面 / 全部还原");
            Say("/ui                     界面元素统计");
            Say("/mc                     进入/退出 MC 世界");
            Say("~ 代表史蒂夫所在格，例如 /setblock ~ ~1 ~ stone");
        }

        private static void CmdGive(string[] a)
        {
            if (a.Length < 2)
            {
                Say("[MC] 用法：/give <方块|pickaxe|sword|shield|pearl>");
                return;
            }
            string arg = a[1].ToLowerInvariant();
            switch (arg)
            {
                case "pickaxe":
                case "sword":
                case "shield":
                case "pearl":
                    W.Hotbar[W.SelectedBlock] = "tool:" + arg;
                    RefreshHeldItems();
                    Say("[MC] 第 " + (W.SelectedBlock + 1) + " 格 ← " + ItemLabel("tool:" + arg));
                    return;
            }

            if (!BlockAliases.TryGetValue(a[1], out var key))
            {
                Say("[MC] 没有这个物品：" + a[1]);
                return;
            }
            W.Hotbar[W.SelectedBlock] = "block:" + key;
            RefreshHeldItems();
            Say("[MC] 第 " + (W.SelectedBlock + 1) + " 格 ← " + ItemLabel("block:" + key));
        }

        private static void CmdSetBlock(string[] a)
        {
            if (a.Length < 5)
            {
                Say("[MC] 用法：/setblock <x> <y> <z> <方块|air>");
                return;
            }
            if (!TryCoord(a[1], W.StevePos.x - W.Origin.x, out int x) ||
                !TryCoord(a[2], W.StevePos.y - W.Origin.y, out int y) ||
                !TryCoord(a[3], W.StevePos.z - W.Origin.z, out int z))
            {
                Say("[MC] 坐标无效");
                return;
            }
            var cell = new Vector3Int(x, y, z);
            if (IsAir(a[4]))
            {
                RemoveBlock(cell);
                Say("[MC] 已移除 " + x + " " + y + " " + z);
                return;
            }
            if (!BlockAliases.TryGetValue(a[4], out var key))
            {
                Say("[MC] 没有这个方块：" + a[4]);
                return;
            }
            if (W.Blocks.ContainsKey(cell))
            {
                RemoveBlock(cell);
            }
            SpawnBlock(cell, key.ToLowerInvariant());
            Say("[MC] 已放置 " + a[4] + " @ " + x + " " + y + " " + z);
        }

        private static void CmdFill(string[] a)
        {
            if (a.Length < 8)
            {
                Say("[MC] 用法：/fill <x1 y1 z1> <x2 y2 z2> <方块|air>");
                return;
            }
            if (!TryCoord(a[1], W.StevePos.x - W.Origin.x, out int x1) ||
                !TryCoord(a[2], W.StevePos.y - W.Origin.y, out int y1) ||
                !TryCoord(a[3], W.StevePos.z - W.Origin.z, out int z1) ||
                !TryCoord(a[4], W.StevePos.x - W.Origin.x, out int x2) ||
                !TryCoord(a[5], W.StevePos.y - W.Origin.y, out int y2) ||
                !TryCoord(a[6], W.StevePos.z - W.Origin.z, out int z2))
            {
                Say("[MC] 坐标无效");
                return;
            }

            bool air = IsAir(a[7]);
            string key = null;
            if (!air && !BlockAliases.TryGetValue(a[7], out key))
            {
                Say("[MC] 没有这个方块：" + a[7]);
                return;
            }

            int lo = Mathf.Min(x1, x2), hi = Mathf.Max(x1, x2);
            int loY = Mathf.Min(y1, y2), hiY = Mathf.Max(y1, y2);
            int loZ = Mathf.Min(z1, z2), hiZ = Mathf.Max(z1, z2);
            long volume = (long)(hi - lo + 1) * (hiY - loY + 1) * (hiZ - loZ + 1);
            if (volume > 2048)
            {
                Say("[MC] 区域太大：" + volume + " 格（上限 2048）");
                return;
            }

            int placed = 0, removed = 0;
            for (int x = lo; x <= hi; x++)
            {
                for (int y = loY; y <= hiY; y++)
                {
                    for (int z = loZ; z <= hiZ; z++)
                    {
                        var cell = new Vector3Int(x, y, z);
                        if (air)
                        {
                            if (W.Blocks.ContainsKey(cell))
                            {
                                RemoveBlock(cell);
                                removed++;
                            }
                        }
                        else
                        {
                            if (!W.Blocks.ContainsKey(cell))
                            {
                                SpawnBlock(cell, key.ToLowerInvariant());
                                placed++;
                            }
                        }
                    }
                }
            }
            Say("[MC] /fill 完成：放置 " + placed + "，移除 " + removed);
        }

        private static void CmdTp(string[] a)
        {
            if (!W.Active)
            {
                Say("[MC] 需要先进入 MC 世界（F7）");
                return;
            }
            if (a.Length >= 2 && a[1].ToLowerInvariant() == "here")
            {
                Transform tr = NearestCharacter(out _);
                if (tr == null)
                {
                    Say("[MC] 附近没有学员");
                    return;
                }
                Vector3 p = tr.position;
                Vector3 back = tr.forward;
                W.StevePos = p - back * 1.0f;
                W.StevePos.y = p.y;
                W.SteveVel = Vector3.zero;
                Say("[MC] 已传送到最近学员身边");
                return;
            }
            if (a.Length < 4 ||
                !TryCoord(a[1], W.StevePos.x - W.Origin.x, out int x) ||
                !TryCoord(a[2], W.StevePos.y - W.Origin.y, out int y) ||
                !TryCoord(a[3], W.StevePos.z - W.Origin.z, out int z))
            {
                Say("[MC] 用法：/tp <x> <y> <z> 或 /tp here");
                return;
            }
            W.StevePos = W.Origin + new Vector3(x + 0.5f, y, z + 0.5f);
            W.SteveVel = Vector3.zero;
            Say("[MC] 已传送到 " + x + " " + y + " " + z);
        }

        private static void CmdPos()
        {
            if (!W.OriginReady)
            {
                Say("[MC] 还没有世界原点");
                return;
            }
            Vector3 p = W.StevePos - W.Origin;
            Say("[MC] 史蒂夫 @ " + Mathf.FloorToInt(p.x) + " " + Mathf.FloorToInt(p.y) + " " +
                Mathf.FloorToInt(p.z) + "（方块 " + W.Blocks.Count + "，世界原点 " + W.Origin.ToString("F1") + "）");
        }

        private static void CmdKill(string[] a)
        {
            bool all = a.Length >= 2 && a[1].ToLowerInvariant() == "all";
            Transform[] targets;
            lock (W.Gate)
            {
                targets = W.CharTransforms.ToArray();
            }
            if (targets.Length == 0)
            {
                Say("[MC] 场上没有学员");
                return;
            }

            int kills = 0;
            if (all)
            {
                for (int i = 0; i < targets.Length && i < 40; i++)
                {
                    if (KillUnit(targets[i]))
                    {
                        kills++;
                    }
                }
            }
            else
            {
                Transform nearest = NearestCharacter(out _);
                if (nearest != null && KillUnit(nearest))
                {
                    kills = 1;
                }
            }
            Say("[MC] /kill 命中 " + kills + " 个目标");
        }

        private static void CmdHurt(string[] a)
        {
            if (a.Length < 2 || !float.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
            {
                Say("[MC] 用法：/hurt <0-1>，当前 " + W.DamageFraction.ToString("F2"));
                return;
            }
            W.DamageFraction = Mathf.Clamp(f, 0.01f, 1f);
            Say("[MC] 剑每次造成最大生命的 " + (W.DamageFraction * 100f).ToString("F0") + "%");
        }

        private static void CmdHeal()
        {
            Transform[] targets;
            lock (W.Gate)
            {
                targets = W.CharTransforms.ToArray();
            }
            int n = 0;
            for (int i = 0; i < targets.Length; i++)
            {
                var be = BattleEntityOf(targets[i]);
                if (be == null)
                {
                    continue;
                }
                try
                {
                    long max = be.MaxHitPoint;
                    long hp = be.HitPoint;
                    if (hp >= max)
                    {
                        continue;
                    }
                    be.AddHitPoint(max - hp);
                    if (be.HitPoint <= hp)
                    {
                        WriteHitPoint(be, max);
                    }
                    n++;
                }
                catch { }
            }
            Say("[MC] 已治疗 " + n + " 个学员");
        }

        private static void CmdTime(string[] a)
        {
            string what = a.Length >= 3 ? a[2].ToLowerInvariant() : (a.Length >= 2 ? a[1].ToLowerInvariant() : "day");
            float target;
            switch (what)
            {
                case "day":
                case "noon":
                case "morning":
                    target = 0f;
                    break;
                case "sunset":
                    target = 0.35f;
                    break;
                case "night":
                    target = 0.62f;
                    break;
                case "midnight":
                    target = 0.78f;
                    break;
                default:
                    Say("[MC] 用法：/time set <day|sunset|night|midnight>");
                    return;
            }
            W.NightTarget = target;
            Say("[MC] 时间：" + what);
        }

        private static void CmdSpeed(string[] a)
        {
            if (a.Length < 2 || !float.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
            {
                Say("[MC] 用法：/speed <0.2-8>，当前 " + W.SpeedMul.ToString("F2"));
                return;
            }
            W.SpeedMul = Mathf.Clamp(f, 0.2f, 8f);
            Say("[MC] 速度倍率 " + W.SpeedMul.ToString("F2"));
        }

        private static void CmdFov(string[] a)
        {
            if (a.Length < 2 || !float.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
            {
                Say("[MC] 用法：/fov <20-100>");
                return;
            }
            Plugin.CameraFov.Value = Mathf.Clamp(f, 20f, 100f);
            Say("[MC] FOV = " + Plugin.CameraFov.Value.ToString("F0"));
        }

        private static void CmdCam(string[] a)
        {
            if (a.Length >= 2 && float.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float pitch))
            {
                Plugin.CameraPitch.Value = Mathf.Clamp(pitch, 15f, 80f);
            }
            if (a.Length >= 3 && float.TryParse(a[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float dist))
            {
                Plugin.CameraDistance.Value = Mathf.Clamp(dist, 4f, 30f);
            }
            Say("[MC] 相机 pitch=" + Plugin.CameraPitch.Value.ToString("F0") +
                " dist=" + Plugin.CameraDistance.Value.ToString("F1") + "（用法 /cam <pitch> <dist>）");
        }

        // ---- helpers --------------------------------------------------------

        private static bool IsAir(string s)
        {
            string n = s.ToLowerInvariant().Replace("minecraft:", "");
            return n == "air" || n == "0" || n == "空";
        }

        private static bool TryCoord(string s, float baseValue, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(s))
            {
                return false;
            }
            if (s[0] == '~')
            {
                int delta = 0;
                if (s.Length > 1 &&
                    !int.TryParse(s.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out delta))
                {
                    return false;
                }
                value = Mathf.FloorToInt(baseValue) + delta;
                return true;
            }
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static Transform NearestCharacter(out Vector3 point)
        {
            point = Vector3.zero;
            Transform[] targets;
            lock (W.Gate)
            {
                targets = W.CharTransforms.ToArray();
            }
            Transform best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < targets.Length; i++)
            {
                var tr = targets[i];
                if (tr == null)
                {
                    continue;
                }
                float d = (tr.position - W.StevePos).sqrMagnitude;
                if (d < bestD)
                {
                    bestD = d;
                    best = tr;
                    point = tr.position;
                }
            }
            return best;
        }

        private static BattleEntity BattleEntityOf(Transform tr)
        {
            try
            {
                if (tr == null)
                {
                    return null;
                }
                var cv = tr.gameObject.GetComponent(Il2CppType.Of<CharacterVisual>())?.TryCast<CharacterVisual>();
                if (cv == null)
                {
                    return null;
                }
                var ch = cv.Character;
                return ch == null ? null : ch.TryCast<BattleEntity>();
            }
            catch
            {
                return null;
            }
        }

        private static bool KillUnit(Transform tr)
        {
            var be = BattleEntityOf(tr);
            if (be == null)
            {
                return false;
            }
            try
            {
                long max = be.MaxHitPoint;
                be.AddHitPoint(-max);
                if (be.HitPoint > 0)
                {
                    WriteHitPoint(be, 0);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
