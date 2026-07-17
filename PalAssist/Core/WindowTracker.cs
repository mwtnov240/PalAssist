using System;
using System.Diagnostics;
using System.Text;
using System.Timers;
using PalAssist.Win32;

namespace PalAssist.Core
{
    /// <summary>
    /// Locates the Palworld window and tracks bounds + focus with high accuracy.
    /// Uses a WinEvent foreground hook plus a short poll fallback.
    /// </summary>
    public sealed class WindowTracker : IDisposable
    {
        private static readonly string[] ProcessNames = {
            "Pal-Win64-Shipping",
            "Palworld-Win64-Shipping"
        };

        private readonly System.Timers.Timer _timer;
        private IntPtr _targetHwnd;
        private uint _targetProcessId;
        private bool _disposed;

        // Keep delegate alive so the native hook does not call a GC'd target.
        private readonly NativeMethods.WinEventDelegate _winEventProc;
        private IntPtr _winEventHook = IntPtr.Zero;

        private readonly uint _ownProcessId;

        /// <summary>True when the game window has been found.</summary>
        public bool IsFound => _targetHwnd != IntPtr.Zero;

        /// <summary>
        /// True when the game is the effective foreground target:
        /// found, not minimized, and foreground window is the game HWND or same PID.
        /// PalAssist itself never counts as game-focused.
        /// </summary>
        public bool IsFocused { get; private set; }

        /// <summary>
        /// Launch platform when known: "Steam", "Epic", "Xbox", or null if unknown / not found.
        /// </summary>
        public string? Platform { get; private set; }

        /// <summary>Current bounds of the game window.</summary>
        public NativeMethods.RECT Bounds { get; private set; }

        /// <summary>Game process id when found; 0 otherwise.</summary>
        public uint TargetProcessId => _targetProcessId;

        /// <summary>Raised when the window bounds change (move / resize).</summary>
        public event Action<NativeMethods.RECT>? BoundsChanged;

        /// <summary>Raised when the game is found or lost.</summary>
        public event Action<bool>? GameDetected;

        /// <summary>Raised when <see cref="IsFocused"/> changes.</summary>
        public event Action<bool>? FocusChanged;

        /// <param name="pollMs">Backup poll interval (default 100 ms).</param>
        public WindowTracker(int pollMs = 100)
        {
            _ownProcessId = NativeMethods.GetCurrentProcessId();
            _winEventProc = OnWinEvent;

            _timer = new System.Timers.Timer(pollMs);
            _timer.Elapsed += OnTick;
            _timer.AutoReset = true;
        }

        public void Start()
        {
            try
            {
                _winEventHook = NativeMethods.SetWinEventHook(
                    NativeMethods.EVENT_SYSTEM_FOREGROUND,
                    NativeMethods.EVENT_SYSTEM_FOREGROUND,
                    IntPtr.Zero,
                    _winEventProc,
                    0,
                    0,
                    NativeMethods.WINEVENT_OUTOFCONTEXT);
            }
            catch
            {
                _winEventHook = IntPtr.Zero;
            }

            _timer.Start();
            // Immediate evaluate so UI is correct before first timer tick.
            EvaluateFocus(raiseEvents: true);
        }

        public void Stop()
        {
            _timer.Stop();
            Unhook();
        }

        private void Unhook()
        {
            if (_winEventHook != IntPtr.Zero)
            {
                try { NativeMethods.UnhookWinEvent(_winEventHook); }
                catch { /* ignore */ }
                _winEventHook = IntPtr.Zero;
            }
        }

        private void OnWinEvent(
            IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
        {
            if (_disposed) return;
            // Only care about foreground changes; re-evaluate fully.
            try
            {
                EvaluateFocus(raiseEvents: true);
            }
            catch
            {
                // Never throw out of a native callback.
            }
        }

        private void OnTick(object? sender, ElapsedEventArgs e)
        {
            if (_disposed) return;

            // Drop stale handle (game crashed / closed without clean exit)
            if (_targetHwnd != IntPtr.Zero && !NativeMethods.IsWindow(_targetHwnd))
            {
                ClearGameState();
                GameDetected?.Invoke(false);
            }

            IntPtr hwnd = FindGameWindow();

            // Game was lost
            if (hwnd == IntPtr.Zero && _targetHwnd != IntPtr.Zero)
            {
                ClearGameState();
                GameDetected?.Invoke(false);
                EvaluateFocus(raiseEvents: true);
                return;
            }

            // Game was found
            if (hwnd != IntPtr.Zero && _targetHwnd == IntPtr.Zero)
            {
                _targetHwnd = hwnd;
                NativeMethods.GetWindowThreadProcessId(hwnd, out _targetProcessId);
                Platform = DetectPlatform(hwnd, _targetProcessId);
                GameDetected?.Invoke(true);
            }
            else if (hwnd != IntPtr.Zero && hwnd != _targetHwnd)
            {
                // Window handle changed (restart / multi-instance)
                _targetHwnd = hwnd;
                NativeMethods.GetWindowThreadProcessId(hwnd, out _targetProcessId);
                Platform = DetectPlatform(hwnd, _targetProcessId);
            }

            if (_targetHwnd != IntPtr.Zero)
            {
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

            EvaluateFocus(raiseEvents: true);
        }

        /// <summary>
        /// Computes whether Palworld is the effective active target and raises FocusChanged.
        /// </summary>
        private void EvaluateFocus(bool raiseEvents)
        {
            bool focused = ComputeIsGameActive();
            if (focused == IsFocused) return;
            IsFocused = focused;
            if (raiseEvents)
                FocusChanged?.Invoke(focused);
        }

        private bool ComputeIsGameActive()
        {
            if (_targetHwnd == IntPtr.Zero || !NativeMethods.IsWindow(_targetHwnd))
                return false;

            if (NativeMethods.IsIconic(_targetHwnd))
                return false;

            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero)
                return false;

            // Exact game window
            if (fg == _targetHwnd)
                return true;

            NativeMethods.GetWindowThreadProcessId(fg, out uint fgPid);
            if (fgPid == 0)
                return false;

            // Never treat our own process as "game focused"
            if (fgPid == _ownProcessId)
                return false;

            // Same process as game (child / companion windows)
            if (_targetProcessId != 0 && fgPid == _targetProcessId)
                return true;

            return false;
        }

        private void ClearGameState()
        {
            _targetHwnd = IntPtr.Zero;
            _targetProcessId = 0;
            Platform = null;
            if (IsFocused)
            {
                IsFocused = false;
                FocusChanged?.Invoke(false);
            }
        }

        private static IntPtr FindGameWindow()
        {
            foreach (string name in ProcessNames)
            {
                Process[] procs;
                try
                {
                    procs = Process.GetProcessesByName(name);
                }
                catch
                {
                    continue;
                }

                foreach (var p in procs)
                {
                    try
                    {
                        if (p.HasExited) continue;
                        IntPtr h = p.MainWindowHandle;
                        if (h != IntPtr.Zero && NativeMethods.IsWindow(h))
                            return h;
                    }
                    catch
                    {
                        // Process may exit between queries
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }

            IntPtr found = IntPtr.Zero;
            NativeMethods.EnumWindows((hWnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hWnd)) return true;

                var sb = new StringBuilder(256);
                NativeMethods.GetWindowText(hWnd, sb, 256);
                string title = sb.ToString();

                if (!title.StartsWith("Palworld", StringComparison.OrdinalIgnoreCase))
                    return true;

                NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid != 0 && IsKnownGameProcess(pid))
                {
                    found = hWnd;
                    return false;
                }

                if (found == IntPtr.Zero)
                    found = hWnd;
                return true;
            }, IntPtr.Zero);

            return found;
        }

        private static bool IsKnownGameProcess(uint processId)
        {
            try
            {
                using var p = Process.GetProcessById((int)processId);
                string name = p.ProcessName;
                foreach (string known in ProcessNames)
                {
                    if (string.Equals(name, known, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch
            {
                // access denied / exited
            }
            return false;
        }

        private static string? DetectPlatform(IntPtr hwnd, uint processId)
        {
            if (processId == 0)
                NativeMethods.GetWindowThreadProcessId(hwnd, out processId);
            if (processId == 0) return null;

            string? path = NativeMethods.TryGetProcessImagePath(processId);
            if (string.IsNullOrEmpty(path))
            {
                try
                {
                    using var p = Process.GetProcessById((int)processId);
                    path = p.MainModule?.FileName;
                }
                catch
                {
                    return null;
                }
            }

            if (string.IsNullOrEmpty(path)) return null;

            string lower = path.Replace('/', '\\').ToLowerInvariant();

            if (lower.Contains("steamapps") || lower.Contains("\\steam\\"))
                return "Steam";
            if (lower.Contains("epic games") || lower.Contains("epicgames"))
                return "Epic";
            if (lower.Contains("windowsapps") || lower.Contains("xboxgames") ||
                lower.Contains("\\xbox\\"))
                return "Xbox";

            return null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
            _timer.Dispose();
            Unhook();
        }
    }
}
