using System;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace SchaleMcWorld
{
    public partial class McWorldBehaviour
    {
        private static readonly string[] ShaderCandidates =
        {
            "Unlit/Texture",
            "UI/Default",
            "Sprites/Default",
            "Legacy Shaders/Unlit/Texture",
        };

        /// <summary>
        /// Our own unlit shader, shipped as the "mcshader" asset bundle next to the plugin.
        /// This is the only reliable way to get it: the player strips Unlit/Texture, so
        /// Shader.Find returns null, and every shader the game does have applies its toon
        /// lighting to our meshes (Steve came out as a dark brown silhouette).
        /// </summary>
        private static Shader LoadMcShader()
        {
            if (W.McShader != null)
            {
                return W.McShader;
            }

            // Once a shader has been loaded from any bundle Unity keeps it in memory, and
            // Shader.Find can see it. This matters because an AssetBundle can only be loaded
            // from file once per process: the second F7 used to get a null bundle, fall back
            // to a character material, and render Steve as a grey blob.
            try
            {
                var live = Shader.Find("McWorld/Unlit");
                if (live != null)
                {
                    W.McShader = live;
                    Plugin.L.LogInfo("[McWorld] shader found in memory: " + live.name);
                    return live;
                }
            }
            catch { }

            // A bundle that is already open can still be asked for its assets.
            var cached = TryShaderFrom(W.McBundle);
            if (cached != null)
            {
                W.McShader = cached;
                Plugin.L.LogInfo("[McWorld] shader loaded from cached bundle: " + cached.name);
                return cached;
            }

            if (W.McShaderFailed)
            {
                return null;
            }

            string path = null;
            try
            {
                path = System.IO.Path.Combine(AssetDir, "mcshader");
            }
            catch { }

            try
            {
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
                {
                    W.McShaderFailed = true;
                    Plugin.L.LogWarning("[McWorld] shader bundle missing: " + path);
                    return null;
                }

                AssetBundle bundle = null;
                try
                {
                    bundle = AssetBundle.LoadFromFile(path);
                }
                catch (Exception e)
                {
                    Plugin.L.LogWarning("[McWorld] shader bundle load: " + e.Message);
                }
                if (bundle == null)
                {
                    // "Already loaded" is a normal, recoverable state - never latch the failure
                    // flag here, the bundle may simply be open from a previous session.
                    // Already-open bundle: the shader is registered, so Shader.Find above will
                    // pick it up on the next call. Never latch a failure here.
                    Plugin.L.LogWarning("[McWorld] shader bundle already open: " + path);
                    return null;
                }

                Shader sh = TryShaderFrom(bundle);
                if (sh == null)
                {
                    Plugin.L.LogWarning("[McWorld] no shader inside bundle " + path);
                    return null;
                }

                // Keep the bundle referenced for as long as the shader is in use.
                W.McBundle = bundle;
                W.McShader = sh;
                Plugin.L.LogInfo("[McWorld] shader loaded from bundle: " + sh.name + " supported=" + sh.isSupported);
                return sh;
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("[McWorld] shader bundle exception: " + e.Message);
                return null;
            }
        }

        /// <summary>Pulls the McWorld shader out of a bundle (null-safe, never throws).</summary>
        private static Shader TryShaderFrom(AssetBundle bundle)
        {
            if (bundle == null)
            {
                return null;
            }
            try
            {
                try
                {
                    var byName = bundle.LoadAsset("McWorldUnlit", Il2CppType.Of<Shader>())?.TryCast<Shader>();
                    if (byName != null)
                    {
                        return byName;
                    }
                }
                catch { }
                var all = bundle.LoadAllAssets(Il2CppType.Of<Shader>());
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        var c = all[i]?.TryCast<Shader>();
                        if (c != null)
                        {
                            return c;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Last-resort shader when no character material could be cloned. Shader.Find only sees
        /// shaders that were included in the player, so every candidate is also looked up in
        /// the set of shaders already loaded from the game's bundles.
        /// </summary>
        private static Shader ResolveShader()
        {
            if (W.FallbackShader != null)
            {
                return W.FallbackShader;
            }

            // Escape hatch: the player can force any shader that is already loaded by name.
            string wanted = null;
            try
            {
                wanted = Plugin.ShaderOverride.Value;
            }
            catch { }

            try
            {
                var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Shader>());
                if (all != null)
                {
                    var names = new System.Text.StringBuilder();
                    Shader overrideHit = null;
                    for (int i = 0; i < all.Length; i++)
                    {
                        var sh = all[i]?.TryCast<Shader>();
                        if (sh == null || sh.name == null)
                        {
                            continue;
                        }
                        if (names.Length < 1600)
                        {
                            names.Append(sh.name).Append(" | ");
                        }
                        if (!string.IsNullOrEmpty(wanted) && sh.name == wanted)
                        {
                            overrideHit = sh;
                        }
                    }
                    Plugin.L.LogInfo("[McWorld] loaded shaders: " + names);
                    if (overrideHit != null)
                    {
                        W.FallbackShader = overrideHit;
                        Plugin.L.LogInfo("[McWorld] shader (override): " + overrideHit.name);
                        return overrideHit;
                    }
                }
            }
            catch { }

            for (int i = 0; i < ShaderCandidates.Length; i++)
            {
                try
                {
                    var found = Shader.Find(ShaderCandidates[i]);
                    if (found != null)
                    {
                        W.FallbackShader = found;
                        Plugin.L.LogInfo("[McWorld] shader: " + ShaderCandidates[i]);
                        return found;
                    }
                }
                catch { }
            }

            FindFallbackShader();
            return W.FallbackShader;
        }

        /// <summary>Borrows a supported shader from any renderer already alive in the scene.</summary>
        private static void FindFallbackShader()
        {
            if (W.FallbackShader != null)
            {
                return;
            }
            try
            {
                var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<Renderer>());
                if (all == null)
                {
                    return;
                }
                for (int i = 0; i < all.Length; i++)
                {
                    var r = all[i]?.TryCast<Renderer>();
                    if (r == null)
                    {
                        continue;
                    }
                    var m = r.sharedMaterial;
                    if (m == null || m.shader == null || !m.shader.isSupported)
                    {
                        continue;
                    }
                    W.FallbackShader = m.shader;
                    Plugin.L.LogInfo("[McWorld] fallback shader: " + m.shader.name);
                    return;
                }
            }
            catch { }
        }

        private static void ApplyLayer(GameObject root, int layer)
        {
            if (layer < 0)
            {
                return;
            }
            try
            {
                root.layer = layer;
                var stack = new System.Collections.Generic.Stack<Transform>();
                stack.Push(root.transform);
                while (stack.Count > 0)
                {
                    var tr = stack.Pop();
                    if (tr == null)
                    {
                        continue;
                    }
                    tr.gameObject.layer = layer;
                    int n = tr.childCount;
                    for (int i = 0; i < n; i++)
                    {
                        stack.Push(tr.GetChild(i));
                    }
                }
            }
            catch { }
        }
    }
}
