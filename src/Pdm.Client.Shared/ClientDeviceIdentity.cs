#nullable disable
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Upton.Pdm.ClientShared;

// 桌面和插件共用凭证；私钥由 Windows 当前用户保护，不把 MAC 当作登录密码。
public static class ClientDeviceIdentity
{
    private static readonly object Sync = new object();
    private static readonly string Instance = Guid.NewGuid().ToString("N");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("UPLM.LoginDevice.v1");
    private static RSACryptoServiceProvider key;
    private static string component = "desktop";
    private static string mac = "";

    public static void Initialize(string source) { component = source == "solidworks" ? "solidworks" : "desktop"; }

    public static void Apply(HttpRequestMessage request)
    {
        if (request.RequestUri == null || !request.RequestUri.AbsolutePath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) return;
        var headers = Headers(request.Method.Method, request.RequestUri, request.Headers.Authorization?.ToString() ?? "");
        request.Headers.Remove("X-UPLM-Device"); request.Headers.Remove("X-UPLM-Proof");
        request.Headers.Add("X-UPLM-Device", headers.Item1); request.Headers.Add("X-UPLM-Proof", headers.Item2);
    }

    public static Tuple<string, string> Headers(string method, Uri uri, string authorization)
    {
        lock (Sync)
        {
            EnsureKey();
            var json = new JavaScriptSerializer().Serialize(new
            {
                type = component, instance = Instance, publicKey = key.ToXmlString(false), machine = Environment.MachineName,
                mac, time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), nonce = Guid.NewGuid().ToString("N")
            });
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(authorization ?? ""))).Replace("-", "").ToLowerInvariant();
            var content = method + "\n" + uri.PathAndQuery + "\n" + encoded + "\n" + hash;
            return Tuple.Create(encoded, Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(content), CryptoConfig.MapNameToOID("SHA256"))));
        }
    }

    private static void EnsureKey()
    {
        if (key != null) return;
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UPLM", "Device");
        var path = Path.Combine(directory, "login-identity.dat");
        using (var mutex = new Mutex(false, "Local\\UPLM.LoginDeviceIdentity"))
        {
            bool acquired;
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new InvalidOperationException("读取电脑登录凭证超时，请稍后重试。");
            try
            {
                Directory.CreateDirectory(directory);
                var candidate = new RSACryptoServiceProvider(2048) { PersistKeyInCsp = false };
                try
                {
                    if (File.Exists(path)) candidate.FromXmlString(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser)));
                    else
                    {
                        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try
                        {
                            File.WriteAllBytes(temporary, ProtectedData.Protect(Encoding.UTF8.GetBytes(candidate.ToXmlString(true)), Entropy, DataProtectionScope.CurrentUser));
                            File.Move(temporary, path);
                        }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    }
                    mac = string.Join(", ", NetworkInterface.GetAllNetworkInterfaces()
                        .Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet || n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                        .Select(n => n.GetPhysicalAddress().ToString()).Where(m => m.Length == 12).Distinct().OrderBy(m => m).Take(8));
                    key = candidate;
                }
                catch (Exception error) when (error is CryptographicException || error is IOException || error is UnauthorizedAccessException)
                {
                    candidate.Dispose();
                    throw new InvalidOperationException("无法读取电脑登录凭证，请联系管理员检查用户目录权限；重装系统后需要重新绑定电脑。", error);
                }
            }
            finally { mutex.ReleaseMutex(); }
        }
    }
}
