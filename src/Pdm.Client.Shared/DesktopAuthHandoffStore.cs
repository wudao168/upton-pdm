using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Upton.Pdm.ClientShared;

public static class DesktopAuthHandoffStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("UPLM.Desktop.AuthHandoff.v1");
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UPTON PDM",
        "auth-handoff");

    public static string Create(string sessionJson)
    {
        if (string.IsNullOrWhiteSpace(sessionJson)) throw new ArgumentException("登录会话不能为空。", nameof(sessionJson));

        Directory.CreateDirectory(DirectoryPath);
        DeleteExpiredFiles();

        var id = Guid.NewGuid().ToString("N");
        var envelope = new HandoffEnvelope
        {
            CreatedAtUtc = DateTime.UtcNow,
            SessionJson = sessionJson
        };
        var json = new JavaScriptSerializer().Serialize(envelope);
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(PathFor(id), protectedBytes);
        return id;
    }

    public static bool TryConsume(string id, out string sessionJson)
    {
        sessionJson = string.Empty;
        if (!TryNormalizeId(id, out var normalizedId)) return false;

        var path = PathFor(normalizedId);
        if (!File.Exists(path)) return false;

        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            var envelope = new JavaScriptSerializer().Deserialize<HandoffEnvelope>(Encoding.UTF8.GetString(bytes));
            if (envelope == null
                || string.IsNullOrWhiteSpace(envelope.SessionJson)
                || envelope.CreatedAtUtc > DateTime.UtcNow.AddSeconds(30)
                || DateTime.UtcNow - envelope.CreatedAtUtc > Lifetime)
            {
                return false;
            }

            sessionJson = envelope.SessionJson;
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        finally
        {
            Delete(normalizedId);
        }
    }

    public static void Delete(string id)
    {
        if (!TryNormalizeId(id, out var normalizedId)) return;
        try { File.Delete(PathFor(normalizedId)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void DeleteExpiredFiles()
    {
        try
        {
            foreach (var path in Directory.GetFiles(DirectoryPath, "*.dat"))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) <= Lifetime) continue;
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string PathFor(string id) => Path.Combine(DirectoryPath, id + ".dat");

    private static bool TryNormalizeId(string id, out string normalizedId)
    {
        normalizedId = string.Empty;
        if (!Guid.TryParseExact(id, "N", out var value)) return false;
        normalizedId = value.ToString("N");
        return true;
    }

    private sealed class HandoffEnvelope
    {
        public DateTime CreatedAtUtc { get; set; }
        public string SessionJson { get; set; } = string.Empty;
    }
}
