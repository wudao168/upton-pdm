using System;
using System.Threading;
using System.Windows;
using Upton.Pdm.ClientShared;

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
            DesktopExternalRequestStore.Write(e.Args);
            using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowRequestEventName))
            {
                showEvent.Set();
            }
            Shutdown();
            return;
        }

        // 只有确认当前是唯一实例后才能切换版本：其它实例仍在运行时替换目录会失败，客户端会表现为刚打开就关闭。
        var target = AppDomain.CurrentDomain.BaseDirectory;
        ClientDiagnostics.Initialize("desktop", ClientPackageUpdater.GetLoadedVersion(typeof(App).Assembly));
        DispatcherUnhandledException += (_, args) => ClientDiagnostics.Record("error", "界面未处理异常", args.Exception.ToString());
        if (ClientPackageUpdater.DesktopInstallationInProgress(target) || ClientPackageUpdater.TryLaunchDesktopUpdate(target))
        {
            try { if (e.Args.Length != 0) DesktopExternalRequestStore.Write(e.Args); }
            catch (Exception error) { ClientDiagnostics.Record("error", "保存客户端启动请求失败", error.ToString()); }
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
                            window.RestoreFromExternalRequest(DesktopExternalRequestStore.ReadAndDelete());
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
