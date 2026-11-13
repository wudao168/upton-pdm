#nullable disable
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Upton.Pdm.ClientShared;

internal sealed class ClientBootstrapConfiguration
{
    public int SchemaVersion { get; set; } = 1;
    public string ConfigurationVersion { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = "http://127.0.0.1:5080/";
    public string UiBaseUrl { get; set; } = "http://127.0.0.1:5173/";
    public int PollSeconds { get; set; } = 30;
    public ClientPackageConfiguration Desktop { get; set; } = new ClientPackageConfiguration();
    public ClientPackageConfiguration SolidWorksAddin { get; set; } = new ClientPackageConfiguration();
}

internal sealed class ClientPackageConfiguration
{
    public string Version { get; set; } = string.Empty;
    public string PackageUrl { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string Signature { get; set; } = string.Empty;
}

internal static class ClientServerSettingsStore
{
    private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

    public static string GetServerAddress()
    {
        try
        {
            var path = GetPath();
            if (!File.Exists(path)) return string.Empty;
            var settings = Serializer.Deserialize<ServerSettings>(File.ReadAllText(path, Encoding.UTF8));
            return NormalizeServerAddress(settings?.ServerAddress);
        }
        catch { return string.Empty; }
    }

    public static Uri BuildBootstrapUrl(string serverAddress) => new Uri(NormalizeServerAddress(serverAddress) + "/client-bootstrap.json");

    public static string NormalizeServerAddress(string serverAddress)
    {
        var value = (serverAddress ?? string.Empty).Trim();
        if (!value.Contains("://")) value = "http://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.AbsolutePath != "/" && uri.AbsolutePath != "/client-bootstrap.json"))
            throw new InvalidOperationException("请输入服务器 IP 或域名及端口，例如 http://192.168.2.8:5173。");
        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    public static void Save(string serverAddress)
    {
        var settings = new ServerSettings { ServerAddress = NormalizeServerAddress(serverAddress) };
        var path = GetPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, Serializer.Serialize(settings), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporaryPath, path, null);
            else File.Move(temporaryPath, path);
        }
        finally { try { File.Delete(temporaryPath); } catch { } }
    }

    private static string GetPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UPLM", "server-settings.json");

    private sealed class ServerSettings
    {
        public string ServerAddress { get; set; } = string.Empty;
    }
}

internal static class ClientBootstrapLoader
{
    private const string DefaultBootstrapUrl = "http://127.0.0.1:5173/client-bootstrap.json";
    private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

    public static ClientBootstrapConfiguration LoadLocalConfiguration() => LoadLocalConfiguration(ResolveBootstrapUrl());

    internal static ClientBootstrapConfiguration LoadLocalConfiguration(Uri bootstrapUrl)
    {
        if (bootstrapUrl == null) throw new ArgumentNullException(nameof(bootstrapUrl));
        return TryLoadCache(bootstrapUrl) ?? Normalize(new ClientBootstrapConfiguration(), bootstrapUrl);
    }

    public static async Task<ClientBootstrapConfiguration> LoadAsync(CancellationToken cancellationToken)
    {
        return await LoadAsync(ResolveBootstrapUrl(), cancellationToken).ConfigureAwait(false);
    }

    public static async Task<ClientBootstrapConfiguration> LoadAsync(CancellationToken cancellationToken, bool allowCache)
    {
        return await LoadAsync(ResolveBootstrapUrl(), cancellationToken, allowCache).ConfigureAwait(false);
    }

    public static async Task<ClientBootstrapConfiguration> LoadAsync(Uri bootstrapUrl, CancellationToken cancellationToken)
    {
        return await LoadAsync(bootstrapUrl, cancellationToken, true).ConfigureAwait(false);
    }

    public static async Task<ClientBootstrapConfiguration> LoadAsync(
        Uri bootstrapUrl,
        CancellationToken cancellationToken,
        bool allowCache)
    {
        if (bootstrapUrl == null) throw new ArgumentNullException(nameof(bootstrapUrl));
        try
        {
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(allowCache ? 5 : 15) })
            using (var response = await client.GetAsync(bootstrapUrl, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var configuration = Normalize(Serializer.Deserialize<ClientBootstrapConfiguration>(json), bootstrapUrl);
                SaveCache(configuration, bootstrapUrl);
                WriteDiagnostic($"在线配置：bootstrap={bootstrapUrl} ui={configuration.UiBaseUrl} api={configuration.ApiBaseUrl} version={configuration.ConfigurationVersion}");
                return configuration;
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (!allowCache) throw;
            var cached = TryLoadCache(bootstrapUrl);
            var configuration = cached ?? Normalize(new ClientBootstrapConfiguration(), bootstrapUrl);
            WriteDiagnostic($"配置获取失败，使用{(cached == null ? "安装入口" : "匹配缓存")}：bootstrap={bootstrapUrl} ui={configuration.UiBaseUrl} api={configuration.ApiBaseUrl} error={exception.GetType().Name}: {exception.Message}");
            return configuration;
        }
    }

    private static Uri ResolveBootstrapUrl()
    {
        var serverAddress = ClientServerSettingsStore.GetServerAddress();
        if (!string.IsNullOrWhiteSpace(serverAddress)) return ClientServerSettingsStore.BuildBootstrapUrl(serverAddress);
        var assemblyDirectory = Path.GetDirectoryName(typeof(ClientBootstrapLoader).Assembly.Location);
        var locatorDirectory = string.IsNullOrWhiteSpace(assemblyDirectory)
            ? AppDomain.CurrentDomain.BaseDirectory
            : assemblyDirectory;
        var locatorPath = Path.Combine(locatorDirectory, "uplm-bootstrap.json");
        if (File.Exists(locatorPath))
        {
            try
            {
                var locator = Serializer.Deserialize<BootstrapLocator>(File.ReadAllText(locatorPath, Encoding.UTF8));
                if (TryGetHttpUrl(locator?.BootstrapUrl, out var locatorUrl)) return locatorUrl;
            }
            catch
            {
            }
        }

        // 已安装客户端以固定配置为准，开发环境变量仅用于没有安装配置的场景。
        var environmentValue = Environment.GetEnvironmentVariable("UPLM_BOOTSTRAP_URL");
        if (TryGetHttpUrl(environmentValue, out var environmentUrl)) return environmentUrl;
        return new Uri(DefaultBootstrapUrl);
    }

    public static string GetServerAddress() => ResolveBootstrapUrl().GetLeftPart(UriPartial.Authority).TrimEnd('/');

    private static ClientBootstrapConfiguration TryLoadCache(Uri bootstrapUrl)
    {
        try
        {
            var path = GetCachePath(bootstrapUrl);
            if (File.Exists(path)) return ReadCache(File.ReadAllText(path, Encoding.UTF8), bootstrapUrl);
            var legacyPath = Path.Combine(Path.GetDirectoryName(GetCacheDirectory()), "bootstrap-cache.json");
            return File.Exists(legacyPath) ? ReadCache(File.ReadAllText(legacyPath, Encoding.UTF8), bootstrapUrl) : null;
        }
        catch
        {
            return null;
        }
    }

    private static ClientBootstrapConfiguration ReadCache(string json, Uri bootstrapUrl)
    {
        var cache = Serializer.Deserialize<BootstrapCache>(json);
        if (cache?.Configuration != null)
        {
            if (!TryGetHttpUrl(cache.BootstrapUrl, out var source) || source != bootstrapUrl) return null;
            return Normalize(cache.Configuration, bootstrapUrl);
        }

        // 旧缓存没有来源标记，只有地址与安装入口一致时才允许迁移。
        var legacy = Serializer.Deserialize<ClientBootstrapConfiguration>(json);
        if (legacy == null
            || !TryGetHttpUrl(legacy.UiBaseUrl, out var ui)
            || !TryGetHttpUrl(legacy.ApiBaseUrl, out var api)
            || !string.Equals(ui.Authority, bootstrapUrl.Authority, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(api.Authority, bootstrapUrl.Authority, StringComparison.OrdinalIgnoreCase)
            || ui.Scheme != bootstrapUrl.Scheme || api.Scheme != bootstrapUrl.Scheme) return null;
        return Normalize(legacy, bootstrapUrl);
    }

    private static void SaveCache(ClientBootstrapConfiguration configuration, Uri bootstrapUrl)
    {
        string temporaryPath = null;
        try
        {
            var path = GetCachePath(bootstrapUrl);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var json = Serializer.Serialize(new BootstrapCache { BootstrapUrl = bootstrapUrl.AbsoluteUri, Configuration = configuration });
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporaryPath, path, null);
            else File.Move(temporaryPath, path);
        }
        catch
        {
        }
        finally
        {
            try { if (temporaryPath != null) File.Delete(temporaryPath); } catch { }
        }
    }

    private static string GetCacheDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UPLM", "bootstrap-cache");

    private static string GetCachePath(Uri bootstrapUrl)
    {
        using (var sha = SHA256.Create())
        {
            var key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(bootstrapUrl.AbsoluteUri))).Replace("-", string.Empty);
            return Path.Combine(GetCacheDirectory(), key + ".json");
        }
    }

    private static bool TryGetHttpUrl(string value, out Uri url) =>
        Uri.TryCreate(value, UriKind.Absolute, out url)
        && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps);

    private static void WriteDiagnostic(string message)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UPTON", "PLM", "Logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "client-bootstrap.log"), $"{DateTimeOffset.Now:O} | {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { }
    }

    private static ClientBootstrapConfiguration Normalize(ClientBootstrapConfiguration configuration, Uri bootstrapUrl)
    {
        configuration = configuration ?? new ClientBootstrapConfiguration();
        configuration.UiBaseUrl = ResolveUrl(bootstrapUrl, configuration.UiBaseUrl, "http://127.0.0.1:5173/");
        // A cached loopback API address belongs to the server, not a remote client.
        configuration.ApiBaseUrl = ResolveUrl(bootstrapUrl, configuration.ApiBaseUrl, "http://127.0.0.1:5080/");
        configuration.PollSeconds = Math.Max(15, Math.Min(configuration.PollSeconds, 3600));
        configuration.Desktop = NormalizePackage(configuration.Desktop, bootstrapUrl);
        configuration.SolidWorksAddin = NormalizePackage(configuration.SolidWorksAddin, bootstrapUrl);
        return configuration;
    }

    private static ClientPackageConfiguration NormalizePackage(ClientPackageConfiguration package, Uri bootstrapUrl)
    {
        package = package ?? new ClientPackageConfiguration();
        if (!string.IsNullOrWhiteSpace(package.PackageUrl))
        {
            var packageUrl = new Uri(bootstrapUrl, package.PackageUrl);
            if (!bootstrapUrl.IsLoopback
                && (!string.Equals(packageUrl.Authority, bootstrapUrl.Authority, StringComparison.OrdinalIgnoreCase)
                    || packageUrl.Scheme != bootstrapUrl.Scheme))
            {
                packageUrl = new Uri(bootstrapUrl, packageUrl.PathAndQuery);
            }
            package.PackageUrl = packageUrl.AbsoluteUri;
        }
        return package;
    }

    private static string ResolveUrl(Uri bootstrapUrl, string value, string fallback)
    {
        var resolved = new Uri(bootstrapUrl, string.IsNullOrWhiteSpace(value) ? fallback : value);
        // 远程客户端通过安装入口的同源网关访问 UI/API，不采用清单中打包机器的 IP、端口或协议。
        if (!bootstrapUrl.IsLoopback
            && (!string.Equals(resolved.Authority, bootstrapUrl.Authority, StringComparison.OrdinalIgnoreCase)
                || resolved.Scheme != bootstrapUrl.Scheme))
        {
            resolved = new Uri(bootstrapUrl, "/");
        }
        return resolved.AbsoluteUri.TrimEnd('/') + "/";
    }

    private sealed class BootstrapLocator
    {
        public string BootstrapUrl { get; set; } = string.Empty;
    }

    private sealed class BootstrapCache
    {
        public string BootstrapUrl { get; set; } = string.Empty;
        public ClientBootstrapConfiguration Configuration { get; set; }
    }
}
