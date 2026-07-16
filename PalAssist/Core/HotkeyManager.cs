using System;
using System.Windows.Interop;
using PalAssist.Win32;

namespace PalAssist.Core
{
    /// <summary>
    /// Registers and dispatches global hotkeys that work even when the game has focus.
    /// Each hotkey is assigned a unique integer ID and a callback action.
    /// </summary>
    public sealed class HotkeyManager : IDisposable
    {
        private readonly IntPtr _hwnd;
        private readonly Dictionary<int, Action> _callbacks = new();
        private int _nextId = 1;
        private bool _disposed;

        /// <summary>
        /// Initialise the hotkey manager. Must be called after the WPF window has a handle.
        /// </summary>
        /// <param name="hwnd">The overlay window handle.</param>
        public HotkeyManager(IntPtr hwnd)
        {
            _hwnd = hwnd;
        }

        /// <summary>
        /// Register a global hotkey.
        /// </summary>
        /// <param name="vk">Virtual-key code (e.g. VK_INSERT).</param>
        /// <param name="modifiers">Modifier keys (0 for none).</param>
        /// <param name="callback">Action to invoke when the key is pressed.</param>
        /// <returns>The hotkey ID, or -1 on failure.</returns>
        public int Register(uint vk, uint modifiers, Action callback)
        {
            int id = _nextId++;
            bool ok = NativeMethods.RegisterHotKey(_hwnd, id, modifiers, vk);
            if (!ok) return -1;

            _callbacks[id] = callback;
            return id;
        }

        /// <summary>
        /// Unregister a single hotkey by its ID so it can be re-bound.
        /// </summary>
        public bool Unregister(int id)
        {
            NativeMethods.UnregisterHotKey(_hwnd, id);
            return _callbacks.Remove(id);
        }

        /// <summary>
        /// Call from the WndProc hook. Returns true if the message was handled.
        /// </summary>
        public bool ProcessMessage(int msg, IntPtr wParam)
        {
            if (msg != NativeMethods.WM_HOTKEY) return false;

            int id = wParam.ToInt32();
            if (_callbacks.TryGetValue(id, out var cb))
            {
                cb.Invoke();
                return true;
            }
            return false;
        }

        /// <summary>
        /// Unregisters all hotkeys.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (int id in _callbacks.Keys)
                NativeMethods.UnregisterHotKey(_hwnd, id);

            _callbacks.Clear();
        }
    }
}
