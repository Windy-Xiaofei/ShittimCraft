using System;
using System.Collections.Generic;
using System.IO;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace SchaleMcWorld
{
    /// <summary>
    /// Everything the world layer draws is generated here at runtime: textures are read
    /// straight from the PNGs on disk and the meshes are built in managed code with
    /// Minecraft's texture unwrap. The asset bundle is gone from this path on purpose -
    /// its meshes shipped with collapsed UVs (all faces sampling one texel), which is why
    /// Steve kept rendering as a flat coloured blob.
    ///
    /// Materials are clones of a live character material, so our meshes use the exact same
    /// shader technique the game uses for the students. Shader.Find("Unlit/Texture")
    /// returns null in this build, and borrowing an arbitrary shader (MX/C-Weapon) rendered
    /// black because its texture property and vertex colour handling differ.
    /// </summary>
    public partial class McWorldBehaviour
    {
        private const float Px = 1f / 16f;

        private struct BoxDef
        {
            public string Name;
            public Vector3 Centre;
            public Vector3 Size;
            public int U, V, W, H, D;
        }

        // Standard 64x64 Steve layout: head 8^3 at (0,0), body 8x12x4 at (16,16),
        // right arm (40,16), right leg (0,16), left arm (32,48), left leg (16,48).
        private static readonly BoxDef[] SteveBoxes =
        {
            new BoxDef { Name = "head", Centre = new Vector3(0f, 28f * Px, 0f), Size = new Vector3(8f * Px, 8f * Px, 8f * Px),  U = 0,  V = 0,  W = 8, H = 8,  D = 8 },
            new BoxDef { Name = "body", Centre = new Vector3(0f, 18f * Px, 0f), Size = new Vector3(8f * Px, 12f * Px, 4f * Px), U = 16, V = 16, W = 8, H = 12, D = 4 },
            new BoxDef { Name = "armR", Centre = new Vector3(-6f * Px, 18f * Px, 0f), Size = new Vector3(4f * Px, 12f * Px, 4f * Px), U = 40, V = 16, W = 4, H = 12, D = 4 },
            new BoxDef { Name = "armL", Centre = new Vector3(6f * Px, 18f * Px, 0f), Size = new Vector3(4f * Px, 12f * Px, 4f * Px), U = 32, V = 48, W = 4, H = 12, D = 4 },
            new BoxDef { Name = "legR", Centre = new Vector3(-2f * Px, 6f * Px, 0f), Size = new Vector3(4f * Px, 12f * Px, 4f * Px), U = 0,  V = 16, W = 4, H = 12, D = 4 },
            new BoxDef { Name = "legL", Centre = new Vector3(2f * Px, 6f * Px, 0f), Size = new Vector3(4f * Px, 12f * Px, 4f * Px), U = 16, V = 48, W = 4, H = 12, D = 4 },
        };

        // face index: 0 = +Z front, 1 = -Z back, 2 = +X, 3 = -X, 4 = +Y top, 5 = -Y bottom
        private static Rect SkinRect(int u, int v, int w, int h, int d, int face)
        {
            int x = 0, y = 0, rw = 0, rh = 0;
            switch (face)
            {
                case 0: x = u + d; y = v + d; rw = w; rh = h; break;              // front
                case 1: x = u + d + w + d; y = v + d; rw = w; rh = h; break;      // back
                case 2: x = u + d + w; y = v + d; rw = d; rh = h; break;          // character's left (+X)
                case 3: x = u; y = v + d; rw = d; rh = h; break;                  // character's right (-X)
                case 4: x = u + d; y = v; rw = w; rh = d; break;                  // top
                default: x = u + d + w; y = v; rw = w; rh = d; break;             // bottom
            }
            // PNG row 0 is the top of the image, Unity's UV v=1 is the top of the texture.
            return new Rect(x / 64f, 1f - (y + rh) / 64f, rw / 64f, rh / 64f);
        }

        private static string AssetDir
        {
            get
            {
                string p = Plugin.BundlePath.Value ?? "BepInEx/plugins/McWorld/mcassets";
                string dir = System.IO.Path.GetDirectoryName(p);
                return string.IsNullOrEmpty(dir) ? "BepInEx/plugins/McWorld" : dir;
            }
        }

        private static readonly System.Collections.Generic.Dictionary<string, byte[]> PngBytes =
            new System.Collections.Generic.Dictionary<string, byte[]>(32);
        private static readonly System.Collections.Generic.Dictionary<string, float> NextReload =
            new System.Collections.Generic.Dictionary<string, float>(32);
        private static readonly System.Collections.Generic.HashSet<string> MissingLogged =
            new System.Collections.Generic.HashSet<string>();

        /// <summary>Reads a PNG from the plugin folder. Point filtered, no mips, no compression.</summary>
        private static Texture2D LoadDiskTexture(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }
            if (W.PngTextures.TryGetValue(name, out var cached) && cached != null)
            {
                try
                {
                    if (cached.width > 0)
                    {
                        return cached;
                    }
                }
                catch
                {
                    // Wrapper survived but the il2cpp object did not: reload below.
                }
                W.PngTextures.Remove(name);
            }

            // Blue Archive destroys unowned runtime textures. Reloading a PNG, decoding it
            // and re-uploading it on EVERY call (nine hotbar icons + block faces + the hand,
            // every frame) was the real reason the MC UI sat at ~12 fps. Keep the bytes in
            // managed memory and allow at most one rebuild per texture per 0.35 s; a missing
            // icon for a frame is invisible, a stuttering UI is not.
            float now = Time.unscaledTime;
            // First load of a given PNG is never throttled: BuildTools / BlockMaterials must
            // be able to create every tool and block material on the first frame. Throttling
            // only the *rebuilds* keeps the protection without leaving a freshly summoned
            // Steve empty-handed for 0.35 s (or with a textureless black material).
            bool firstLoad = !PngBytes.ContainsKey(name);
            if (!firstLoad)
            {
                if (NextReload.TryGetValue(name, out var next) && now < next)
                {
                    return null;
                }
                NextReload[name] = now + 0.35f;
            }

            try
            {
                if (!PngBytes.TryGetValue(name, out var bytes))
                {
                    string path = System.IO.Path.Combine(AssetDir, "textures", name + ".png");
                    if (!System.IO.File.Exists(path))
                    {
                        if (MissingLogged.Add(name))
                        {
                            Plugin.L.LogWarning("[McWorld] texture missing: " + path);
                        }
                        return null;
                    }
                    bytes = System.IO.File.ReadAllBytes(path);
                    PngBytes[name] = bytes;
                }
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!tex.LoadImage(bytes))
                {
                    Plugin.L.LogWarning("[McWorld] texture decode failed: " + name);
                    return null;
                }
                tex.filterMode = FilterMode.Point;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.name = name;
                if (string.Equals(name, "grass_block_top", StringComparison.OrdinalIgnoreCase))
                {
                    // Vanilla ships the grass top as a grey mask and colours it at runtime with
                    // the biome tint. We have no lighting and no tinting pass, so a raw copy
                    // renders snow white - bake the grass colour in here.
                    TintGrass(tex);
                }
                // Explicitly opt out of Resources.UnloadUnusedAssets as well.
                tex.hideFlags = HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset;
                NativeRoot.Keep(tex);
                AssetKeeper.Keep(tex);
                W.PngTextures[name] = tex;
                return tex;
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("[McWorld] texture load failed (" + name + "): " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// Multiplies a grey mask with Minecraft's grass colour. The mask averages ~147/255,
        /// so it is normalised first - otherwise the result comes out nearly black.
        /// </summary>
        private static void TintGrass(Texture2D tex)
        {
            try
            {
                var px = tex.GetPixels();
                const float mean = 147f / 255f;
                var grass = new Color(0.45f, 0.72f, 0.32f);
                for (int i = 0; i < px.Length; i++)
                {
                    var c = px[i];
                    float l = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
                    float b = Mathf.Clamp(l / mean, 0f, 1.7f);
                    px[i] = new Color(
                        Mathf.Clamp01(grass.r * b),
                        Mathf.Clamp01(grass.g * b),
                        Mathf.Clamp01(grass.b * b),
                        c.a);
                }
                tex.SetPixels(px);
                tex.Apply(false);
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("[McWorld] grass tint failed: " + e.Message);
            }
        }

        // ---- materials ----------------------------------------------------

        /// <summary>
        /// Flattens a borrowed character material: white tint, no shadow term, no glow.
        /// Only used when the unlit shader bundle is unavailable.
        /// </summary>
        private static void Neutralise(Material m)
        {
            try
            {
                string[] colors = { "_Color", "_ShadowTint", "_SpecColor", "_EmissionColor" };
                for (int i = 0; i < colors.Length; i++)
                {
                    if (m.HasProperty(colors[i]))
                    {
                        m.SetColor(colors[i], Color.white);
                    }
                }
                string[] bright = { "_LightValue", "_LightStrong", "_ShadowThreshold" };
                for (int i = 0; i < bright.Length; i++)
                {
                    if (m.HasProperty(bright[i]))
                    {
                        m.SetFloat(bright[i], 1f);
                    }
                }
                string[] off = { "_ShadowStrong", "_SpecStrong", "_UseGlow", "_AdditionalLightStrength" };
                for (int i = 0; i < off.Length; i++)
                {
                    if (m.HasProperty(off[i]))
                    {
                        m.SetFloat(off[i], 0f);
                    }
                }
                m.renderQueue = 2000;
            }
            catch { }
        }

        /// <summary>
        /// A live material borrowed from a student - the template every clone starts from.
        /// Only opaque character materials are accepted: a first version happily picked an
        /// FX circle (queue 3000, transparent), which is why the second summon produced an
        /// invisible Steve.
        /// </summary>
        private static Material FindTemplateMaterial()
        {
            if (W.TemplateMat != null)
            {
                return W.TemplateMat;
            }
            try
            {
                Transform[] targets;
                lock (W.Gate)
                {
                    targets = W.CharTransforms.ToArray();
                }

                Renderer best = null;
                float bestScore = -1f;
                for (int i = 0; i < targets.Length; i++)
                {
                    var tr = targets[i];
                    if (tr == null)
                    {
                        continue;
                    }
                    var rs = tr.gameObject.GetComponentsInChildren(Il2CppType.Of<Renderer>());
                    if (rs == null)
                    {
                        continue;
                    }
                    for (int k = 0; k < rs.Length; k++)
                    {
                        var r = rs[k]?.TryCast<Renderer>();
                        if (r == null)
                        {
                            continue;
                        }
                        var m = r.sharedMaterial;
                        if (m == null || m.shader == null || !m.shader.isSupported)
                        {
                            continue;
                        }
                        // Effects, UI, weapons and anything transparent are useless here:
                        // cloning them produced invisible (queue 3000) or unlit-black models.
                        string sn = m.shader.name ?? "";
                        string sl = sn.ToLowerInvariant();
                        if (sl.Contains("fx") || sl.Contains("effect") || sl.Contains("ui") ||
                            sl.Contains("weapon") || sl.Contains("particle") ||
                            m.renderQueue >= 2500)
                        {
                            continue;
                        }
                        var s = r.bounds.size;
                        float score = Mathf.Abs(s.x * s.y * s.z);
                        if (sl.Contains("char") || sl.Contains("skin") || sl.Contains("body"))
                        {
                            score *= 4f;
                        }
                        string gn = (r.gameObject != null ? r.gameObject.name : "").ToLowerInvariant();
                        if (gn.Contains("body") || gn.Contains("chara"))
                        {
                            score *= 2f;
                        }
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = r;
                        }
                    }
                }

                if (best != null)
                {
                    var m = best.sharedMaterial;
                    W.TemplateMat = m;
                    Plugin.L.LogInfo("[McWorld] template material: " + m.name + " shader=" + m.shader.name +
                                     " from renderer " + best.gameObject.name);
                    return m;
                }
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("[McWorld] template lookup failed: " + e.Message);
            }
            return null;
        }

        private static string FirstTextureProperty(Material m)
        {
            try
            {
                var sh = m.shader;
                if (sh == null)
                {
                    return null;
                }
                int n = sh.GetPropertyCount();
                for (int i = 0; i < n; i++)
                {
                    if (sh.GetPropertyType(i) == UnityEngine.Rendering.ShaderPropertyType.Texture)
                    {
                        return sh.GetPropertyName(i);
                    }
                }
            }
            catch { }
            return null;
        }

        private static string DumpProperties(Material m)
        {
            try
            {
                var sh = m.shader;
                if (sh == null)
                {
                    return "-";
                }
                var sb = new System.Text.StringBuilder();
                int n = sh.GetPropertyCount();
                for (int i = 0; i < n && i < 14; i++)
                {
                    if (sb.Length > 0)
                    {
                        sb.Append(",");
                    }
                    sb.Append(sh.GetPropertyName(i)).Append(":").Append(sh.GetPropertyType(i));
                }
                return sb.ToString();
            }
            catch
            {
                return "?";
            }
        }

        /// <summary>Puts our texture on whatever the material calls its base map.</summary>
        private static string BindTexture(Material m, Texture2D tex)
        {
            string prop = "_MainTex";
            try
            {
                var current = m.mainTexture;
                if (current != null)
                {
                    var sh = m.shader;
                    int n = sh != null ? sh.GetPropertyCount() : 0;
                    for (int i = 0; i < n; i++)
                    {
                        if (sh.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture)
                        {
                            continue;
                        }
                        string pn = sh.GetPropertyName(i);
                        if (m.GetTexture(pn) == current)
                        {
                            prop = pn;
                            break;
                        }
                    }
                }
                else
                {
                    var first = FirstTextureProperty(m);
                    if (first != null)
                    {
                        prop = first;
                    }
                }
                if (!m.HasProperty(prop))
                {
                    var first = FirstTextureProperty(m);
                    if (first == null)
                    {
                        return null;
                    }
                    prop = first;
                }
                m.SetTexture(prop, tex);
                m.SetTextureScale(prop, Vector2.one);
                m.SetTextureOffset(prop, Vector2.zero);
                return prop;
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("[McWorld] bind texture failed: " + e.Message);
                return null;
            }
        }

        private static Material MakeMat(Texture2D tex, bool friendly)
        {
            // The game sweeps runtime textures every second or so; a swept texture throws on
            // GetInstanceID and the caller reloads it from disk. The material built from the
            // OLD instance stays in the cache as a dead wrapper - returning it made every
            // block, tool and hand render BLACK. Dead entries are evicted here instead.
            long key;
            try
            {
                key = ((long)(tex != null ? tex.GetInstanceID() : 0) << 1) | (friendly ? 1L : 0L);
            }
            catch
            {
                return null; // texture already collected; caller will reload it
            }
            if (W.MaterialCache.TryGetValue(key, out var cached) && cached != null)
            {
                if (MatAlive(cached))
                {
                    if (tex != null)
                    {
                        try
                        {
                            if (cached.mainTexture == null)
                            {
                                BindTexture(cached, tex);
                            }
                        }
                        catch { }
                    }
                    return cached;
                }
                // Dead material: drop it so a fresh one is built below.
                W.MaterialCache.Remove(key);
            }

            Material mat = null;
            string prop = null;
            try
            {
                bool forceShader = false;
                try
                {
                    forceShader = !string.IsNullOrEmpty(Plugin.ShaderOverride.Value);
                }
                catch { }

                // Preferred path: our own unlit shader. No lighting term, no vertex colour
                // trickery - the texture is the final colour, like Minecraft.
                var unlit = forceShader ? null : LoadMcShader();
                if (unlit != null && !unlit.isSupported)
                {
                    Plugin.L.LogWarning("[McWorld] bundled shader not supported by this GPU - using a character material");
                    unlit = null;
                }
                if (unlit != null)
                {
                    mat = new Material(unlit);
                    mat.name = "McWorld_" + (tex != null ? tex.name : "plain");
                    if (tex != null)
                    {
                        mat.SetTexture("_MainTex", tex);
                    }
                    mat.SetTextureScale("_MainTex", Vector2.one);
                    mat.SetTextureOffset("_MainTex", Vector2.zero);
                    mat.SetColor("_Color", Color.white);
                    mat.renderQueue = 2000;
                    prop = "_MainTex";
                }
                else
                {
                    var template = forceShader ? null : FindTemplateMaterial();
                    if (template != null)
                    {
                        mat = new Material(template);
                        mat.name = "McWorld_" + (tex != null ? tex.name : "plain");
                        // A character material is set up for skinned meshes; our boxes are static,
                        // and leaving a skinning keyword on collapses them to nothing.
                        string[] kill = { "_SKINNED", "_SKINNING", "_USE_SKINNING", "_SKINNED_ON" };
                        for (int i = 0; i < kill.Length; i++)
                        {
                            try { mat.DisableKeyword(kill[i]); } catch { }
                        }
                        prop = BindTexture(mat, tex);
                        // The toon shaders multiply by a shadow tint / vertex light term, which
                        // is what made Steve a dark brown silhouette. Neutralise it.
                        Neutralise(mat);
                        if (friendly)
                        {
                            string kw = "";
                            try
                            {
                                var keywords = mat.shaderKeywords;
                                if (keywords != null)
                                {
                                    for (int i = 0; i < keywords.Length; i++)
                                    {
                                        kw += keywords[i] + " ";
                                    }
                                }
                            }
                            catch { }
                            Plugin.L.LogInfo("[McWorld] template keywords: [" + kw + "] queue=" + mat.renderQueue);
                        }
                    }
                    else
                    {
                        var shader = ResolveShader();
                        if (shader == null)
                        {
                            return null;
                        }
                        mat = new Material(shader);
                        mat.name = "McWorld_" + (tex != null ? tex.name : "plain");
                        if (tex != null)
                        {
                            mat.mainTexture = tex;
                        }
                        mat.mainTextureScale = Vector2.one;
                        mat.mainTextureOffset = Vector2.zero;
                        if (mat.HasProperty("_Color"))
                        {
                            mat.SetColor("_Color", Color.white);
                        }
                        prop = "_MainTex";
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.L.LogWarning("[McWorld] material failed: " + e.Message);
                return null;
            }

            if (friendly)
            {
                Plugin.L.LogInfo("[McWorld] material " + mat.name + " shader=" + mat.shader.name +
                                 " texProp=" + (prop ?? "<none>") +
                                 " props=" + DumpProperties(mat));
            }
            W.MaterialCache[key] = mat;
            return mat;
        }

        /// <summary>Destroys every runtime asset we created. Called when the world layer is left.</summary>
        private static void ReleaseAssets()
        {
            try
            {
                foreach (var m in W.MaterialCache.Values)
                {
                    if (m != null)
                    {
                        UnityEngine.Object.Destroy(m);
                    }
                }
            }
            catch { }
            W.MaterialCache.Clear();

            try
            {
                foreach (var t in W.PngTextures.Values)
                {
                    if (t != null)
                    {
                        UnityEngine.Object.Destroy(t);
                    }
                }
            }
            catch { }
            W.PngTextures.Clear();

            try
            {
                if (W.BlockMeshCached != null)
                {
                    UnityEngine.Object.Destroy(W.BlockMeshCached);
                }
            }
            catch { }
            W.BlockMeshCached = null;
            W.BlockMats.Clear();
            W.TemplateMat = null;
            W.WhitePixel = null;
        }

        /// <summary>Builds a box mesh; uvPerFace is indexed by the face order documented above.</summary>
        private static Mesh BuildBox(Vector3 size, Rect[] uvPerFace, int[] faceSubmesh, int submeshCount)
        {
            float hx = size.x * 0.5f, hy = size.y * 0.5f, hz = size.z * 0.5f;

            var verts = new List<Vector3>(24);
            var uvs = new List<Vector2>(24);
            var cols = new List<Color>(24);
            var tris = new List<int>[submeshCount];
            for (int i = 0; i < submeshCount; i++)
            {
                tris[i] = new List<int>(12);
            }

            void Quad(int face, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
            {
                int i = verts.Count;
                verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
                var r = uvPerFace[face];
                uvs.Add(new Vector2(r.xMin, r.yMin));   // bottom-left
                uvs.Add(new Vector2(r.xMax, r.yMin));   // bottom-right
                uvs.Add(new Vector2(r.xMax, r.yMax));   // top-right
                uvs.Add(new Vector2(r.xMin, r.yMax));   // top-left
                // Some of the game's shaders multiply by vertex colour; without the channel
                // they sample black, which is what turned Steve into a silhouette.
                cols.Add(Color.white); cols.Add(Color.white);
                cols.Add(Color.white); cols.Add(Color.white);
                int sub = faceSubmesh != null ? Mathf.Clamp(faceSubmesh[face], 0, submeshCount - 1) : 0;
                tris[sub].Add(i); tris[sub].Add(i + 2); tris[sub].Add(i + 1);
                tris[sub].Add(i); tris[sub].Add(i + 3); tris[sub].Add(i + 2);
            }

            Quad(0, new Vector3(-hx, -hy, hz), new Vector3(hx, -hy, hz), new Vector3(hx, hy, hz), new Vector3(-hx, hy, hz));
            Quad(1, new Vector3(hx, -hy, -hz), new Vector3(-hx, -hy, -hz), new Vector3(-hx, hy, -hz), new Vector3(hx, hy, -hz));
            Quad(2, new Vector3(hx, -hy, hz), new Vector3(hx, -hy, -hz), new Vector3(hx, hy, -hz), new Vector3(hx, hy, hz));
            Quad(3, new Vector3(-hx, -hy, -hz), new Vector3(-hx, -hy, hz), new Vector3(-hx, hy, hz), new Vector3(-hx, hy, -hz));
            Quad(4, new Vector3(-hx, hy, hz), new Vector3(hx, hy, hz), new Vector3(hx, hy, -hz), new Vector3(-hx, hy, -hz));
            Quad(5, new Vector3(-hx, -hy, -hz), new Vector3(hx, -hy, -hz), new Vector3(hx, -hy, hz), new Vector3(-hx, -hy, hz));

            var mesh = new Mesh();
            mesh.name = "McBox";
            mesh.vertices = verts.ToArray();
            mesh.uv = uvs.ToArray();
            mesh.colors = cols.ToArray();
            mesh.subMeshCount = submeshCount;
            for (int i = 0; i < submeshCount; i++)
            {
                mesh.SetTriangles(tris[i].ToArray(), i);
            }
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // ---- blocks -------------------------------------------------------

        private struct BlockDef
        {
            public string Key;
            public string Top, Side, Bottom;
        }

        private static readonly BlockDef[] BlockDefs =
        {
            new BlockDef { Key = "mcblock_grass",      Top = "grass_block_top", Side = "grass_block_side", Bottom = "dirt" },
            new BlockDef { Key = "mcblock_stone",      Top = "stone",           Side = "stone",            Bottom = "stone" },
            new BlockDef { Key = "mcblock_dirt",       Top = "dirt",            Side = "dirt",             Bottom = "dirt" },
            new BlockDef { Key = "mcblock_cobble",     Top = "cobblestone",     Side = "cobblestone",      Bottom = "cobblestone" },
            new BlockDef { Key = "mcblock_planks",     Top = "oak_planks",      Side = "oak_planks",       Bottom = "oak_planks" },
            new BlockDef { Key = "mcblock_bricks",     Top = "bricks",          Side = "bricks",           Bottom = "bricks" },
            new BlockDef { Key = "mcblock_glass",      Top = "glass",           Side = "glass",            Bottom = "glass" },
            new BlockDef { Key = "mcblock_sand",       Top = "sand",            Side = "sand",             Bottom = "sand" },
            new BlockDef { Key = "mcblock_obsidian",   Top = "obsidian",        Side = "obsidian",         Bottom = "obsidian" },
            new BlockDef { Key = "mcblock_tnt",        Top = "tnt_top",         Side = "tnt_side",         Bottom = "tnt_top" },
            new BlockDef { Key = "mcblock_netherrack", Top = "netherrack",      Side = "netherrack",       Bottom = "netherrack" },
            new BlockDef { Key = "mcblock_iron",       Top = "iron_block",      Side = "iron_block",       Bottom = "iron_block" },
            new BlockDef { Key = "mcblock_gold",       Top = "gold_block",      Side = "gold_block",       Bottom = "gold_block" },
            new BlockDef { Key = "mcblock_diamond",    Top = "diamond_block",   Side = "diamond_block",    Bottom = "diamond_block" },
        };

        /// <summary>A material whose native side was swept still has a live wrapper: touch it.</summary>
        private static bool MatAlive(Material m)
        {
            if (m == null)
            {
                return false;
            }
            try
            {
                return m.shader != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>A mesh whose native side was swept renders nothing and throws on upload.</summary>
        private static bool MeshAlive(Mesh m)
        {
            if (m == null)
            {
                return false;
            }
            try
            {
                return m.vertexCount > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Unit cube: submesh 0 = four sides, 1 = top, 2 = bottom.</summary>
        private static Mesh BlockMesh()
        {
            if (MeshAlive(W.BlockMeshCached))
            {
                return W.BlockMeshCached;
            }
            W.BlockMeshCached = null;
            var full = new Rect[6];
            for (int i = 0; i < 6; i++)
            {
                full[i] = new Rect(0f, 0f, 1f, 1f);
            }
            var map = new[] { 0, 0, 0, 0, 1, 2 };
            W.BlockMeshCached = BuildBox(Vector3.one, full, map, 3);
            return W.BlockMeshCached;
        }

        /// <summary>[side, top, bottom] materials for a block key such as "mcblock_grass".</summary>
        private static Material[] BlockMaterials(string key)
        {
            if (W.BlockMats.TryGetValue(key, out var cached) && cached != null && cached.Length == 3 &&
                MatAlive(cached[0]) && MatAlive(cached[1]) && MatAlive(cached[2]))
            {
                return cached;
            }
            W.BlockMats.Remove(key);

            BlockDef def = BlockDefs[1]; // stone
            for (int i = 0; i < BlockDefs.Length; i++)
            {
                if (BlockDefs[i].Key == key)
                {
                    def = BlockDefs[i];
                    break;
                }
            }

            var mats = new Material[3];
            mats[0] = MakeMat(LoadDiskTexture(def.Side), false);
            mats[1] = MakeMat(LoadDiskTexture(def.Top), false);
            mats[2] = MakeMat(LoadDiskTexture(def.Bottom), false);
            if (mats[0] == null || mats[1] == null || mats[2] == null)
            {
                return null;
            }
            W.BlockMats[key] = mats;
            return mats;
        }

        /// <summary>Icon texture for a hotbar slot, e.g. "mcblock_grass" -> coloured grass top.</summary>
        private static Texture2D BlockIconTexture(string key)
        {
            BlockDef def = BlockDefs[1];
            for (int i = 0; i < BlockDefs.Length; i++)
            {
                if (BlockDefs[i].Key == key)
                {
                    def = BlockDefs[i];
                    break;
                }
            }
            // Grass reads as grass only from above; everything else looks best from the side.
            return LoadDiskTexture(key == "mcblock_grass" ? def.Top : def.Side);
        }

        // ---- Steve --------------------------------------------------------

        private static GameObject BuildSteveRuntime()
        {
            var skin = LoadDiskTexture("steve");
            if (skin == null)
            {
                Plugin.L.LogError("[McWorld] steve.png missing - cannot build the player model");
                return null;
            }
            var mat = MakeMat(skin, true);
            if (mat == null)
            {
                Plugin.L.LogError("[McWorld] no usable material for the player model");
                return null;
            }

            var root = new GameObject("McSteve");
            for (int i = 0; i < SteveBoxes.Length; i++)
            {
                var b = SteveBoxes[i];
                var part = new GameObject(b.Name);
                part.transform.SetParent(root.transform, false);
                part.transform.localPosition = b.Centre;

                var rects = new Rect[6];
                for (int f = 0; f < 6; f++)
                {
                    rects[f] = SkinRect(b.U, b.V, b.W, b.H, b.D, f);
                }

                var mf = part.AddComponent<MeshFilter>();
                var mr = part.AddComponent<MeshRenderer>();
                mf.sharedMesh = BuildBox(b.Size, rects, null, 1);
                mr.sharedMaterial = mat;

                if (i == 0)
                {
                    var uv = mf.sharedMesh.uv;
                    Plugin.L.LogInfo("[McWorld] steve part " + b.Name + " uvCount=" + (uv != null ? uv.Length : 0) +
                                     " uv0=" + (uv != null && uv.Length > 0 ? uv[0].x.ToString("F3") + "," + uv[0].y.ToString("F3") : "?") +
                                     " tex=" + skin.name + " " + skin.width + "x" + skin.height +
                                     " shader=" + (mat.shader != null ? mat.shader.name : "null"));
                }
            }
            return root;
        }
    }
}
