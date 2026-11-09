using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using MX.Logic.BattleEntities;
using MX.Logic.Battles;
using MX.Logic.Skills;
using UnityEngine;

namespace SchaleMcWorld
{
    /// <summary>
    /// Held tools: a pickaxe for mining, a sword for hitting students and enemies. Both are
    /// flat item sprites (the vanilla 16x16 textures) exactly like Minecraft's held item.
    ///
    /// Damage goes through BattleEntity.ApplyDamage - the battle's own entry point - instead
    /// of poking HitPoint directly. That is what raises CharacterDying / CharacterDied and
    /// removes the unit: writing the field left students standing at 0 HP forever, which is
    /// the "locked HP / blank model" bug.
    /// </summary>
    public partial class McWorldBehaviour
    {
        private const float SwingTime = 0.26f;

        /// <summary>Held item size in Steve's model space (1 unit = 16 skin pixels).</summary>
        private const float ItemSize = 0.52f;
        private const float ItemThickness = 0.04f;
        private const float HeldBlockSize = 0.34f;

        /// <summary>
        /// Local offset inside the right arm. The arm is 0.75 long, so -0.42 is just below the
        /// hand; +0.20 in Z pushes the item out in front of the torso. The old +0.06 left it
        /// buried inside the arm/body from the 55 degree dungeon camera - the item existed but
        /// could not be seen.
        /// </summary>
        private static readonly Vector3 RightHandLocal = new Vector3(0f, -0.42f, 0.20f);
        private static readonly Vector3 LeftHandLocal = new Vector3(0f, -0.40f, 0.22f);

        private static readonly Rect FullUv = new Rect(0f, 0f, 1f, 1f);

        [DllImport("GameAssembly.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr il2cpp_class_get_field_from_name(IntPtr klass, IntPtr name);

        [DllImport("GameAssembly.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int il2cpp_field_get_offset(IntPtr field);

        private static void BuildTools(Transform armR, Transform armL)
        {
            if (armR == null)
            {
                return;
            }
            W.HeldVisuals.Clear();

            var pick = MakeItemTool(armR, "toolPickaxe", LoadDiskTexture("iron_pickaxe"), ItemSize, RightHandLocal);
            var sword = MakeItemTool(armR, "toolSword", LoadDiskTexture("diamond_sword"), ItemSize + 0.06f, RightHandLocal);
            var pearl = MakeItemTool(armR, "toolPearl", LoadDiskTexture("ender_pearl"), 0.38f, RightHandLocal);

            // The shield rides the left arm, a little in front of the body, so raising the arm
            // puts the plate over the chest.
            var shield = armL != null
                ? MakeItemTool(armL, "toolShield", LoadDiskTexture("shield"), 0.66f, LeftHandLocal)
                : null;

            // The hotbar starts with nine BLOCKS, not tools. Without a 3D block in the hand,
            // RefreshHeldItems() hid every tool and Steve looked empty-handed - the long
            // standing "third person does not show the held item" complaint.
            var block = MakeHeldBlock(armR);

            W.ToolPick = pick;
            W.ToolSword = sword;
            W.ToolShield = shield;
            W.ToolPearl = pearl;
            W.HeldBlock = block;
            SetActiveSafe(sword, false);
            SetActiveSafe(shield, false);
            SetActiveSafe(pearl, false);
            Plugin.L.LogInfo("[McWorld] tools built: pick=" + (pick != null) + " sword=" + (sword != null) +
                             " shield=" + (shield != null) + " pearl=" + (pearl != null) +
                             " heldBlock=" + (block != null));
        }

        /// <summary>
        /// A small 3D cube using the very same three materials as a placed block. It is tilted
        /// so the dungeon camera sees the top and two sides, exactly like Minecraft's
        /// third-person held block. Deliberately NOT added to HeldVisuals: billboarding a cube
        /// would flatten it into a sprite.
        /// </summary>
        private static GameObject MakeHeldBlock(Transform arm)
        {
            try
            {
                var mats = BlockMaterials("mcblock_grass");
                var mesh = BlockMesh();
                if (mats == null || mesh == null)
                {
                    return null;
                }
                var go = new GameObject("heldBlock");
                go.transform.SetParent(arm, false);
                go.transform.localPosition = RightHandLocal;
                go.transform.localRotation = Quaternion.Euler(-18f, 34f, 0f);
                go.transform.localScale = Vector3.one * HeldBlockSize;

                var mf = go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                mf.sharedMesh = mesh;
                mr.sharedMaterials = mats;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                ApplyLayer(go, W.CharLayer);
                return go;
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("[McWorld] held block: " + e.Message);
                return null;
            }
        }

        private static void SetActiveSafe(GameObject go, bool on)
        {
            try
            {
                if (go != null && go.activeSelf != on)
                {
                    go.SetActive(on);
                }
            }
            catch { }
        }

        /// <summary>
        /// A flat, alpha clipped quad carrying the item texture, welded to the hand and turned
        /// towards the camera every frame (BillboardHeld). A plate fixed to the arm is edge-on
        /// from a 55 degree camera and reads as a stray coloured line.
        /// </summary>
        private static GameObject MakeItemTool(Transform arm, string name, Texture2D tex,
                                               float size, Vector3 localPos)
        {
            if (tex == null)
            {
                // A missing PNG must not make the whole tool vanish. Bind the engine-owned
                // white pixel so the object exists; RebindTool swaps in the real sprite as
                // soon as the texture becomes available.
                Plugin.L.LogWarning("[McWorld] tool texture missing: " + name);
                tex = Texture2D.whiteTexture;
            }
            var mat = MakeMat(tex, false);
            if (mat == null)
            {
                Plugin.L.LogWarning("[McWorld] tool material failed: " + name);
                return null;
            }

            var go = new GameObject(name);
            go.transform.SetParent(arm, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.identity;

            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            var uvs = new Rect[6];
            for (int i = 0; i < 6; i++)
            {
                uvs[i] = FullUv;
            }
            mf.sharedMesh = BuildBox(new Vector3(size, size, ItemThickness), uvs, null, 1);
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            ApplyLayer(go, W.CharLayer);
            W.HeldVisuals.Add(go);
            return go;
        }

        /// <summary>
        /// Turns every visible held item towards the camera. Without this the sword is a
        /// millimetre-thin sliver from behind, and the shield shows its back.
        /// </summary>
        private static void BillboardHeld()
        {
            if (W.Cam == null || W.HeldVisuals.Count == 0 || W.UiMode)
            {
                return;
            }
            Quaternion face;
            try
            {
                face = W.Cam.transform.rotation * Quaternion.Euler(-6f, 180f, 0f);
            }
            catch
            {
                return;
            }
            for (int i = 0; i < W.HeldVisuals.Count; i++)
            {
                var go = W.HeldVisuals[i];
                if (go == null)
                {
                    continue;
                }
                try
                {
                    if (go.activeInHierarchy)
                    {
                        go.transform.rotation = face;
                    }
                }
                catch { }
            }
        }

        private static void SetSwordOut(bool sword)
        {
            if (W.HeldSword == sword)
            {
                return;
            }
            W.HeldSword = sword;
            RefreshHeldItems();
        }

        /// <summary>Draws the sword when a student is within striking distance of a pickaxe.</summary>
        private static void UpdateHeldTool()
        {
            if (W.ToolPick == null || W.ToolSword == null)
            {
                return;
            }
            string item = CurrentItem;
            if (item != "tool:pickaxe")
            {
                if (W.HeldSword)
                {
                    W.HeldSword = false;
                    RefreshHeldItems();
                }
                return;
            }
            SetSwordOut(NearestStudent(Reach + 1.6f, out _) != null);
        }

        private static void StartSwing()
        {
            W.SwingUntil = Time.time + SwingTime;
        }

        private static CharacterVisual VisualOf(Transform tr)
        {
            try
            {
                return tr.gameObject.GetComponent(Il2CppType.Of<CharacterVisual>())?.TryCast<CharacterVisual>();
            }
            catch
            {
                return null;
            }
        }

        private static BattleEntity EntityOf(CharacterVisual cv)
        {
            try
            {
                var ch = cv.Character;
                return ch == null ? null : ch.TryCast<BattleEntity>();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>False once the unit is dead - corpses stay in the scene for a moment.</summary>
        private static bool IsAlive(Transform tr)
        {
            try
            {
                var cv = VisualOf(tr);
                var be = cv == null ? null : EntityOf(cv);
                if (be == null)
                {
                    return true; // not a battle unit (turret, prop): let it be hit
                }
                return be.HitPoint > 0;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>Nearest student roughly in front of the crosshair, or null.</summary>
        private static Transform NearestStudent(float maxDist, out Vector3 point)
        {
            point = Vector3.zero;
            if (!CrosshairRay(out var o, out var dir))
            {
                return null;
            }
            Vector3 eye = SteveEye();
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
                if (!IsAlive(tr))
                {
                    continue;
                }
                Vector3 body = tr.position + Vector3.up * 0.5f;
                Vector3 v = body - eye;
                float d = v.magnitude;
                if (d > maxDist || d < 0.01f)
                {
                    continue;
                }
                if (Vector3.Angle(dir, v) > 32f)
                {
                    continue;
                }
                if (d < bestD)
                {
                    bestD = d;
                    best = tr;
                    point = body;
                }
            }
            return best;
        }

        /// <summary>Sword hit: knock the student back, swing the arm, apply real damage.</summary>
        private static bool TryAttackStudent()
        {
            var tr = NearestStudent(Reach + 1.2f, out var body);
            if (tr == null)
            {
                return false;
            }
            try
            {
                SetSwordOut(true);
                StartSwing();

                Vector3 from = new Vector3(W.StevePos.x, 0f, W.StevePos.z);
                Vector3 to = new Vector3(body.x, 0f, body.z);
                Vector3 dir = to - from;
                dir.y = 0f;
                if (dir.sqrMagnitude < 0.0001f)
                {
                    dir = Vector3.forward;
                }
                dir = dir.normalized * 0.28f;
                Vector3 p = tr.position;
                tr.position = new Vector3(p.x + dir.x, p.y, p.z + dir.z);

                HitUnit(tr);
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("[McWorld] attack failed: " + e.Message);
            }
            return true;
        }

        /// <summary>
        /// Removes a fraction of the target's MAXIMUM HP through the battle's own damage
        /// entry point, so HP bars, damage floaters, the dying event and the actual death all
        /// happen exactly like a normal hit. With SwordDamage = 1.0 this is a one hit kill.
        /// </summary>
        private static void HitUnit(Transform tr)
        {
            try
            {
                var cv = VisualOf(tr);
                if (cv == null)
                {
                    Plugin.L.LogWarning("[McWorld] hit: no CharacterVisual on " + tr.gameObject.name);
                    return;
                }
                var be = EntityOf(cv);
                if (be == null)
                {
                    Plugin.L.LogWarning("[McWorld] hit: Character is not a BattleEntity (" + tr.gameObject.name + ")");
                    return;
                }

                long max = be.MaxHitPoint;
                long before = be.HitPoint;
                if (before <= 0)
                {
                    return;
                }
                float frac = Mathf.Clamp(W.DamageFraction, 0.01f, 1f);
                long dmg = (long)Math.Ceiling(max * (double)frac);
                if (dmg < 1)
                {
                    dmg = 1;
                }
                // Never "overkill-heal": a hit removes at most what is left.
                bool lethal = dmg >= before;
                if (dmg > before)
                {
                    dmg = before;
                }

                string how = "ApplyDamage";
                if (!ApplyThroughPipeline(be, dmg))
                {
                    how = "AddHitPoint";
                    if (!ApplyHitPoint(be, dmg))
                    {
                        how = "field";
                        WriteHitPoint(be, Math.Max(0, before - dmg));
                    }
                }

                long after = be.HitPoint;
                bool dead = after <= 0;
                if (dead)
                {
                    // The visual is what turns a dead unit into the blank, frozen model that
                    // could not be killed again. Tell it once, explicitly.
                    try
                    {
                        if (!cv.isDeadCalled)
                        {
                            cv.Dead();
                        }
                    }
                    catch (Exception e)
                    {
                        Plugin.L.LogWarning("[McWorld] visual.Dead: " + e.Message);
                    }
                }
                ShowDamageNumber(cv, dmg);

                Plugin.L.LogInfo("[McWorld] sword hit " + tr.gameObject.name + ": hp " + before + " -> " +
                                 after + " (max " + max + ", dmg " + dmg + ", via " + how +
                                 ", lethal=" + lethal + ", dead=" + dead + ")");
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("[McWorld] damage failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>BattleEntity.ApplyDamage(attacker, DamageResult, SkillSpecification).</summary>
        private static bool ApplyThroughPipeline(BattleEntity be, long dmg)
        {
            long before = be.HitPoint;
            try
            {
                var result = new DamageResult(dmg, FlatData.FontType.Damage);
                var spec = SkillSpecification.Dummy;
                be.ApplyDamage(be, result, spec);
                return be.HitPoint != before;
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("[McWorld] ApplyDamage threw: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        private static bool ApplyHitPoint(BattleEntity be, long dmg)
        {
            long before = be.HitPoint;
            try
            {
                be.AddHitPoint(-dmg);
                return be.HitPoint != before;
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("[McWorld] AddHitPoint threw: " + e.Message);
                return false;
            }
        }

        /// <summary>Pops the game's own damage number above the target.</summary>
        private static void ShowDamageNumber(CharacterVisual cv, long dmg)
        {
            try
            {
                var floater = cv.uiDamageFloater;
                if (floater != null)
                {
                    floater.OnStatusRegisted(cv, FlatData.FontType.Damage, dmg);
                }
            }
            catch { }
        }

        private static bool WriteHitPoint(BattleEntity be, long value)
        {
            try
            {
                IntPtr klass = IL2CPP.il2cpp_object_get_class(be.Pointer);
                if (klass == IntPtr.Zero)
                {
                    return false;
                }
                IntPtr namePtr = Marshal.StringToHGlobalAnsi("_HitPoint_k__BackingField");
                IntPtr field;
                try
                {
                    field = il2cpp_class_get_field_from_name(klass, namePtr);
                }
                finally
                {
                    Marshal.FreeHGlobal(namePtr);
                }
                if (field == IntPtr.Zero)
                {
                    return false;
                }
                int offset = il2cpp_field_get_offset(field);
                if (offset <= 0)
                {
                    return false;
                }
                Marshal.WriteInt64(be.Pointer + offset, value);
                return true;
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("[McWorld] direct HP write failed: " + e.Message);
                return false;
            }
        }
    }
}
