using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace SchaleMcWorld
{
    /// <summary>
    /// Two extra modes on top of the dungeon view:
    ///
    ///  * F8 first person - the camera moves into the player's eyes, the mouse looks around,
    ///    WASD walks, and a real 3D hand holding the selected item sits in the lower right.
    ///    Tab releases the mouse so Blue Archive's own buttons can be clicked.
    ///
    ///  * Q interface mining - Blue Archive's UI is NGUI (UIWidget / UIPanel / UIButton), so a
    ///    click can hide a widget and the collider behind it, and put it back later.
    /// </summary>
    public partial class McWorldBehaviour
    {
        internal class BrokenUi
        {
            public string Name;
            public Vector2 Center;
            public readonly List<UIWidget> Widgets = new List<UIWidget>();
            public readonly List<Collider> Colliders = new List<Collider>();
        }

        /// <summary>Scratch buffer reused by the hit test (cleared on every click).</summary>
        private static readonly List<UIWidget> ScanWidgets = new List<UIWidget>(512);

        // ---- mode switching --------------------------------------------------

        private static void SetUiMode(bool on)
        {
            if (W.UiMode == on)
            {
                return;
            }
            W.UiMode = on;
            W.CamInited = false;
            W.CamApplied = false;

            if (on)
            {
                EnsureFirstPerson();
                // The battle keeps re-driving its camera from scripts attached elsewhere.
                // Re-assert ownership on every mode flip, including the FP branch (the old
                // code only did this in the dungeon camera path, so F8 left the view drifting).
                if (W.Cam != null)
                {
                    ForceTakeOver(W.Cam);
                }
                NativeInput.Install();
                ApplyFpProfile();
                NativeInput.ClearHeld();
                if (W.Steve != null)
                {
                    W.Steve.SetActive(false);
                }
                Say("[MC] 第一人称：Tab 锁定/放开鼠标 ｜ WASD 移动 ｜ 左键挖/打 ｜ 右键放/用");
            }
            else
            {
                DestroyFpHand();
                if (W.Steve != null)
                {
                    W.Steve.SetActive(true);
                }
                if (W.Active)
                {
                    NativeInput.Install();
                    NativeInput.SetProfile(false, true, true, true);
                    NativeInput.ClearHeld();
                    Cursor.visible = false;
                    W.CamInited = false;
                }
                else
                {
                    // Leaving first person without a world layer means handing the camera back
                    // to the game, otherwise the battle view stays frozen in the eyes.
                    ReleaseCamera();
                    W.Cam = null;
                    W.FpCamOwned = false;
                    NativeInput.Uninstall();
                    Cursor.visible = true;
                }
                Cursor.lockState = CursorLockMode.None;
                Say("[MC] 返回斜俯视视角");
            }
        }

        /// <summary>Prepares the viewpoint, the camera ownership and the body pose.</summary>
        private static void EnsureFirstPerson()
        {
            if (!W.FpInited)
            {
                W.FpInited = true;
                // Face the way Steve is facing, not the way the dungeon camera is pointing:
                // those differ by the camera yaw/pitch, so reading eulerAngles.y off the
                // camera spun the first-person view away from the block world.
                float yaw = W.SteveYaw;
                try
                {
                    if (W.Cam != null && Mathf.Abs(yaw) < 0.001f)
                    {
                        yaw = W.Cam.transform.eulerAngles.y;
                    }
                }
                catch { }
                W.FpYaw = yaw;
                // A level view never touches the ground plane, so blocks could neither be
                // seen nor placed. Start with a slight downward tilt like a real FPS spawn.
                W.FpPitch = 12f;
                // Mouse look ON by default: that is what "first person" means. Tab releases
                // the pointer when Blue Archive's buttons need clicking.
                W.FpLook = true;
            }

            if (!W.Active)
            {
                // No world layer: put the player where the camera is looking, on the ground.
                if (W.StevePos.sqrMagnitude < 0.0001f)
                {
                    Vector3 start = Vector3.zero;
                    bool have = false;
                    try
                    {
                        var cam = PickCamera();
                        if (cam != null)
                        {
                            Vector3 p = cam.transform.position + cam.transform.forward * 10f;
                            start = new Vector3(p.x, W.GroundY, p.z);
                            have = true;
                        }
                    }
                    catch { }
                    if (!have)
                    {
                        start = new Vector3(0f, W.GroundY, 0f);
                    }
                    // Find the actual floor below that point: guessing y=0 dropped the view
                    // under the lobby ground and rendered "nothing".
                    try
                    {
                        if (Physics.Raycast(start + Vector3.up * 25f, Vector3.down, out var g,
                                            80f, ~0, QueryTriggerInteraction.Ignore))
                        {
                            if (g.collider != null && g.collider.gameObject != null &&
                                (g.collider.gameObject.name ?? "").IndexOf("Mc", StringComparison.Ordinal) != 0)
                            {
                                start.y = g.point.y;
                                W.GroundY = g.point.y;
                            }
                        }
                    }
                    catch { }
                    W.StevePos = start;
                    W.SteveVel = Vector3.zero;
                    W.FpAnchor = start;
                }
            }

            if (W.Cam == null)
            {
                W.Cam = PickCamera();
            }
            if (W.Cam != null && !W.CamFovSaved)
            {
                TakeOverCamera(W.Cam);
                W.FpCamOwned = true;
            }

            if (W.FpHand == null)
            {
                BuildFpHand();
                RefreshHeldItems();
            }
        }

        private static void ApplyFpProfile()
        {
            if (W.FpLook)
            {
                // Mouse look: pointer pinned and hidden, everything swallowed.
                NativeInput.SetProfile(false, true, true, true);
                Cursor.visible = false;
                Cursor.lockState = CursorLockMode.None;
            }
            else
            {
                // Free pointer: the player can click Blue Archive. Buttons are only eaten in
                // mining mode, where a click means "break this widget".
                NativeInput.SetProfile(true, false, W.MineMode, true, true);
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }
        }

        private static void ToggleLookMode()
        {
            W.FpLook = !W.FpLook;
            ApplyFpProfile();
            NativeInput.ClearHeld();
            Say(W.FpLook
                ? "[MC] 鼠标已锁定：移动鼠标环视（Tab 放开鼠标去点蔚蓝档案）"
                : "[MC] 鼠标已放开：可以直接点击蔚蓝档案的按钮（Tab 锁定视角）");
        }

        private static void ToggleMineMode()
        {
            W.MineMode = !W.MineMode;
            if (W.MineMode && W.FpLook)
            {
                // Mining needs the free pointer - keeping the mouse locked made Q look like
                // a dead key ("switching break mode does nothing").
                W.FpLook = false;
                ApplyFpProfile();
            }
            NativeInput.SetButtons(W.MineMode);
            if (W.MineMode)
            {
                SetSwordOut(false);
            }
            Say(W.MineMode
                ? "[MC] 破坏模式：左键挖掉界面元素，右键还原（再按 Q 回到互动）"
                : "[MC] 互动模式：鼠标点击正常操作蔚蓝档案的按钮");
        }

        // ---- hit testing -----------------------------------------------------

        private static void GatherWidgets()
        {
            ScanWidgets.Clear();
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<UIWidget>());
            if (all == null)
            {
                return;
            }
            for (int i = 0; i < all.Length; i++)
            {
                var obj = all[i];
                if (obj == null)
                {
                    continue;
                }
                var w = obj.TryCast<UIWidget>();
                if (w == null)
                {
                    continue;
                }
                try
                {
                    var go = w.gameObject;
                    if (go == null || !go.activeInHierarchy)
                    {
                        continue;
                    }
                    if (w.width < 8 || w.height < 8)
                    {
                        continue;
                    }
                    if (w.alpha < 0.04f)
                    {
                        continue;
                    }
                    if (!w.isVisible)
                    {
                        continue;
                    }
                    ScanWidgets.Add(w);
                }
                catch { }
            }
        }

        private static int CountUiWidgets()
        {
            GatherWidgets();
            return ScanWidgets.Count;
        }

        private static UIWidget PickWidget(Vector2 point)
        {
            GatherWidgets();
            UIWidget best = null;
            float bestScore = float.MinValue;

            for (int i = 0; i < ScanWidgets.Count; i++)
            {
                var w = ScanWidgets[i];
                try
                {
                    var go = w.gameObject;
                    var cam = NGUITools.FindCameraForLayer(go.layer);
                    if (cam == null)
                    {
                        continue;
                    }
                    var corners = w.worldCorners;
                    if (corners == null || corners.Length < 4)
                    {
                        continue;
                    }

                    float minX = float.MaxValue, maxX = float.MinValue;
                    float minY = float.MaxValue, maxY = float.MinValue;
                    bool behind = false;
                    for (int k = 0; k < 4; k++)
                    {
                        Vector3 sp = cam.WorldToScreenPoint(corners[k]);
                        if (sp.z < 0f)
                        {
                            behind = true;
                            break;
                        }
                        if (sp.x < minX) minX = sp.x;
                        if (sp.x > maxX) maxX = sp.x;
                        if (sp.y < minY) minY = sp.y;
                        if (sp.y > maxY) maxY = sp.y;
                    }
                    if (behind)
                    {
                        continue;
                    }
                    if (point.x < minX || point.x > maxX || point.y < minY || point.y > maxY)
                    {
                        continue;
                    }

                    int panelDepth = 0;
                    var panel = w.panel;
                    if (panel != null)
                    {
                        panelDepth = panel.depth;
                    }
                    float area = Mathf.Max(1f, (maxX - minX) * (maxY - minY));
                    float score = panelDepth * 100000f + w.depth * 100f - area * 0.001f;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = w;
                    }
                }
                catch { }
            }
            return best;
        }

        private static GameObject FindUiGroup(GameObject go)
        {
            try
            {
                Transform t = go.transform;
                for (int i = 0; i < 4 && t != null; i++)
                {
                    var btn = t.gameObject.GetComponent(Il2CppType.Of<UIButton>())?.TryCast<UIButton>();
                    if (btn != null)
                    {
                        return t.gameObject;
                    }
                    var lis = t.gameObject.GetComponent(Il2CppType.Of<UIEventListener>())?.TryCast<UIEventListener>();
                    if (lis != null)
                    {
                        return t.gameObject;
                    }
                    t = t.parent;
                }
            }
            catch { }
            return null;
        }

        private static void CollectGroup(Transform t, int depth, BrokenUi into)
        {
            if (t == null || depth > 4)
            {
                return;
            }
            try
            {
                var go = t.gameObject;
                var w = go.GetComponent(Il2CppType.Of<UIWidget>())?.TryCast<UIWidget>();
                if (w != null)
                {
                    into.Widgets.Add(w);
                }
                var c = go.GetComponent(Il2CppType.Of<Collider>())?.TryCast<Collider>();
                if (c != null)
                {
                    into.Colliders.Add(c);
                }
                int n = t.childCount;
                for (int i = 0; i < n; i++)
                {
                    CollectGroup(t.GetChild(i), depth + 1, into);
                }
            }
            catch { }
        }

        // ---- break / restore -------------------------------------------------

        private static void BreakUiUnderCursor()
        {
            try
            {
                Vector2 point = NativeInput.CursorScreen();
                var w = PickWidget(point);
                Plugin.L.LogInfo("[McWorld] ui break: cursor=" + point.ToString("F0") +
                                 " visible widgets=" + ScanWidgets.Count +
                                 " -> " + (w != null && w.gameObject != null ? w.gameObject.name : "(none)"));
                if (w == null)
                {
                    Say("[MC] 光标下没有界面元素");
                    return;
                }

                var entry = new BrokenUi { Name = (w.gameObject != null ? w.gameObject.name : "ui") };
                entry.Center = point;

                var group = FindUiGroup(w.gameObject);
                if (group != null)
                {
                    CollectGroup(group.transform, 0, entry);
                }
                if (entry.Widgets.Count == 0)
                {
                    entry.Widgets.Add(w);
                }
                // A runaway group (a whole screen) is broken one widget at a time instead:
                // hiding everything at once looks like the game crashed.
                if (entry.Widgets.Count > 48)
                {
                    entry.Widgets.Clear();
                    entry.Colliders.Clear();
                    entry.Widgets.Add(w);
                    try
                    {
                        var c = w.gameObject.GetComponent(Il2CppType.Of<Collider>())?.TryCast<Collider>();
                        if (c != null)
                        {
                            entry.Colliders.Add(c);
                        }
                    }
                    catch { }
                }

                int hidden = 0;
                for (int i = 0; i < entry.Widgets.Count; i++)
                {
                    try
                    {
                        entry.Widgets[i].enabled = false;
                        hidden++;
                    }
                    catch { }
                }
                for (int i = 0; i < entry.Colliders.Count; i++)
                {
                    try { entry.Colliders[i].enabled = false; }
                    catch { }
                }

                if (hidden == 0)
                {
                    Say("[MC] 这个元素关不掉，换一个试试");
                    return;
                }
                W.Broken.Add(entry);
                StartSwing();
                Say("[MC] 破坏了界面元素 '" + entry.Name + "'（" + hidden + " 块），右键或 /restore 还原");
            }
            catch (Exception e)
            {
                Say("[MC] 破坏失败：" + e.Message);
            }
        }

        private static void RestoreLast()
        {
            if (W.Broken.Count == 0)
            {
                Say("[MC] 没有可以还原的东西");
                return;
            }
            var entry = W.Broken[W.Broken.Count - 1];
            W.Broken.RemoveAt(W.Broken.Count - 1);
            Restore(entry);
            Say("[MC] 已还原界面元素 '" + entry.Name + "'");
        }

        private static void RestoreAllUi()
        {
            int n = W.Broken.Count;
            for (int i = n - 1; i >= 0; i--)
            {
                Restore(W.Broken[i]);
            }
            W.Broken.Clear();
            Say("[MC] 已还原 " + n + " 个界面元素");
        }

        private static void Restore(BrokenUi entry)
        {
            if (entry == null)
            {
                return;
            }
            for (int i = 0; i < entry.Widgets.Count; i++)
            {
                try
                {
                    var w = entry.Widgets[i];
                    if (w != null)
                    {
                        w.enabled = true;
                    }
                }
                catch { }
            }
            for (int i = 0; i < entry.Colliders.Count; i++)
            {
                try
                {
                    var c = entry.Colliders[i];
                    if (c != null)
                    {
                        c.enabled = true;
                    }
                }
                catch { }
            }
        }

        // ---- first person camera -------------------------------------------

        private static float FirstPersonEyeHeight()
        {
            return 1.62f * Mathf.Clamp(W.SteveScale <= 0f ? 1f : W.SteveScale, 0.2f, 3f);
        }

        /// <summary>Mouse look. Only runs while the pointer is locked (Tab).</summary>
        private static void UpdateMouseLook()
        {
            if (!W.FpLook)
            {
                NativeInput.ConsumeMouseDelta(); // discard: the pointer is doing UI work
                return;
            }
            var md = NativeInput.ConsumeMouseDelta();
            if (md.sqrMagnitude < 0.0001f)
            {
                return;
            }
            float sens = 1.1f;
            try
            {
                sens = Mathf.Clamp(Plugin.FirstPersonSensitivity.Value, 0.2f, 4f);
            }
            catch { }
            // 0.12 deg per pixel at sens 1 is close to Minecraft's default.
            // +pitch looks DOWN in Unity (Euler(p, y, 0) sends +Z to -Y). Mouse y grows
            // downward on screen, so moving the mouse down must ADD pitch. The old code
            // subtracted it, which inverted the vertical look - "turning is wrong".
            W.FpYaw += md.x * 0.12f * sens;
            W.FpPitch += md.y * 0.12f * sens;
            W.FpPitch = Mathf.Clamp(W.FpPitch, -85f, 85f);
            if (W.FpYaw > 360f) W.FpYaw -= 360f;
            if (W.FpYaw < -360f) W.FpYaw += 360f;
        }

        private static void ApplyFirstPerson(Camera cam, Transform tr)
        {
            Vector3 eye = W.StevePos + Vector3.up * FirstPersonEyeHeight();
            tr.position = eye;
            tr.rotation = Quaternion.Euler(W.FpPitch, W.FpYaw, 0f);

            try
            {
                if (W.CamFovSaved)
                {
                    float want = 74f;
                    try { want = Mathf.Clamp(Plugin.FirstPersonFov.Value, 50f, 110f); } catch { }
                    if (Mathf.Abs(cam.fieldOfView - want) > 0.5f)
                    {
                        cam.fieldOfView = want;
                    }
                }
                // The battle camera's near plane is tuned for a close-up of the students;
                // at eye height it slices the hand and the first block in half. A short near
                // plane is also what lets the 3D hand sit comfortably in the lower right.
                if (cam.nearClipPlane > 0.06f)
                {
                    cam.nearClipPlane = 0.05f;
                }
                if (cam.farClipPlane < 250f)
                {
                    cam.farClipPlane = 250f;
                }
                // Every MC object (blocks, tools, the FP hand) lives on the character layer.
                // If the game swapped cameras or rebuilt its culling mask, that layer can be
                // dropped - which is exactly "first person shows no blocks".
                if (W.CharLayer >= 0 && (cam.cullingMask & (1 << W.CharLayer)) == 0)
                {
                    cam.cullingMask |= 1 << W.CharLayer;
                    Plugin.L.LogInfo("[McWorld] fp camera: restored character layer " + W.CharLayer);
                }
            }
            catch { }
            W.CamIntended = eye;
            W.CamApplied = true;
        }

        // ---- first person hand ---------------------------------------------

        /// <summary>
        /// A real 3D arm and item welded to the camera. The old version painted a flat block
        /// icon with IMGUI, which read as a giant yellow square pasted over the game. This is
        /// seen in perspective, swings when you click, and is raised when the shield is up.
        /// </summary>
        private static void BuildFpHand()
        {
            DestroyFpHand();
            if (W.Cam == null)
            {
                return;
            }
            try
            {
                var skin = LoadDiskTexture("steve");
                var root = new GameObject("McFpHand");
                root.transform.SetParent(W.Cam.transform, false);
                root.transform.localPosition = Vector3.zero;
                root.transform.localRotation = Quaternion.identity;

                var armMat = MakeMat(skin, false);
                if (armMat != null)
                {
                    var arm = new GameObject("arm");
                    arm.transform.SetParent(root.transform, false);
                    arm.transform.localPosition = new Vector3(0.34f, -0.38f, 0.55f);
                    arm.transform.localRotation = Quaternion.Euler(-58f, -10f, -18f);
                    var mf = arm.AddComponent<MeshFilter>();
                    var mr = arm.AddComponent<MeshRenderer>();
                    // The vanilla skin layout: right forearm is the 4x12x4 tile at (40,16).
                    var uvs = new Rect[6];
                    for (int f = 0; f < 6; f++)
                    {
                        uvs[f] = SkinRect(40, 16, 4, 12, 4, f);
                    }
                    mf.sharedMesh = BuildBox(new Vector3(0.13f, 0.13f, 0.46f), uvs, null, 1);
                    mr.sharedMaterial = armMat;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    ApplyLayer(arm, W.CharLayer);
                    W.FpArm = arm;
                }

                var item = new GameObject("item");
                item.transform.SetParent(root.transform, false);
                item.transform.localPosition = new Vector3(0.30f, -0.24f, 0.70f);
                // The face carrying the icon must point AT the camera. A 172 degree yaw put
                // the back of the quad towards the lens, so every block/tool looked mirrored
                // or blank ("the block and hand are wrong").
                item.transform.localRotation = Quaternion.Euler(4f, -14f, -12f);
                var imf = item.AddComponent<MeshFilter>();
                var imr = item.AddComponent<MeshRenderer>();
                var iuv = new Rect[6];
                for (int f = 0; f < 6; f++)
                {
                    iuv[f] = FullUv;
                }
                imf.sharedMesh = BuildBox(new Vector3(0.30f, 0.30f, 0.02f), iuv, null, 1);
                var startTex = ItemIcon(CurrentItem) ?? LoadDiskTexture("iron_pickaxe");
                imr.sharedMaterial = MakeMat(startTex, false);
                imr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                ApplyLayer(item, W.CharLayer);

                W.FpItem = item;
                W.FpHand = root;
                _fpItemKey = CurrentItem;
                UpdateFpHand();
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("[McWorld] fp hand: " + e.Message);
            }
        }

        private static void DestroyFpHand()
        {
            try
            {
                if (W.FpHand != null)
                {
                    UnityEngine.Object.Destroy(W.FpHand);
                }
            }
            catch { }
            W.FpHand = null;
            W.FpArm = null;
            W.FpItem = null;
        }

        private static string _fpItemKey;

        /// <summary>Swaps the icon, and animates swing + shield raise.</summary>
        private static void UpdateFpHand()
        {
            if (W.FpHand == null || W.FpItem == null)
            {
                return;
            }
            try
            {
                // MakeMat() calls GetInstanceID through IL2CPP. Doing that every frame for a
                // texture the game may have just swept returned a dead material -> black hand.
                // Only re-bind when the selected item actually changes.
                string key = CurrentItem;
                if (key != _fpItemKey)
                {
                    _fpItemKey = key;
                    var tex = ItemIcon(key);
                    if (tex != null)
                    {
                        var mr = W.FpItem.GetComponent(Il2CppType.Of<MeshRenderer>())?.TryCast<MeshRenderer>();
                        if (mr != null)
                        {
                            var mat = MakeMat(tex, false);
                            if (mat != null)
                            {
                                mr.sharedMaterial = mat;
                            }
                        }
                    }
                }

                float swing = 0f;
                if (W.SwingUntil > Time.time)
                {
                    float t = 1f - Mathf.Clamp01((W.SwingUntil - Time.time) / 0.26f);
                    swing = Mathf.Sin(t * Mathf.PI);
                }
                float bob = Mathf.Sin(Time.time * 1.6f) * 0.012f;

                Vector3 pos = new Vector3(0.30f, -0.24f, 0.70f);
                Vector3 armPos = new Vector3(0.34f, -0.38f, 0.55f);
                if (W.Blocking)
                {
                    // Plate in the middle of the screen, like a raised shield.
                    pos = new Vector3(0.02f, -0.10f, 0.62f);
                    armPos = new Vector3(0.14f, -0.30f, 0.48f);
                }

                pos.y += bob - swing * 0.10f;
                pos.z -= swing * 0.06f;
                var t1 = W.FpItem.transform;
                t1.localPosition = pos;
                t1.localRotation = Quaternion.Euler(4f + swing * 40f, -14f, -12f);

                if (W.FpArm != null)
                {
                    var t2 = W.FpArm.transform;
                    t2.localPosition = armPos + new Vector3(0f, bob - swing * 0.06f, 0f);
                }
            }
            catch { }
        }

        private static void ApplyNightOverlay()
        {
            if (Mathf.Abs(W.NightAlpha - W.NightTarget) < 0.002f)
            {
                W.NightAlpha = W.NightTarget;
                return;
            }
            float k = 1f - Mathf.Exp(-4f * Mathf.Max(Time.deltaTime, 0.0001f));
            W.NightAlpha = Mathf.Lerp(W.NightAlpha, W.NightTarget, k);
        }
    }
}
