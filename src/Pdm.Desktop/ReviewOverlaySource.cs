using System;

namespace Upton.Pdm.Desktop;

/// <summary>
/// 图纸审核浮层页面的来源选择：优先随主界面一样从服务器加载，
/// 保证客户端审核界面与网页端完全一致；服务器不可达时才回退到客户端内置副本。
/// </summary>
internal static class ReviewOverlaySource
{
    public static Uri Build(string page, string? serverUiBaseUrl, string configurationVersion, long localUiVersion, bool useLocalFallback)
    {
        if (!useLocalFallback
            && !string.IsNullOrWhiteSpace(serverUiBaseUrl)
            && Uri.TryCreate(serverUiBaseUrl, UriKind.Absolute, out var serverUri))
        {
            return new Uri(serverUri, $"{page}?configuration={Uri.EscapeDataString(configurationVersion ?? string.Empty)}");
        }
        return new Uri($"https://{MainWindow.UiHostName}/{page}?v={localUiVersion}");
    }
}
