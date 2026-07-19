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
            NativeMethods.SCAN_W     => NativeMethods.VK_W,
            NativeMethods.SCAN_E     => NativeMethods.VK_E,
            NativeMethods.SCAN_F     => NativeMethods.VK_F,
            NativeMethods.SCAN_SHIFT => NativeMethods.VK_LSHIFT,
            _ => 0
        };
    }
}
