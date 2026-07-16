using System;
using System.Runtime.InteropServices;

namespace PalAssist.Win32
{
    /// <summary>
    /// All Win32 P/Invoke declarations used by PalAssist.
    /// Grouped by purpose: window management, input simulation, hotkeys, process queries.
    /// </summary>
    public static class NativeMethods
    {
        // ───────────────────────────────────────────────
        //  Window style constants
        // ───────────────────────────────────────────────

        public const int GWL_EXSTYLE = -20;

        public const int WS_EX_TRANSPARENT  = 0x00000020;
        public const int WS_EX_LAYERED      = 0x00080000;
        public const int WS_EX_TOOLWINDOW   = 0x00000080;
        public const int WS_EX_NOACTIVATE   = 0x08000000;

        // ───────────────────────────────────────────────
        //  Window management
        // ───────────────────────────────────────────────

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        public const uint SWP_NOSIZE     = 0x0001;
        public const uint SWP_NOMOVE     = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        // ───────────────────────────────────────────────
        //  Global hotkeys
        // ───────────────────────────────────────────────

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public const int WM_HOTKEY = 0x0312;

        // Virtual-key codes we care about
        public const uint VK_INSERT  = 0x2D;
        public const uint VK_F1      = 0x70;
        public const uint VK_F2      = 0x71;
        public const uint VK_CONTROL = 0x11;

        // ───────────────────────────────────────────────
        //  Input simulation (SendInput)
        // ───────────────────────────────────────────────

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        public const int INPUT_KEYBOARD = 1;

        public const uint KEYEVENTF_KEYDOWN   = 0x0000;
        public const uint KEYEVENTF_KEYUP     = 0x0002;
        public const uint KEYEVENTF_SCANCODE  = 0x0008;

        // Scan codes (US layout)
        public const ushort SCAN_E     = 0x12;
        public const ushort SCAN_W     = 0x11;
        public const ushort SCAN_SHIFT = 0x2A;  // Left Shift

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public int type;
            public INPUTUNION union;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct INPUTUNION
        {
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint   dwFlags;
            public uint   time;
            public IntPtr dwExtraInfo;
        }

        // ───────────────────────────────────────────────
        //  EnumWindows (fallback detection)
        // ───────────────────────────────────────────────

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        // ───────────────────────────────────────────────
        //  Real-time key state (for dodge detection)
        // ───────────────────────────────────────────────

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        /// <summary>Returns true if the key is currently physically held down.</summary>
        public static bool IsKeyDown(uint vk) => (GetAsyncKeyState((int)vk) & 0x8000) != 0;
    }
}
