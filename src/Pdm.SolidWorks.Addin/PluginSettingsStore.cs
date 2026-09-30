#nullable disable
using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using Upton.Pdm.ClientShared;

namespace Upton.Pdm.SolidWorks;

internal sealed class PluginSettings
{
    private const double DefaultDrawingQrXMillimeters = 10d;
    private const double DefaultDrawingQrYMillimeters = 55d;
    private const double DefaultDrawingQrSizeMillimeters = 20d;

    public int SchemaVersion { get; set; } = 3;
    public string ServerAddress { get; set; } = string.Empty;
    public bool AutomaticUpdatesEnabled { get; set; } = true;
    public int PlannedTaskScrollIntervalSeconds { get; set; } = 30;
    public bool UseCustomDrawingQrPosition { get; set; }
    public double DrawingQrXMillimeters { get; set; } = DefaultDrawingQrXMillimeters;
    public double DrawingQrYMillimeters { get; set; } = DefaultDrawingQrYMillimeters;
    public double DrawingQrLengthMillimeters { get; set; } = DefaultDrawingQrSizeMillimeters;
    public double DrawingQrWidthMillimeters { get; set; } = DefaultDrawingQrSizeMillimeters;

    public void Normalize()
    {
        SchemaVersion = 3;
        if (PlannedTaskScrollIntervalSeconds != 15 && PlannedTaskScrollIntervalSeconds != 30 && PlannedTaskScrollIntervalSeconds != 60)
            PlannedTaskScrollIntervalSeconds = 30;
        DrawingQrXMillimeters = Clamp(DrawingQrXMillimeters, 0d, 2000d);
        DrawingQrYMillimeters = Clamp(DrawingQrYMillimeters, 0d, 2000d);
        DrawingQrLengthMillimeters = ClampSize(DrawingQrLengthMillimeters);
        DrawingQrWidthMillimeters = ClampSize(DrawingQrWidthMillimeters);
    }

    private static double Clamp(double value, double minimum, double maximum) =>
        double.IsNaN(value) || double.IsInfinity(value) ? minimum : Math.Max(minimum, Math.Min(maximum, value));

    private static double ClampSize(double value) =>
        value <= 0d || double.IsNaN(value) || double.IsInfinity(value)
            ? DefaultDrawingQrSizeMillimeters
            : Clamp(value, 8d, 2000d);
}

internal static class PluginSettingsStore
{
    private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

    public static PluginSettings Load()
    {
        var settings = LoadSavedSettings();
        settings.ServerAddress = ClientBootstrapLoader.GetServerAddress();
        return settings;
    }

    private static PluginSettings LoadSavedSettings()
    {
        try
        {
            var path = GetPath();
            if (!File.Exists(path)) return new PluginSettings();
            var settings = Serializer.Deserialize<PluginSettings>(File.ReadAllText(path, Encoding.UTF8));
            if (settings == null || settings.SchemaVersion < 1 || settings.SchemaVersion > 3)
                return new PluginSettings();
            settings.Normalize();
            return settings;
        }
        catch
        {
            return new PluginSettings();
        }
    }

    public static void Save(PluginSettings settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        settings.Normalize();
        ClientServerSettingsStore.Save(settings.ServerAddress);
        var path = GetPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, Serializer.Serialize(settings), new UTF8Encoding(false));
    }

    public static Uri BuildBootstrapUrl(string serverAddress)
    {
        return ClientServerSettingsStore.BuildBootstrapUrl(serverAddress);
    }

    public static string NormalizeServerAddress(string serverAddress)
    {
        return ClientServerSettingsStore.NormalizeServerAddress(serverAddress);
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
    public bool ReadyToInstall { get; set; }
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
