using System;
using System.Threading;

namespace PalAssist.Core
{
    /// <summary>
    /// Ensures only one PalAssist process runs per user session (Local\ mutex).
    /// Second launches signal the first instance to show the menu, then exit.
    /// </summary>
    public sealed class SingleInstance : IDisposable
    {
        private const string MutexName = @"Local\PalAssist2.SingleInstance";
        private const string EventName = @"Local\PalAssist2.Activate";

        private Mutex? _mutex;
        private EventWaitHandle? _activateEvent;
        private Thread? _listenThread;
        private volatile bool _stop;
        private bool _disposed;

        /// <summary>Raised on a background thread when another process requests activation.</summary>
        public event Action? Activated;

        /// <summary>
        /// Try to become the primary instance. Returns false if another instance holds the mutex.
        /// </summary>
        public bool TryAcquire()
        {
            try
            {
                _mutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out bool createdNew);
                if (!createdNew)
                {
                    try { _mutex.Dispose(); } catch { /* ignore */ }
                    _mutex = null;
                    return false;
                }

                _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
                _stop = false;
                _listenThread = new Thread(ListenLoop)
                {
                    IsBackground = true,
                    Name = "PalAssist.SingleInstance"
                };
                _listenThread.Start();
                AppLog.Info("SingleInstance", "Acquired primary instance mutex");
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error("SingleInstance.TryAcquire", ex.Message, ex);
                // Fail open: allow start if mutex infrastructure is broken
                return true;
            }
        }

        /// <summary>Ask the primary instance to show UI (best-effort).</summary>
        public static void SignalActivate()
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(EventName);
                ev.Set();
                AppLog.Info("SingleInstance", "Signaled primary instance to activate");
            }
            catch (Exception ex)
            {
                AppLog.Warn("SingleInstance", "Could not signal primary instance: " + ex.Message);
            }
        }

        private void ListenLoop()
        {
            while (!_stop)
            {
                try
                {
                    var handle = _activateEvent;
                    if (handle == null) break;
                    if (handle.WaitOne(500))
                    {
                        if (_stop) break;
                        try { Activated?.Invoke(); }
                        catch (Exception ex)
                        {
                            AppLog.Error("SingleInstance.Activated", ex.Message, ex);
                        }
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLog.Error("SingleInstance.ListenLoop", ex.Message, ex);
                    try { Thread.Sleep(1000); } catch { /* ignore */ }
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stop = true;

            try
            {
                _activateEvent?.Set();
            }
            catch { /* ignore */ }

            try
            {
                if (_listenThread != null && _listenThread.IsAlive)
                    _listenThread.Join(1000);
            }
            catch { /* ignore */ }

            try { _activateEvent?.Dispose(); } catch { /* ignore */ }
            _activateEvent = null;

            try
            {
                if (_mutex != null)
                {
                    try { _mutex.ReleaseMutex(); } catch { /* ignore */ }
                    _mutex.Dispose();
                }
            }
            catch { /* ignore */ }
            _mutex = null;
        }
    }
}
