using System;
using System.Runtime.InteropServices;
using PalAssist.Win32;

namespace PalAssist.Core
{
    /// <summary>
    /// Simulates keyboard input via Win32 SendInput.
    /// Uses virtual-key + scan code together (without KEYEVENTF_SCANCODE) so both
    /// the system key state and DirectInput-style games receive the press.
    /// </summary>
    public static class InputSimulator
    {
        /// <summary>
        /// Key-down using a known scan code. Maps common scans to virtual keys.
        /// </summary>
        public static void KeyDown(ushort scanCode)
        {
            ushort vk = ScanToVk(scanCode);
            SendKey(vk, scanCode, keyUp: false);
        }

        /// <summary>Key-up matching <see cref="KeyDown(ushort)"/>.</summary>
        public static void KeyUp(ushort scanCode)
        {
            ushort vk = ScanToVk(scanCode);
            SendKey(vk, scanCode, keyUp: true);
        }

        /// <summary>Key-down with explicit virtual-key + scan code.</summary>
        public static void KeyDown(ushort virtualKey, ushort scanCode)
        {
            if (scanCode == 0 && virtualKey != 0)
                scanCode = (ushort)NativeMethods.MapVirtualKey(virtualKey, NativeMethods.MAPVK_VK_TO_VSC);
            SendKey(virtualKey, scanCode, keyUp: false);
        }

        /// <summary>Key-up with explicit virtual-key + scan code.</summary>
        public static void KeyUp(ushort virtualKey, ushort scanCode)
        {
            if (scanCode == 0 && virtualKey != 0)
                scanCode = (ushort)NativeMethods.MapVirtualKey(virtualKey, NativeMethods.MAPVK_VK_TO_VSC);
            SendKey(virtualKey, scanCode, keyUp: true);
        }

        /// <summary>
        /// Post a key-down to a specific window (does not affect the global key state
        /// or the currently focused app). Used for Active Hold background work.
        /// </summary>
        public static void PostKeyDown(IntPtr hwnd, ushort virtualKey, ushort scanCode)
        {
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return;
            if (scanCode == 0 && virtualKey != 0)
                scanCode = (ushort)NativeMethods.MapVirtualKey(virtualKey, NativeMethods.MAPVK_VK_TO_VSC);
            IntPtr wParam = new IntPtr(virtualKey);
            // First press: previous-down=false; subsequent holds use previous-down=true
            IntPtr lParam = NativeMethods.MakeKeyLParam(scanCode, keyUp: false, previousDown: false);
            NativeMethods.PostMessage(hwnd, NativeMethods.WM_KEYDOWN, wParam, lParam);
        }

        /// <summary>Repeat-style key-down (previous key state = down) for held keys.</summary>
        public static void PostKeyDownRepeat(IntPtr hwnd, ushort virtualKey, ushort scanCode)
        {
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return;
            if (scanCode == 0 && virtualKey != 0)
                scanCode = (ushort)NativeMethods.MapVirtualKey(virtualKey, NativeMethods.MAPVK_VK_TO_VSC);
            IntPtr wParam = new IntPtr(virtualKey);
            IntPtr lParam = NativeMethods.MakeKeyLParam(scanCode, keyUp: false, previousDown: true, repeatCount: 1);
            NativeMethods.PostMessage(hwnd, NativeMethods.WM_KEYDOWN, wParam, lParam);
        }

        /// <summary>Post a key-up to a specific window.</summary>
        public static void PostKeyUp(IntPtr hwnd, ushort virtualKey, ushort scanCode)
        {
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return;
            if (scanCode == 0 && virtualKey != 0)
                scanCode = (ushort)NativeMethods.MapVirtualKey(virtualKey, NativeMethods.MAPVK_VK_TO_VSC);
            IntPtr wParam = new IntPtr(virtualKey);
            IntPtr lParam = NativeMethods.MakeKeyLParam(scanCode, keyUp: true, previousDown: true);
            NativeMethods.PostMessage(hwnd, NativeMethods.WM_KEYUP, wParam, lParam);
        }

        private static void SendKey(ushort virtualKey, ushort scanCode, bool keyUp)
        {
            int size = Marshal.SizeOf<NativeMethods.INPUT>();
            uint upFlag = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0u;

            // Primary: VK + scan together (updates GetAsyncKeyState / most games)
            var primary = new[]
            {
                MakeInput(virtualKey, scanCode, upFlag)
            };
            uint sent = NativeMethods.SendInput(1, primary, size);
            if (sent != 0)
                return;

            // Fallback: scan-code only (DirectInput-style)
            if (scanCode != 0)
            {
                var scanOnly = new[]
                {
                    MakeInput(0, scanCode, NativeMethods.KEYEVENTF_SCANCODE | upFlag)
                };
                NativeMethods.SendInput(1, scanOnly, size);
            }
        }

        private static NativeMethods.INPUT MakeInput(ushort vk, ushort scan, uint flags)
        {
            return new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                union = new NativeMethods.INPUTUNION
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = scan,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
        }

        private static ushort ScanToVk(ushort scanCode) => scanCode switch
        {
            NativeMethods.SCAN_W => NativeMethods.VK_W,
            NativeMethods.SCAN_F => NativeMethods.VK_F,
            _ => 0
        };
    }
}
