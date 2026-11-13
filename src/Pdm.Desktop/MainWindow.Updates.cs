using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Upton.Pdm.ClientShared;

namespace Upton.Pdm.Desktop;

public partial class MainWindow
{
    private static string DesktopTarget => AppDomain.CurrentDomain.BaseDirectory;
    private static string LoadedDesktopVersion => ClientPackageUpdater.GetLoadedVersion(typeof(MainWindow).Assembly);

    private static void ReportDesktopStartup(bool healthy) =>
        ClientPackageUpdater.ReportStartup(ClientUpdateState.Desktop, DesktopTarget, LoadedDesktopVersion, healthy);

    private static bool QueueClientUpdates(ClientBootstrapConfiguration configuration, bool automatic)
    {
        var desktop = QueueComponent(ClientUpdateState.Desktop, DesktopTarget, configuration.Desktop, automatic);
        var plugin = ResolveSolidWorksAddinDirectory();
        var addin = false;
        if (File.Exists(Path.Combine(plugin, ClientUpdateState.ProgramName(ClientUpdateState.Addin)))
            || ClientPackageUpdater.GetStatus(ClientUpdateState.Addin, plugin)?.RecoveryRequired == true)
        {
            if (!automatic || ClientUpdateState.PluginAutomaticUpdatesEnabled())
                addin = QueueComponent(ClientUpdateState.Addin, plugin, configuration.SolidWorksAddin, automatic);
            else
                ClientPackageUpdater.ResumeRecovery(ClientUpdateState.Addin, plugin);
        }
        return desktop || addin;
    }

    private static bool QueueComponent(string component, string target, ClientPackageConfiguration package, bool automatic)
    {
        if (ClientPackageUpdater.GetStatus(component, target)?.RecoveryRequired == true)
        {
            ClientPackageUpdater.ResumeRecovery(component, target);
            return false;
        }
        return ClientPackageUpdater.QueueDownload(component, package, target, automatic);
    }

    private void ShowClientUpdates()
    {
        RestoreFromNotificationArea();
        var dialog = new Window
        {
            Title = "客户端与插件更新",
            Owner = this,
            Width = 550,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };
        var layout = new StackPanel { Margin = new Thickness(16) };
        var desktop = AddVersionRow(layout, "客户端");
        var addin = AddVersionRow(layout, "插件端");
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, MinHeight = 28 };
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var check = new System.Windows.Controls.Button { Content = "检查更新" };
        var install = new System.Windows.Controls.Button { Content = "重启更新" };
        var close = new System.Windows.Controls.Button { Content = "关闭", IsCancel = true };
        close.Click += (_, _) => dialog.Close();
        var buttonStyle = (Style)System.Windows.Markup.XamlReader.Parse(@"
<Style xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" TargetType=""Button"">
  <Setter Property=""Width"" Value=""80""/><Setter Property=""Height"" Value=""28""/>
  <Setter Property=""Margin"" Value=""8,0,0,0""/><Setter Property=""Background"" Value=""#F4F6F8""/>
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""Button"">
        <Border x:Name=""surface"" xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
                Background=""{TemplateBinding Background}"" BorderBrush=""#8493A4"" BorderThickness=""1"" CornerRadius=""14"">
          <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property=""IsMouseOver"" Value=""True""><Setter TargetName=""surface"" Property=""Background"" Value=""#E3EBF3""/></Trigger>
          <Trigger Property=""IsKeyboardFocused"" Value=""True""><Setter TargetName=""surface"" Property=""BorderBrush"" Value=""#1265AD""/><Setter TargetName=""surface"" Property=""BorderThickness"" Value=""2""/></Trigger>
          <Trigger Property=""IsEnabled"" Value=""False""><Setter TargetName=""surface"" Property=""Opacity"" Value=""0.45""/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>");
        foreach (var button in new[] { check, install, close }) { button.Style = buttonStyle; buttons.Children.Add(button); }
        layout.Children.Add(result);
        layout.Children.Add(buttons);
        dialog.Content = layout;
        var closed = false;
        ClientBootstrapConfiguration? checkedReleases = null;
        var releaseServer = ClientBootstrapLoader.GetServerAddress();
        dialog.Closed += (_, _) => closed = true;

        void RefreshStatus()
        {
            var plugin = ResolveSolidWorksAddinDirectory();
            var desktopState = ClientPackageUpdater.GetStatus(ClientUpdateState.Desktop, DesktopTarget);
            var addinState = ClientPackageUpdater.GetStatus(ClientUpdateState.Addin, plugin);
            var activeServer = ClientBootstrapLoader.GetServerAddress();
            var currentReleases = checkedReleases != null
                && string.Equals(releaseServer, activeServer, StringComparison.OrdinalIgnoreCase)
                && !ClientPackageUpdater.IsUpdateAvailable(checkedReleases.ConfigurationVersion, bootstrapConfiguration.ConfigurationVersion)
                ? checkedReleases : bootstrapConfiguration;
            SetVersionRow(desktop, LoadedDesktopVersion, currentReleases.Desktop.Version, desktopState);
            SetVersionRow(addin, ClientPackageUpdater.GetInstalledVersion(plugin), currentReleases.SolidWorksAddin.Version, addinState);
            install.IsEnabled = desktopState?.Phase == ClientUpdateState.Ready || desktopState?.Phase == ClientUpdateState.Waiting;
        }

        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            result.Text = "正在检查更新…";
            try
            {
                var requestedServer = ClientBootstrapLoader.GetServerAddress();
                var configuration = await ClientBootstrapLoader.LoadAsync(bootstrapLifetime.Token, false);
                if (!string.Equals(requestedServer, ClientBootstrapLoader.GetServerAddress(), StringComparison.OrdinalIgnoreCase))
                {
                    if (!closed) result.Text = "服务器地址已变更，请重新检查更新。";
                    return;
                }
                checkedReleases = configuration;
                releaseServer = requestedServer;
                var queued = await Task.Run(() => QueueClientUpdates(configuration, false), bootstrapLifetime.Token);
                if (!closed) result.Text = queued ? "更新任务已安排。" : "检查完成。";
            }
            catch (Exception error)
            {
                AppendClientDiagnostic($"检查客户端更新失败：{error}");
                if (!closed) result.Text = "检查更新失败，请核对服务器连接后重试。";
            }
            finally { if (!closed) { check.IsEnabled = true; RefreshStatus(); } }
        };
        install.Click += (_, _) =>
        {
            if (ClientPackageUpdater.TryLaunchDesktopUpdate(DesktopTarget))
            {
                dialog.Close();
                ExitApplication(scheduleUpdate: false);
            }
            else result.Text = "暂时无法启动更新，请稍后重试。";
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => RefreshStatus();
        RefreshStatus();
        timer.Start();
        try { dialog.ShowDialog(); }
        finally { timer.Stop(); }
    }

    private static (TextBlock Current, TextBlock Latest, TextBlock Status) AddVersionRow(StackPanel layout, string title)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var current = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var latest = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, MinHeight = 26, Margin = new Thickness(68, 0, 0, 8) };
        row.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
        Grid.SetColumn(current, 1);
        Grid.SetColumn(latest, 2);
        row.Children.Add(current);
        row.Children.Add(latest);
        layout.Children.Add(row);
        layout.Children.Add(status);
        return (current, latest, status);
    }

    private static void SetVersionRow((TextBlock Current, TextBlock Latest, TextBlock Status) row,
        string current, string latest, ClientUpdateState? state)
    {
        row.Current.Text = "当前版本  " + (string.IsNullOrWhiteSpace(current) ? "未检测到" : current);
        row.Latest.Text = "最新版本  " + (string.IsNullOrWhiteSpace(latest) ? "未获取" : latest);
        row.Status.Text = state?.Message ?? (ClientPackageUpdater.IsUpdateAvailable(current, latest) && !string.IsNullOrWhiteSpace(current)
            ? "发现新版本。" : "");
        if (state?.Phase == ClientUpdateState.Downloading && state.Progress.HasValue)
            row.Status.Text += $" {state.Progress}%";
    }

}
