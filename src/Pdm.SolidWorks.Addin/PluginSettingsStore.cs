#nullable disable
using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace Upton.Pdm.SolidWorks;

internal sealed class PluginSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string ServerAddress { get; set; } = string.Empty;
    public bool AutomaticUpdatesEnabled { get; set; } = true;
}

internal static class PluginSettingsStore
{
    private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

    public static PluginSettings Load()
    {
        try
        {
            var path = GetPath();
            if (!File.Exists(path)) return new PluginSettings();
            var settings = Serializer.Deserialize<PluginSettings>(File.ReadAllText(path, Encoding.UTF8));
            return settings?.SchemaVersion == 1 ? settings : new PluginSettings();
        }
        catch
        {
            return new PluginSettings();
        }
    }

    public static void Save(PluginSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        var path = GetPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, Serializer.Serialize(settings), new UTF8Encoding(false));
    }

    public static Uri BuildBootstrapUrl(string serverAddress)
    {
        var value = (serverAddress ?? string.Empty).Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("服务器地址必须是完整的HTTP或HTTPS地址，例如：http://192.168.2.8:5173。");
        }

        var builder = new UriBuilder(uri);
        var path = builder.Path.TrimEnd('/');
        if (!path.EndsWith("/client-bootstrap.json", StringComparison.OrdinalIgnoreCase))
        {
            path = string.Concat(path, "/client-bootstrap.json");
        }
        builder.Path = path;
        builder.Query = string.Empty;
        builder.Fragment = string.Empty;
        return builder.Uri;
    }

    public static string NormalizeServerAddress(string serverAddress)
    {
        var bootstrapUrl = BuildBootstrapUrl(serverAddress);
        var builder = new UriBuilder(bootstrapUrl)
        {
            Path = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }

    public static string ServerAddressFromConfiguration(Upton.Pdm.ClientShared.ClientBootstrapConfiguration configuration)
    {
        var candidate = configuration?.UiBaseUrl;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
        {
            candidate = configuration?.ApiBaseUrl;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out uri)) return string.Empty;
        }

        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    private static string GetPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UPLM",
        "solidworks-addin-settings.json");
}

internal sealed class PluginUpdateSnapshot
{
    public string InstalledVersion { get; set; } = string.Empty;
    public string AvailableVersion { get; set; } = string.Empty;
    public string Status { get; set; } = "尚未检查更新";
    public DateTimeOffset? LastCheckedAt { get; set; }
    public int? ProgressPercentage { get; set; }
    public bool UpdateAvailable { get; set; }
    public bool Busy { get; set; }

    public PluginUpdateSnapshot Clone() => (PluginUpdateSnapshot)MemberwiseClone();
}

internal sealed class PluginConnectionResult
{
    public Upton.Pdm.ClientShared.ClientBootstrapConfiguration Configuration { get; set; }
    public string Message { get; set; } = string.Empty;
}

internal sealed class CallbackProgress<T> : IProgress<T>
{
    private readonly Action<T> callback;

    public CallbackProgress(Action<T> callback)
    {
        this.callback = callback ?? throw new ArgumentNullException(nameof(callback));
    }

    public void Report(T value) => callback(value);
}
