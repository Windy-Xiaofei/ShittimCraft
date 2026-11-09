using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MX.Logic.Battles;
using MX.Logic.Skills;
using UnityEngine;

namespace SchaleMcWorld
{
    /// <summary>
    /// A built-in smoke test. It answers the questions that used to need a video to notice:
    /// are the textures there, did the shader load, does every hotbar slot produce an icon,
    /// do the block materials build, does the damage API accept a hit - and, once a battle is
    /// running, does a real hit actually move HP.
    ///
    /// Results go to BepInEx/LogOutput.log and to plugins/McWorld/selftest.log.
    /// </summary>
    public partial class McWorldBehaviour
    {
        private static readonly string[] RequiredTextures =
        {
            "steve", "grass_block_top", "grass_block_side", "stone", "dirt", "cobblestone",
            "oak_planks", "bricks", "glass", "sand", "obsidian", "tnt_top", "tnt_side",
            "netherrack", "iron_block", "gold_block", "diamond_block",
            "iron_pickaxe", "diamond_sword", "shield", "ender_pearl",
        };

        internal static void RunSelfTest(string reason)
        {
            W.SelfTestLines.Clear();
            W.SelfTestPass = 0;
            W.SelfTestFail = 0;

            Line("MC World self test (" + reason + ") - " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

            // 1. textures
            int missing = 0;
            var sizes = new StringBuilder();
            for (int i = 0; i < RequiredTextures.Length; i++)
            {
                string n = RequiredTextures[i];
                var t = LoadDiskTexture(n);
                if (t == null)
                {
                    missing++;
                    Line("  FAIL texture " + n + ".png is missing");
                }
                else if (i < 6)
                {
                    sizes.Append(n).Append('=').Append(t.width).Append('x').Append(t.height).Append(' ');
                }
            }
            Check("textures (" + RequiredTextures.Length + ")", missing == 0,
                missing == 0 ? sizes.ToString().Trim() : missing + " missing");

            // 2. shader
            Shader sh = null;
            try { sh = LoadMcShader(); } catch { }
            Check("shader", sh != null && sh.isSupported,
                sh == null ? "not loaded (character-material fallback)" : sh.name + " supported=" + sh.isSupported);

            // 3. block materials
            int badBlock = 0;
            var keys = new[]
            {
                "mcblock_grass", "mcblock_stone", "mcblock_dirt", "mcblock_cobble", "mcblock_planks",
                "mcblock_bricks", "mcblock_glass", "mcblock_sand", "mcblock_obsidian", "mcblock_tnt",
                "mcblock_netherrack", "mcblock_iron", "mcblock_gold", "mcblock_diamond",
            };
            for (int i = 0; i < keys.Length; i++)
            {
                if (BlockMaterials(keys[i]) == null)
                {
                    badBlock++;
                    Line("  FAIL block material " + keys[i]);
                }
            }
            Check("block materials (" + keys.Length + ")", badBlock == 0, badBlock + " broken");

            // 4. icons: every slot of the hotbar grid and the tool rack
            int iconFail = 0;
            for (int i = 0; i < W.Hotbar.Length; i++)
            {
                if (ItemIcon(W.Hotbar[i]) == null)
                {
                    iconFail++;
                    Line("  FAIL hotbar icon slot " + (i + 1) + " (" + W.Hotbar[i] + ")");
                }
            }
            for (int i = 0; i < InventoryBlocks.Length; i++)
            {
                string item = "block:" + InventoryBlocks[i];
                if (ItemIcon(item) == null)
                {
                    iconFail++;
                    Line("  FAIL inventory icon " + item);
                }
            }
            for (int i = 0; i < InventoryTools.Length; i++)
            {
                if (ItemIcon(InventoryTools[i]) == null)
                {
                    iconFail++;
                    Line("  FAIL inventory icon " + InventoryTools[i]);
                }
            }
            Check("item icons (" + (W.Hotbar.Length + InventoryBlocks.Length + InventoryTools.Length) + ")",
                iconFail == 0, iconFail + " empty slots");

            // 5. UI kit textures
            bool ui = false;
            try
            {
                UiKit.EnsureTextures();
                ui = UiKit.HasTextures;
            }
            catch { }
            Check("ui kit", ui, ui ? "rounded card textures ready" : "texture build failed");

            // 5b. IMGUI textures must be rooted on the il2cpp heap, otherwise the collector
            // reclaims them and every hotbar/badge draw dies with ObjectCollectedException.
            // Two probes, one per defence layer, plus a forced collect to prove which one
            // actually holds when the game's GC runs.
            int rootHits = 0;
            bool gced = false;
            bool handleAlive = false;
            bool sceneAlive = false;
            try
            {
                var probe = new Texture2D(4, 4);
                probe.Apply();
                var sceneProbe = new Texture2D(4, 4);
                sceneProbe.name = "probe_scene";
                sceneProbe.Apply();
                NativeRoot.Keep(probe);
                AssetKeeper.Keep(sceneProbe);
                rootHits = NativeRoot.Kept;
                try
                {
                    Il2CppSystem.GC.Collect();
                    Il2CppSystem.GC.WaitForPendingFinalizers();
                    Il2CppSystem.GC.Collect();
                    gced = true;
                }
                catch { }
                handleAlive = UiKit.Alive(probe);
                sceneAlive = UiKit.Alive(sceneProbe);
            }
            catch { }
            Check("native roots (" + rootHits + ")", rootHits > 0, rootHits > 0
                ? "gchandles registered for runtime textures"
                : "no gchandle - disk textures will be collected");
            Check("gc survival (handle=" + handleAlive + " flag=" + sceneAlive + " gced=" + gced +
                   " reloads=" + AssetKeeper.KeptCount + (AssetKeeper.Failed ? " KEEPER-FAILED" : "") + ")",
                handleAlive || sceneAlive,
                "handle-rooted " + (handleAlive ? "alive" : "swept") +
                ", hideFlags-rooted " + (sceneAlive ? "alive" : "swept") +
                (AssetKeeper.Failed ? ", asset keeper threw" : ""));

            // 5c. The HUD now draws only with the engine-owned whiteTexture. If that is not
            // available, no card, slot or chip can be drawn at all.
            bool whiteOk = false;
            try { whiteOk = UiKit.Alive(Texture2D.whiteTexture); } catch { }
            Check("hud white texture", whiteOk, whiteOk
                ? "engine-owned whiteTexture alive, zero generated sheets"
                : "whiteTexture unavailable - HUD cannot draw");

            // 6. damage API is bound and usable as a value
            bool dmgOk = false;
            string dmgInfo = "";
            try
            {
                var r = new DamageResult(1234, FlatData.FontType.Damage);
                var spec = SkillSpecification.Dummy;
                dmgOk = r.Damage == 1234 && spec != null;
                dmgInfo = "DamageResult ok, SkillSpecification.Dummy=" + (spec != null);
            }
            catch (Exception e)
            {
                dmgInfo = e.GetType().Name + ": " + e.Message;
            }
            Check("damage pipeline", dmgOk, dmgInfo);

            // 7. grid math used by placement and the voxel ray
            bool gridOk = false;
            try
            {
                Vector3 save = W.Origin;
                W.Origin = new Vector3(2f, 0f, -4f);
                var g = ToGrid(new Vector3(2.6f, 0.4f, -3.5f));
                var c = GridCenter(g);
                gridOk = g.x == 0 && g.y == 0 && g.z == 0 &&
                         Mathf.Abs(c.x - 2.5f) < 0.001f && Mathf.Abs(c.z - -3.5f) < 0.001f;
                W.Origin = save;
            }
            catch { }
            Check("world grid math", gridOk, gridOk ? "ToGrid/GridCenter agree" : "mismatch");

            // 8. avatar (only meaningful once a world exists)
            if (W.Active)
            {
                Check("avatar", W.Steve != null && W.SteveHead != null,
                    "steve=" + (W.Steve != null) + " head=" + (W.SteveHead != null) +
                    " scale=" + W.SteveScale.ToString("F2"));

                // 8b. Third-person hand: the default hotbar holds blocks, so the held cube
                // must exist AND be visible. Tools alone are not enough - with a block
                // selected all four of them are switched off.
                bool handOk = false;
                string handInfo = "no held item object";
                try
                {
                    bool blockSel = IsBlockItem(CurrentItem);
                    GameObject visible = blockSel ? W.HeldBlock :
                        CurrentItem == "tool:shield" ? W.ToolShield :
                        CurrentItem == "tool:pearl" ? W.ToolPearl :
                        CurrentItem == "tool:sword" ? W.ToolSword : W.ToolPick;
                    handOk = visible != null && visible.activeInHierarchy;
                    handInfo = "item=" + CurrentItem + " visible=" + handOk +
                               " heldBlock=" + (W.HeldBlock != null);
                }
                catch (Exception e) { handInfo = e.GetType().Name; }
                Check("third person held item", handOk, handInfo);

                // 8c. Spawn sanity: Steve must be near the actual fight. In Total Assault a
                // distant boss used to drag the origin tens of units away, so this catches
                // the regression directly instead of relying on a screenshot.
                float nearest = float.MaxValue;
                try
                {
                    lock (W.Gate)
                    {
                        for (int i = 0; i < W.CharPos.Count; i++)
                        {
                            var p = W.CharPos[i];
                            float d = Vector2.Distance(new Vector2(p.x, p.z),
                                                       new Vector2(W.StevePos.x, W.StevePos.z));
                            if (d < nearest) nearest = d;
                        }
                    }
                }
                catch { }
                bool spawnOk = nearest < 25f;
                Check("spawn position", spawnOk,
                    "steve=" + W.StevePos.ToString("F1") + " origin=" + W.Origin.ToString("F1") +
                    " nearest unit=" + (nearest < float.MaxValue ? nearest.ToString("F1") : "n/a"));
            }

            Line("");
            Line("RESULT: " + W.SelfTestPass + " passed, " + W.SelfTestFail + " failed");
            Flush("selftest");
        }

        /// <summary>
        /// Proves a real hit moves HP through the battle pipeline. Runs once per session, the
        /// first time a world exists: one point of damage, immediately healed back.
        /// </summary>
        internal static void BattleSelfTest()
        {
            if (W.BattleTestRun)
            {
                return;
            }
            Transform target = null;
            lock (W.Gate)
            {
                for (int i = 0; i < W.CharTransforms.Count; i++)
                {
                    if (W.CharTransforms[i] != null)
                    {
                        target = W.CharTransforms[i];
                        break;
                    }
                }
            }
            if (target == null)
            {
                return;
            }
            W.BattleTestRun = true;

            W.SelfTestLines.Clear();
            Line("battle damage probe - " + DateTime.Now.ToString("HH:mm:ss"));
            try
            {
                var cv = VisualOf(target);
                var be = cv == null ? null : EntityOf(cv);
                if (be == null)
                {
                    Check("damage probe", false, "no BattleEntity on " + target.gameObject.name);
                }
                else
                {
                    long max = be.MaxHitPoint;
                    long before = be.HitPoint;
                    bool moved = ApplyThroughPipeline(be, 1);
                    long after = be.HitPoint;
                    if (moved)
                    {
                        try { be.AddHitPoint(1); } catch { }
                    }
                    Check("damage probe", moved && before - after == 1,
                        target.gameObject.name + " hp " + before + " -> " + after +
                        " (max " + max + ", pipeline=" + moved + ")");
                }
            }
            catch (Exception e)
            {
                Check("damage probe", false, e.GetType().Name + ": " + e.Message);
            }
            Line("");
            Line("RESULT: " + W.SelfTestPass + " passed, " + W.SelfTestFail + " failed");
            Flush("selftest");
        }

        private static void Check(string name, bool ok, string detail)
        {
            if (ok)
            {
                W.SelfTestPass++;
                Line("  PASS " + name + (string.IsNullOrEmpty(detail) ? "" : " - " + detail));
            }
            else
            {
                W.SelfTestFail++;
                Line("  FAIL " + name + (string.IsNullOrEmpty(detail) ? "" : " - " + detail));
            }
        }

        private static readonly StringBuilder SelfTestBuffer = new StringBuilder(4096);

        private static void Line(string s)
        {
            W.SelfTestLines.Add(s);
            SelfTestBuffer.AppendLine(s);
        }

        private static void Flush(string kind)
        {
            string text = SelfTestBuffer.ToString();
            SelfTestBuffer.Clear();
            try
            {
                Plugin.L.LogInfo("[McWorld] self test\n" + text);
            }
            catch { }
            try
            {
                string dir = AssetDir;
                dir = Path.GetFullPath(dir);
                string path = Path.Combine(dir, "selftest.log");
                File.AppendAllText(path, text + Environment.NewLine, Encoding.UTF8);
                W.SelfTestRun = kind == "selftest" ? true : W.SelfTestRun;
            }
            catch (Exception e)
            {
                try { Plugin.L.LogWarning("[McWorld] selftest.log: " + e.Message); } catch { }
            }
        }
    }
}
