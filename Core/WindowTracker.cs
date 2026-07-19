using System;
using System.Diagnostics;
using System.Text;
using System.Timers;
using PalAssist.Win32;

namespace PalAssist.Core
{
    /// <summary>
    /// Locates the Palworld window and tracks bounds + focus.
    /// WinEvent foreground hook + poll fallback. All public events may fire
    /// off the UI thread — subscribers must marshal to the dispatcher.
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

        private readonly NativeMethods.WinEventDelegate _winEventProc;
        private IntPtr _winEventHook = IntPtr.Zero;
        private readonly uint _ownProcessId;

        // When game is found, only re-hunt processes periodically
        private DateTime _nextProcessHuntUtc = DateTime.MinValue;
        private static readonly TimeSpan FoundHuntInterval = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan LostHuntInterval = TimeSpan.FromMilliseconds(100);

        public bool IsFound => _targetHwnd != IntPtr.Zero;

        public bool IsFocused { get; private set; }

        public string? Platform { get; private set; }

        public NativeMethods.RECT Bounds { get; private set; }

        public uint TargetProcessId => _targetProcessId;

        public event Action<NativeMethods.RECT>? BoundsChanged;
        public event Action<bool>? GameDetected;
        public event Action<bool>? FocusChanged;

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
            catch (Exception ex)
            {
                AppLog.Error("WindowTracker.Start", "WinEvent hook failed: " + ex.Message, ex);
                _winEventHook = IntPtr.Zero;
            }

            _timer.Start();
            try { EvaluateFocus(raiseEvents: true); }
            catch (Exception ex) { AppLog.Error("WindowTracker.Start.Evaluate", ex.Message, ex); }
        }

        public void Stop()
        {
            try { _timer.Stop(); } catch { /* ignore */ }
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
            try
            {
                EvaluateFocus(raiseEvents: true);
            }
            catch (Exception ex)
            {
                AppLog.Error("WindowTracker.OnWinEvent", ex.Message, ex);
            }
        }

        private void OnTick(object? sender, ElapsedEventArgs e)
        {
            if (_disposed) return;

            try
            {
                // Stale handle (game crashed)
                if (_targetHwnd != IntPtr.Zero && !NativeMethods.IsWindow(_targetHwnd))
                {
                    ClearGameState();
                    SafeRaise(GameDetected, false);
                }

                bool needHunt = _targetHwnd == IntPtr.Zero
                    || DateTime.UtcNow >= _nextProcessHuntUtc
                    || (_targetHwnd != IntPtr.Zero && !NativeMethods.IsWindow(_targetHwnd));

                if (needHunt)
                {
                    IntPtr hwnd = FindGameWindow();
                    _nextProcessHuntUtc = DateTime.UtcNow +
                        (hwnd != IntPtr.Zero ? FoundHuntInterval : LostHuntInterval);

                    if (hwnd == IntPtr.Zero && _targetHwnd != IntPtr.Zero)
                    {
                        ClearGameState();
                        SafeRaise(GameDetected, false);
                        EvaluateFocus(raiseEvents: true);
                        return;
                    }

                    if (hwnd != IntPtr.Zero && _targetHwnd == IntPtr.Zero)
                    {
                        _targetHwnd = hwnd;
                        NativeMethods.GetWindowThreadProcessId(hwnd, out _targetProcessId);
                        Platform = DetectPlatform(hwnd, _targetProcessId);
                        SafeRaise(GameDetected, true);
                    }
                    else if (hwnd != IntPtr.Zero && hwnd != _targetHwnd)
                    {
                        _targetHwnd = hwnd;
                        NativeMethods.GetWindowThreadProcessId(hwnd, out _targetProcessId);
                        Platform = DetectPlatform(hwnd, _targetProcessId);
                    }
                }

                if (_targetHwnd != IntPtr.Zero)
                {
                    if (NativeMethods.GetWindowRect(_targetHwnd, out var rect))
                    {
                        if (rect.Left != Bounds.Left || rect.Top != Bounds.Top ||
                            rect.Right != Bounds.Right || rect.Bottom != Bounds.Bottom)
                        {
                            Bounds = rect;
                            try { BoundsChanged?.Invoke(rect); }
                            catch (Exception ex) { AppLog.Error("WindowTracker.BoundsChanged", ex.Message, ex); }
                        }
                    }
                }

                EvaluateFocus(raiseEvents: true);
            }
            catch (Exception ex)
            {
                AppLog.Error("WindowTracker.OnTick", ex.Message, ex);
            }
        }

        private void EvaluateFocus(bool raiseEvents)
        {
            bool focused = ComputeIsGameActive();
            if (focused == IsFocused) return;
            IsFocused = focused;
            if (raiseEvents)
                SafeRaise(FocusChanged, focused);
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

            if (fg == _targetHwnd)
                return true;

            NativeMethods.GetWindowThreadProcessId(fg, out uint fgPid);
            if (fgPid == 0)
                return false;

            if (fgPid == _ownProcessId)
                return false;

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
                SafeRaise(FocusChanged, false);
            }
        }

        private static void SafeRaise(Action<bool>? handler, bool value)
        {
            if (handler == null) return;
            try { handler.Invoke(value); }
            catch (Exception ex) { AppLog.Error("WindowTracker.event", ex.Message, ex); }
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
            try
            {
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
            }
            catch (Exception ex)
            {
                AppLog.Error("WindowTracker.FindGameWindow", ex.Message, ex);
            }

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
            try { _timer.Stop(); _timer.Dispose(); } catch { /* ignore */ }
            Unhook();
        }
    }
}
