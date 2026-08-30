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
        var bootstrapUrl = ResolveBootstrapUrl();
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
            var cached = TryLoadCache(bootstrapUrl);
            return cached ?? Normalize(new ClientBootstrapConfiguration(), new Uri(DefaultBootstrapUrl));
        }
    }

    private static Uri ResolveBootstrapUrl()
    {
        var environmentValue = Environment.GetEnvironmentVariable("UPLM_BOOTSTRAP_URL");
        if (Uri.TryCreate(environmentValue, UriKind.Absolute, out var environmentUrl)) return environmentUrl;

        var locatorPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "uplm-bootstrap.json");
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

    public static async Task<bool> StageAsync(string component, ClientPackageConfiguration package, string targetDirectory, CancellationToken cancellationToken)
    {
        if (package == null
            || string.IsNullOrWhiteSpace(package.Version)
            || string.IsNullOrWhiteSpace(package.PackageUrl)
            || string.IsNullOrWhiteSpace(package.Sha256)
            || string.Equals(GetInstalledVersion(targetDirectory), package.Version, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var componentRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UPLM",
            "updates",
            component);
        var stageRoot = Path.Combine(componentRoot, SanitizeSegment(package.Version));
        var payloadRoot = Path.Combine(stageRoot, "payload");
        var pendingPath = GetPendingPath(component);
        if (File.Exists(pendingPath)) return false;

        Directory.CreateDirectory(stageRoot);
        var archivePath = Path.Combine(stageRoot, "package.zip");
        using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        using (var response = await client.GetAsync(package.PackageUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var destination = new FileStream(archivePath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await source.CopyToAsync(destination, 81920, cancellationToken).ConfigureAwait(false);
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

        Directory.CreateDirectory(Path.GetDirectoryName(pendingPath));
        File.WriteAllText(pendingPath, Serializer.Serialize(new PendingUpdate
        {
            Component = component,
            Version = package.Version,
            PayloadDirectory = payloadRoot,
            TargetDirectory = Path.GetFullPath(targetDirectory)
        }), new UTF8Encoding(false));
        return true;
    }

    public static bool TryLaunchPendingUpdate(string component, int processId, string restartPath)
    {
        var pendingPath = GetPendingPath(component);
        if (!File.Exists(pendingPath)) return false;
        var launchMarker = pendingPath + ".launched";
        if (File.Exists(launchMarker)) return false;

        try
        {
            var pending = Serializer.Deserialize<PendingUpdate>(File.ReadAllText(pendingPath, Encoding.UTF8));
            if (pending == null || !Directory.Exists(pending.PayloadDirectory) || string.IsNullOrWhiteSpace(pending.TargetDirectory)) return false;
            File.WriteAllText(launchMarker, DateTimeOffset.UtcNow.ToString("O"), new UTF8Encoding(false));
            var scriptPath = Path.Combine(Path.GetDirectoryName(pendingPath), "apply-pending-update.ps1");
            File.WriteAllText(scriptPath, ApplyScript, new UTF8Encoding(false));
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = string.Format(
                    "-NoProfile -ExecutionPolicy Bypass -File \"{0}\" -ProcessId {1} -PendingPath \"{2}\" -LaunchMarker \"{3}\" -RestartPath \"{4}\"",
                    scriptPath, processId, pendingPath, launchMarker, restartPath ?? string.Empty),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
            return true;
        }
        catch
        {
            try { if (File.Exists(launchMarker)) File.Delete(launchMarker); } catch { }
            return false;
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

    private const string ApplyScript = @"param([int]$ProcessId,[string]$PendingPath,[string]$LaunchMarker,[string]$RestartPath)
$ErrorActionPreference='Stop'
try {
  Wait-Process -Id $ProcessId -ErrorAction SilentlyContinue
  $pending = Get-Content -LiteralPath $PendingPath -Raw -Encoding UTF8 | ConvertFrom-Json
  $target = [IO.Path]::GetFullPath([string]$pending.TargetDirectory)
  $payload = [IO.Path]::GetFullPath([string]$pending.PayloadDirectory)
  if (-not (Test-Path -LiteralPath $payload)) { throw 'Update payload is missing.' }
  $backupRoot = Split-Path -Parent $PendingPath
  $backup = Join-Path $backupRoot ('backup-' + [DateTimeOffset]::Now.ToString('yyyyMMdd-HHmmss'))
  New-Item -ItemType Directory -Path $backup -Force | Out-Null
  if (Test-Path -LiteralPath $target) { Copy-Item -Path (Join-Path $target '*') -Destination $backup -Recurse -Force }
  Get-ChildItem -LiteralPath $backupRoot -Directory -Filter 'backup-*' -ErrorAction SilentlyContinue |
    Where-Object { -not [string]::Equals($_.FullName, $backup, [StringComparison]::OrdinalIgnoreCase) } |
    Remove-Item -Recurse -Force
  New-Item -ItemType Directory -Path $target -Force | Out-Null
  Copy-Item -Path (Join-Path $payload '*') -Destination $target -Recurse -Force
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
