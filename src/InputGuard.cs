using System;
using UnityEngine;

namespace SchaleMcWorld
{
    public partial class McWorldBehaviour
    {
        /// <summary>
        /// Blue Archive draws its UI without UGUI - there is not a single Canvas or
        /// GraphicRaycaster in the scene (the only one ever found was the blocker created by
        /// this very file), so a UI overlay can never stop a click from reaching the game.
        /// Input is now taken away at the window procedure level (see NativeInput); this
        /// class only keeps the log line and the bookkeeping the enter/leave paths expect.
        /// </summary>
        private static void BuildInputBlocker()
        {
            Plugin.L.LogInfo("[McWorld] input taken at window level: " + (NativeInput.Hooked ? "active" : "NOT hooked"));
        }

        private static void RemoveInputBlocker()
        {
        }
    }
}
