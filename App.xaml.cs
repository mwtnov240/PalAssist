using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PalAssist.Core;

namespace PalAssist
{
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            AppLog.Info("App", "PalAssist 2 starting v" + UpdateService.GetCurrentVersion());

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
