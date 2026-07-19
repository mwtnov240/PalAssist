using System;
using PalAssist.Features;

namespace PalAssist.Core
{
    /// <summary>
    /// Process-wide best-effort key release for exit and crash paths.
    /// MainWindow registers a callback while it is alive.
    /// Optional state snapshot improves crash logs.
    /// </summary>
    public static class EmergencyRelease
    {
        private static readonly object Gate = new();
        private static Action? _releaseCallback;
        private static Func<string>? _stateSnapshot;

        public static void SetCallback(Action? callback)
        {
            lock (Gate)
                _releaseCallback = callback;
        }

        public static void SetStateSnapshot(Func<string>? snapshot)
        {
            lock (Gate)
                _stateSnapshot = snapshot;
        }

        public static string? CaptureState()
        {
            try
            {
                Func<string>? snap;
                lock (Gate) snap = _stateSnapshot;
                return snap?.Invoke();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Disable assists / release keys. Safe from any thread.
        /// Always attempts a hard KeyUp even if no callback is registered.
        /// </summary>
        public static void ReleaseAll()
        {
            Action? cb;
            lock (Gate)
                cb = _releaseCallback;

            try
            {
                cb?.Invoke();
            }
            catch (Exception ex)
            {
                AppLog.Error("EmergencyRelease.callback", ex.Message, ex);
            }

            try
            {
                FeatureManager.ForceReleaseCommonKeys();
            }
            catch (Exception ex)
            {
                AppLog.Error("EmergencyRelease.ForceRelease", ex.Message, ex);
            }
        }

        public static void LogCrash(string source, Exception? ex)
        {
            string? state = CaptureState();
            AppLog.Crash(source, ex, state);
        }
    }
}
