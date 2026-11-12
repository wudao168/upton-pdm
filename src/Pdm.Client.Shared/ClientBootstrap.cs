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

internal static class ClientPackageUpdater
{
    private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    private static readonly object PendingUpdateSync = new object();

    public static string GetInstalledVersion(string targetDirectory)
    {
        try
        {
            var marker = Path.Combine(targetDirectory, ".uplm-version");
            return File.Exists(marker) ? File.ReadAllText(marker, Encoding.UTF8).Trim() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static bool IsUpdateAvailable(string installedVersion, string availableVersion)
    {
        if (string.IsNullOrWhiteSpace(availableVersion)) return false;
        if (string.IsNullOrWhiteSpace(installedVersion)) return true;
        if (string.Equals(installedVersion.Trim(), availableVersion.Trim(), StringComparison.OrdinalIgnoreCase)) return false;

        return TryParseReleaseVersion(installedVersion, out var installed)
            && TryParseReleaseVersion(availableVersion, out var available)
            && available > installed;
    }

    public static async Task<bool> StageAsync(
        string component,
        ClientPackageConfiguration package,
        string targetDirectory,
        CancellationToken cancellationToken,
        IProgress<ClientUpdateProgress> progress = null)
    {
        if (package == null
            || string.IsNullOrWhiteSpace(package.Version)
            || string.IsNullOrWhiteSpace(package.PackageUrl)
            || string.IsNullOrWhiteSpace(package.Sha256))
        {
            return false;
        }

        var installedVersion = GetInstalledVersion(targetDirectory);
        var repairFailedUpdate = false;
        var componentRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UPLM",
            "updates",
            component);
        Directory.CreateDirectory(componentRoot);
        using var stageLease = TryAcquireStageLease(Path.Combine(componentRoot, "download.lock"));
        if (stageLease == null) return false;
        var stageRoot = Path.Combine(componentRoot, SanitizeSegment(package.Version) + "-" + Guid.NewGuid().ToString("N"));
        var payloadRoot = Path.Combine(stageRoot, "payload");
        var pendingPath = GetPendingPath(component);
        lock (PendingUpdateSync)
        {
            if (File.Exists(pendingPath))
            {
                if (TryReadPendingUpdate(pendingPath, out var existing, out _))
                {
                    var samePackage = string.Equals(
                        existing.Version,
                        package.Version,
                        StringComparison.OrdinalIgnoreCase);
                    var hasInstallError = File.Exists(pendingPath + ".error.txt");
                    var hasFileHashes = existing.FileHashes != null && existing.FileHashes.Count > 0;
                    var canReuse = hasFileHashes && (component != "solidworks-addin"
                        || string.Equals(existing.PackageSha256, package.Sha256, StringComparison.OrdinalIgnoreCase));
                    if (samePackage && !hasInstallError && canReuse)
                    {
                        return false;
                    }
                    repairFailedUpdate = samePackage && (hasInstallError || !canReuse);
                }
                if (IsInstallerRunning(component) || (existing != null && IsUpdateAvailable(package.Version, existing.Version))) return false;
            }
        }

        if (!repairFailedUpdate && !IsUpdateAvailable(installedVersion, package.Version))
        {
            return false;
        }

        Directory.CreateDirectory(stageRoot);
        var archivePath = Path.Combine(stageRoot, "package.zip");
        using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        using (var response = await client.GetAsync(package.PackageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var destination = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                var totalBytes = response.Content.Headers.ContentLength;
                long receivedBytes = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                    receivedBytes += read;
                    progress?.Report(new ClientUpdateProgress(receivedBytes, totalBytes));
                }
            }
        }

        if (!string.Equals(ComputeSha256(archivePath), package.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("UPLM update package hash verification failed.");
        }

        if (Directory.Exists(payloadRoot)) Directory.Delete(payloadRoot, true);
        Directory.CreateDirectory(payloadRoot);
        using (var archive = ZipFile.OpenRead(archivePath))
        {
            var payloadPrefix = Path.GetFullPath(payloadRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var entry in archive.Entries)
            {
                var destinationPath = Path.GetFullPath(Path.Combine(payloadRoot, entry.FullName));
                if (!destinationPath.StartsWith(payloadPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("UPLM update package contains an invalid path.");
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
                entry.ExtractToFile(destinationPath, true);
            }
        }

        if (component == "solidworks-addin" && (!File.Exists(Path.Combine(payloadRoot, "Upton.Pdm.SolidWorks.Addin.dll"))
            || !string.Equals(GetInstalledVersion(payloadRoot), package.Version, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("插件更新包的版本或主文件不完整，请重新发布。");
        var pendingJson = Serializer.Serialize(new PendingUpdate
        {
            Component = component,
            Version = package.Version,
            PayloadDirectory = payloadRoot,
            TargetDirectory = Path.GetFullPath(targetDirectory),
            PackageSha256 = component == "solidworks-addin" ? package.Sha256 : null,
            FileHashes = Directory.GetFiles(payloadRoot, "*", SearchOption.AllDirectories)
                .Where(path => !string.Equals(Path.GetFileName(path), "uplm-bootstrap.json", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(path => path.Substring(payloadRoot.Length + 1), ComputeSha256, StringComparer.OrdinalIgnoreCase)
        });
        lock (PendingUpdateSync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pendingPath));
            using var publishLease = TryAcquireStageLease(Path.Combine(componentRoot, "launch.lock"));
            if (publishLease == null || IsInstallerRunning(component)) return false;
            try { File.Delete(pendingPath + ".error.txt"); } catch { }
            WriteAllTextAtomically(pendingPath, pendingJson);
        }
        return true;
    }

    private static FileStream TryAcquireStageLease(string path)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }

    public static bool IsPluginDownloadRunning() => TryGetRunningUpdater(GetPendingPath("solidworks-addin") + ".downloading");

    // 下载进程独立于 SolidWorks 生命周期，退出时尚未完成的下载也能继续并自动安装。
    public static bool StagePluginInBackground(ClientPackageConfiguration package, string targetDirectory, int processId, bool automaticInstall)
    {
        if (package == null || string.IsNullOrWhiteSpace(package.Version) || string.IsNullOrWhiteSpace(package.PackageUrl)
            || string.IsNullOrWhiteSpace(package.Sha256)) return false;
        var pendingPath = GetPendingPath("solidworks-addin");
        var root = Path.Combine(Path.GetDirectoryName(pendingPath), "solidworks-addin");
        Directory.CreateDirectory(root);
        using var lease = TryAcquireStageLease(Path.Combine(root, "background-download.lock"));
        if (lease == null || IsPluginDownloadRunning()) return true;
        if (IsInstallerRunning("solidworks-addin")) return true;
        var workerRoot = Path.Combine(root, "worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workerRoot);
        var assemblyPath = Path.Combine(workerRoot, "UPLM.UpdateWorker.dll");
        File.Copy(typeof(ClientPackageUpdater).Assembly.Location, assemblyPath);
        var request = Serializer.Serialize(new { package.Version, package.PackageUrl, package.Sha256,
            TargetDirectory = Path.GetFullPath(targetDirectory), ProcessId = processId, AutomaticInstall = automaticInstall });
        var marker = pendingPath + ".downloading";
        var command = "& {" + DownloadScript + "\n} -AssemblyPath " + PowerShellLiteral(assemblyPath)
            + " -RequestJson " + PowerShellLiteral(request) + " -MarkerPath " + PowerShellLiteral(marker);
        try
        {
            var worker = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe", Arguments = EncodePowerShellCommand(command),
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
            });
            if (worker == null) return false;
            using (worker) WriteAllTextAtomically(marker, worker.Id.ToString());
            return true;
        }
        catch
        {
            try { File.Delete(marker); } catch { }
            throw;
        }
    }

    private const string DownloadScript = @"param([string]$AssemblyPath,[string]$RequestJson,[string]$MarkerPath)
$ErrorActionPreference = 'Stop'
try {
  $request = $RequestJson | ConvertFrom-Json
  $assembly = [Reflection.Assembly]::LoadFile($AssemblyPath)
  $updater = $assembly.GetType('Upton.Pdm.ClientShared.ClientPackageUpdater', $true)
  $packageType = $assembly.GetType('Upton.Pdm.ClientShared.ClientPackageConfiguration', $true)
  $package = [Activator]::CreateInstance($packageType, $true)
  foreach ($name in @('Version','PackageUrl','Sha256')) { $packageType.GetProperty($name).SetValue($package, [string]$request.$name, $null) }
  $stage = $updater.GetMethod('StageAsync', [Reflection.BindingFlags]'Public,Static')
  for ($attempt = 0; $attempt -lt 10; $attempt++) {
    try {
      $task = $stage.Invoke($null, [object[]]@('solidworks-addin', $package, [string]$request.TargetDirectory, [Threading.CancellationToken]::None, $null))
      [void]$task.GetAwaiter().GetResult()
      $schedule = $updater.GetMethod('SchedulePluginUpdate', [Reflection.BindingFlags]'Public,Static')
      $scheduled = $schedule.Invoke($null, [object[]]@([int]$request.ProcessId, [bool]$request.AutomaticInstall))
      $running = $updater.GetMethod('IsInstallerRunning').Invoke($null, [object[]]@('solidworks-addin'))
      $installed = $updater.GetMethod('GetInstalledVersion').Invoke($null, [object[]]@([string]$request.TargetDirectory))
      $needed = $updater.GetMethod('IsUpdateAvailable').Invoke($null, [object[]]@([string]$installed,[string]$request.Version))
      if (-not $scheduled -and -not $running -and $needed) { throw '更新尚未就绪，后台将自动重试。' }
      Remove-Item -LiteralPath ($MarkerPath + '.error.txt') -Force -ErrorAction SilentlyContinue
      return
    } catch {
      ($_ | Out-String) | Set-Content -LiteralPath ($MarkerPath + '.error.txt') -Encoding UTF8
      if ($attempt -eq 9) { throw }
      Start-Sleep -Seconds 30
    }
  }
} catch {
  ($_ | Out-String) | Set-Content -LiteralPath ($MarkerPath + '.error.txt') -Encoding UTF8
} finally {
  Remove-Item -LiteralPath $MarkerPath -Force -ErrorAction SilentlyContinue
}";

    public static bool IsInstallerRunning(string component) => TryGetRunningUpdater(GetPendingPath(component) + ".launched");

    public static bool SchedulePluginOnExit(int processId) =>
        SchedulePluginUpdate(processId, true);

    public static bool SchedulePluginUpdate(int processId, bool automaticInstall) =>
        LaunchPendingUpdate("solidworks-addin", processId, string.Empty, false, true, automaticInstall);

    public static bool TryLaunchPendingUpdate(string component, int processId, string restartPath, bool visible = false)
        => LaunchPendingUpdate(component, processId, restartPath, visible, false);

    private static bool LaunchPendingUpdate(string component, int processId, string restartPath, bool visible, bool pluginExit, bool automaticInstall = false)
    {
        // 插件更新由插件自己的后台流程调度，桌面客户端不能代替插件启动更新。
        if (component == "solidworks-addin" && !visible && !pluginExit) return false;
        var pendingPath = GetPendingPath(component);
        lock (PendingUpdateSync)
        {
            if (!File.Exists(pendingPath)) return false;
            var componentRoot = Path.Combine(Path.GetDirectoryName(pendingPath), component);
            Directory.CreateDirectory(componentRoot);
            using var launchLease = TryAcquireStageLease(Path.Combine(componentRoot, "launch.lock"));
            if (launchLease == null) return false;
            var launchMarker = pendingPath + ".launched";
            if (File.Exists(launchMarker))
            {
                if (TryGetRunningUpdater(launchMarker)) return false;
                try { File.Delete(launchMarker); } catch { return false; }
            }

            // 上一次更换已经失败（例如目录仍被占用）时先正常启动客户端，避免每次打开都被更新流程立即关闭。
            var previousErrorPath = pendingPath + ".error.txt";
            try
            {
                if (!visible && File.Exists(previousErrorPath)
                    && File.GetLastWriteTimeUtc(previousErrorPath) > File.GetLastWriteTimeUtc(pendingPath))
                {
                    return false;
                }
            }
            catch
            {
            }

            try
            {
                if (!TryReadPendingUpdate(pendingPath, out var pending, out _)
                    || !Directory.Exists(pending.PayloadDirectory)
                    || string.IsNullOrWhiteSpace(pending.TargetDirectory))
                {
                    QuarantinePendingUpdate(pendingPath);
                    return false;
                }
                if (!IsUpdateAvailable(GetInstalledVersion(pending.TargetDirectory), pending.Version))
                {
                    QuarantinePendingUpdate(pendingPath);
                    return false;
                }

                // 插件能够读取的文件不一定能被 PowerShell 读取（例如透明加密软件按进程授权）。
                // 从插件内存传递脚本和已解析状态，不再让安装进程读取落盘的脚本及 JSON。
                pending.InteractiveInstall = visible;
                pending.AutomaticInstall = automaticInstall;
                if ((visible || pluginExit) && (pending.FileHashes == null || pending.FileHashes.Count == 0))
                    throw new InvalidDataException("此更新包缺少逐文件校验信息，请重新下载更新包。");
                var arguments = BuildUpdaterArguments(processId, pendingPath, launchMarker, restartPath, Serializer.Serialize(pending));
                if (visible) arguments = "-NoExit " + arguments;
                var updater = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = !visible,
                    WindowStyle = visible ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden
                });
                if (updater == null) return false;
                WriteAllTextAtomically(launchMarker, updater.Id.ToString());
                return true;
            }
            catch (Exception exception)
            {
                try { if (File.Exists(launchMarker)) File.Delete(launchMarker); } catch { }
                try { WriteAllTextAtomically(previousErrorPath, exception.ToString()); } catch { }
                return false;
            }
        }
    }

    private static string BuildUpdaterArguments(int processId, string pendingPath, string launchMarker, string restartPath, string pendingJson)
    {
        var command = string.Concat("& {", ApplyScript, "\n} -ProcessId ", processId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            " -PendingPath ", PowerShellLiteral(pendingPath), " -LaunchMarker ", PowerShellLiteral(launchMarker),
            " -RestartPath ", PowerShellLiteral(restartPath), " -PendingJson ", PowerShellLiteral(pendingJson));
        return EncodePowerShellCommand(command);
    }

    private static string EncodePowerShellCommand(string command)
    {
        byte[] compressed;
        using (var stream = new MemoryStream())
        {
            using (var gzip = new GZipStream(stream, CompressionMode.Compress, true))
            {
                var bytes = Encoding.UTF8.GetBytes(command);
                gzip.Write(bytes, 0, bytes.Length);
            }
            compressed = stream.ToArray();
        }
        var bootstrap = "$m=[IO.MemoryStream]::new([Convert]::FromBase64String('" + Convert.ToBase64String(compressed)
            + "'));$g=[IO.Compression.GZipStream]::new($m,[IO.Compression.CompressionMode]::Decompress);"
            + "$r=[IO.StreamReader]::new($g,[Text.Encoding]::UTF8);$s=$r.ReadToEnd();$r.Dispose();$m.Dispose();& ([ScriptBlock]::Create($s))";
        var arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap));
        if (arguments.Length > 32000) throw new InvalidOperationException("更新安装命令过长，无法启动安装进程。");
        return arguments;
    }

    private static string PowerShellLiteral(string value) => "'" + (value ?? string.Empty).Replace("'", "''") + "'";

    public static bool PendingUpdateHasFileHashes(string component) =>
        TryReadPendingUpdate(GetPendingPath(component), out var pending, out _)
        && pending.FileHashes != null && pending.FileHashes.Count > 0;

    public static bool TryGetPendingUpdate(string component, out string version, out string error)
    {
        version = string.Empty;
        error = string.Empty;
        var pendingPath = GetPendingPath(component);
        try
        {
            if (!File.Exists(pendingPath)) return false;
            if (!TryReadPendingUpdate(pendingPath, out var pending, out var pendingError))
            {
                error = pendingError;
                return true;
            }
            version = pending?.Version ?? string.Empty;
            var errorPath = pendingPath + ".error.txt";
            if (File.Exists(errorPath)) error = File.ReadAllText(errorPath, Encoding.UTF8).Trim();
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return true;
        }
    }

    private static string GetPendingPath(string component) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UPLM",
        "updates",
        component + "-pending.json");

    private static string ComputeSha256(string path)
    {
        using (var stream = File.OpenRead(path))
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
    }

    private static string SanitizeSegment(string value) => new string(value.Select(character =>
        Path.GetInvalidFileNameChars().Contains(character) ? '_' : character).ToArray());

    private static bool TryReadPendingUpdate(string path, out PendingUpdate pending, out string error)
    {
        pending = null;
        error = string.Empty;
        try
        {
            pending = Serializer.Deserialize<PendingUpdate>(File.ReadAllText(path, Encoding.UTF8));
            if (pending == null
                || string.IsNullOrWhiteSpace(pending.Component)
                || string.IsNullOrWhiteSpace(pending.Version)
                || string.IsNullOrWhiteSpace(pending.PayloadDirectory)
                || string.IsNullOrWhiteSpace(pending.TargetDirectory))
            {
                error = "待安装更新状态文件不完整。";
                pending = null;
                return false;
            }
            return true;
        }
        catch (Exception exception)
        {
            error = string.Concat("待安装更新状态文件已损坏：", exception.Message);
            return false;
        }
    }

    private static bool TryGetRunningUpdater(string launchMarker)
    {
        try
        {
            if (!int.TryParse(File.ReadAllText(launchMarker, Encoding.UTF8).Trim(), out var updaterProcessId)) return false;
            using (var updater = Process.GetProcessById(updaterProcessId))
                return !updater.HasExited
                    && string.Equals(updater.ProcessName, "powershell", StringComparison.OrdinalIgnoreCase)
                    && updater.StartTime.ToUniversalTime() <= File.GetLastWriteTimeUtc(launchMarker);
        }
        catch
        {
            return false;
        }
    }

    private static void QuarantinePendingUpdate(string pendingPath)
    {
        var root = Path.Combine(
            Path.GetDirectoryName(pendingPath),
            string.Concat("cancelled-", DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff")));
        Directory.CreateDirectory(root);
        foreach (var path in new[] { pendingPath, pendingPath + ".launched", pendingPath + ".error.txt" })
        {
            if (!File.Exists(path)) continue;
            File.Move(path, Path.Combine(root, Path.GetFileName(path)));
        }
    }

    private static void WriteAllTextAtomically(string path, string content, bool withBom = false)
    {
        var temporaryPath = string.Concat(path, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            File.WriteAllText(temporaryPath, content, new UTF8Encoding(withBom));
            if (File.Exists(path)) File.Replace(temporaryPath, path, null);
            else File.Move(temporaryPath, path);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    private static bool TryParseReleaseVersion(string value, out Version version)
    {
        var normalized = (value ?? string.Empty).Trim().TrimStart('V', 'v');
        var qualifierIndex = normalized.IndexOf('-');
        if (qualifierIndex >= 0) normalized = normalized.Substring(0, qualifierIndex);
        return Version.TryParse(normalized, out version);
    }

    private const string ApplyScript = @"param([int]$ProcessId,[string]$PendingPath,[string]$LaunchMarker,[string]$RestartPath,[string]$PendingJson)
$ErrorActionPreference='Stop'
function Verify-PackageFiles([string]$Directory,$Hashes) {
  if ($null -eq $Hashes) { return }
  $prefix = [IO.Path]::GetFullPath($Directory).TrimEnd('\') + '\'
  $files = @($Hashes.PSObject.Properties)
  $completed = 0
  foreach ($file in $files) {
    $path = [IO.Path]::GetFullPath((Join-Path $Directory $file.Name))
    if (-not $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid verification path.' }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.Value) { throw ('File verification failed: ' + $file.Name) }
    $completed++
    Write-Progress -Activity '逐项校验更新文件' -Status ($completed.ToString() + '/' + $files.Count) -PercentComplete ([int](100 * $completed / $files.Count))
  }
  Write-Progress -Activity '逐项校验更新文件' -Completed
}
function Copy-DirectoryWithRetry([string]$Source,[string]$Destination) {
  $lastError = $null
  for ($attempt = 1; $attempt -le 30; $attempt++) {
    try {
      New-Item -ItemType Directory -Path $Destination -Force | Out-Null
      Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $Destination -Recurse -Force -ErrorAction Stop
      }
      return
    } catch {
      $lastError = $_
      Start-Sleep -Seconds 1
    }
  }
  throw $lastError
}
function Expand-PluginInterface([string]$Directory,$Hashes) {
  $zipPath = Join-Path $Directory 'uplm-ui.zip'
  if (-not (Test-Path -LiteralPath $zipPath)) { return }
  $ui = Join-Path $Directory 'ui'
  $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
  try {
    $prefix = [IO.Path]::GetFullPath($ui).TrimEnd('\') + '\'
    foreach ($entry in $archive.Entries) {
      if (-not $entry.Name) { continue }
      $path = [IO.Path]::GetFullPath((Join-Path $ui $entry.FullName))
      if (-not $path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw '插件界面资源路径无效。' }
      [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))
      [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$path,$true)
      $stream = $entry.Open()
      $sha = [Security.Cryptography.SHA256]::Create()
      try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','') }
      finally { $stream.Dispose(); $sha.Dispose() }
      $Hashes | Add-Member -MemberType NoteProperty -Name ('ui\' + $entry.FullName.Replace('/','\')) -Value $hash -Force
    }
  } finally { $archive.Dispose() }
  foreach ($entry in @('index.html','review-overlay.html','server-settings.html','client-bootstrap.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $ui $entry) -PathType Leaf)) { throw ('插件界面资源缺失：' + $entry) }
  }
  Verify-PackageFiles $Directory $Hashes
}
function Expand-VerifiedPluginPackage([string]$ArchivePath,[string]$Destination,[string]$ExpectedHash) {
  Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
  $stream = [IO.File]::OpenRead($ArchivePath)
  try {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $sha.Dispose() }
    if ($hash -ne $ExpectedHash) { throw '插件原始更新包校验失败。' }
    $stream.Position = 0
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $true)
    try { [IO.Compression.ZipFileExtensions]::ExtractToDirectory($archive, $Destination) }
    finally { $archive.Dispose() }
  } finally { $stream.Dispose() }
}
function Move-DirectoryWithRetry([string]$Source,[string]$Destination) {
  $lastError = $null
  for ($attempt = 1; $attempt -le 90; $attempt++) {
    try {
      Move-Item -LiteralPath $Source -Destination $Destination -ErrorAction Stop
      return
    } catch {
      $lastError = $_
      # WebView2 子进程会在客户端退出后短暂占用客户端目录，先结束它们再重试，避免整次更新失败。
      Stop-LeftoverWebViewProcesses $Source
      Start-Sleep -Seconds 2
    }
  }
  throw $lastError
}
function Stop-LeftoverWebViewProcesses([string]$TargetDirectory) {
  $prefix = [IO.Path]::GetFullPath($TargetDirectory).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
  $processes = @(Get-Process -Name 'msedgewebview2' -ErrorAction SilentlyContinue)
  foreach ($process in $processes) {
    try {
      $path = $process.Path
      if (-not [string]::IsNullOrWhiteSpace($path) -and $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
      }
    } catch {
    } finally {
      $process.Dispose()
    }
  }
}
function Test-UsesPluginDirectory($Process,[string]$TargetDirectory) {
  $localRoot = [IO.Path]::GetFullPath([Environment]::GetFolderPath('LocalApplicationData')).TrimEnd('\') + '\'
  if (-not $TargetDirectory.StartsWith($localRoot, [StringComparison]::OrdinalIgnoreCase)) { return $true }
  # 用户独立安装只等待同一账号的实例，共享安装仍等待全部实例。
  try {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $instance = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $Process.Id) -ErrorAction Stop
    $owner = Invoke-CimMethod -InputObject $instance -MethodName GetOwnerSid -ErrorAction Stop
    if ($owner.ReturnValue -eq 0 -and $owner.Sid) {
      if ($owner.Sid -eq $identity) { return $true }
      # HKLM COM 注册可能让另一账号也加载此目录，须确认其未使用当前插件。
      $prefix = $TargetDirectory.TrimEnd('\') + '\'
      foreach ($module in $Process.Modules) {
        if ($module.FileName.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { return $true }
      }
      return $false
    }
  } catch { }
  return $true
}
function Wait-ForSolidWorksExit([int]$TimeoutSeconds,[string]$TargetDirectory) {
  for ($attempt = 1; $TimeoutSeconds -eq 0 -or $attempt -le $TimeoutSeconds; $attempt++) {
    $running = @(Get-Process -Name 'SLDWORKS' -ErrorAction SilentlyContinue)
    try {
      if (@($running | Where-Object { Test-UsesPluginDirectory $_ $TargetDirectory }).Count -eq 0) { return $true }
    } finally {
      foreach ($process in $running) { $process.Dispose() }
    }
    Start-Sleep -Seconds 1
  }
  return $false
}
$failureHint = '更新准备失败，请重新检查并下载更新。'
try {
  $pending = if ($PendingJson) { $PendingJson | ConvertFrom-Json } else { Get-Content -LiteralPath $PendingPath -Raw -Encoding UTF8 | ConvertFrom-Json }
  if ($pending.InteractiveInstall) {
    $Host.UI.RawUI.WindowTitle = 'UPLM 插件更新安装'
    Write-Host ('准备安装版本：' + $pending.Version) -ForegroundColor Cyan
    Write-Host '请保存图纸并关闭 SolidWorks。此窗口将显示安装进度和校验结果，请勿在安装中关闭。' -ForegroundColor Yellow
  }
  if ($ProcessId -gt 0) { Wait-Process -Id $ProcessId -ErrorAction SilentlyContinue }
  $target = [IO.Path]::GetFullPath([string]$pending.TargetDirectory)
  $payload = [IO.Path]::GetFullPath([string]$pending.PayloadDirectory)
  $installedMarker = Join-Path $target '.uplm-version'
  if (Test-Path -LiteralPath $installedMarker) {
    $installedText = ((Get-Content -LiteralPath $installedMarker -Raw -Encoding UTF8).Trim() -replace '^[Vv]', '' -split '-', 2)[0]
    $pendingText = (([string]$pending.Version).Trim() -replace '^[Vv]', '' -split '-', 2)[0]
    if ([Version]::Parse($pendingText) -le [Version]::Parse($installedText)) {
      Remove-Item -LiteralPath $PendingPath -Force -ErrorAction SilentlyContinue
      Remove-Item -LiteralPath $LaunchMarker -Force -ErrorAction SilentlyContinue
      Write-Host '当前已安装此版本，无需重复更新。' -ForegroundColor Green
      return
    }
  }
  if ([string]$pending.Component -eq 'solidworks-addin') {
    # SolidWorks 还开着就先不动目录：保留待安装状态（不写 error.txt），等客户端轮询或插件下次检查时重试。
    if (-not (Wait-ForSolidWorksExit 0 $target)) {
      Remove-Item -LiteralPath $LaunchMarker -Force -ErrorAction SilentlyContinue
      throw '等待 SolidWorks 退出超时，未修改安装目录。请关闭 SolidWorks 后重新点击安装更新。'
    }
  }
  if ($pending.AutomaticInstall) {
    $settingsPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'UPLM/solidworks-addin-settings.json'
    if (Test-Path -LiteralPath $settingsPath) {
      $settings = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
      if ($settings.AutomaticUpdatesEnabled -eq $false) { return }
    }
  }
  $backupRoot = Split-Path -Parent $PendingPath
  $component = [string]$pending.Component
  $targetParent = Split-Path -Parent $target
  # 备份必须与客户端目录同盘：跨盘移动会退化为复制，WebView2 缓存被占用时会直接失败。
  $backupRoot = Join-Path $targetParent ('.uplm-backup-' + $component)
  New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
  $backup = Join-Path $backupRoot ('backup-' + $component + '-' + [DateTimeOffset]::Now.ToString('yyyyMMdd-HHmmss-fff'))
  $candidate = Join-Path $targetParent ('.uplm-update-' + $component + '-' + [Guid]::NewGuid().ToString('N'))
  Write-Host '正在准备更新文件并校验…'
  $failureHint = '更新文件无法读取或校验失败，原版本已保留。请重新下载更新后再试。'
  if ($component -eq 'solidworks-addin' -and $pending.PackageSha256) {
    $archivePath = Join-Path (Split-Path -Parent $payload) 'package.zip'
    Expand-VerifiedPluginPackage $archivePath $candidate $pending.PackageSha256
  } else {
    if (-not (Test-Path -LiteralPath $payload -PathType Container)) { throw 'Update payload is missing.' }
    Copy-DirectoryWithRetry $payload $candidate
  }
  Verify-PackageFiles $candidate $pending.FileHashes
  if ($component -eq 'solidworks-addin') { Expand-PluginInterface $candidate $pending.FileHashes }
  # 服务器地址属于本机安装配置，不能被更新包的打包环境覆盖。
  $installedLocator = Join-Path $target 'uplm-bootstrap.json'
  if (Test-Path -LiteralPath $installedLocator) {
    Copy-Item -LiteralPath $installedLocator -Destination (Join-Path $candidate 'uplm-bootstrap.json') -Force -ErrorAction Stop
  }
  $candidateVersionPath = Join-Path $candidate '.uplm-version'
  if (-not (Test-Path -LiteralPath $candidateVersionPath)) { throw 'Update package version marker is missing.' }
  $candidateVersion = (Get-Content -LiteralPath $candidateVersionPath -Raw -Encoding UTF8).Trim()
  if (-not [string]::Equals($candidateVersion, [string]$pending.Version, [StringComparison]::OrdinalIgnoreCase)) {
    throw ('Update package version verification failed. Expected=' + [string]$pending.Version + ' Actual=' + $candidateVersion)
  }
  if ($component -eq 'solidworks-addin') { [void](Wait-ForSolidWorksExit 0 $target) }
  $failureHint = '安装未完成，请关闭占用插件的程序，再检查更新。详细原因已写入更新日志。'
  if (Test-Path -LiteralPath $target) { Move-DirectoryWithRetry $target $backup }
  try {
    Write-Host '正在安装并校验全部替换文件…'
    Move-DirectoryWithRetry $candidate $target
    Verify-PackageFiles $target $pending.FileHashes
    $installedVersion = (Get-Content -LiteralPath (Join-Path $target '.uplm-version') -Raw -Encoding UTF8).Trim()
    if ($installedVersion -ne [string]$pending.Version) { throw 'Installed version verification failed.' }
  } catch {
    if (Test-Path -LiteralPath $backup) {
      if (Test-Path -LiteralPath $target) { Move-DirectoryWithRetry $target $candidate }
      Move-DirectoryWithRetry $backup $target
      Write-Host '安装失败，已恢复旧版本。' -ForegroundColor Yellow
    }
    throw
  }
  Get-ChildItem -LiteralPath $backupRoot -Directory -Filter ('backup-' + $component + '-*') -ErrorAction SilentlyContinue |
    Where-Object { -not [string]::Equals($_.FullName, $backup, [StringComparison]::OrdinalIgnoreCase) } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
  $installedVersionPath = Join-Path $target '.uplm-version'
  if (-not (Test-Path -LiteralPath $installedVersionPath)) { throw 'Installed version marker is missing.' }
  $installedVersion = (Get-Content -LiteralPath $installedVersionPath -Raw -Encoding UTF8).Trim()
  if (-not [string]::Equals($installedVersion, [string]$pending.Version, [StringComparison]::OrdinalIgnoreCase)) {
    throw ('Installed version verification failed. Expected=' + [string]$pending.Version + ' Actual=' + $installedVersion)
  }
  Remove-Item -LiteralPath $PendingPath -Force
  Remove-Item -LiteralPath $LaunchMarker -Force -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath ($PendingPath + '.error.txt') -Force -ErrorAction SilentlyContinue
  Write-Host ('安装成功：' + $installedVersion + '，全部文件校验通过。现在可以重新启动 SolidWorks。') -ForegroundColor Green
  if ($RestartPath -and (Test-Path -LiteralPath $RestartPath)) { Start-Process -FilePath $RestartPath -WorkingDirectory (Split-Path -Parent $RestartPath) -WindowStyle Hidden }
} catch {
  Write-Host $failureHint -ForegroundColor Red
  ($_ | Out-String) | Set-Content -LiteralPath ($PendingPath + '.error.txt') -Encoding UTF8
} finally {
  Remove-Item -LiteralPath $LaunchMarker -Force -ErrorAction SilentlyContinue
}";

    private sealed class PendingUpdate
    {
        public string Component { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string PayloadDirectory { get; set; } = string.Empty;
        public string TargetDirectory { get; set; } = string.Empty;
        public string PackageSha256 { get; set; }
        public System.Collections.Generic.Dictionary<string, string> FileHashes { get; set; }
        public bool InteractiveInstall { get; set; }
        public bool AutomaticInstall { get; set; }
    }
}

internal sealed class ClientUpdateProgress
{
    public ClientUpdateProgress(long receivedBytes, long? totalBytes)
    {
        ReceivedBytes = receivedBytes;
        TotalBytes = totalBytes;
    }

    public long ReceivedBytes { get; }
    public long? TotalBytes { get; }
    public int? Percentage => TotalBytes.HasValue && TotalBytes.Value > 0
        ? (int)Math.Min(100, ReceivedBytes * 100L / TotalBytes.Value)
        : (int?)null;
}
