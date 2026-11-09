using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace SchaleMcWorld
{
    public partial class McWorldBehaviour
    {
        [DllImport("GameAssembly.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr il2cpp_object_get_class(IntPtr obj);

        [DllImport("GameAssembly.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr il2cpp_class_get_name(IntPtr klass);

        /// <summary>
        /// The real IL2CPP class name. Il2CppInterop proxies report their declared type
        /// ("Behaviour", "Component") from GetType(), which is useless for introspection.
        /// </summary>
        private static string ClassName(IntPtr ptr)
        {
            try
            {
                IntPtr cls = il2cpp_object_get_class(ptr);
                if (cls == IntPtr.Zero)
                {
                    return "";
                }
                IntPtr name = il2cpp_class_get_name(cls);
                return name == IntPtr.Zero ? "" : (Marshal.PtrToStringAnsi(name) ?? "");
            }
            catch
            {
                return "";
            }
        }

        /// <summary>Consecutive frames where the game moved the camera out from under us.</summary>
        private static int _camOverrides;

        /// <summary>True when the camera object carries an Animator that could move it.</summary>
        private static bool LooksAnimated(GameObject go)
        {
            try
            {
                if (go == null)
                {
                    return false;
                }
                var comps = go.GetComponents(Il2CppType.Of<Behaviour>());
                if (comps == null)
                {
                    return false;
                }
                for (int i = 0; i < comps.Length; i++)
                {
                    var b = comps[i]?.TryCast<Behaviour>();
                    if (b == null)
                    {
                        continue;
                    }
                    if (ClassName(b.Pointer) == "Animator")
                    {
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool IsUiCamera(Camera c)
        {
            try
            {
                if (c.gameObject == null)
                {
                    return true;
                }
                if (c.gameObject.layer == 5)
                {
                    return true; // Unity's built-in UI layer
                }
                string n = (c.name ?? "").ToLowerInvariant();
                if (n.Contains("ui") || n.Contains("ngui") || n.Contains("gui"))
                {
                    return true;
                }
                // NGUI cameras are orthographic with a small size, and they usually have no
                // depth buffer of their own.
                if (c.orthographic && c.orthographicSize > 0.5f && c.orthographicSize < 50f)
                {
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// The camera that actually renders the battle. Camera.main is not reliable in this
        /// build (the tag is not on the battle camera), so the one whose culling mask sees the
        /// character layer wins.
        /// </summary>
        private static Camera PickCamera()
        {
            try
            {
                var cams = Camera.allCameras;
                if (cams != null && cams.Length > 0)
                {
                    Camera best = null;
                    float bestScore = float.MinValue;
                    for (int i = 0; i < cams.Length; i++)
                    {
                        var c = cams[i];
                        if (c == null)
                        {
                            continue;
                        }
                        if (IsUiCamera(c))
                        {
                            // Never hijack an NGUI camera: Blue Archive draws every button
                            // through it, and moving it breaks clicking entirely.
                            continue;
                        }
                        float score = c.depth;
                        if (W.CharLayer >= 0 && (c.cullingMask & (1 << W.CharLayer)) != 0)
                        {
                            score += 1000f;
                        }
                        if (!c.orthographic)
                        {
                            score += 30f; // the battle camera is perspective
                        }
                        // A camera with an Animator on it is a cutscene / intro rig. Taking it
                        // over is what produced the "camera overridden by the game" loop.
                        if (LooksAnimated(c.gameObject))
                        {
                            score -= 400f;
                        }
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = c;
                        }
                    }
                    if (best != null)
                    {
                        Plugin.L.LogInfo("[McWorld] camera: " + best.name + " depth=" + best.depth +
                                         " mask=" + best.cullingMask + " fov=" + best.fieldOfView);
                        return best;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("[McWorld] camera scan: " + e.Message);
            }

            try
            {
                return Camera.main;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Switches off the game's camera controllers while the world layer owns the view.
        /// Without this the battle camera rewrites the transform every frame and the player
        /// never moves: that is why the view never changed in earlier builds.
        /// </summary>
        private static void TakeOverCamera(Camera cam)
        {
            if (cam == null)
            {
                return;
            }
            W.CamScripts.Clear();
            try
            {
                // The battle camera is a long lens (fov 10) aimed at a close-up of the
                // characters. Keeping that while pulling the camera 13 units back makes the
                // view a tight third-person shot - the exact complaint about "it is still
                // third person". The dungeon view needs a wide lens.
                W.CamOrigFov = cam.fieldOfView;
                W.CamOrigOrtho = cam.orthographic;
                W.CamOrigNear = cam.nearClipPlane;
                W.CamFovSaved = true;
                cam.orthographic = false; // an orthographic battle camera would ignore fov
                cam.fieldOfView = FovValue();
                Plugin.L.LogInfo("[McWorld] camera fov " + W.CamOrigFov + " -> " + cam.fieldOfView +
                                 " (wasOrtho=" + W.CamOrigOrtho + ")");

                var comps = cam.gameObject.GetComponents(Il2CppType.Of<Behaviour>());
                if (comps == null)
                {
                    return;
                }
                var names = new System.Text.StringBuilder();
                for (int i = 0; i < comps.Length; i++)
                {
                    var b = comps[i]?.TryCast<Behaviour>();
                    if (b == null || b.TryCast<Camera>() != null)
                    {
                        continue;
                    }

                    string tn = ClassName(b.Pointer);
                    if (names.Length < 500)
                    {
                        names.Append(string.IsNullOrEmpty(tn) ? "?" : tn).Append(' ').Append(b.enabled ? "+" : "-").Append("; ");
                    }
                    if (IsCameraDriver(tn))
                    {
                        DisableDriver(b);
                    }
                }
                Plugin.L.LogInfo("[McWorld] camera components [" + names + "] disabled=" + W.CamScripts.Count);
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("[McWorld] camera takeover: " + e.Message);
            }
        }

        /// <summary>
        /// True for the scripts that re-drive an already hijacked camera. Animator matters most:
        /// an animated camera rewrites its transform after our LateUpdate, so the view stayed
        /// wherever the game wanted it and every later frame looked "rendered wrong".
        /// UniversalAdditionalCameraData is deliberately excluded - it is the URP rendering
        /// data, not a driver, and switching it off breaks post processing and colour.
        /// </summary>
        /// <summary>
        /// Rendering-pipeline components. Disabling any of these starves URP of per-camera
        /// data and large parts of the scene turn into black blocks - the F8 black-square
        /// complaint. RenderConfigLoader / CameraFovLimiter used to be killed because their
        /// names contain "Loader" / "Camera"; they must survive.
        /// </summary>
        private static bool IsRendererData(string tn)
        {
            if (string.IsNullOrEmpty(tn))
            {
                return true; // unknown -> leave it alone
            }
            return tn.IndexOf("UniversalAdditional", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   tn.IndexOf("Config", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   tn.IndexOf("Loader", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   tn.IndexOf("Fov", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   tn.IndexOf("Render", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   tn.IndexOf("Shadow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   tn.IndexOf("Light", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   tn.IndexOf("Receive", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   tn.IndexOf("Data", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   tn.IndexOf("Post", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   tn.IndexOf("Volume", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   tn.IndexOf("Audio", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   tn.IndexOf("Listener", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsCameraDriver(string tn)
        {
            if (string.IsNullOrEmpty(tn) || IsRendererData(tn))
            {
                return false;
            }
            if (tn == "Animator")
            {
                return true; // camera animation curve: the worst offender
            }
            return tn.IndexOf("Cinemachine", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   tn.IndexOf("CameraController", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   tn.IndexOf("CameraFollow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   tn.IndexOf("CameraMove", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   tn.IndexOf("CameraShake", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   tn.IndexOf("Director", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   tn.IndexOf("Brain", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   tn.IndexOf("Dolly", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   tn.IndexOf("FollowTarget", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   tn.IndexOf("LookAt", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void DisableDriver(Behaviour b)
        {
            try
            {
                if (!b.enabled)
                {
                    return;
                }
                b.enabled = false;
                W.CamScripts.Add(b);
            }
            catch { }
        }

        /// <summary>
        /// The game keeps writing the camera transform after ours. Called when that is detected:
        /// switch off every other behaviour on the camera so only the Camera itself is left.
        /// </summary>
        private static void ForceTakeOver(Camera cam)
        {
            if (cam == null)
            {
                return;
            }
            var comps = cam.gameObject.GetComponents(Il2CppType.Of<Behaviour>());
            if (comps == null)
            {
                return;
            }
            var killed = new System.Text.StringBuilder();
            for (int i = 0; i < comps.Length; i++)
            {
                var b = comps[i]?.TryCast<Behaviour>();
                if (b == null || b.TryCast<Camera>() != null || !b.enabled)
                {
                    continue;
                }
                string tn = ClassName(b.Pointer);
                // Never switch off URP / render-config components: that is what turned the
                // scenery into black blocks when F8 took the camera. Only known transform
                // drivers are safe to stop; everything else is left running, and our
                // LateUpdate writes the final transform anyway.
                if (IsRendererData(tn) ||
                    tn.IndexOf("McWorld", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    !IsCameraDriver(tn))
                {
                    continue;
                }
                if (killed.Length < 300)
                {
                    killed.Append(string.IsNullOrEmpty(tn) ? "?" : tn).Append(' ');
                }
                DisableDriver(b);
            }
            Plugin.L.LogWarning("[McWorld] camera still being driven - forced takeover, disabled [" +
                                killed + "]");
        }

        private static void ReleaseCamera()
        {
            if (W.Cam != null && W.CamFovSaved)
            {
            try
            {
                W.Cam.fieldOfView = W.CamOrigFov;
                W.Cam.orthographic = W.CamOrigOrtho;
                W.Cam.nearClipPlane = W.CamOrigNear;
            }
            catch { }
            }
            W.CamFovSaved = false;

            for (int i = 0; i < W.CamScripts.Count; i++)
            {
                try
                {
                    var b = W.CamScripts[i];
                    if (b != null)
                    {
                        b.enabled = true;
                    }
                }
                catch { }
            }
            W.CamScripts.Clear();
        }

        /// <summary>
        /// The crosshair is a virtual cursor. NativeInput pins the OS pointer to the window
        /// centre and hands back the movement the game never sees, so this is the only mouse
        /// input available while the world layer runs.
        /// </summary>
        private static void UpdateAim()
        {
            var md = NativeInput.ConsumeMouseDelta();
            float sens = 1f;
            try
            {
                sens = Mathf.Clamp(Plugin.AimSensitivity.Value, 0.2f, 4f);
            }
            catch { }

            if (md.sqrMagnitude > 0f)
            {
                W.AimX = Mathf.Clamp(W.AimX + md.x * sens / Mathf.Max(Screen.width, 1), 0.10f, 0.90f);
                // Screen coordinates grow downward, the aim is stored bottom-up.
                W.AimY = Mathf.Clamp(W.AimY - md.y * sens / Mathf.Max(Screen.height, 1), 0.10f, 0.90f);
            }
        }

        /// <summary>
        /// Ray through the virtual crosshair, built from the camera basis instead of
        /// Camera.ScreenPointToRay: fewer IL2CPP entry points, and it keeps working when the
        /// camera has a non-default viewport.
        /// </summary>
        private static bool AimRay(out Vector3 origin, out Vector3 direction)
        {
            origin = Vector3.zero;
            direction = Vector3.forward;
            if (W.Cam == null)
            {
                return false;
            }
            var t = W.Cam.transform;
            float fovY = Mathf.Clamp(W.Cam.fieldOfView, 5f, 120f) * Mathf.Deg2Rad;
            float tanY = Mathf.Tan(fovY * 0.5f);
            float tanX = tanY * Mathf.Max(W.Cam.aspect, 0.2f);
            float nx = (W.AimX * 2f - 1f) * tanX;
            float ny = (W.AimY * 2f - 1f) * tanY;

            origin = t.position;
            direction = (t.forward + t.right * nx + t.up * ny);
            if (direction.sqrMagnitude < 0.0001f)
            {
                return false;
            }
            direction = direction.normalized;
            return true;
        }

        /// <summary>Where the crosshair meets the plane Steve stands on - used for his facing.</summary>
        private static Vector3 AimGroundPoint()
        {
            if (CrosshairRay(out var o, out var d))
            {
                float planeY = W.StevePos.y + 0.2f;
                if (d.y < -0.02f)
                {
                    float t = (planeY - o.y) / d.y;
                    if (t > 0f && t < 200f)
                    {
                        return o + d * t;
                    }
                }
                var flat = new Vector3(d.x, 0f, d.z);
                if (flat.sqrMagnitude > 0.0001f)
                {
                    return W.StevePos + flat.normalized * 4f;
                }
            }
            return W.StevePos + new Vector3(0f, 0f, 4f);
        }

        private static float FovValue()
        {
            try
            {
                return Mathf.Clamp(Plugin.CameraFov.Value, 20f, 90f);
            }
            catch
            {
                return 55f;
            }
        }

        /// <summary>
        /// Minecraft Dungeons style view: one fixed angle for the whole level, no mouse look.
        /// The camera only translates, smoothly trailing Steve - that is what makes the
        /// battlefield readable, and it keeps blocks anchored on screen while you walk.
        /// </summary>
        private static void ApplyCamera()
        {
            if (W.Cam == null)
            {
                W.Cam = PickCamera();
                if (W.Cam == null)
                {
                    return;
                }
            }
            var tr = W.Cam.transform;

            // Detect the game re-driving the camera BEFORE either view applies its own
            // transform. This used to live only in the dungeon branch, so in first person
            // nobody ever noticed (or fixed) the drift: the hand wandered with the camera,
            // the block world slid out of frame and the view would not turn properly.
            if (W.CamApplied && (tr.position - W.CamIntended).sqrMagnitude > 0.25f)
            {
                if (!W.CamOverrideLogged)
                {
                    W.CamOverrideLogged = true;
                    Plugin.L.LogWarning("[McWorld] camera overridden by the game (wanted " + W.CamIntended +
                                        ", got " + tr.position + ")");
                }
                _camOverrides++;
                if (_camOverrides == 2 || _camOverrides == 20)
                {
                    ForceTakeOver(W.Cam);
                }
            }
            else
            {
                _camOverrides = 0;
            }

            // Interface mode: the camera *is* Steve's head. No smoothing, no look-ahead -
            // a moving background makes Blue Archive's 2D buttons impossible to click.
            if (W.UiMode)
            {
                ApplyFirstPerson(W.Cam, tr);
                return;
            }

            float yawDeg = 0f, pitchDeg = 55f, dist = 13f, focusH = 1.2f, fwd = 2.2f;
            try
            {
                yawDeg = Plugin.CameraYaw.Value;
                pitchDeg = Mathf.Clamp(Plugin.CameraPitch.Value, 15f, 80f);
                dist = Mathf.Clamp(Plugin.CameraDistance.Value, 4f, 30f);
                focusH = Plugin.CameraFocusHeight.Value;
                fwd = Plugin.CameraForwardOffset.Value;
            }
            catch { }

            // Keep the wide lens even if something in the game restores it.
            if (W.CamFovSaved)
            {
                try
                {
                    if (Mathf.Abs(W.Cam.fieldOfView - FovValue()) > 0.5f)
                    {
                        W.Cam.fieldOfView = FovValue();
                    }
                }
                catch { }
            }

            float yaw = yawDeg * Mathf.Deg2Rad;
            float pitch = pitchDeg * Mathf.Deg2Rad;

            // Look a bit ahead of Steve instead of straight at him: that is what puts the
            // player in the lower third of the frame, the signature Dungeons composition.
            Vector3 flatFwd = new Vector3(Mathf.Sin(yaw), 0f, Mathf.Cos(yaw));
            Vector3 focus = W.StevePos + Vector3.up * focusH + flatFwd * fwd;
            Vector3 back = new Vector3(
                -Mathf.Sin(yaw) * Mathf.Cos(pitch),
                Mathf.Sin(pitch),
                -Mathf.Cos(yaw) * Mathf.Cos(pitch));
            Vector3 want = focus + back * dist;

            // Camera collision: the fixed dungeon angle would happily shove the lens through
            // a tall block stack or a wall of scenery, showing the inside of the geometry -
            // the "camera clips into things" shots. Pull the camera in front of whatever is
            // between the focus and the wanted viewpoint.
            try
            {
                int mask = W.CharLayer >= 0 ? ~(1 << W.CharLayer) : ~0;
                Vector3 dirBack = back; // unit length already
                if (Physics.Raycast(focus, dirBack, out var hitC, dist, mask, QueryTriggerInteraction.Ignore))
                {
                    var col = hitC.collider;
                    if (col != null && col.gameObject != null)
                    {
                        string n = col.gameObject.name ?? "";
                        if (!n.StartsWith("Mc") && !n.StartsWith("blk_") && !n.StartsWith("tool"))
                        {
                            float allowed = Mathf.Max(2.5f, hitC.distance - 0.45f);
                            want = focus + dirBack * allowed;
                        }
                    }
                }
            }
            catch { }

            if (!W.CamInited)
            {
                W.CamSmooth = want;
                W.CamInited = true;
            }
            else
            {
                float k = 1f - Mathf.Exp(-10f * Mathf.Max(Time.deltaTime, 0.0001f));
                W.CamSmooth = Vector3.Lerp(W.CamSmooth, want, k);
            }

            // Insurance: never let the view drop under the street. If Steve ends up somewhere
            // odd the camera would otherwise look at the level from below - the "giant cyan
            // wall" shots in the footage.
            if (W.CamSmooth.y < W.GroundY + 1.6f)
            {
                W.CamSmooth.y = W.GroundY + 1.6f;
            }

            Vector3 look = focus - W.CamSmooth;
            if (look.sqrMagnitude < 0.0001f)
            {
                look = Vector3.forward;
            }
            tr.position = W.CamSmooth;
            tr.rotation = Quaternion.LookRotation(look, Vector3.up);

            W.CamIntended = W.CamSmooth;
            W.CamApplied = true;
        }
    }
}
