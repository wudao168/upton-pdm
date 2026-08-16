using System;
using System.Threading;
using System.Windows;

namespace Upton.Pdm.Desktop;

public partial class App : System.Windows.Application
{
    private const string InstanceMutexName = @"Local\Upton.Pdm.Desktop";
    private const string ShowRequestEventName = @"Local\Upton.Pdm.Desktop.Show";
    private Mutex? singleInstanceMutex;
    private EventWaitHandle? showRequestEvent;
    private RegisteredWaitHandle? showRequestRegistration;

    protected override void OnStartup(StartupEventArgs e)
    {
        singleInstanceMutex = new Mutex(true, InstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            singleInstanceMutex.Dispose();
            singleInstanceMutex = null;
            using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowRequestEventName))
            {
                showEvent.Set();
            }
            Shutdown();
            return;
        }

        showRequestEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowRequestEventName);
        showRequestRegistration = ThreadPool.RegisterWaitForSingleObject(
            showRequestEvent,
            (_, timedOut) =>
            {
                if (!timedOut)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (MainWindow is MainWindow window)
                        {
                            window.RestoreFromExternalRequest();
                        }
                    }));
                }
            },
            null,
            Timeout.Infinite,
            false);
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        showRequestRegistration?.Unregister(null);
        showRequestRegistration = null;
        showRequestEvent?.Dispose();
        showRequestEvent = null;
        if (singleInstanceMutex != null)
        {
            singleInstanceMutex.ReleaseMutex();
            singleInstanceMutex.Dispose();
            singleInstanceMutex = null;
        }

        base.OnExit(e);
    }
}
