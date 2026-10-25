using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private int serverInitializationScheduled;

    private void TryInitializeServerAfterStartup()
    {
        if (disconnecting || application == null || lifetime == null || Volatile.Read(ref serverInitializationScheduled) != 0) return;
        try { if (!application.StartupProcessCompleted) return; }
        catch (System.Runtime.InteropServices.COMException) { return; }
        if (Interlocked.CompareExchange(ref serverInitializationScheduled, 1, 0) != 0) return;
        var connectionLifetime = lifetime;
        GetTaskPaneSynchronizationContext().Post(async _ => await InitializeServerAfterStartupAsync(connectionLifetime), null);
    }

    private async Task InitializeServerAfterStartupAsync(CancellationTokenSource connectionLifetime)
    {
        bool IsCurrentConnection() => !disconnecting && ReferenceEquals(lifetime, connectionLifetime) && !connectionLifetime.IsCancellationRequested;
        if (!IsCurrentConnection()) return;
        var initialConfiguration = currentBootstrap;
        var initialClient = apiClient;
        var timer = Stopwatch.StartNew();
        LogOperation("SolidWorks startup complete; server configuration sync started pid=" + Process.GetCurrentProcess().Id);
        try
        {
            if (!initialClient.IsAuthenticated) taskPaneControl?.SetConnectionState(false, "正在连接服务器");
            var latest = await LoadConfiguredBootstrapAsync(pluginSettings, connectionLifetime.Token);
            if (!IsCurrentConnection() || !ReferenceEquals(currentBootstrap, initialConfiguration) || !ReferenceEquals(apiClient, initialClient)) return;
            var apiChanged = !string.Equals(initialConfiguration.ApiBaseUrl.TrimEnd('/'), latest.ApiBaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
            if (apiChanged)
            {
                // 用户已手动登录时保留其连接；后台配置请求不能替换正在使用的账号或服务器。
                if (initialClient.IsAuthenticated) return;
                var replacement = new PdmApiClient(latest.ApiBaseUrl);
                replacement.BeforeCheckout = FinishPendingCloseBeforeCheckoutAsync;
                replacement.AuthenticationExpired += OnAuthenticationExpired;
                initialClient.AuthenticationExpired -= OnAuthenticationExpired;
                apiClient = replacement;
                controlledWorkspace = new ControlledWorkspaceManager(replacement);
                initialClient.Dispose();
            }
            currentBootstrap = latest;
            if (!apiClient.IsAuthenticated)
            {
                taskPaneControl?.SetConnectionState(false, "未登录");
                await LoginRememberedCredentialsAsync();
            }
            LogOperation("Deferred server configuration sync completed elapsedMs=" + timer.ElapsedMilliseconds + " pid=" + Process.GetCurrentProcess().Id);
        }
        catch (OperationCanceledException) when (!IsCurrentConnection()) { }
        catch (Exception error)
        {
            if (!IsCurrentConnection()) return;
            LogDiagnostic("启动完成后连接服务器", error);
            taskPaneControl?.SetConnectionState(false, "连接暂未完成，请稍后登录");
        }
        finally
        {
            if (IsCurrentConnection())
            {
                _ = MonitorClientUpdatesAsync();
                _ = MonitorEditHeartbeatAsync();
                _ = MonitorThemeAsync();
            }
        }
    }
}
