using System;
using System.Collections.Generic;
using System.Timers;
using PalAssist.Core;
using PalAssist.Win32;

namespace PalAssist.Features
{
    /// <summary>
    /// Central registry for assist features. Owns enable/suspend/tick/release serialization
    /// so Focus Lock, AFK, emergency stop, and the tick loop cannot race held keys.
    /// Supports selective suspend: Active Hold can keep Work Assist ticking while Focus Lock suspends other assists.
    /// </summary>
    public sealed class FeatureManager : IDisposable
    {
        private readonly List<IFeature> _features = new();
        private readonly System.Timers.Timer _tickTimer;
        private readonly object _gate = new();
        private bool _disposed;
        private bool _inputSuspended;

        /// <summary>Read-only view of all registered features.</summary>
        public IReadOnlyList<IFeature> Features => _features;

        /// <summary>
        /// True when Focus Lock (or equivalent) requested a suspend.
        /// Individual features may still be active under Active Hold.
        /// </summary>
        public bool IsInputSuspended
        {
            get { lock (_gate) return _inputSuspended; }
        }

        /// <summary>Raised whenever any feature's enabled state changes (may fire off UI thread).</summary>
        public event Action? StateChanged;

        public FeatureManager()
        {
            // ~30 Hz: enough for hold re-assert without 60 Hz CPU cost on multi-day runs
            _tickTimer = new System.Timers.Timer(33);
            _tickTimer.Elapsed += OnTick;
            _tickTimer.AutoReset = true;
        }

        public void Register(IFeature feature)
        {
            lock (_gate)
                _features.Add(feature);
        }

        public void Start()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _tickTimer.Start();
            }
        }

        public void Stop()
        {
            try { _tickTimer.Stop(); }
            catch (Exception ex) { AppLog.Error("FeatureManager.Stop", ex.Message, ex); }
        }

        public void Toggle(IFeature feature)
        {
            Action? raise = null;
            lock (_gate)
            {
                if (_disposed) return;
                try
                {
                    if (feature.IsEnabled)
                        feature.OnDisable();
                    else
                    {
                        feature.OnEnable();
                        // Focus Lock suspend: newly enabled features start suspended.
                        // MainWindow may promote Work Assist to Active Hold background mode after this.
                        if (_inputSuspended)
                            feature.SuspendInput();
                    }
                    raise = StateChanged;
                }
                catch (Exception ex)
                {
                    AppLog.Error("FeatureManager.Toggle", ex.Message, ex);
                    try { ForceReleaseCommonKeys(); } catch { /* ignore */ }
                }
            }
            SafeRaise(raise);
        }

        public void DisableAll()
        {
            Action? raise = null;
            lock (_gate)
            {
                try
                {
                    foreach (var f in _features)
                    {
                        try
                        {
                            if (f.IsEnabled)
                                f.OnDisable();
                        }
                        catch (Exception ex)
                        {
                            AppLog.Error("FeatureManager.DisableAll", f.Name + ": " + ex.Message, ex);
                        }
                    }
                    _inputSuspended = false;
                    raise = StateChanged;
                }
                catch (Exception ex)
                {
                    AppLog.Error("FeatureManager.DisableAll", ex.Message, ex);
                }
            }
            SafeRaise(raise);
        }

        /// <summary>
        /// Disable every assist and force-release keys. Safe from any thread
        /// (exit, AFK, emergency stop, crash handlers).
        /// </summary>
        public void ReleaseAllInput()
        {
            lock (_gate)
            {
                try
                {
                    foreach (var f in _features)
                    {
                        try
                        {
                            if (f.IsEnabled)
                                f.OnDisable();
                        }
                        catch (Exception ex)
                        {
                            AppLog.Error("FeatureManager.ReleaseAllInput", f.Name + ": " + ex.Message, ex);
                        }
                    }
                    _inputSuspended = false;
                }
                catch (Exception ex)
                {
                    AppLog.Error("FeatureManager.ReleaseAllInput", ex.Message, ex);
                }

                ForceReleaseCommonKeys();
            }

            SafeRaise(StateChanged);
        }

        /// <summary>
        /// Focus-loss suspend. When <paramref name="activeHoldWorkAssist"/> is true and a game
        /// HWND is provided, Work Assist enters background hold instead of full suspend.
        /// </summary>
        public void SetInputSuspended(bool suspended, bool activeHoldWorkAssist = false, IntPtr gameHwnd = default)
        {
            lock (_gate)
            {
                if (_disposed) return;

                if (suspended)
                {
                    _inputSuspended = true;
                    foreach (var f in _features)
                    {
                        if (!f.IsEnabled) continue;
                        try
                        {
                            if (f is WorkAssistFeature wa && activeHoldWorkAssist)
                            {
                                // Stay in background hold if already there (avoid interrupting work)
                                if (wa.IsBackgroundHold)
                                    wa.UpdateBackgroundHwnd(gameHwnd);
                                else
                                    wa.EnterBackgroundHold(gameHwnd);
                            }
                            else if (f is WorkAssistFeature waNoHold)
                            {
                                // Active Hold off: full suspend (exit background if needed)
                                if (waNoHold.IsBackgroundHold || !waNoHold.IsInputSuspended)
                                    waNoHold.SuspendInput();
                            }
                            else if (!f.IsInputSuspended)
                            {
                                f.SuspendInput();
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLog.Error("FeatureManager.SetInputSuspended", f.Name + ": " + ex.Message, ex);
                        }
                    }
                }
                else
                {
                    if (!_inputSuspended)
                    {
                        // Still may need to leave Active Hold background when Focus Lock is off
                        foreach (var f in _features)
                        {
                            if (f is WorkAssistFeature wa && wa.IsEnabled && wa.IsBackgroundHold)
                            {
                                try { wa.ExitBackgroundHoldToForeground(); }
                                catch (Exception ex)
                                {
                                    AppLog.Error("FeatureManager.SetInputSuspended.bg", ex.Message, ex);
                                }
                            }
                        }
                        return;
                    }

                    _inputSuspended = false;
                    foreach (var f in _features)
                    {
                        if (!f.IsEnabled) continue;
                        try
                        {
                            if (f is WorkAssistFeature wa)
                            {
                                if (wa.IsBackgroundHold)
                                    wa.ExitBackgroundHoldToForeground();
                                else if (wa.IsInputSuspended)
                                    wa.ResumeInput();
                            }
                            else if (f.IsInputSuspended)
                            {
                                f.ResumeInput();
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLog.Error("FeatureManager.SetInputSuspended.resume", f.Name + ": " + ex.Message, ex);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Keep Work Assist in (or enter) background hold without changing other feature suspend state.
        /// Used when Active Hold is on, Focus Lock is off, and the game loses focus.
        /// </summary>
        public void ApplyWorkAssistBackgroundHold(IntPtr gameHwnd)
        {
            lock (_gate)
            {
                if (_disposed) return;
                foreach (var f in _features)
                {
                    if (f is WorkAssistFeature wa && wa.IsEnabled)
                    {
                        try
                        {
                            if (wa.IsBackgroundHold)
                                wa.UpdateBackgroundHwnd(gameHwnd);
                            else
                                wa.EnterBackgroundHold(gameHwnd);
                        }
                        catch (Exception ex)
                        {
                            AppLog.Error("FeatureManager.ApplyWorkAssistBackgroundHold", ex.Message, ex);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// If Work Assist is in background hold and the game is focused again (Focus Lock off path),
        /// return to SendInput hold.
        /// </summary>
        public void ApplyWorkAssistForegroundHold()
        {
            lock (_gate)
            {
                if (_disposed) return;
                foreach (var f in _features)
                {
                    if (f is WorkAssistFeature wa && wa.IsEnabled && wa.IsBackgroundHold)
                    {
                        try
                        {
                            wa.ExitBackgroundHoldToForeground();
                        }
                        catch (Exception ex)
                        {
                            AppLog.Error("FeatureManager.ApplyWorkAssistForegroundHold", ex.Message, ex);
                        }
                    }
                }
            }
        }

        /// <summary>Refresh background HWND or suspend Work Assist if the game vanished.</summary>
        public void UpdateWorkAssistBackgroundHwnd(IntPtr gameHwnd)
        {
            lock (_gate)
            {
                if (_disposed) return;
                foreach (var f in _features)
                {
                    if (f is WorkAssistFeature wa && wa.IsEnabled && wa.IsBackgroundHold)
                    {
                        try
                        {
                            wa.UpdateBackgroundHwnd(gameHwnd);
                        }
                        catch (Exception ex)
                        {
                            AppLog.Error("FeatureManager.UpdateWorkAssistBackgroundHwnd", ex.Message, ex);
                        }
                    }
                }
            }
        }

        private void OnTick(object? sender, ElapsedEventArgs e)
        {
            try
            {
                lock (_gate)
                {
                    if (_disposed) return;

                    // Tick per-feature: Work Assist may still run under Active Hold while
                    // Focus Lock has suspended other assists.
                    foreach (var f in _features)
                    {
                        try
                        {
                            if (f.IsEnabled && !f.IsInputSuspended)
                                f.Update();
                        }
                        catch (Exception ex)
                        {
                            AppLog.Error("FeatureManager.OnTick", f.Name + ": " + ex.Message, ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("FeatureManager.OnTick", "outer: " + ex.Message, ex);
            }
        }

        /// <summary>Hard KeyUp for keys PalAssist may hold, even if feature state is wrong.</summary>
        public static void ForceReleaseCommonKeys()
        {
            try
            {
                // Twice: some games miss a single synthetic KeyUp under load
                for (int i = 0; i < 2; i++)
                {
                    InputSimulator.KeyUp(NativeMethods.VK_F, NativeMethods.SCAN_F);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("FeatureManager.ForceReleaseCommonKeys", ex.Message, ex);
            }
        }

        private static void SafeRaise(Action? handler)
        {
            if (handler == null) return;
            try { handler.Invoke(); }
            catch (Exception ex) { AppLog.Error("FeatureManager.StateChanged", ex.Message, ex); }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }

            try
            {
                _tickTimer.Stop();
                _tickTimer.Dispose();
            }
            catch (Exception ex)
            {
                AppLog.Error("FeatureManager.Dispose", ex.Message, ex);
            }

            try
            {
                ReleaseAllInput();
            }
            catch (Exception ex)
            {
                AppLog.Error("FeatureManager.Dispose.Release", ex.Message, ex);
                try { ForceReleaseCommonKeys(); } catch { /* ignore */ }
            }
        }
    }
}
