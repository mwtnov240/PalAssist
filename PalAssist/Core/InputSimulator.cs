using System;
using System.Runtime.InteropServices;
using PalAssist.Win32;

namespace PalAssist.Core
{
    /// <summary>
    /// Simulates keyboard input using the Win32 SendInput API.
    /// Uses scan-codes so that DirectInput-based games (like Palworld) accept the input.
    /// </summary>
    public static class InputSimulator
    {
        /// <summary>
        /// Sends a key-down event for the given scan code.
        /// </summary>
        public static void KeyDown(ushort scanCode)
        {
            var input = MakeKeyInput(scanCode, NativeMethods.KEYEVENTF_SCANCODE);
            NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeMethods.INPUT>());
        }

        /// <summary>
        /// Sends a key-up event for the given scan code.
        /// </summary>
        public static void KeyUp(ushort scanCode)
        {
            var input = MakeKeyInput(scanCode, NativeMethods.KEYEVENTF_SCANCODE | NativeMethods.KEYEVENTF_KEYUP);
            NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeMethods.INPUT>());
        }

        /// <summary>
        /// Helper to construct a keyboard INPUT struct.
        /// </summary>
        private static NativeMethods.INPUT MakeKeyInput(ushort scanCode, uint flags)
        {
            return new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                union = new NativeMethods.INPUTUNION
                {
                    ki = new NativeMethods.KEYBDINPUT
                    {
                        wVk = 0,            // zero when using scan codes
                        wScan = scanCode,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };
        }
    }
}
