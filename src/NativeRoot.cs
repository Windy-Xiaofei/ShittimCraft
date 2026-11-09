using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace SchaleMcWorld
{
    /// <summary>
    /// Keeps runtime-built IL2CPP objects alive.
    ///
    /// Anything we create from managed code (Texture2D, GUIStyle, Mesh, Material) only exists
    /// because C# still points at it: Il2CppInterop hands back a wrapper, but the Boehm
    /// collector on the il2cpp heap is free to sweep the object underneath. The next member
    /// access then throws
    ///     "ObjectCollectedException: Object was garbage collected in IL2CPP domain"
    /// which is exactly what killed the HUD: every card, slot and label went through a
    /// GUIStyle or a generated texture, and those were swept after a few seconds.
    ///
    /// il2cpp_gchandle_new(ptr, pinned:false) is a strong root on the il2cpp heap, so the
    /// object can never be collected. One handle per object, for the lifetime of the process.
    /// </summary>
    internal static class NativeRoot
    {
        private const int MaxHandles = 2048;

        private static readonly List<IntPtr> Handles = new List<IntPtr>(128);
        private static readonly HashSet<object> Pinned = new HashSet<object>();
        private static bool _unavailable;
        private static int _kept;
        private static bool _warned;
        private static bool _capped;

        public static int Kept
        {
            get { return _kept; }
        }

        /// <summary>Strongly roots a wrapper so the il2cpp GC can never reclaim it.</summary>
        public static void Keep(Il2CppObjectBase obj)
        {
            if (obj == null || _unavailable)
            {
                return;
            }
            // The sheets are rebuilt every time the collector sweeps them; without this
            // check each rebuild would leak another handle (the log showed 10k+).
            lock (Pinned)
            {
                if (!Pinned.Add(obj))
                {
                    return;
                }
                if (Pinned.Count > MaxHandles)
                {
                    if (!_capped)
                    {
                        _capped = true;
                        Plugin.L.LogWarning("[McWorld] native root: pin table full (" + MaxHandles +
                                            ") - objects are dying faster than expected");
                    }
                    Pinned.Remove(obj);
                    return;
                }
            }
            IntPtr ptr;
            try
            {
                ptr = IL2CPP.Il2CppObjectBaseToPtr(obj);
            }
            catch (Exception e)
            {
                // Already collected - rooting it now is pointless. Unpin so the table does
                // not fill up with corpses of dead wrappers.
                lock (Pinned)
                {
                    Pinned.Remove(obj);
                }
                if (!_warned)
                {
                    _warned = true;
                    Plugin.L.LogWarning("[McWorld] native root: object already collected (" +
                                        e.GetType().Name + ")");
                }
                return;
            }

            if (ptr == IntPtr.Zero)
            {
                return;
            }

            try
            {
                IntPtr handle = IL2CPP.il2cpp_gchandle_new(ptr, false);
                if (handle == IntPtr.Zero)
                {
                    return;
                }
                Handles.Add(handle);
                _kept++;
            }
            catch (Exception e)
            {
                // Only a missing export disables the mechanism permanently.
                _unavailable = true;
                Plugin.L.LogWarning("[McWorld] native root unavailable, runtime objects may be collected: " +
                                    e.GetType().Name + ": " + e.Message);
            }
        }
    }
}
