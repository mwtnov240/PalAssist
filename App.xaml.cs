using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PalAssist.Core;

namespace PalAssist
{
    public partial class App : System.Windows.Application
    {
        private SingleInstance? _singleInstance;

        /// <summary>Primary-instance guard; null when this process is a secondary launch.</summary>
        internal SingleInstance? SingleInstance => _singleInstance;

        protected override void OnStartup(StartupEventArgs e)
        {
            // Single instance BEFORE MainWindow (StartupUri) loads.
            _singleInstance = new SingleInstance();
            if (!_singleInstance.TryAcquire())
            {
                SingleInstance.SignalActivate();
                try { _singleInstance.Dispose(); } catch { /* ignore */ }
                _singleInstance = null;
                // Do not call base.OnStartup — avoids creating a second MainWindow.
                Shutdown(0);
                return;
            }

            base.OnStartup(e);

            AppLog.Info("App", "PalAssist 2 starting v" + UpdateService.GetCurrentVersion()
                + " pid=" + Environment.ProcessId
                + " screens=" + System.Windows.Forms.Screen.AllScreens.Length);

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                AppLog.Info("App", "Exiting — releasing keys");
                EmergencyRelease.ReleaseAll();
                EmergencyRelease.SetCallback(null);
                EmergencyRelease.SetStateSnapshot(null);
            }
            catch
            {
                // ignore
            }

            try { _singleInstance?.Dispose(); } catch { /* ignore */ }
            _singleInstance = null;

            base.OnExit(e);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            // Fatal for UI integrity: always release keys, log, then shut down cleanly.
            EmergencyRelease.LogCrash("DispatcherUnhandledException", e.Exception);
            EmergencyRelease.ReleaseAll();
            e.Handled = true;
            try
            {
                Shutdown(-1);
            }
            catch
            {
                // ignore
            }
        }

        private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            EmergencyRelease.LogCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);
            EmergencyRelease.ReleaseAll();
        }

        private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            // Recoverable by default: log, observe, release keys defensively, keep process if UI is fine.
            EmergencyRelease.LogCrash("TaskScheduler.UnobservedTaskException", e.Exception);
            EmergencyRelease.ReleaseAll();
            e.SetObserved();
        }
    }
}
