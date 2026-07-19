using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PalAssist.Core
{
    /// <summary>
    /// Thread-safe rotating log with a simple error budget to avoid disk spam
    /// during multi-day runs.
    /// </summary>
    public static class AppLog
    {
        private static readonly object Gate = new();
        private const long MaxFileBytes = 8L * 1024 * 1024; // 8 MB
        private const int KeepRotated = 2; // .1 and .2
        private const int ErrorBudgetMax = 20;
        private static readonly TimeSpan ErrorBudgetWindow = TimeSpan.FromMinutes(5);

        private static string? _logPath;
        private static readonly Queue<DateTime> RecentErrors = new();
        private static int _throttledSinceReport;

        public static string LogPath
        {
            get
            {
                lock (Gate)
                {
                    _logPath ??= Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PalAssist2.log");
                    return _logPath;
                }
            }
        }

        public static void Info(string source, string message) => Write("INFO", source, message, null);
        public static void Warn(string source, string message) => Write("WARN", source, message, null);

        public static void Error(string source, string message, Exception? ex = null)
        {
            lock (Gate)
            {
                var now = DateTime.UtcNow;
                while (RecentErrors.Count > 0 && now - RecentErrors.Peek() > ErrorBudgetWindow)
                    RecentErrors.Dequeue();

                if (RecentErrors.Count >= ErrorBudgetMax)
                {
                    _throttledSinceReport++;
                    if (_throttledSinceReport == 1 || _throttledSinceReport % 50 == 0)
                    {
                        WriteUnlocked("WARN", "AppLog",
                            $"Error budget exceeded — suppressed {_throttledSinceReport} error(s) in window.", null);
                    }
                    return;
                }

                RecentErrors.Enqueue(now);
                _throttledSinceReport = 0;
                WriteUnlocked("ERROR", source, message, ex);
            }
        }

        /// <summary>Structured crash-style entry (always written; still under error budget).</summary>
        public static void Crash(string source, Exception? ex, string? extraState = null)
        {
            var sb = new StringBuilder();
            sb.Append(ex?.Message ?? "(no exception)");
            if (!string.IsNullOrWhiteSpace(extraState))
                sb.Append(" | state: ").Append(extraState);
            Error(source, sb.ToString(), ex);
        }

        private static void Write(string level, string source, string message, Exception? ex)
        {
            lock (Gate)
                WriteUnlocked(level, source, message, ex);
        }

        private static void WriteUnlocked(string level, string source, string message, Exception? ex)
        {
            try
            {
                string path = LogPath;
                RotateIfNeeded(path);

                var line = new StringBuilder();
                line.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("] ");
                line.Append(level).Append(' ').Append(source).Append(": ").Append(message);
                if (ex != null)
                {
                    line.Append(" | ").Append(ex.GetType().Name);
                    if (!string.IsNullOrEmpty(ex.StackTrace))
                    {
                        string stack = ex.StackTrace;
                        if (stack.Length > 1200)
                            stack = stack[..1200] + "…";
                        line.AppendLine().Append(stack);
                    }
                }
                line.AppendLine();

                File.AppendAllText(path, line.ToString());
            }
            catch
            {
                // never throw from logging
            }
        }

        private static void RotateIfNeeded(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                var fi = new FileInfo(path);
                if (fi.Length < MaxFileBytes) return;

                for (int i = KeepRotated; i >= 1; i--)
                {
                    string older = path + "." + i;
                    string newer = i == 1 ? path : path + "." + (i - 1);
                    if (i == KeepRotated && File.Exists(older))
                        File.Delete(older);
                    if (File.Exists(newer))
                    {
                        string dest = path + "." + i;
                        if (File.Exists(dest)) File.Delete(dest);
                        File.Move(newer, dest);
                    }
                }
            }
            catch
            {
                // ignore rotation failures
            }
        }
    }
}
