using System;
using System.Reflection;
using Microsoft.Win32;

namespace Upton.Pdm.Desktop;

internal static class DesktopStartupSettings
{
    private const string PreferenceKeyPath = @"Software\UPTON\PDM Desktop";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string PreferenceName = "StartWithWindows";
    private const string RunValueName = "UPTON PDM";

    public static bool EnsureConfigured()
    {
        using var preferenceKey = Registry.CurrentUser.CreateSubKey(PreferenceKeyPath);
        var configured = preferenceKey?.GetValue(PreferenceName);
        var enabled = configured == null || Convert.ToInt32(configured) != 0;
        if (configured == null)
        {
            preferenceKey?.SetValue(PreferenceName, 1, RegistryValueKind.DWord);
        }

        ApplyRunEntry(enabled);
        return enabled;
    }

    public static void SetEnabled(bool enabled)
    {
        using (var preferenceKey = Registry.CurrentUser.CreateSubKey(PreferenceKeyPath))
        {
            preferenceKey?.SetValue(PreferenceName, enabled ? 1 : 0, RegistryValueKind.DWord);
        }

        ApplyRunEntry(enabled);
    }

    private static void ApplyRunEntry(bool enabled)
    {
        using var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
        {
            var executable = Assembly.GetEntryAssembly()?.Location
                ?? throw new InvalidOperationException("无法确定PDM客户端程序路径。");
            runKey?.SetValue(RunValueName, string.Concat('"', executable, '"', " --startup"), RegistryValueKind.String);
        }
        else
        {
            runKey?.DeleteValue(RunValueName, false);
        }
    }
}
