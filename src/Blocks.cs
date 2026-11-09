using System;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace SchaleMcWorld
{
    public partial class McWorldBehaviour
    {
        /// <summary>How far Steve can reach, measured from his body - not from the camera.</summary>
        private const float Reach = 5.5f;

        private static void HandleHotbarKeys()
        {
            bool changed = false;
            for (int i = 0; i < 9 && i < W.Hotbar.Length; i++)
            {
                if (NativeInput.Pressed(NativeInput.VkDigit1 + i))
                {
                    W.SelectedBlock = i;
                    changed = true;
                }
            }

            float scroll = UnityEngine.Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) > 0.01f)
            {
                int n = W.Hotbar.Length;
                W.SelectedBlock = (W.SelectedBlock + (scroll > 0f ? -1 : 1) + n) % n;
                changed = true;
            }
            if (changed)
            {
                W.HeldSword = false;
                RefreshHeldItems();
                UpdateFpHand();
            }
        }

        private static void HandleInput()
        {
            HandleHotbarKeys();

            // Buttons are read from Win32: the window procedure swallows them before the game
            // sees them, so UnityEngine.Input.GetMouseButtonDown would always be false.
            // A student under the crosshair takes priority over the block behind them, so the
            // sword swing happens instead of digging through the floor.
            if (NativeInput.Pressed(NativeInput.VkLButton))
            {
                UseItem();
            }
            if (NativeInput.Pressed(NativeInput.VkRButton))
            {
                SecondaryUse();
            }
        }

        /// <summary>Left click: attack a unit, otherwise break the block under the crosshair.</summary>
        private static void UseItem()
        {
            if (TryAttackStudent())
            {
                return;
            }
            StartSwing();
            BreakBlockAtCursor();
        }

        /// <summary>Right click: throw a pearl, place a block, or (in UI mode) restore a widget.</summary>
        private static void SecondaryUse()
        {
            string item = CurrentItem;
            if (item == "tool:pearl")
            {
                ThrowPearl();
                return;
            }
            if (item == "tool:shield")
            {
                return; // the shield is used by holding the button
            }
            if (item == "tool:sword" || item == "tool:pickaxe")
            {
                return;
            }
            PlaceBlockAtCursor();
        }

        private static string CurrentBlockType()
        {
            string key = BlockKeyOf(CurrentItem);
            return key == null ? null : key.ToLowerInvariant();
        }

        /// <summary>Steve's eye: the origin every reach check is measured from.</summary>
        private static Vector3 SteveEye()
        {
            return W.StevePos + Vector3.up * (W.SteveBodyHeight * 0.82f);
        }

        // ---- aiming ---------------------------------------------------------

        /// <summary>
        /// The ray the player is actually aiming with.
        ///  * third person: the virtual crosshair (W.AimX / W.AimY) through the battle camera.
        ///  * first person, mouse locked: straight through the centre of the screen.
        ///  * first person, free pointer: through the OS cursor, so the block under the arrow
        ///    is the one you get.
        /// The old code kept using the crosshair coordinates in first person, where they are
        /// stale - that is why placing and digging "did nothing" there.
        /// </summary>
        private static bool CrosshairRay(out Vector3 origin, out Vector3 direction)
        {
            if (W.UiMode && W.Cam != null)
            {
                origin = Vector3.zero;
                direction = Vector3.forward;
                try
                {
                    var t = W.Cam.transform;
                    float nx = 0f, ny = 0f;
                    if (!W.FpLook)
                    {
                        var cur = NativeInput.CursorScreen();
                        nx = (cur.x / Mathf.Max(Screen.width, 1f)) * 2f - 1f;
                        ny = (cur.y / Mathf.Max(Screen.height, 1f)) * 2f - 1f;
                    }
                    float fovY = Mathf.Clamp(W.Cam.fieldOfView, 5f, 120f) * Mathf.Deg2Rad;
                    float tanY = Mathf.Tan(fovY * 0.5f);
                    float tanX = tanY * Mathf.Max(W.Cam.aspect, 0.2f);
                    origin = t.position;
                    direction = t.forward + t.right * (nx * tanX) + t.up * (ny * tanY);
                    if (direction.sqrMagnitude < 0.0001f)
                    {
                        return false;
                    }
                    direction = direction.normalized;
                    return true;
                }
                catch
                {
                    return false;
                }
            }
            return AimRay(out origin, out direction);
        }

        /// <summary>
        /// The point the player is aiming at, clamped to Steve's reach. The crosshair decides
        /// the target exactly (block face, or the ground tile under it); only when that point
        /// is further than an arm's length does it slide back along the Steve->target line,
        /// which is Minecraft's "place at the closest reachable spot" behaviour. Measuring
        /// from the eye along a re-aimed ray - the old approach - landed one or two tiles off
        /// from what the crosshair was on, the "placing is inaccurate" bug.
        /// </summary>
        private static bool TargetPoint(out Vector3 point, out bool onBlock,
                                        out Vector3Int blockCell, out Vector3Int blockNormal)
        {
            point = Vector3.zero;
            onBlock = false;
            blockCell = default;
            blockNormal = default;
            if (!CrosshairRay(out var origin, out var dir))
            {
                return false;
            }

            Vector3 hit;
            if (TraceBlocks(origin, dir, 90f, out blockCell, out blockNormal))
            {
                hit = GridCenter(blockCell) -
                      new Vector3(blockNormal.x, blockNormal.y, blockNormal.z) * 0.5f;
                onBlock = true;
            }
            else if (dir.y < -0.02f)
            {
                float t = (W.GroundY - origin.y) / dir.y;
                if (t <= 0.2f || t >= 200f)
                {
                    return false;
                }
                hit = origin + dir * t;
            }
            else
            {
                // Looking level or up: the ray never meets the ground plane. Refusing here
                // made first-person placing impossible whenever the player was not staring
                // at their feet. Fall back to a reachable tile straight ahead of Steve.
                var flatDir = new Vector3(dir.x, 0f, dir.z);
                if (flatDir.sqrMagnitude < 0.0001f)
                {
                    return false;
                }
                hit = W.StevePos + flatDir.normalized * (Reach * 0.6f);
                hit.y = W.GroundY;
            }

            // Reach is measured horizontally from Steve's feet, like an arm's length.
            Vector3 flat = hit - W.StevePos;
            flat.y = 0f;
            float dist = flat.magnitude;
            if (dist > Reach)
            {
                if (onBlock)
                {
                    return false; // a block out of arm's length simply cannot be touched
                }
                if (dist < 0.001f)
                {
                    return false;
                }
                hit = W.StevePos + (flat / dist) * Reach;
            }
            point = hit;
            return true;
        }

        private static void BreakBlockAtCursor()
        {
            if (TargetPoint(out _, out bool onBlock, out var cell, out _) && onBlock)
            {
                RemoveBlock(cell);
                return;
            }
            // Nothing of ours in the way: let the crosshair chew through the level's own
            // scenery instead, which is what "break the background blocks" means.
            BreakSceneProp();
        }

        private static void PlaceBlockAtCursor()
        {
            // A tool in hand places nothing: without this guard the tool slots silently built
            // stone blocks, because BlockMaterials() falls back to stone for unknown keys.
            string type = CurrentBlockType();
            if (type == null)
            {
                return;
            }
            if (!TargetPoint(out var hit, out bool onBlock, out var cell, out var normal))
            {
                return;
            }

            Vector3Int target;
            if (onBlock)
            {
                target = cell + normal;
            }
            else
            {
                // Ground tile: the cell the crosshair is on (reach-clamped above).
                target = new Vector3Int(
                    Mathf.FloorToInt(hit.x - W.Origin.x),
                    Mathf.FloorToInt((W.GroundY + 0.01f) - W.Origin.y),
                    Mathf.FloorToInt(hit.z - W.Origin.z));
            }

            if (W.Blocks.ContainsKey(target))
            {
                return;
            }
            if (OverlapsSteve(target))
            {
                return;
            }
            SpawnBlock(target, type);
        }

        private static bool OverlapsSteve(Vector3Int g)
        {
            Vector3 c = GridCenter(g);
            float half = 0.5f;
            return Mathf.Abs(c.x - W.StevePos.x) < half + SteveRadius
                && Mathf.Abs(c.z - W.StevePos.z) < half + SteveRadius
                && c.y + half > W.StevePos.y
                && c.y - half < W.StevePos.y + SteveHeight;
        }

        /// <summary>
        /// Amanatides &amp; Woo voxel traversal over W.Blocks. Walking the grid directly avoids
        /// adding colliders to every block (and avoids tripping over the scene's physics).
        /// </summary>
        private static bool TraceBlocks(Vector3 origin, Vector3 dir, float maxT,
                                        out Vector3Int hitCell, out Vector3Int hitNormal)
        {
            hitCell = default;
            hitNormal = default;
            if (dir.sqrMagnitude < 0.0001f)
            {
                return false;
            }
            dir = dir.normalized;

            Vector3 p = origin - W.Origin;
            int x = Mathf.FloorToInt(p.x);
            int y = Mathf.FloorToInt(p.y);
            int z = Mathf.FloorToInt(p.z);

            int stepX = dir.x > 0f ? 1 : -1;
            int stepY = dir.y > 0f ? 1 : -1;
            int stepZ = dir.z > 0f ? 1 : -1;

            float tDeltaX = Mathf.Abs(dir.x) < 1e-6f ? float.PositiveInfinity : Mathf.Abs(1f / dir.x);
            float tDeltaY = Mathf.Abs(dir.y) < 1e-6f ? float.PositiveInfinity : Mathf.Abs(1f / dir.y);
            float tDeltaZ = Mathf.Abs(dir.z) < 1e-6f ? float.PositiveInfinity : Mathf.Abs(1f / dir.z);

            float nextX = stepX > 0 ? (x + 1 - p.x) : (p.x - x);
            float nextY = stepY > 0 ? (y + 1 - p.y) : (p.y - y);
            float nextZ = stepZ > 0 ? (z + 1 - p.z) : (p.z - z);

            float tMaxX = tDeltaX * nextX;
            float tMaxY = tDeltaY * nextY;
            float tMaxZ = tDeltaZ * nextZ;

            var normal = new Vector3Int();
            float t = 0f;
            var cell = new Vector3Int(x, y, z);

            if (IsSolid(cell))
            {
                hitCell = cell;
                return true;
            }

            for (int i = 0; i < 256 && t <= maxT; i++)
            {
                if (tMaxX < tMaxY && tMaxX < tMaxZ)
                {
                    x += stepX;
                    t = tMaxX;
                    tMaxX += tDeltaX;
                    normal = new Vector3Int(-stepX, 0, 0);
                }
                else if (tMaxY < tMaxZ)
                {
                    y += stepY;
                    t = tMaxY;
                    tMaxY += tDeltaY;
                    normal = new Vector3Int(0, -stepY, 0);
                }
                else
                {
                    z += stepZ;
                    t = tMaxZ;
                    tMaxZ += tDeltaZ;
                    normal = new Vector3Int(0, 0, -stepZ);
                }

                cell = new Vector3Int(x, y, z);
                if (IsSolid(cell))
                {
                    hitCell = cell;
                    hitNormal = normal;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Removes one piece of the level's own scenery. Everything is filtered hard: only
        /// small, static, opaque-looking props are eligible, never a character, never
        /// something we created, never the floor or the skybox. The object is hidden rather
        /// than destroyed so the game's own references stay valid.
        /// </summary>
        private static bool BreakSceneProp()
        {
            try
            {
                if (!Plugin.BreakSceneProps.Value)
                {
                    return false;
                }
                if (!CrosshairRay(out var o, out var d))
                {
                    return false;
                }
                Vector3 eye = SteveEye();
                float maxT = (o - eye).magnitude + Reach + 1f;

                RaycastHit[] hits = Physics.RaycastAll(o, d, maxT);
                if (hits == null || hits.Length == 0)
                {
                    return false;
                }

                GameObject best = null;
                float bestDist = float.MaxValue;
                for (int i = 0; i < hits.Length; i++)
                {
                    var h = hits[i];
                    if (h.collider == null)
                    {
                        continue;
                    }
                    if ((h.point - eye).sqrMagnitude > (Reach + 0.8f) * (Reach + 0.8f))
                    {
                        continue;
                    }
                    if (h.distance >= bestDist)
                    {
                        continue;
                    }
                    var go = h.collider.gameObject;
                    if (go == null || !IsBreakableProp(go))
                    {
                        continue;
                    }
                    best = go;
                    bestDist = h.distance;
                }

                if (best == null)
                {
                    return false;
                }
                var mr = best.GetComponentInChildren(Il2CppType.Of<MeshRenderer>())?.TryCast<MeshRenderer>();
                string nm = best.name;
                Vector3 sz = mr != null ? mr.bounds.size : Vector3.zero;
                best.SetActive(false);
                Plugin.L.LogInfo("[McWorld] broke scene prop '" + nm + "' size=" + sz.ToString("F2") +
                                 " at " + best.transform.position.ToString("F2"));
                return true;
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("[McWorld] scene prop break failed: " + e.Message);
                return false;
            }
        }

        private static bool IsBreakableProp(GameObject go)
        {
            try
            {
                string nm = go.name ?? "";
                if (nm.StartsWith("blk_") || nm.StartsWith("Mc"))
                {
                    return false; // ours
                }
                string low = nm.ToLowerInvariant();
                string[] never =
                {
                    "floor", "ground", "terrain", "land", "sky", "dome", "bg", "backdrop",
                    "camera", "light", "canvas", "ui_", "button", "quad",
                };
                for (int i = 0; i < never.Length; i++)
                {
                    if (low.Contains(never[i]))
                    {
                        return false;
                    }
                }

                if (go.GetComponentInParent(Il2CppType.Of<CharacterVisual>()) != null)
                {
                    return false;
                }

                var mr = go.GetComponentInChildren(Il2CppType.Of<MeshRenderer>())?.TryCast<MeshRenderer>();
                if (mr == null || !mr.enabled)
                {
                    return false;
                }

                Vector3 s = mr.bounds.size;
                if (s.x > 9f || s.y > 6f || s.z > 9f || s.x < 0.05f || s.y < 0.05f || s.z < 0.05f)
                {
                    return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void SpawnBlock(Vector3Int g, string typeKey)
        {
            var mats = BlockMaterials(typeKey);
            var mesh = BlockMesh();
            if (mats == null || mesh == null)
            {
                Plugin.L.LogWarning("[McWorld] block " + typeKey + " has no materials");
                return;
            }

            var go = new GameObject("blk_" + g.x + "_" + g.y + "_" + g.z);
            go.transform.position = GridCenter(g);
            go.transform.rotation = Quaternion.identity;
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();
            mf.sharedMesh = mesh;
            mr.sharedMaterials = mats;

            if (W.BlockRoot != null)
            {
                go.transform.SetParent(W.BlockRoot.transform, true);
            }
            ApplyLayer(go, W.CharLayer);
            W.Blocks[g] = go;
            W.BlockTypes[g] = typeKey;
        }

        // ---- material self healing ------------------------------------------

        private static float _nextMatRefresh;

        /// <summary>
        /// The game sweeps runtime materials just like textures. A swept material keeps its
        /// wrapper, so a placed block silently turns black (or invisible). MakeMat() evicts
        /// dead cache entries and rebuilds them, so re-assigning the "same" materials once a
        /// second heals every block, tool and hand without anyone seeing it happen.
        /// </summary>
        internal static void RefreshVisualMaterials()
        {
            if (Time.unscaledTime < _nextMatRefresh)
            {
                return;
            }
            _nextMatRefresh = Time.unscaledTime + 2f;

            try
            {
                foreach (var pair in W.Blocks)
                {
                    try
                    {
                        var go = pair.Value;
                        if (go == null)
                        {
                            continue;
                        }
                        string key;
                        if (!W.BlockTypes.TryGetValue(pair.Key, out key) || key == null)
                        {
                            continue;
                        }
                        var mr = go.GetComponent(Il2CppType.Of<MeshRenderer>())?.TryCast<MeshRenderer>();
                        if (mr == null)
                        {
                            continue;
                        }
                        var mats = BlockMaterials(key);
                        if (mats != null)
                        {
                            mr.sharedMaterials = mats;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // Held tools (third person body).
            RebindTool(W.ToolPick, "iron_pickaxe");
            RebindTool(W.ToolSword, "diamond_sword");
            RebindTool(W.ToolShield, "shield");
            RebindTool(W.ToolPearl, "ender_pearl");
            // Low-frequency insurance: re-assert which hand item is visible and rebind the
            // block materials. If a wrapper was swept while switching slots, the hand would
            // otherwise stay empty until the next key press.
            RefreshHeldItems();
        }

        /// <summary>
        /// Keeps the third-person held block showing the CURRENT hotbar item. The materials
        /// are shared with the placed blocks, so a sweep that turns the world black would
        /// take the held cube with it.
        /// </summary>
        internal static void RebindHeldBlock(string item)
        {
            try
            {
                var go = W.HeldBlock;
                if (go == null || !go.activeInHierarchy || !IsBlockItem(item))
                {
                    return;
                }
                string key = BlockKeyOf(item);
                var mats = BlockMaterials(string.IsNullOrEmpty(key) ? "mcblock_grass" : key.ToLowerInvariant());
                if (mats == null)
                {
                    return;
                }
                var mr = go.GetComponent(Il2CppType.Of<MeshRenderer>())?.TryCast<MeshRenderer>();
                if (mr != null)
                {
                    mr.sharedMaterials = mats;
                }
            }
            catch { }
        }

        private static void RebindTool(GameObject go, string texName)
        {
            try
            {
                if (go == null || !go.activeInHierarchy)
                {
                    return;
                }
                var mr = go.GetComponent(Il2CppType.Of<MeshRenderer>())?.TryCast<MeshRenderer>();
                if (mr == null)
                {
                    return;
                }
                // LoadDiskTexture is throttled after a sweep. A null here means "wait for the
                // next tick", NOT "bind a textureless material" - that silently turned the
                // tool white/black and looked exactly like a missing held item.
                var tex = LoadDiskTexture(texName);
                if (tex == null)
                {
                    return;
                }
                var mat = MakeMat(tex, false);
                if (mat != null)
                {
                    mr.sharedMaterial = mat;
                }
            }
            catch { }
        }

        private static void RemoveBlock(Vector3Int g)
        {
            if (W.Blocks.TryGetValue(g, out var go))
            {
                if (go != null)
                {
                    UnityEngine.Object.Destroy(go);
                }
                W.Blocks.Remove(g);
                W.BlockTypes.Remove(g);
            }
        }

        private static void ClearBlocks()
        {
            foreach (var pair in W.Blocks)
            {
                if (pair.Value != null)
                {
                    UnityEngine.Object.Destroy(pair.Value);
                }
            }
            W.Blocks.Clear();
            W.BlockTypes.Clear();
        }
    }
}
