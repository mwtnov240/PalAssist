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

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentProcessId();

        // ───────────────────────────────────────────────
        //  WinEvent hooks (foreground focus changes)
        // ───────────────────────────────────────────────

        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

        public delegate void WinEventDelegate(
            IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWinEventHook(
            uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        /// <summary>
        /// Shows or hides the cursor. Returns the new display counter
        /// (visible when counter &gt;= 0). Each call adjusts an internal ref-count.
        /// </summary>
        [DllImport("user32.dll")]
        public static extern int ShowCursor([MarshalAs(UnmanagedType.Bool)] bool bShow);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        // ───────────────────────────────────────────────
        //  Process path (for launcher platform detection)
        // ───────────────────────────────────────────────

        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryFullProcessImageName(
            IntPtr hProcess,
            uint dwFlags,
            System.Text.StringBuilder lpExeName,
            ref uint lpdwSize);

        /// <summary>
        /// Returns the full executable path for a process id, or null on failure.
        /// </summary>
        public static string? TryGetProcessImagePath(uint processId)
        {
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var sb = new System.Text.StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                if (!QueryFullProcessImageName(handle, 0, sb, ref size) || size == 0)
                    return null;
                return sb.ToString(0, (int)size);
            }
            catch
            {
                return null;
            }
            finally
            {
                CloseHandle(handle);
            }
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

        [DllImport("user32.dll")]
        public static extern uint MapVirtualKey(uint uCode, uint uMapType);

        public const uint MAPVK_VK_TO_VSC = 0;

        public const int INPUT_KEYBOARD = 1;
        public const int INPUT_MOUSE    = 0;

        public const uint KEYEVENTF_KEYDOWN   = 0x0000;
        public const uint KEYEVENTF_KEYUP     = 0x0002;
        public const uint KEYEVENTF_SCANCODE  = 0x0008;
        public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

        // Virtual keys used for game input
        public const ushort VK_W     = 0x57;
        public const ushort VK_E     = 0x45;
        public const ushort VK_F     = 0x46;
        public const ushort VK_LSHIFT = 0xA0;

        // Scan codes (US layout) — physical key positions
        public const ushort SCAN_W     = 0x11;
        public const ushort SCAN_E     = 0x12;
        public const ushort SCAN_F     = 0x21;
        public const ushort SCAN_SHIFT = 0x2A;  // Left Shift

        /// <summary>
        /// Full INPUT size on x64 must match the largest union arm (MOUSEINPUT).
        /// Undersized structs cause SendInput to fail silently.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public int type;
            public INPUTUNION union;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int    dx;
            public int    dy;
            public uint   mouseData;
            public uint   dwFlags;
            public uint   time;
            public IntPtr dwExtraInfo;
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
