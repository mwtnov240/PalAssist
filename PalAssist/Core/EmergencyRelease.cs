using System;
using System.IO;
using PalAssist.Features;

namespace PalAssist.Core
{
    /// <summary>
    /// Process-wide best-effort key release for exit and crash paths.
    /// MainWindow registers a callback while it is alive.
    /// </summary>
    public static class EmergencyRelease
    {
        private static readonly object Gate = new();
        private static Action? _releaseCallback;

        /// <summary>Register the live window's release routine (or null to clear).</summary>
        public static void SetCallback(Action? callback)
        {
            lock (Gate)
                _releaseCallback = callback;
        }

        /// <summary>
        /// Disable assists / release keys. Safe to call from any thread.
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
            catch
            {
                // ignore
            }

            try
            {
                FeatureManager.ForceReleaseCommonKeys();
            }
            catch
            {
                // ignore
            }
        }

        /// <summary>Append a crash line next to the executable.</summary>
        public static void LogCrash(string source, Exception? ex)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PalAssist-crash.log");
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {ex?.GetType().Name}: {ex?.Message}\n{ex?.StackTrace}\n---\n";
                File.AppendAllText(path, line);
            }
            catch
            {
                // ignore
            }
        }
    }
}
