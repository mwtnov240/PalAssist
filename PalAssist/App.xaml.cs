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

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            EmergencyRelease.ReleaseAll();
            EmergencyRelease.SetCallback(null);
            base.OnExit(e);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            EmergencyRelease.LogCrash("DispatcherUnhandledException", e.Exception);
            EmergencyRelease.ReleaseAll();
            // Mark handled so the process can exit without leaving keys stuck when possible.
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
            EmergencyRelease.LogCrash("TaskScheduler.UnobservedTaskException", e.Exception);
            EmergencyRelease.ReleaseAll();
            e.SetObserved();
        }
    }
}
