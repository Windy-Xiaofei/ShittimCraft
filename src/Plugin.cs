using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace SchaleMcWorld
{
    /// <summary>
    /// Rebuilds a Minecraft-style play layer *inside* the Blue Archive client:
    /// a Steve character, block placing/breaking, a dungeon camera and a first-person mode.
    /// </summary>
    [BepInPlugin(Guid, "Schale MC World", "0.9.5")]
    public class Plugin : BasePlugin
    {
        public const string Guid = "com.shittim.ba.mcworld";

        internal static ManualLogSource L;
        internal static ConfigEntry<string> BundlePath;
        internal static ConfigEntry<string> ToggleKeyName;
        internal static ConfigEntry<string> UiModeKeyName;
        internal static ConfigEntry<float> CameraDistance;
        internal static ConfigEntry<float> CameraYaw;
        internal static ConfigEntry<float> CameraPitch;
        internal static ConfigEntry<float> CameraFocusHeight;
        internal static ConfigEntry<float> CameraFov;
        internal static ConfigEntry<float> CameraForwardOffset;
        internal static ConfigEntry<float> AimSensitivity;
        internal static ConfigEntry<bool> BreakSceneProps;
        internal static ConfigEntry<string> ShaderOverride;
        internal static ConfigEntry<float> FirstPersonFov;
        internal static ConfigEntry<float> FirstPersonSensitivity;
        internal static ConfigEntry<float> SteveHeight;
        internal static ConfigEntry<float> SwordDamage;
        internal static ConfigEntry<bool> HideConsole;
        internal static ConfigEntry<bool> AutoSelfTest;

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        public override void Load()
        {
            L = Log;

            // Kept for compatibility: its directory is where textures/ and mcshader live.
            BundlePath = Config.Bind("World", "BundlePath", "BepInEx/plugins/McWorld/mcassets",
                "Folder anchor for the runtime textures and the shader bundle (both live next to this path).");
            ToggleKeyName = Config.Bind("World", "ToggleKey", "F7",
                "Key that enters/leaves the Minecraft world layer.");
            CameraDistance = Config.Bind("Camera", "CameraDistance", 13.0f,
                "Distance from Steve in blocks (Minecraft Dungeons sits around 10-12).");
            CameraYaw = Config.Bind("Camera", "CameraYaw", 0f,
                "Fixed view direction in degrees. 0 looks along +Z, the direction the students face.");
            CameraPitch = Config.Bind("Camera", "CameraPitch", 55f,
                "Fixed downward angle in degrees. Higher = more top-down.");
            CameraFocusHeight = Config.Bind("Camera", "CameraFocusHeight", 1.2f,
                "Height above Steve's feet that the camera looks at.");
            CameraFov = Config.Bind("Camera", "CameraFov", 55f,
                "Field of view while the world layer is active. The battle camera uses a very " +
                "long lens (fov 10); leaving it alone makes the dungeon view look like a " +
                "third-person close-up.");
            CameraForwardOffset = Config.Bind("Camera", "CameraForwardOffset", 2.2f,
                "Looks this many blocks ahead of Steve, which places him in the lower part of " +
                "the screen like Minecraft Dungeons does.");
            AimSensitivity = Config.Bind("Camera", "AimSensitivity", 1.0f,
                "Virtual crosshair speed.");
            BreakSceneProps = Config.Bind("World", "BreakSceneProps", true,
                "Allow the crosshair to remove the level's own small props/blocks, not just placed blocks.");
            ShaderOverride = Config.Bind("World", "ShaderOverride", "",
                "Force a shader by exact name instead of the bundled McWorld/Unlit. " +
                "Empty is correct for normal use.");

            UiModeKeyName = Config.Bind("World", "UiModeKey", "F8",
                "Key that switches between the dungeon camera and the first-person view.");
            FirstPersonFov = Config.Bind("Camera", "FirstPersonFov", 74f,
                "Field of view in first-person mode.");
            FirstPersonSensitivity = Config.Bind("Camera", "FirstPersonSensitivity", 1.1f,
                "Mouse look speed in first-person mode.");

            SteveHeight = Config.Bind("World", "SteveHeight", 1.8f,
                "Steve's height in blocks (Minecraft's player is 1.8).");

            SwordDamage = Config.Bind("Combat", "SwordDamage", 1.0f,
                "Fraction of the target's MAX HP removed per sword hit. 1.0 = one hit kill " +
                "(the diamond sword's damage is not modelled, so this is the dial). " +
                "0.25 reproduces the old 'quarter HP per hit' behaviour.");

            HideConsole = Config.Bind("Misc", "HideConsole", true,
                "Hide the BepInEx console window. Logs are still written to " +
                "BepInEx/LogOutput.log and to plugins/McWorld/selftest.log.");
            AutoSelfTest = Config.Bind("Misc", "AutoSelfTest", true,
                "Run the built-in asset/UI/runtime self test at startup and write the result " +
                "to plugins/McWorld/selftest.log.");

            if (HideConsole.Value)
            {
                try
                {
                    var hwnd = GetConsoleWindow();
                    if (hwnd != IntPtr.Zero)
                    {
                        ShowWindow(hwnd, 0); // SW_HIDE
                    }
                }
                catch { }
            }

            AddComponent<McWorldBehaviour>();
            Log.LogInfo("[McWorld] loaded v0.9.5");
        }
    }

    /// <summary>All mutable state is static: Il2CppInterop object wrappers must not hold managed fields.</summary>
    internal static class W
    {
        public static bool Inited;
        public static bool InitFailed;

        public static readonly string[] BlockOrder =
        {
            "MCBlock_Grass", "MCBlock_Stone", "MCBlock_Dirt", "MCBlock_Cobble", "MCBlock_Planks",
            "MCBlock_Bricks", "MCBlock_Glass", "MCBlock_Sand", "MCBlock_Obsidian",
        };
        public static int SelectedBlock;

        public static bool Active;
        public static KeyCode ToggleKey = KeyCode.F7;
        public static float NextToggle;
        public static float NextCanvasCheck;
        public static float PendingEnterUntil;

        public static Vector3 Origin;
        public static float GroundY;
        public static bool OriginReady;

        public static GameObject Steve;
        public static Transform SteveHead;
        public static Transform SteveArmR;
        public static Transform SteveArmL;
        public static Transform SteveLegR;
        public static Transform SteveLegL;

        public static GameObject ToolPick;
        public static GameObject ToolSword;
        /// <summary>Third-person block held in the right hand (the default hotbar slots).</summary>
        public static GameObject HeldBlock;
        public static bool HeldSword;
        public static float SwingUntil;

        public static Vector3 StevePos;
        public static Vector3 SteveVel;
        public static float SteveYaw;
        public static float StudentRadius = 0.4f;
        public static float SteveScale = 1f;
        public static bool SteveIsFullSize;

        /// <summary>Player model metrics in world units, derived from the scale.</summary>
        public static float SteveBodyHeight => 1.75f * Mathf.Max(0.2f, SteveScale);
        public static float SteveBodyRadius => 0.30f * Mathf.Max(0.2f, SteveScale);
        public static GameObject SteveShadow;
        public static Texture2D ShadowTex;
        public static readonly List<GameObject> HeldVisuals = new();
        public static bool BumpLogged;
        public static float CamYaw;
        public static float WalkPhase;

        // Virtual crosshair, normalised screen coordinates with y up (dungeon view only).
        public static float AimX = 0.5f;
        public static float AimY = 0.5f;
        public static bool CamInited;
        public static Vector3 CamSmooth;

        public static Camera Cam;
        public static float CamOrigFov = 60f;
        public static bool CamOrigOrtho;
        public static float CamOrigNear = 0.3f;
        public static bool CamFovSaved;
        public static readonly Dictionary<Vector3Int, GameObject> Blocks = new();
        public static readonly Dictionary<Vector3Int, string> BlockTypes = new();
        public static GameObject BlockRoot;
        public static Mesh BlockMeshCached;
        public static readonly Dictionary<string, Material[]> BlockMats = new();

        public static int CharCount;
        public static readonly List<Vector3> CharPos = new();
        public static readonly List<Transform> CharTransforms = new();
        public static readonly object Gate = new();
        public static string Scene = "";
        public static int LastLoggedChars = -1;

        public static float NextScan;
        public static float NextPush;
        public static float LastLog = -10f;
        public static bool HudReady;
        public static Texture2D WhitePixel;
        public static Shader FallbackShader;
        public static Shader McShader;
        public static AssetBundle McBundle;
        public static bool McShaderFailed;
        public static readonly List<Behaviour> CamScripts = new();
        public static Vector3 CamIntended;
        public static bool CamApplied;
        public static bool CamOverrideLogged;

        // ---- level bounds -----------------------------------------------------
        public static bool EdgeGuard;
        public static float MapRadius;
        public static Vector3 LastSafe;
        public static bool EdgeGuardChecked;
        public static bool GroundProbeLogged;
        public static int CharLayer = -1;
        public static readonly Dictionary<long, Material> MaterialCache = new();
        public static readonly Dictionary<string, Texture2D> PngTextures = new();
        public static Material TemplateMat;

        public static GameObject Blocker;
        public static bool InputIntrospected;
        public static readonly List<UnityEngine.UI.GraphicRaycaster> DisabledRaycasters = new();
        public static readonly List<Canvas> HiddenCanvases = new();

        // ---- first person -----------------------------------------------------
        public static bool UiMode;
        public static bool FpLook;          // true = mouse look locked, false = free cursor
        public static float FpYaw;
        public static float FpPitch;
        public static bool FpInited;
        public static Vector3 FpAnchor;          // lobby FP spawn - bounds anchor without a world
        public static float ActiveSince;         // when the world layer was entered
        public static GameObject FpHand;
        public static GameObject FpArm;
        public static GameObject FpItem;
        public static bool FpCamOwned;
        public static float NextLookToggle;

        public static bool MineMode;
        public static bool Fly;
        public static float SpeedMul = 1f;
        public static float DamageFraction = 1f;
        public static float NightTarget;
        public static float NightAlpha;
        public static readonly List<McWorldBehaviour.BrokenUi> Broken = new();

        // ---- hotbar / inventory -----------------------------------------------
        public static readonly string[] Hotbar =
        {
            "block:MCBlock_Grass", "block:MCBlock_Stone", "block:MCBlock_Dirt",
            "block:MCBlock_Cobble", "block:MCBlock_Planks", "block:MCBlock_Bricks",
            "block:MCBlock_Glass", "block:MCBlock_Sand", "block:MCBlock_Obsidian",
        };

        public static bool InventoryOpen;
        public static float NextInvToggle;
        public static bool Blocking;
        public static float PearlReadyAt;
        public static readonly List<McWorldBehaviour.EnderPearl> Pearls = new();

        public static GameObject ToolShield;
        public static GameObject ToolPearl;

        // ---- command bar ------------------------------------------------------
        public static bool ChatOpen;
        public static string ChatInput = "";
        public static float ChatIgnoreUntil;
        public const float ChatStay = 12f;
        public static readonly List<McWorldBehaviour.ChatLine> ChatLog = new();
        public static KeyCode UiToggleKey = KeyCode.F8;
        public static float NextUiToggle;

        // ---- self test --------------------------------------------------------
        public static bool SelfTestRun;
        public static bool BattleTestRun;
        public static int SelfTestPass;
        public static int SelfTestFail;
        public static readonly List<string> SelfTestLines = new();
    }
}
