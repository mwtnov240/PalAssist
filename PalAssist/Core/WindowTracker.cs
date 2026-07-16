using System;
using System.Diagnostics;
using System.Text;
using System.Timers;
using PalAssist.Win32;

namespace PalAssist.Core
{
    /// <summary>
    /// Periodically polls the system to locate the Palworld window and track its
    /// position, size, and focus state.  Raises events when changes are detected.
    /// </summary>
    public sealed class WindowTracker : IDisposable
    {
        // Process names to look for (without .exe)
        private static readonly string[] ProcessNames = {
            "Pal-Win64-Shipping",
            "Palworld-Win64-Shipping"
        };

        private readonly System.Timers.Timer _timer;
        private IntPtr _targetHwnd;
        private bool _disposed;

        /// <summary>True when the game window has been found.</summary>
        public bool IsFound => _targetHwnd != IntPtr.Zero;

        /// <summary>True when the game window is the foreground window.</summary>
        public bool IsFocused { get; private set; }

        /// <summary>Current bounds of the game window.</summary>
        public NativeMethods.RECT Bounds { get; private set; }

        /// <summary>Raised when the window bounds change (move / resize).</summary>
        public event Action<NativeMethods.RECT>? BoundsChanged;

        /// <summary>Raised when the game is found or lost.</summary>
        public event Action<bool>? GameDetected;

        public WindowTracker(int pollMs = 250)
        {
            _timer = new System.Timers.Timer(pollMs);
            _timer.Elapsed += OnTick;
            _timer.AutoReset = true;
        }

        public void Start() => _timer.Start();
        public void Stop()  => _timer.Stop();

        private void OnTick(object? sender, ElapsedEventArgs e)
        {
            IntPtr hwnd = FindGameWindow();

            // Game was lost
            if (hwnd == IntPtr.Zero && _targetHwnd != IntPtr.Zero)
            {
                _targetHwnd = IntPtr.Zero;
                GameDetected?.Invoke(false);
                return;
            }

            // Game was found
            if (hwnd != IntPtr.Zero && _targetHwnd == IntPtr.Zero)
            {
                _targetHwnd = hwnd;
                GameDetected?.Invoke(true);
            }

            if (_targetHwnd == IntPtr.Zero) return;

            // Update focus state
            IsFocused = NativeMethods.GetForegroundWindow() == _targetHwnd;

            // Update bounds
            if (NativeMethods.GetWindowRect(_targetHwnd, out var rect))
            {
                if (rect.Left != Bounds.Left || rect.Top != Bounds.Top ||
                    rect.Right != Bounds.Right || rect.Bottom != Bounds.Bottom)
                {
                    Bounds = rect;
                    BoundsChanged?.Invoke(rect);
                }
            }
        }

        /// <summary>
        /// Tries to find the game window: first by process name, then by window title fallback.
        /// </summary>
        private static IntPtr FindGameWindow()
        {
            // Strategy 1: look for known process names
            foreach (string name in ProcessNames)
            {
                var procs = Process.GetProcessesByName(name);
                foreach (var p in procs)
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                        return p.MainWindowHandle;
                }
            }

            // Strategy 2: fallback — enumerate windows whose title starts with "Pal"
            IntPtr found = IntPtr.Zero;
            NativeMethods.EnumWindows((hWnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hWnd)) return true;

                var sb = new StringBuilder(256);
                NativeMethods.GetWindowText(hWnd, sb, 256);
                string title = sb.ToString();

                if (title.StartsWith("Pal", StringComparison.OrdinalIgnoreCase) && title.Length > 3)
                {
                    found = hWnd;
                    return false; // stop enumerating
                }
                return true;
            }, IntPtr.Zero);

            return found;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
            _timer.Dispose();
        }
    }
}
