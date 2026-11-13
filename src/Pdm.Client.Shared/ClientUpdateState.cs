#nullable disable
using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Upton.Pdm.ClientShared;

internal sealed class ClientUpdateState
{
    public const string Desktop = "desktop";
    public const string Addin = "solidworks-addin";
    public const string UpdaterName = "UPLM.Updater.exe";
    public const string Downloading = "downloading";
    public const string Ready = "ready";
    public const string Waiting = "waiting";
    public const string Switching = "switching";
    public const string Verifying = "verifying";
    public const string Restoring = "restoring";
    public const string Complete = "complete";
    public const string Failed = "failed";
    public const string Restored = "restored";

    public string Id { get; set; }
    public string Component { get; set; }
    public string Target { get; set; }
    public string Version { get; set; }
    public string PackageUrl { get; set; }
    public string Sha256 { get; set; }
    public string Signature { get; set; }
    public string Phase { get; set; }
    public bool InstallAfterDownload { get; set; }
    public bool Automatic { get; set; }
    public int? Progress { get; set; }
    public string Message { get; set; }
    public long InstalledUtcTicks { get; set; }
    public int ObservedHostId { get; set; }
    public long ObservedHostStartTicks { get; set; }
    public long RetryUtcTicks { get; set; }

    public bool RecoveryRequired => Phase == Switching || Phase == Verifying || Phase == Restoring;

    public static string ProgramName(string component) => component switch
    {
        Desktop => "Upton.Pdm.Desktop.exe",
        Addin => "Upton.Pdm.SolidWorks.Addin.dll",
        _ => throw new InvalidDataException("未知的更新组件。")
    };

    public const string AddinGuid = "{BCFD8A8A-472B-42E2-AC62-58BC17773650}";

    public static bool PluginAutomaticUpdatesEnabled() =>
        Read<PluginUpdatePreference>(Path.Combine(LocalRoot, "solidworks-addin-settings.json"))?.AutomaticUpdatesEnabled != false;

    public static string NormalizeTarget(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static string LocalRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UPLM");

    public static string StateRoot(string component, string target)
    {
        using var sha = SHA256.Create();
        var key = sha.ComputeHash(Encoding.UTF8.GetBytes(component + "\n" + NormalizeTarget(target).ToUpperInvariant()));
        return Path.Combine(LocalRoot, "updater", BitConverter.ToString(key, 0, 16).Replace("-", ""));
    }

    public static byte[] SignedPayload(string component, string version, string sha256) =>
        Encoding.UTF8.GetBytes("UPLM-UPDATE-V1\n" + component + "\n" + version + "\n" + sha256.ToUpperInvariant());

    public static bool TryVersion(string value, out Version version) =>
        System.Version.TryParse((value ?? "").Trim().TrimStart('v', 'V').Split('-')[0], out version);

    public static string ReadVersion(string directory)
    {
        try { return File.ReadAllText(Path.Combine(directory, ".uplm-version")).Trim(); }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    public static string ProductVersion(string file) =>
        (FileVersionInfo.GetVersionInfo(file).ProductVersion ?? "").Split('+')[0];

    public static T Read<T>(string path) where T : class
    {
        try { return new JavaScriptSerializer().Deserialize<T>(File.ReadAllText(path)); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    public static void Write(string path, object value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer().Serialize(value));
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static FileStream TryLock(string path)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }

    public static void Log(string root, Exception exception)
    {
        try
        {
            var path = Path.Combine(root, "update.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                File.Delete(path);
            File.AppendAllText(path, DateTimeOffset.Now.ToString("O") + " " + exception + Environment.NewLine);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class PluginUpdatePreference
    {
        public bool AutomaticUpdatesEnabled { get; set; } = true;
    }
}

internal sealed class ClientUpdateReceipt
{
    public string Id { get; set; }
    public string Version { get; set; }
    public bool Healthy { get; set; }
}

internal sealed class DesktopUpdateExitRequest
{
    public string Id { get; set; }
}
