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

        /// <summary>True when all features have input suspended (Focus Lock).</summary>
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

        public void SetInputSuspended(bool suspended)
        {
            lock (_gate)
            {
                if (_disposed) return;
                if (_inputSuspended == suspended) return;
                _inputSuspended = suspended;

                foreach (var f in _features)
                {
                    if (!f.IsEnabled) continue;
                    try
                    {
                        if (suspended)
                            f.SuspendInput();
                        else
                            f.ResumeInput();
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("FeatureManager.SetInputSuspended", f.Name + ": " + ex.Message, ex);
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
                    if (_disposed || _inputSuspended) return;

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
                    InputSimulator.KeyUp(NativeMethods.VK_W, NativeMethods.SCAN_W);
                    InputSimulator.KeyUp(NativeMethods.VK_E, NativeMethods.SCAN_E);
                    InputSimulator.KeyUp(NativeMethods.VK_LSHIFT, NativeMethods.SCAN_SHIFT);
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
