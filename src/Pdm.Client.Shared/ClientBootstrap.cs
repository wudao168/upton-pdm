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

internal static class ClientBootstrapLoader
{
    private const string DefaultBootstrapUrl = "http://127.0.0.1:5173/client-bootstrap.json";
    private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

    public static async Task<ClientBootstrapConfiguration> LoadAsync(CancellationToken cancellationToken)
    {
        return await LoadAsync(ResolveBootstrapUrl(), cancellationToken).ConfigureAwait(false);
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
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            using (var response = await client.GetAsync(bootstrapUrl, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var configuration = Normalize(Serializer.Deserialize<ClientBootstrapConfiguration>(json), bootstrapUrl);
                SaveCache(json);
                return configuration;
            }
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            if (!allowCache) throw;
            var cached = TryLoadCache(bootstrapUrl);
            return cached ?? Normalize(new ClientBootstrapConfiguration(), new Uri(DefaultBootstrapUrl));
        }
    }

    private static Uri ResolveBootstrapUrl()
    {
        var environmentValue = Environment.GetEnvironmentVariable("UPLM_BOOTSTRAP_URL");
        if (Uri.TryCreate(environmentValue, UriKind.Absolute, out var environmentUrl)) return environmentUrl;

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
                if (Uri.TryCreate(locator?.BootstrapUrl, UriKind.Absolute, out var locatorUrl)) return locatorUrl;
            }
            catch
            {
            }
        }

        return new Uri(DefaultBootstrapUrl);
    }

    private static ClientBootstrapConfiguration TryLoadCache(Uri bootstrapUrl)
    {
        try
        {
            var path = GetCachePath();
            if (!File.Exists(path)) return null;
            return Normalize(Serializer.Deserialize<ClientBootstrapConfiguration>(File.ReadAllText(path, Encoding.UTF8)), bootstrapUrl);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveCache(string json)
    {
        try
        {
            var path = GetCachePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }
        catch
        {
        }
    }

    private static string GetCachePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UPLM",
        "bootstrap-cache.json");

    private static ClientBootstrapConfiguration Normalize(ClientBootstrapConfiguration configuration, Uri bootstrapUrl)
    {
        configuration = configuration ?? new ClientBootstrapConfiguration();
        configuration.ApiBaseUrl = ResolveUrl(bootstrapUrl, configuration.ApiBaseUrl, "http://127.0.0.1:5080/");
        configuration.UiBaseUrl = ResolveUrl(bootstrapUrl, configuration.UiBaseUrl, "http://127.0.0.1:5173/");
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
            package.PackageUrl = new Uri(bootstrapUrl, package.PackageUrl).AbsoluteUri;
        }
        return package;
    }

    private static string ResolveUrl(Uri bootstrapUrl, string value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return new Uri(bootstrapUrl, value).AbsoluteUri.TrimEnd('/') + "/";
    }

    private sealed class BootstrapLocator
    {
        public string BootstrapUrl { get; set; } = string.Empty;
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
            || string.IsNullOrWhiteSpace(package.Sha256)
            || !IsUpdateAvailable(GetInstalledVersion(targetDirectory), package.Version))
        {
            return false;
        }

        var installedVersion = GetInstalledVersion(targetDirectory);
        var componentRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UPLM",
            "updates",
            component);
        var stageRoot = Path.Combine(componentRoot, SanitizeSegment(package.Version));
        var payloadRoot = Path.Combine(stageRoot, "payload");
        var pendingPath = GetPendingPath(component);
        lock (PendingUpdateSync)
        {
            if (File.Exists(pendingPath))
            {
                if (TryReadPendingUpdate(pendingPath, out var existing, out _)
                    && string.Equals(existing.Version, package.Version, StringComparison.OrdinalIgnoreCase)
                    && IsUpdateAvailable(installedVersion, existing.Version))
                {
                    return false;
                }
                QuarantinePendingUpdate(pendingPath);
            }
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

        var pendingJson = Serializer.Serialize(new PendingUpdate
        {
            Component = component,
            Version = package.Version,
            PayloadDirectory = payloadRoot,
            TargetDirectory = Path.GetFullPath(targetDirectory)
        });
        lock (PendingUpdateSync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pendingPath));
            WriteAllTextAtomically(pendingPath, pendingJson);
        }
        return true;
    }

    public static bool TryLaunchPendingUpdate(string component, int processId, string restartPath)
    {
        var pendingPath = GetPendingPath(component);
        lock (PendingUpdateSync)
        {
            if (!File.Exists(pendingPath)) return false;
            var launchMarker = pendingPath + ".launched";
            if (File.Exists(launchMarker))
            {
                if (TryGetRunningUpdater(launchMarker)) return false;
                try { File.Delete(launchMarker); } catch { return false; }
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

                var scriptPath = Path.Combine(Path.GetDirectoryName(pendingPath), "apply-pending-update.ps1");
                WriteAllTextAtomically(scriptPath, ApplyScript);
                var updater = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = string.Format(
                        "-NoProfile -ExecutionPolicy Bypass -File \"{0}\" -ProcessId {1} -PendingPath \"{2}\" -LaunchMarker \"{3}\" -RestartPath \"{4}\"",
                        scriptPath, processId, pendingPath, launchMarker, restartPath ?? string.Empty),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                if (updater == null) return false;
                WriteAllTextAtomically(launchMarker, updater.Id.ToString());
                return true;
            }
            catch
            {
                try { if (File.Exists(launchMarker)) File.Delete(launchMarker); } catch { }
                return false;
            }
        }
    }

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
            using (var updater = Process.GetProcessById(updaterProcessId)) return !updater.HasExited;
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

    private static void WriteAllTextAtomically(string path, string content)
    {
        var temporaryPath = string.Concat(path, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            File.WriteAllText(temporaryPath, content, new UTF8Encoding(false));
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

    private const string ApplyScript = @"param([int]$ProcessId,[string]$PendingPath,[string]$LaunchMarker,[string]$RestartPath)
$ErrorActionPreference='Stop'
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
try {
  if ($ProcessId -gt 0) { Wait-Process -Id $ProcessId -ErrorAction SilentlyContinue }
  $pending = Get-Content -LiteralPath $PendingPath -Raw -Encoding UTF8 | ConvertFrom-Json
  $target = [IO.Path]::GetFullPath([string]$pending.TargetDirectory)
  $payload = [IO.Path]::GetFullPath([string]$pending.PayloadDirectory)
  if (-not (Test-Path -LiteralPath $payload)) { throw 'Update payload is missing.' }
  $backupRoot = Split-Path -Parent $PendingPath
  $backup = Join-Path $backupRoot ('backup-' + [DateTimeOffset]::Now.ToString('yyyyMMdd-HHmmss'))
  New-Item -ItemType Directory -Path $backup -Force | Out-Null
  if (Test-Path -LiteralPath $target) { Copy-DirectoryWithRetry $target $backup }
  Get-ChildItem -LiteralPath $backupRoot -Directory -Filter 'backup-*' -ErrorAction SilentlyContinue |
    Where-Object { -not [string]::Equals($_.FullName, $backup, [StringComparison]::OrdinalIgnoreCase) } |
    Remove-Item -Recurse -Force
  Copy-DirectoryWithRetry $payload $target
  $installedVersionPath = Join-Path $target '.uplm-version'
  if (-not (Test-Path -LiteralPath $installedVersionPath)) { throw 'Installed version marker is missing.' }
  $installedVersion = (Get-Content -LiteralPath $installedVersionPath -Raw -Encoding UTF8).Trim()
  if (-not [string]::Equals($installedVersion, [string]$pending.Version, [StringComparison]::OrdinalIgnoreCase)) {
    throw ('Installed version verification failed. Expected=' + [string]$pending.Version + ' Actual=' + $installedVersion)
  }
  Remove-Item -LiteralPath $PendingPath -Force
  Remove-Item -LiteralPath $LaunchMarker -Force -ErrorAction SilentlyContinue
  if ($RestartPath -and (Test-Path -LiteralPath $RestartPath)) { Start-Process -FilePath $RestartPath -WorkingDirectory (Split-Path -Parent $RestartPath) -WindowStyle Hidden }
} catch {
  ($_ | Out-String) | Set-Content -LiteralPath ($PendingPath + '.error.txt') -Encoding UTF8
  Remove-Item -LiteralPath $LaunchMarker -Force -ErrorAction SilentlyContinue
}";

    private sealed class PendingUpdate
    {
        public string Component { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string PayloadDirectory { get; set; } = string.Empty;
        public string TargetDirectory { get; set; } = string.Empty;
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
