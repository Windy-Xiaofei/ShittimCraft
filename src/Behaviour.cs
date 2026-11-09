using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace SchaleMcWorld
{
    public partial class McWorldBehaviour : MonoBehaviour
    {
        private static float _lastError;
        private static float _lastHudError;

        void Update()
        {
            try
            {
                Tick();
            }
            catch (Exception e)
            {
                if (Time.unscaledTime - _lastError > 3f)
                {
                    _lastError = Time.unscaledTime;
                    Plugin.L.LogError("[McWorld] " + e.GetType().Name + ": " + e.Message);
                }
            }
            try
            {
                ApplyNightOverlay();
            }
            catch { }
        }

        void LateUpdate()
        {
            if (!W.Active && !W.UiMode)
            {
                return;
            }
            try
            {
                ApplyCamera();
            }
            catch (Exception e)
            {
                if (Time.unscaledTime - _lastError > 3f)
                {
                    _lastError = Time.unscaledTime;
                    Plugin.L.LogError("[McWorld] camera: " + e.Message);
                }
            }
            try
            {
                // Held items are billboarded towards the camera, so they must be oriented
                // after the camera has been placed for this frame.
                BillboardHeld();
            }
            catch { }
            try
            {
                // Late, not Update: the game moves students in its own Update pass, so a push
                // applied earlier in the frame would be overwritten before anything is drawn.
                PushStudents();
            }
            catch (Exception e)
            {
                if (Time.unscaledTime - _lastError > 3f)
                {
                    _lastError = Time.unscaledTime;
                    Plugin.L.LogError("[McWorld] push: " + e.Message);
                }
            }
        }

        void OnGUI()
        {
            // Draw in the battle, in the first-person interface layer and while a command is
            // being typed - those are exactly the three places a HUD is expected.
            if (!W.Active && !W.UiMode && !W.ChatOpen && !W.InventoryOpen && !HasRecentChat())
            {
                return;
            }
            try
            {
                DrawHud();
            }
            catch (Exception e)
            {
                if (Time.unscaledTime - _lastHudError > 3f)
                {
                    _lastHudError = Time.unscaledTime;
                    Plugin.L.LogError("[McWorld] hud: " + e.GetType().Name + ": " + e.Message);
                }
            }
        }

        void OnDestroy()
        {
            try
            {
                LeaveWorld();
            }
            catch { }
        }

        private static float _fpsAccum;
        private static int _fpsFrames;
        private static float _fpsNext = -1f;
        private static int _lastReloads;

        /// <summary>Logs the average frame rate every 5 s while a layer is active.</summary>
        static void SampleFps()
        {
            if (!W.Active && !W.UiMode)
            {
                return;
            }
            if (_fpsNext < 0f)
            {
                _fpsNext = Time.unscaledTime + 5f;
                _fpsAccum = 0f;
                _fpsFrames = 0;
            }
            _fpsAccum += Time.unscaledDeltaTime;
            _fpsFrames++;
            if (Time.unscaledTime >= _fpsNext)
            {
                float avg = _fpsFrames / Mathf.Max(_fpsAccum, 0.0001f);
                int reloads = AssetKeeper.KeptCount;
                Plugin.L.LogInfo("[McWorld] fps " + avg.ToString("F0") + " (" + _fpsFrames + " frames / " +
                                 _fpsAccum.ToString("F1") + "s) texReloads=+" + (reloads - _lastReloads));
                _lastReloads = reloads;
                _fpsNext = Time.unscaledTime + 5f;
                _fpsAccum = 0f;
                _fpsFrames = 0;
            }
        }

        static void Tick()
        {
            SampleFps();
            if (!W.Inited)
            {
                InitOnce();
            }
            if (W.InitFailed)
            {
                return;
            }

            if (Time.unscaledTime >= W.NextScan)
            {
                // FindObjectsOfTypeAll is heavy on IL2CPP; scan rarely, and a bit faster
                // only while the world layer is actually running.
                W.NextScan = Time.unscaledTime + (W.Active ? 0.5f : 1.5f);
                ScanScene();
            }

            // Typing owns the frame: no movement, no toggles, the window procedure already
            // swallows every keystroke so the game cannot react either.
            if (W.ChatOpen)
            {
                NativeInput.Tick();
                PumpChat();
                return;
            }

            // The inventory also owns the pointer while it is open.
            if (W.InventoryOpen)
            {
                NativeInput.Tick();
                HandleInventoryInput();
                return;
            }

            // Function keys are often remapped to media keys on laptops (Fn+F7 never reaches
            // Unity), so a second, always-standard key is accepted as well. The raw Win32
            // state is checked too: the low level hook can swallow a key before Unity sees it.
            bool unityF7 = UnityEngine.Input.GetKeyDown(W.ToggleKey);
            bool unityCtrl = UnityEngine.Input.GetKeyDown(KeyCode.RightControl);
            bool nativeF7 = NativeInput.Pressed(NativeInput.VkF7);
            bool nativeCtrl = NativeInput.Pressed(NativeInput.VkRControl);
            bool toggle = unityF7 || unityCtrl || nativeF7 || nativeCtrl;

            if (toggle && Time.unscaledTime >= W.NextToggle)
            {
                // Guard against one press being seen twice (key repeat / duplicate update):
                // an instant enter+leave looks exactly like "the summon did nothing".
                W.NextToggle = Time.unscaledTime + 0.5f;
                Plugin.L.LogInfo("[McWorld] toggle pressed (active=" + W.Active + ") src=" +
                                 (unityF7 ? "unityF7 " : "") + (unityCtrl ? "unityRCtrl " : "") +
                                 (nativeF7 ? "nativeF7 " : "") + (nativeCtrl ? "nativeRCtrl" : ""));
                if (W.Active)
                {
                    LeaveWorld();
                }
                else
                {
                    EnterWorld();
                }
            }

            // F8 flips between the dungeon camera and the first-person interface view.
            bool uiToggle = UnityEngine.Input.GetKeyDown(W.UiToggleKey) ||
                            NativeInput.Pressed(NativeInput.VkF8);
            if (uiToggle && Time.unscaledTime >= W.NextUiToggle)
            {
                W.NextUiToggle = Time.unscaledTime + 0.3f;
                SetUiMode(!W.UiMode);
            }

            // MC muscle memory: T (or "/") opens the command bar, E opens the inventory.
            if (NativeInput.Pressed(NativeInput.VkE) && Time.unscaledTime >= W.NextInvToggle)
            {
                W.NextInvToggle = Time.unscaledTime + 0.3f;
                ToggleInventory();
                return;
            }
            if (NativeInput.Pressed(NativeInput.VkT) || NativeInput.Pressed(NativeInput.VkSlash))
            {
                OpenChat();
                return;
            }

            if (W.UiMode && !W.Active)
            {
                // The first-person layer works without any students: Blue Archive's own menus
                // are NGUI, so both walking around and mining them need no world at all.
                NativeInput.Tick();
                EnsureFirstPerson();
                HandleInterfaceInput();
                return;
            }

            if (!W.Active)
            {
                // A summon that found no students retries for a moment instead of silently
                // doing nothing - this is what made the second summon look broken.
                if (W.PendingEnterUntil > 0f)
                {
                    if (Time.unscaledTime <= W.PendingEnterUntil)
                    {
                        EnterWorld();
                    }
                    else
                    {
                        W.PendingEnterUntil = 0f;
                        Plugin.L.LogWarning("[McWorld] enter gave up: no students in scene");
                    }
                }
                return;
            }

            NativeInput.Tick();

            // Materials get swept like textures; a swept material renders black. Re-assigning
            // the (self rebuilding) materials once a second heals every block and tool.
            if (W.Active)
            {
                RefreshVisualMaterials();
            }

            if (W.UiMode)
            {
                HandleInterfaceInput();
                return;
            }

            UpdateAim();
            UpdateBlocking();
            HandleInput();
            UpdateHeldTool();
            StepSteve(Time.deltaTime);
            UpdatePearls(Time.deltaTime);
            // Students are pushed in LateUpdate, after the game has moved them.
        }

        /// <summary>Input while the first-person layer is on top of the battle.</summary>
        static void HandleInterfaceInput()
        {
            if (NativeInput.Pressed(NativeInput.VkTab) && Time.unscaledTime >= W.NextLookToggle)
            {
                W.NextLookToggle = Time.unscaledTime + 0.25f;
                ToggleLookMode();
            }
            if (NativeInput.Pressed(NativeInput.VkQ))
            {
                ToggleMineMode();
            }
            HandleHotbarKeys();

            UpdateMouseLook();
            UpdateBlocking();
            UpdateFpHand();
            // First person used to return from Tick() before StepSteve(), so WASD did
            // nothing and the view could only "turn" in place - reported as wrong turning.
            // Walking and pearls must run in both views.
            StepSteve(Time.deltaTime);
            UpdatePearls(Time.deltaTime);

            if (W.FpLook)
            {
                // Pointer locked: the buttons belong to the world, exactly like the dungeon view.
                if (NativeInput.Pressed(NativeInput.VkLButton))
                {
                    UseItem();
                }
                if (NativeInput.Pressed(NativeInput.VkRButton))
                {
                    SecondaryUse();
                }
            }
            else if (W.MineMode)
            {
                // Free pointer + mining: a click is aimed at Blue Archive's widgets.
                if (NativeInput.Pressed(NativeInput.VkLButton))
                {
                    BreakUiUnderCursor();
                }
                else if (NativeInput.Pressed(NativeInput.VkRButton))
                {
                    RestoreLast();
                }
            }
            else if (CurrentItem == "tool:pearl" && NativeInput.Pressed(NativeInput.VkRButton))
            {
                // A pearl is thrown even with the pointer released - otherwise it looks broken
                // in the mode where the mouse is used for Blue Archive's buttons.
                ThrowPearl();
            }

            // WASD still walks the player - the camera is their eyes, so this moves the view.
            StepSteve(Time.deltaTime);
            UpdatePearls(Time.deltaTime);
        }

        static void InitOnce()
        {
            W.Inited = true;
            try
            {
                if (Enum.TryParse<KeyCode>(Plugin.ToggleKeyName.Value, true, out var key))
                {
                    W.ToggleKey = key;
                }
                if (Enum.TryParse<KeyCode>(Plugin.UiModeKeyName.Value, true, out var uiKey))
                {
                    W.UiToggleKey = uiKey;
                }
                try
                {
                    W.DamageFraction = Mathf.Clamp(Plugin.SwordDamage.Value, 0.01f, 1f);
                }
                catch { }

                string steve = System.IO.Path.Combine(AssetDir, "textures", "steve.png");
                Plugin.L.LogInfo("[McWorld] textures dir: " + System.IO.Path.Combine(AssetDir, "textures") +
                                 " steve.png=" + System.IO.File.Exists(steve));

                EnsureWhitePixel();
                NativeInput.Prime();
                if (Plugin.AutoSelfTest.Value)
                {
                    RunSelfTest("startup");
                }
                Plugin.L.LogInfo("[McWorld] ready");
            }
            catch (Exception e)
            {
                W.InitFailed = true;
                Plugin.L.LogError("[McWorld] init failed: " + e);
            }
        }

        static void EnsureWhitePixel()
        {
            // Engine-owned and never swept. An earlier version allocated a 1x1 Texture2D
            // here, which the game's asset sweep destroyed immediately - starting the
            // rebuild/GC-handle loop that made the HUD stutter.
            W.WhitePixel = Texture2D.whiteTexture;
        }

        static void ScanScene()
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<CharacterVisual>());
            if (all == null)
            {
                return;
            }

            var found = new List<Vector3>();
            var foundTr = new List<Transform>();
            int skippedInactive = 0, skippedDead = 0, skippedWild = 0;
            for (int i = 0; i < all.Length; i++)
            {
                var obj = all[i];
                if (obj == null)
                {
                    continue;
                }
                var cv = obj.TryCast<CharacterVisual>();
                if (cv == null || cv.transform == null)
                {
                    continue;
                }

                // FindObjectsOfTypeAll also returns prefabs and pooled (inactive) instances.
                // In a Total Assault stage those sit at template coordinates tens of units
                // away from the fight and used to drag the world origin out into empty space.
                var go = cv.gameObject;
                if (go == null || !go.activeInHierarchy)
                {
                    skippedInactive++;
                    continue;
                }
                try
                {
                    if (cv.isDeadCalled)
                    {
                        skippedDead++;
                        continue;
                    }
                }
                catch { }

                Vector3 p = cv.transform.position;
                if (float.IsNaN(p.x) || float.IsNaN(p.y) || float.IsNaN(p.z) ||
                    Mathf.Abs(p.x) > 500f || Mathf.Abs(p.z) > 500f || Mathf.Abs(p.y) > 200f)
                {
                    skippedWild++;
                    continue;
                }

                found.Add(p);
                foundTr.Add(cv.transform);
                if (W.CharLayer < 0)
                {
                    W.CharLayer = go.layer;
                    Plugin.L.LogInfo("[McWorld] character layer: " + W.CharLayer);
                }
            }

            lock (W.Gate)
            {
                W.CharPos.Clear();
                W.CharPos.AddRange(found);
                W.CharTransforms.Clear();
                W.CharTransforms.AddRange(foundTr);
            }
            W.CharCount = found.Count;

            if (W.CharCount != W.LastLoggedChars)
            {
                W.LastLoggedChars = W.CharCount;
                Plugin.L.LogInfo("[McWorld] characters in scene: " + W.CharCount +
                                 " (inactive=" + skippedInactive + " dead=" + skippedDead +
                                 " wild=" + skippedWild + ")");
            }

            // The battle ended (settlement / result screen removes every student). Keep
            // pushing them there and the game crashes on transforms it just pooled - so the
            // layer leaves on its own instead of waiting for a key nobody is pressing.
            if (W.Active && found.Count == 0 && Time.unscaledTime - W.ActiveSince > 3f)
            {
                Plugin.L.LogInfo("[McWorld] battle over (no characters left) - auto leaving world layer");
                LeaveWorld();
            }
        }

        /// <summary>How far from the cluster median a unit may be and still count as "in the fight".</summary>
        private const float SpawnClusterRadius = 20f;

        /// <summary>Steve stands three blocks on the camera side of the cluster centre.</summary>
        private static Vector3 SpawnPoint(Vector3 origin, Vector3 camFlat)
        {
            Vector3 flat = new Vector3(camFlat.x, 0f, camFlat.z);
            if (flat.sqrMagnitude < 0.01f)
            {
                flat = new Vector3(0f, 0f, 1f);
            }
            return origin - flat.normalized * 3f;
        }

        /// <summary>
        /// Median-based centre of the active battle units, with far-away outliers removed.
        /// A plain min/max bounding box is dominated by a single distant boss or pooled
        /// prefab - exactly what happens in Total Assault. The ground height is a median too,
        /// so a sunken effect object cannot drop the whole block grid below the floor.
        /// </summary>
        private static Vector3 RobustOrigin(out int used, out int rejected)
        {
            used = 0;
            rejected = 0;
            Vector3[] pts;
            lock (W.Gate)
            {
                pts = W.CharPos.ToArray();
            }
            if (pts.Length == 0)
            {
                return Vector3.zero;
            }

            var xs = new float[pts.Length];
            var ys = new float[pts.Length];
            var zs = new float[pts.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                xs[i] = pts[i].x;
                ys[i] = pts[i].y;
                zs[i] = pts[i].z;
            }
            Array.Sort(xs);
            Array.Sort(ys);
            Array.Sort(zs);
            float mx = xs[xs.Length / 2];
            float my = ys[ys.Length / 2];
            float mz = zs[zs.Length / 2];
            float r2 = SpawnClusterRadius * SpawnClusterRadius;

            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            var keepY = new List<float>();
            for (int i = 0; i < pts.Length; i++)
            {
                float dx = pts[i].x - mx;
                float dz = pts[i].z - mz;
                if (dx * dx + dz * dz > r2)
                {
                    rejected++;
                    continue;
                }
                if (pts[i].x < minX) minX = pts[i].x;
                if (pts[i].x > maxX) maxX = pts[i].x;
                if (pts[i].z < minZ) minZ = pts[i].z;
                if (pts[i].z > maxZ) maxZ = pts[i].z;
                keepY.Add(pts[i].y);
            }
            if (keepY.Count == 0)
            {
                // Whole cast scattered across a huge arena: the median is still the best guess.
                used = 1;
                rejected = 0;
                return new Vector3(mx, my, mz);
            }
            used = keepY.Count;
            keepY.Sort();
            return new Vector3((minX + maxX) * 0.5f, keepY[keepY.Count / 2], (minZ + maxZ) * 0.5f);
        }

        static void EnterWorld()
        {
            try
            {
                EnterWorldInner();
            }
            catch (Exception e)
            {
                Plugin.L.LogError("[McWorld] enter failed: " + e);
                W.Active = false;
                NativeInput.Uninstall();
                ReleaseCamera();
                W.Cam = null;
                RemoveInputBlocker();
                CleanupAvatar();
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }
        }

        static void EnterWorldInner()
        {
            // The avatar is per-session; the block world belongs to the level and survives.
            if (W.Steve != null || W.Blocker != null)
            {
                Plugin.L.LogInfo("[McWorld] cleaning avatar leftovers before enter");
                LeaveWorld();
            }

            // Positions may be stale after a scene change: take a fresh sample.
            ScanScene();
            if (W.CharCount < 1)
            {
                if (W.PendingEnterUntil <= 0f)
                {
                    W.PendingEnterUntil = Time.unscaledTime + 1.0f;
                    Plugin.L.LogInfo("[McWorld] waiting for students to appear...");
                }
                return;
            }

            // World grid: the dense cluster of ACTIVE battle units, one unit per block.
            // A Total Assault boss (or a pooled prefab sitting at template coordinates) used
            // to stretch the min/max box across the whole arena: origin became x=-60 and
            // Steve spawned far outside the fight. Median + outlier rejection is robust to
            // that, and the ground height is a median too (not the lowest prop in the scene).
            Vector3 centroid = RobustOrigin(out int usedPts, out int rejectedPts);
            if (usedPts == 0)
            {
                Plugin.L.LogWarning("[McWorld] enter cancelled: no usable character positions");
                return;
            }
            Plugin.L.LogInfo("[McWorld] spawn cluster: used=" + usedPts + " rejected=" + rejectedPts +
                             " centre=" + centroid.ToString("F2"));

            EnsureWhitePixel();
            // Every runtime material is built from the bundled unlit shader; if it is missing
            // we fall back to cloning a character material (dimmer, but still textured).
            var unlitShader = LoadMcShader();
            Plugin.L.LogInfo("[McWorld] shading: " + (unlitShader != null
                ? "unlit bundle shader " + unlitShader.name
                : "character material fallback"));

            // Spawn on the camera side of the cluster. The old fixed -Z offset assumed every
            // stage looks along +Z; Total Assault cameras are usually rotated, so Steve ended
            // up behind the party or out of frame entirely.
            Vector3 camFlat = new Vector3(0f, 0f, 1f);
            var spawnCam = W.Cam ?? PickCamera();
            try
            {
                if (spawnCam != null)
                {
                    var f = spawnCam.transform.forward;
                    var flat = new Vector3(f.x, 0f, f.z);
                    if (flat.sqrMagnitude > 0.01f)
                    {
                        camFlat = flat.normalized;
                    }
                }
            }
            catch { }

            // The block world belongs to the level, not to the avatar: leaving MC mode only
            // removes Steve, the blocks stay where the player put them. Rebuilding them from
            // scratch on every switch is what made them "disappear" when going back to BA.
            bool haveWorld = W.OriginReady && W.BlockRoot != null;
            if (!haveWorld)
            {
                if (W.OriginReady || W.Blocks.Count > 0)
                {
                    // The scene was swapped underneath us and took the block root with it.
                    Plugin.L.LogInfo("[McWorld] block root gone (scene change) - rebuilding");
                    ClearBlocks();
                }
                ReleaseAssets();
                W.OriginReady = false;
            }
            else
            {
                float shift = Vector3.Distance(centroid, W.Origin);
                if (shift > 40f)
                {
                    // A genuinely different stage (Normal -> Total Assault) relocates the whole
                    // battlefield by tens of units; walking students only move a few. Re-anchor
                    // for a new stage, otherwise Steve keeps standing at last level's coordinates.
                    Plugin.L.LogInfo("[McWorld] stage changed (" + shift.ToString("F1") +
                                     " units) - rebuilding block world");
                    ClearBlocks();
                    try
                    {
                        if (W.BlockRoot != null)
                        {
                            UnityEngine.Object.Destroy(W.BlockRoot);
                        }
                    }
                    catch { }
                    W.BlockRoot = null;
                    W.OriginReady = false;
                    haveWorld = false;
                }
                else if (shift > 6f)
                {
                    // Walking students must NOT move the block grid - re-anchoring here made
                    // everything the player built disappear after leaving and coming back.
                    Plugin.L.LogInfo("[McWorld] students moved " + shift.ToString("F1") +
                                     " - keeping the block grid where it is");
                }
            }

            if (!haveWorld)
            {
                W.Origin = centroid;
                W.GroundY = centroid.y;
                W.OriginReady = true;
                W.BlockRoot = new GameObject("McWorldBlocks");

                W.StevePos = SpawnPoint(W.Origin, camFlat);
                W.SteveVel = Vector3.zero;
                W.SteveYaw = Mathf.Atan2(camFlat.x, camFlat.z) * Mathf.Rad2Deg;
                W.CamYaw = 0f;
            }
            else
            {
                Plugin.L.LogInfo("[McWorld] keeping block world (" + W.Blocks.Count + " block(s)) and Steve at " +
                                 W.StevePos.ToString("F2"));
            }

            // Level bounds: the local floor decides where Steve may walk. Without this he can
            // step off the end of the street, and the camera then looks into the sky sphere -
            // the giant cyan area and the "level seen from below" shots in the footage.
            CalibrateEdgeGuard();
            if (!CanStep(W.StevePos))
            {
                Vector3 safe = SpawnPoint(W.Origin, camFlat);
                Plugin.L.LogInfo("[McWorld] stored position " + W.StevePos.ToString("F1") +
                                 " is outside the floor - reset to " + safe.ToString("F1"));
                W.StevePos = safe;
                W.SteveVel = Vector3.zero;
                W.LastSafe = safe;
            }

            // Fresh viewpoint + centred crosshair each time the layer is entered.
            W.AimX = 0.5f;
            W.AimY = 0.5f;
            W.CamInited = false;
            W.CamApplied = false;
            W.CamOverrideLogged = false;
            try { W.CamYaw = Plugin.CameraYaw.Value; } catch { W.CamYaw = 0f; }

            if (!BuildSteve())
            {
                Plugin.L.LogWarning("[McWorld] enter cancelled: Steve could not be built");
                CleanupAvatar();
                return;
            }

            // In interface mode Steve is the camera, not a body on screen.
            if (W.UiMode)
            {
                W.FpInited = false;
                EnsureFirstPerson();
                if (W.Steve != null)
                {
                    W.Steve.SetActive(false);
                }
            }

            // Own the view: pick the camera that actually renders the students and switch off
            // whatever script was driving it, otherwise it overwrites our transform every frame.
            W.Cam = spawnCam ?? PickCamera();
            TakeOverCamera(W.Cam);

            W.Active = true;
            W.ActiveSince = Time.unscaledTime;
            W.PendingEnterUntil = 0f;
            BuildInputBlocker();
            NativeInput.Install();
            NativeInput.ClearHeld();
            if (W.UiMode)
            {
                EnsureFirstPerson();
                ApplyFpProfile();
            }
            else
            {
                NativeInput.SetProfile(false, true, true, true);
                Cursor.visible = false;
            }
            Cursor.lockState = CursorLockMode.None;

            Plugin.L.LogInfo("[McWorld] entered at origin " + W.Origin + " groundY=" + W.GroundY +
                             " steve=" + W.StevePos.ToString("F2") + " camFlat=" + camFlat.ToString("F2") +
                             " students=" + W.CharCount + " hooked=" + NativeInput.Hooked);

            if (Plugin.AutoSelfTest.Value)
            {
                RunSelfTest("world");
                BattleSelfTest();
            }
        }

        static void LeaveWorld()
        {
            if (W.UiMode)
            {
                // F7 while in first person: hand the view back to Blue Archive as well,
                // otherwise the battle keeps running behind a frozen camera.
                SetUiMode(false);
            }
            bool wasActive = W.Active;
            W.Active = false;
            W.PendingEnterUntil = 0f;
            ReleaseCamera();
            W.Cam = null;
            RemoveInputBlocker();
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            if (W.UiMode)
            {
                // The interface layer outlives the world: keep the hook so T / Q / F8 and the
                // mining clicks stay with us instead of leaking into the game.
                NativeInput.Install();
                NativeInput.SetProfile(true, false, W.MineMode, true, true);
            }
            else
            {
                NativeInput.Uninstall();
            }
            // Only the avatar goes away. The blocks, their materials and the world origin
            // stay exactly where they are - the level keeps whatever the player built.
            CleanupAvatar();
            if (wasActive)
            {
                Plugin.L.LogInfo("[McWorld] left (blocks kept: " + W.Blocks.Count + ")");
            }
        }

        /// <summary>Removes Steve and his parts. The block world is untouched.</summary>
        static void CleanupAvatar()
        {
            if (W.Steve != null)
            {
                UnityEngine.Object.Destroy(W.Steve);
            }
            W.Steve = null;
            W.SteveHead = null;
            W.SteveArmR = null;
            W.SteveArmL = null;
            W.SteveLegR = null;
            W.SteveLegL = null;
            W.ToolPick = null;
            W.ToolSword = null;
            W.ToolShield = null;
            W.ToolPearl = null;
            W.HeldBlock = null;
            W.HeldVisuals.Clear();
            W.SteveShadow = null;
            W.HeldSword = false;
            W.Blocking = false;
            W.SwingUntil = 0f;
            ClearPearls();
        }

        /// <summary>Destroys the block world and every asset backing it (scene change only).</summary>
        static void WipeWorld()
        {
            ClearBlocks();
            if (W.BlockRoot != null)
            {
                UnityEngine.Object.Destroy(W.BlockRoot);
            }
            W.BlockRoot = null;
            W.OriginReady = false;
            ReleaseAssets();
        }

        static bool BuildSteve()
        {
            var go = BuildSteveRuntime();
            if (go == null)
            {
                return false;
            }
            W.Steve = go;
            W.SteveHead = FindChild(go, "head");
            W.SteveArmR = FindChild(go, "armR");
            W.SteveArmL = FindChild(go, "armL");
            W.SteveLegR = FindChild(go, "legR");
            W.SteveLegL = FindChild(go, "legL");
            ApplyLayer(go, W.CharLayer);
            BuildTools(W.SteveArmR, W.SteveArmL);
            // Show only what the selected hotbar slot actually holds.
            RefreshHeldItems();

            // Collision footprint comes from the students themselves - hardcoding it made
            // Steve stop a whole body-width short of (or overlapping) the smaller models.
            W.StudentRadius = MeasureStudentRadius();

            // The model is 2.0 units tall (head top). It used to be shrunk to the students'
            // renderer height, but that height (~1.0) is only their head+torso: the result was
            // a 1-unit doll standing next to a 1x1 block, i.e. half a block tall. Steve is now
            // scaled to his real Minecraft height, so a block reaches his waist.
            float studentHeight = MeasureStudentHeight();
            float wantHeight = 1.8f;
            try
            {
                wantHeight = Mathf.Clamp(Plugin.SteveHeight.Value, 1.0f, 2.4f);
            }
            catch { }
            float scale = Mathf.Clamp(wantHeight / 2.0f, 0.35f, 1.4f);
            go.transform.localScale = new Vector3(scale, scale, scale);
            go.transform.position = W.StevePos;
            go.transform.rotation = Quaternion.identity;
            W.SteveScale = scale;
            W.SteveIsFullSize = scale > 0.8f;

            Plugin.L.LogInfo("[McWorld] Steve built, head=" + (W.SteveHead != null) +
                             " armR=" + (W.SteveArmR != null) + " layer=" + go.layer +
                             " scale=" + scale.ToString("F2") + " height=" +
                             (2.0f * scale).ToString("F2") + " blocks (studentHeight=" +
                             studentHeight.ToString("F2") + ") contact=" + BodyRadius.ToString("F2"));
            return true;
        }

        /// <summary>Half width of a student's body, from their renderer bounds.</summary>
        static float MeasureStudentRadius()
        {
            try
            {
                Transform tr = null;
                lock (W.Gate)
                {
                    if (W.CharTransforms.Count > 0)
                    {
                        tr = W.CharTransforms[0];
                    }
                }
                if (tr == null)
                {
                    return StudentRadiusDefault;
                }
                var r = tr.gameObject.GetComponentInChildren(Il2CppType.Of<Renderer>())?.TryCast<Renderer>();
                if (r == null)
                {
                    return StudentRadiusDefault;
                }
                Vector3 s = r.bounds.size;
                // The thin horizontal side is the body depth; the wide one includes arms.
                float width = Mathf.Min(s.x, s.z);
                if (width < 0.05f)
                {
                    return StudentRadiusDefault;
                }
                return Mathf.Clamp(width * 0.5f, 0.15f, 0.8f);
            }
            catch
            {
                return StudentRadiusDefault;
            }
        }

        /// <summary>Height of a student in world units, via its renderer bounds.</summary>
        static float MeasureStudentHeight()
        {
            try
            {
                Transform tr = null;
                lock (W.Gate)
                {
                    if (W.CharTransforms.Count > 0)
                    {
                        tr = W.CharTransforms[0];
                    }
                }
                if (tr == null)
                {
                    return 1.7f;
                }
                var r = tr.gameObject.GetComponentInChildren(Il2CppType.Of<Renderer>())?.TryCast<Renderer>();
                if (r == null || r.bounds.size.y < 0.1f)
                {
                    return 1.7f;
                }
                return r.bounds.size.y;
            }
            catch
            {
                return 1.7f;
            }
        }

        static Transform FindChild(GameObject root, string name)
        {
            var t = root.transform;
            int n = t.childCount;
            for (int i = 0; i < n; i++)
            {
                var c = t.GetChild(i);
                if (c != null && c.name == name)
                {
                    return c;
                }
            }
            return null;
        }
    }
}
