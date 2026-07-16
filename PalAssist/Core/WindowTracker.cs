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
    /// Also classifies the launch platform (Steam / Epic / Xbox) when possible.
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
        private uint _targetProcessId;
        private bool _disposed;

        /// <summary>True when the game window has been found.</summary>
        public bool IsFound => _targetHwnd != IntPtr.Zero;

        /// <summary>True when the game window is the foreground window.</summary>
        public bool IsFocused { get; private set; }

        /// <summary>
        /// Launch platform when known: "Steam", "Epic", "Xbox", or null if unknown / not found.
        /// </summary>
        public string? Platform { get; private set; }

        /// <summary>Current bounds of the game window.</summary>
        public NativeMethods.RECT Bounds { get; private set; }

        /// <summary>Raised when the window bounds change (move / resize).</summary>
        public event Action<NativeMethods.RECT>? BoundsChanged;

        /// <summary>Raised when the game is found or lost.</summary>
        public event Action<bool>? GameDetected;

        /// <summary>Raised when the game gains or loses foreground focus.</summary>
        public event Action<bool>? FocusChanged;

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

            if (_targetHwnd == IntPtr.Zero) return;

            // Update focus state
            bool focused = NativeMethods.GetForegroundWindow() == _targetHwnd;
            if (focused != IsFocused)
            {
                IsFocused = focused;
                FocusChanged?.Invoke(focused);
            }

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

        /// <summary>
        /// Tries to find the game window by known process names, then a strict title fallback.
        /// </summary>
        private static IntPtr FindGameWindow()
        {
            // Strategy 1: known Unreal shipping process names
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

            // Strategy 2: strict title fallback — only titles starting with "Palworld"
            // (never bare "Pal…", which caused false positives).
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
                // Prefer a known shipping process; otherwise accept the first Palworld title.
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

        /// <summary>
        /// Classifies Steam / Epic / Xbox from the process image path.
        /// </summary>
        private static string? DetectPlatform(IntPtr hwnd, uint processId)
        {
            if (processId == 0)
                NativeMethods.GetWindowThreadProcessId(hwnd, out processId);
            if (processId == 0) return null;

            string? path = NativeMethods.TryGetProcessImagePath(processId);
            if (string.IsNullOrEmpty(path))
            {
                // Fallback: try Process.MainModule (often fails on Store apps)
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

            // Normalize for case-insensitive substring checks
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
        }
    }
}
