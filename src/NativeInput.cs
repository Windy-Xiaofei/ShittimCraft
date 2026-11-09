using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace SchaleMcWorld
{
    /// <summary>
    /// Takes the keyboard and mouse away from the game by subclassing the game window's
    /// WndProc while the world layer is active.
    ///
    /// Earlier attempts are known to fail on this build:
    ///   * UGUI blockers do nothing - the game has no Canvas (its UI is NGUI).
    ///   * SetWindowsHookEx(WH_*_LL) looked installed but keys still reached the game, and a
    ///     low level hook is skipped by Windows whenever the installing thread is busy for
    ///     more than LowLevelHooksTimeout, which is exactly what a rendering thread is.
    /// Subclassing the window procedure is in-process and cannot time out: the messages are
    /// dropped before Unity's own WndProc ever sees them.
    ///
    /// Because Unity never sees those messages, all state is read back from Win32 with
    /// GetAsyncKeyState/cursor polling, which is unaffected by swallowing messages.
    ///
    /// Two profiles exist:
    ///   * pinned  - first person / dungeon view: cursor locked to the window centre, every
    ///               key and button swallowed, mouse deltas accumulated for the crosshair.
    ///   * free    - "2D UI" mode: the OS cursor stays where the player puts it and NGUI keeps
    ///               receiving mouse movement, so Blue Archive's own buttons still work. Only
    ///               the buttons are swallowed, and only while the pickaxe is out.
    /// </summary>
    internal static class NativeInput
    {
        public const int VkLButton = 0x01;
        public const int VkRButton = 0x02;
        public const int VkSpace = 0x20;
        public const int VkDigit1 = 0x31;
        public const int VkA = 0x41;
        public const int VkC = 0x43;
        public const int VkD = 0x44;
        public const int VkE = 0x45;
        public const int VkQ = 0x51;
        public const int VkS = 0x53;
        public const int VkT = 0x54;
        public const int VkV = 0x56;
        public const int VkW = 0x57;
        public const int VkF7 = 0x76;
        public const int VkF8 = 0x77;
        public const int VkLShift = 0xA0;
        public const int VkRShift = 0xA1;
        public const int VkRControl = 0xA3;
        public const int VkEscape = 0x1B;
        public const int VkTab = 0x09;
        public const int VkSlash = 0xBF;   // OEM_2, the "/" key on a US layout

        private const int GWLP_WNDPROC = -4;

        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP = 0x0101;
        private const uint WM_CHAR = 0x0102;
        private const uint WM_SYSKEYDOWN = 0x0104;

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X, Y;
        }

        private static WndProcDelegate _proc;
        private static IntPtr _hwnd = IntPtr.Zero;
        private static IntPtr _oldProc = IntPtr.Zero;
        private static bool _installed;
        private static bool _active;

        private static int _centerX;
        private static int _centerY;
        private static int _recenterIn;
        private static bool _cursorHidden;
        private static Vector2 _accum;

        // ---- profiles -----------------------------------------------------

        private static bool _freeCursor;
        private static bool _swallowMove = true;
        private static bool _swallowButtons = true;
        private static bool _swallowKeys = true;
        private static bool _passEscape;
        private static bool _captureText;

        private static float _suppressUntil;

        private static readonly StringBuilder Typed = new StringBuilder(64);
        private static int _backspaces;
        private static int _submits;
        private static int _escapes;

        private struct KeyState
        {
            public bool Down;
            public int Frame;
        }

        private static readonly Dictionary<int, KeyState> States = new Dictionary<int, KeyState>();

        public static bool Hooked => _installed;
        public static bool FreeCursor => _freeCursor;
        public static string LastError = "";

        /// <summary>
        /// Configures what the window procedure is allowed to eat.
        /// freeCursor: keep the OS pointer where the player puts it (needed for NGUI clicking).
        /// swallowKeys: eat the keyboard instead of forwarding it to the game.
        /// passEscape: forward VK_ESCAPE even when keys are swallowed (UI mode needs it to
        ///             close Blue Archive's own popups).
        /// </summary>
        public static void SetProfile(bool freeCursor, bool swallowMove, bool swallowButtons,
                                      bool swallowKeys, bool passEscape = false)
        {
            _freeCursor = freeCursor;
            _swallowMove = swallowMove;
            _swallowButtons = swallowButtons;
            _swallowKeys = swallowKeys;
            _passEscape = passEscape;
            if (!freeCursor)
            {
                _recenterIn = 0;
            }
        }

        public static void SetButtons(bool swallow)
        {
            _swallowButtons = swallow;
        }

        public static void SetCaptureText(bool on)
        {
            if (_captureText == on)
            {
                return;
            }
            _captureText = on;
            Typed.Clear();
            _backspaces = 0;
            _submits = 0;
            _escapes = 0;
            if (on)
            {
                // The window procedure has to be in place to see WM_CHAR at all.
                Install();
            }
        }

        public static string TakeChars()
        {
            if (Typed.Length == 0)
            {
                return "";
            }
            string s = Typed.ToString();
            Typed.Clear();
            return s;
        }

        public static int TakeBackspaces()
        {
            int n = _backspaces;
            _backspaces = 0;
            return n;
        }

        public static int TakeSubmits()
        {
            int n = _submits;
            _submits = 0;
            return n;
        }

        public static int TakeEscapes()
        {
            int n = _escapes;
            _escapes = 0;
            return n;
        }

        // ---- install / uninstall ------------------------------------------

        public static bool Install()
        {
            if (_installed)
            {
                _active = true;
                return true;
            }

            try
            {
                _hwnd = FindWindowHandle();
                if (_hwnd == IntPtr.Zero)
                {
                    LastError = "no game window";
                    Plugin.L.LogError("[McWorld] input block failed: " + LastError);
                    return false;
                }

                _proc = WndProc;
                IntPtr fn = Marshal.GetFunctionPointerForDelegate(_proc);
                if (IntPtr.Size == 8)
                {
                    _oldProc = SetWindowLongPtr64(_hwnd, GWLP_WNDPROC, fn);
                }
                else
                {
                    _oldProc = (IntPtr)SetWindowLong32(_hwnd, GWLP_WNDPROC, fn.ToInt32());
                }

                if (_oldProc == IntPtr.Zero)
                {
                    LastError = "SetWindowLong failed err=" + Marshal.GetLastWin32Error() + " hwnd=" + _hwnd;
                    Plugin.L.LogError("[McWorld] input block failed: " + LastError);
                    return false;
                }

                _installed = true;
                _active = true;
                _accum = Vector2.zero;
                States.Clear();
                if (!_freeCursor)
                {
                    Recenter();
                }
                Plugin.L.LogInfo("[McWorld] input hooked (hwnd=" + _hwnd +
                                 ", freeCursor=" + _freeCursor + ")");
                return true;
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Plugin.L.LogError("[McWorld] input block exception: " + e.Message);
                Restore();
                return false;
            }
        }

        public static void Uninstall()
        {
            _active = false;
            _captureText = false;
            _freeCursor = false;
            _swallowMove = true;
            _swallowButtons = true;
            _swallowKeys = true;
            _passEscape = false;
            Restore();
        }

        private static void Restore()
        {
            try
            {
                if (_installed && _hwnd != IntPtr.Zero && _oldProc != IntPtr.Zero)
                {
                    if (IntPtr.Size == 8)
                    {
                        SetWindowLongPtr64(_hwnd, GWLP_WNDPROC, _oldProc);
                    }
                    else
                    {
                        SetWindowLong32(_hwnd, GWLP_WNDPROC, _oldProc.ToInt32());
                    }
                    Plugin.L.LogInfo("[McWorld] input released");
                }
            }
            catch { }

            _installed = false;
            _oldProc = IntPtr.Zero;
            _proc = null;
            _accum = Vector2.zero;
            States.Clear();
            if (_cursorHidden)
            {
                _cursorHidden = false;
                try { Cursor.visible = true; } catch { }
            }
        }

        private static IntPtr FindWindowHandle()
        {
            try
            {
                var p = System.Diagnostics.Process.GetCurrentProcess();
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    return p.MainWindowHandle;
                }
            }
            catch { }

            try
            {
                return GetForegroundWindow();
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        private static IntPtr EnsureHwnd()
        {
            if (_hwnd != IntPtr.Zero)
            {
                return _hwnd;
            }
            try
            {
                // Only trust the process' own main window here: the foreground-window
                // fallback could latch onto some unrelated app and then swallow every
                // input check forever.
                var p = System.Diagnostics.Process.GetCurrentProcess();
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    _hwnd = p.MainWindowHandle;
                }
            }
            catch { }
            return _hwnd;
        }

        // ---- window procedure ---------------------------------------------

        private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (_active)
                {
                    if (_captureText && Capture(msg, wParam))
                    {
                        return IntPtr.Zero;
                    }
                    if (ShouldSwallow(msg, wParam))
                    {
                        // Never trap system shortcuts: alt-tab / alt-f4 must always work.
                        if (!SystemKeysDown())
                        {
                            return IntPtr.Zero;
                        }
                    }
                }
            }
            catch { }
            return CallWindowProc(_oldProc, hWnd, msg, wParam, lParam);
        }

        /// <summary>
        /// Records the typing that the game must never see. Called while the command bar is
        /// open; every key is swallowed so Blue Archive cannot act on it.
        /// </summary>
        private static bool Capture(uint msg, IntPtr wParam)
        {
            int code = wParam.ToInt32();
            if (msg == WM_CHAR)
            {
                int ch = code & 0xFFFF;
                if (ch == 8)
                {
                    _backspaces++;
                }
                else if (ch == 13)
                {
                    _submits++;
                }
                else if (ch >= 32 && ch != 127)
                {
                    Typed.Append((char)ch);
                }
                return true;
            }
            if (msg == WM_KEYDOWN && (code & 0xFFFF) == VkEscape)
            {
                _escapes++;
                return true;
            }
            // Everything else that is keyboard or button while typing is swallowed silently.
            switch (msg)
            {
                case WM_KEYDOWN:
                case WM_KEYUP:
                case WM_SYSKEYDOWN:
                case 0x0105: // WM_SYSKEYUP
                case 0x0103: // WM_DEADCHAR
                case 0x0106: // WM_SYSCHAR
                case 0x0107: // WM_SYSDEADCHAR
                case 0x0201: // WM_LBUTTONDOWN
                case 0x0202:
                case 0x0203:
                case 0x0204:
                case 0x0205:
                case 0x0206:
                case 0x0207:
                case 0x0208:
                case 0x0209:
                case 0x020B:
                case 0x020C:
                    return true;
            }
            return false;
        }

        private static bool ShouldSwallow(uint msg, IntPtr wParam)
        {
            switch (msg)
            {
                case WM_KEYDOWN:
                case WM_KEYUP:
                case WM_SYSKEYDOWN:
                case 0x0105:
                    if (!_swallowKeys)
                    {
                        return false;
                    }
                    if (_passEscape && (wParam.ToInt32() & 0xFFFF) == VkEscape)
                    {
                        return false;
                    }
                    return true;

                case WM_CHAR:
                case 0x0103:
                case 0x0106:
                case 0x0107:
                    return _swallowKeys;

                case 0x00FF: // WM_INPUT  (raw input: the new Input System reads this)
                    // In free-cursor mode NGUI needs the raw stream, otherwise nothing hovers.
                    return !_freeCursor;

                case 0x0200: // WM_MOUSEMOVE
                    return _freeCursor ? _swallowMove : true;

                case 0x0201: // WM_LBUTTONDOWN
                case 0x0202:
                case 0x0203:
                case 0x0204:
                case 0x0205:
                case 0x0206:
                case 0x0207:
                case 0x0208:
                case 0x0209:
                case 0x020B:
                case 0x020C:
                    return _freeCursor ? _swallowButtons : true;
            }
            // WM_MOUSEWHEEL (0x020A) is deliberately left alone: the hotbar reads it.
            return false;
        }

        /// <summary>
        /// True only for shortcuts that must survive the input block: Alt+Tab and Alt+F4.
        ///
        /// The old check returned true whenever Alt, Ctrl *or* Win was down, so pressing the
        /// Windows key alone let WM_SYSKEYDOWN through and opened the Start menu on top of the
        /// game (that is the white Start screen in the first clip).
        /// </summary>
        private static bool SystemKeysDown()
        {
            try
            {
                bool alt = (GetAsyncKeyState(0x12) & 0x8000) != 0;    // VK_MENU
                if (!alt)
                {
                    // Ctrl and Win on their own must never reach the OS from inside the game.
                    return false;
                }
                bool f4 = (GetAsyncKeyState(0x73) & 0x8000) != 0;     // VK_F4
                bool tab = (GetAsyncKeyState(0x09) & 0x8000) != 0;    // VK_TAB
                return f4 || tab;
            }
            catch
            {
                return true;
            }
        }

        // ---- per frame -----------------------------------------------------

        /// <summary>
        /// Call once per frame while the world layer is active: keeps the cursor pinned to the
        /// window centre and accumulates the movement that the game never sees.
        /// </summary>
        public static void Tick()
        {
            if (!_installed || !_active)
            {
                return;
            }

            try
            {
                if (GetForegroundWindow() != _hwnd)
                {
                    // Alt-tabbed: give the OS cursor back, otherwise every other window is
                    // used blind.
                    if (_cursorHidden)
                    {
                        _cursorHidden = false;
                        Cursor.visible = true;
                    }
                    return;
                }

                if (_freeCursor)
                {
                    // 2D UI mode: the pointer is the player's, never steal it. The game likes
                    // to hide/lock the cursor during battle, so put it back every frame.
                    if (_cursorHidden)
                    {
                        _cursorHidden = false;
                    }
                    Cursor.visible = true;
                    if (Cursor.lockState != CursorLockMode.None)
                    {
                        Cursor.lockState = CursorLockMode.None;
                    }
                    return;
                }

                if (!_cursorHidden)
                {
                    _cursorHidden = true;
                }
                // Re-assert every frame: the game loves to show its own pointer during battle,
                // and two cursors on screen (ours plus theirs) makes aiming confusing.
                if (Cursor.visible)
                {
                    Cursor.visible = false;
                }

                if (--_recenterIn <= 0)
                {
                    Recenter();
                    _recenterIn = 30;
                }

                POINT p;
                if (!GetCursorPos(out p))
                {
                    return;
                }

                int dx = p.X - _centerX;
                int dy = p.Y - _centerY;
                if (dx == 0 && dy == 0)
                {
                    return;
                }

                _accum.x += dx;
                _accum.y += dy;
                SetCursorPos(_centerX, _centerY);
            }
            catch { }
        }

        private static void Recenter()
        {
            try
            {
                IntPtr h = EnsureHwnd();
                RECT r;
                if (h == IntPtr.Zero || !GetClientRect(h, out r))
                {
                    return;
                }
                var pt = new POINT { X = (r.Right - r.Left) / 2, Y = (r.Bottom - r.Top) / 2 };
                if (!ClientToScreen(h, ref pt))
                {
                    return;
                }
                _centerX = pt.X;
                _centerY = pt.Y;
                SetCursorPos(_centerX, _centerY);
            }
            catch { }
        }

        /// <summary>Moves the OS pointer to the middle of the window (opening panels).</summary>
        public static void CenterCursor()
        {
            Recenter();
        }

        /// <summary>The OS pointer in Unity screen pixels (origin bottom-left).</summary>
        public static Vector2 CursorScreen()        {
            try
            {
                IntPtr h = EnsureHwnd();
                if (h == IntPtr.Zero)
                {
                    return new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
                }
                POINT p;
                if (!GetCursorPos(out p))
                {
                    return new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
                }
                ScreenToClient(h, ref p);
                RECT r;
                if (!GetClientRect(h, out r))
                {
                    return new Vector2(p.X, Screen.height - p.Y);
                }
                int cw = Mathf.Max(1, r.Right - r.Left);
                int ch = Mathf.Max(1, r.Bottom - r.Top);
                float sx = Screen.width / (float)cw;
                float sy = Screen.height / (float)ch;
                return new Vector2(p.X * sx, (ch - p.Y) * sy);
            }
            catch
            {
                return new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            }
        }

        /// <summary>Mouse movement collected since the last call (already swallowed from the game).</summary>
        public static Vector2 ConsumeMouseDelta()
        {
            var d = _accum;
            _accum = Vector2.zero;
            return d;
        }

        public static bool Down(int vk)
        {
            try
            {
                // Never react to keys typed into another window - the command bar must not
                // open because the player wrote a "t" in a chat app.
                IntPtr h = EnsureHwnd();
                if (h != IntPtr.Zero && GetForegroundWindow() != h)
                {
                    return false;
                }
                return (GetAsyncKeyState(vk) & 0x8000) != 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>True on the first frame the key/button went down. Safe to call repeatedly.</summary>
        public static bool Pressed(int vk)
        {
            bool now = Down(vk);
            int frame = Time.frameCount;
            if (Time.unscaledTime < _suppressUntil)
            {
                States[vk] = new KeyState { Down = now, Frame = frame };
                return false;
            }
            if (States.TryGetValue(vk, out var st))
            {
                if (st.Frame == frame)
                {
                    return false; // already consumed this frame
                }
                bool was = st.Down;
                States[vk] = new KeyState { Down = now, Frame = frame };
                return now && !was;
            }
            // First sighting of this key is a baseline, not a press: otherwise whatever state
            // the key happens to be in when the layer starts counts as a fresh press, which is
            // how the world used to open (and close) itself when nobody touched F7.
            States[vk] = new KeyState { Down = now, Frame = frame };
            return false;
        }

        /// <summary>Snapshots the keys we care about so the first poll is not taken as a press.</summary>
        public static void Prime()
        {
            try
            {
                int[] keys =
                {
                    VkF7, VkF8, VkRControl, VkEscape, VkTab, VkE, VkT, VkQ, VkV, VkC,
                    VkSpace, VkLShift, VkRShift, VkLButton, VkRButton,
                };
                for (int i = 0; i < keys.Length; i++)
                {
                    States[keys[i]] = new KeyState { Down = Down(keys[i]), Frame = Time.frameCount };
                }
            }
            catch { }
        }

        public static void ClearHeld()
        {
            States.Clear();
            _accum = Vector2.zero;
            // Every ClearHeld call happens right after a key opened something (inventory, chat,
            // mode switch). Without this grace period the very next frame sees the same key
            // still held, reports it as a fresh press, and closes the panel again - which is
            // why E opened and instantly closed the inventory in the footage.
            _suppressUntil = Time.unscaledTime + 0.25f;
        }
    }
}
