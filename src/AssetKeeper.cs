using System;
using System.Collections.Generic;
using UnityEngine;

namespace SchaleMcWorld
{
    /// <summary>
    /// Flags runtime textures so the game's asset sweeps can recognise them.
    ///
    /// IMPORTANT: an earlier version of this class also wired every texture into a hidden
    /// holder object (renderer -> material -> texture) to give it a native reference. That
    /// did NOT stop the game from destroying the textures - but it DID leak one GameObject,
    /// MeshFilter, MeshRenderer and Material per texture rebuild, and the textures are
    /// rebuilt every time the collector sweeps them. Thousands of live renderers later the
    /// frame rate was in single digits. The retry-on-collected draw path is the real fix;
    /// this class now only sets the flags and counts.
    /// </summary>
    internal static class AssetKeeper
    {
        private static int _kept;
        private static bool _failedOnce;

        public static int KeptCount
        {
            // Count only. Keeping a managed reference to every swept wrapper grew this list
            // to 32k+ entries and pinned thousands of dead il2cpp objects in memory.
            get { return _kept; }
        }

        /// <summary>False once a Keep() has thrown - surfaces silent breakage in the self test.</summary>
        public static bool Failed
        {
            get { return _failedOnce; }
        }

        /// <summary>Marks a runtime texture as ours. Cheap, allocation free, never breaks a frame.</summary>
        public static void Keep(Texture2D tex)
        {
            if (tex == null)
            {
                return;
            }
            try
            {
                tex.hideFlags = HideFlags.HideAndDontSave;
                _kept++;
            }
            catch (Exception e)
            {
                if (!_failedOnce)
                {
                    _failedOnce = true;
                    try
                    {
                        Plugin.L.LogWarning("[McWorld] asset keeper failed: " +
                                            e.GetType().Name + ": " + e.Message);
                    }
                    catch { }
                }
            }
        }
    }
}
