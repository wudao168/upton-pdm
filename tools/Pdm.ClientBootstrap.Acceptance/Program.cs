using System;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using Upton.Pdm.ClientShared;
using Upton.Pdm.SolidWorks;

internal static class Program
{
    private static readonly JavaScriptSerializer Json = new();

    private static object? Call(string name, params object[] args) =>
        typeof(ClientBootstrapLoader).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }

    private static void Main(string[] args)
    {
        if (Array.IndexOf(args, "--persistence") >= 0) CheckPersistence();
        var source = new Uri("http://192.168.2.8/client-bootstrap.json");
        var locatorPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "uplm-bootstrap.json");
        var originalLocator = File.Exists(locatorPath) ? File.ReadAllBytes(locatorPath) : null;
        var originalEnvironment = Environment.GetEnvironmentVariable("UPLM_BOOTSTRAP_URL");
        try
        {
            File.WriteAllText(locatorPath, Json.Serialize(new { BootstrapUrl = source.AbsoluteUri }));
            Environment.SetEnvironmentVariable("UPLM_BOOTSTRAP_URL", "http://10.7.7.62:5173/client-bootstrap.json");
            var sharedAddress = ClientServerSettingsStore.GetServerAddress();
            var expectedSource = string.IsNullOrWhiteSpace(sharedAddress) ? source : ClientServerSettingsStore.BuildBootstrapUrl(sharedAddress);
            Check(Equals(Call("ResolveBootstrapUrl"), expectedSource), "Fixed shared or installed address takes priority over stale environment variables.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("UPLM_BOOTSTRAP_URL", originalEnvironment);
            if (originalLocator == null) File.Delete(locatorPath);
            else File.WriteAllBytes(locatorPath, originalLocator);
        }
        var config = new ClientBootstrapConfiguration
        {
            UiBaseUrl = "http://10.7.7.62:5173/",
            ApiBaseUrl = "http://127.0.0.1:5080/",
            Desktop = new ClientPackageConfiguration { PackageUrl = "http://10.7.7.62:5173/updates/client.zip" }
        };
        var normalized = (ClientBootstrapConfiguration)Call("Normalize", config, source)!;
        Check(normalized.UiBaseUrl == "http://192.168.2.8/" && normalized.ApiBaseUrl == normalized.UiBaseUrl,
            "Published machine addresses cannot override the client gateway.");
        Check(normalized.Desktop.PackageUrl == "http://192.168.2.8/updates/client.zip", "Update downloads use the client gateway.");
        var stale = Json.Serialize(new ClientBootstrapConfiguration { UiBaseUrl = "http://192.168.2.8:5173/", ApiBaseUrl = "http://192.168.2.8:5173/" });
        Check(Call("ReadCache", stale, source) == null, "Legacy cache with a different port is rejected.");
        var loopback = Json.Serialize(new ClientBootstrapConfiguration());
        Check(Call("ReadCache", loopback, source) == null, "Legacy loopback cache is rejected on a remote client.");
        var legacy = Json.Serialize(normalized);
        Check(Call("ReadCache", legacy, source) != null, "Matching legacy cache remains usable.");
        var tagged = Json.Serialize(new { BootstrapUrl = source.AbsoluteUri, Configuration = normalized });
        Check(Call("ReadCache", tagged, source) != null, "Matching server cache survives restart.");
        Check(Call("ReadCache", tagged, new Uri("http://192.168.2.9/client-bootstrap.json")) == null, "Cache from another server is rejected.");
        Check(!Equals(Call("GetCachePath", source), Call("GetCachePath", new Uri("http://192.168.2.8:5173/client-bootstrap.json"))),
            "Different server ports have separate cache files.");
        var fallback = (ClientBootstrapConfiguration)Call("Normalize", new ClientBootstrapConfiguration(), source)!;
        Check(fallback.UiBaseUrl == "http://192.168.2.8/" && fallback.ApiBaseUrl == fallback.UiBaseUrl,
            "Failed bootstrap with no valid cache keeps the installed gateway.");
        var local = (ClientBootstrapConfiguration)Call("Normalize", new ClientBootstrapConfiguration(), new Uri("http://127.0.0.1:5173/client-bootstrap.json"))!;
        Check(local.ApiBaseUrl == "http://127.0.0.1:5080/", "Local development API port remains supported.");
        Check(ClientServerSettingsStore.NormalizeServerAddress("192.168.2.8:5173") == "http://192.168.2.8:5173", "Bare IP and port can be used in client settings.");
        Check(ClientServerSettingsStore.BuildBootstrapUrl("http://192.168.2.8:5173/").AbsoluteUri == "http://192.168.2.8:5173/client-bootstrap.json", "Settings preserve the chosen port.");
        foreach (var invalid in new[] { "", "ftp://192.168.2.8", "http://user:password@192.168.2.8", "http://192.168.2.8/unrelated", "http://192.168.2.8/?server=old" })
        {
            try { ClientServerSettingsStore.NormalizeServerAddress(invalid); throw new Exception("Invalid server accepted: " + invalid); }
            catch (InvalidOperationException) { Check(true, "Invalid server address rejected: " + invalid); }
        }
    }

    private static void CheckPersistence()
    {
        var path = (string)typeof(ClientServerSettingsStore).GetMethod("GetPath", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        var original = File.Exists(path) ? File.ReadAllBytes(path) : null;
        try
        {
            ClientServerSettingsStore.Save("192.168.2.8:5173");
            Check(ClientServerSettingsStore.GetServerAddress() == "http://192.168.2.8:5173", "Shared settings persist independently of install folders.");
            Check(PluginSettingsStore.Load().ServerAddress == ClientBootstrapLoader.GetServerAddress(), "Desktop and plugin read the same persisted server.");
            ClientServerSettingsStore.Save("192.168.2.9:5173");
            Check(ClientBootstrapLoader.GetServerAddress() == "http://192.168.2.9:5173" && PluginSettingsStore.Load().ServerAddress == "http://192.168.2.9:5173",
                "Replacing shared settings updates both components without old caches overriding them.");
        }
        finally
        {
            if (original == null) File.Delete(path);
            else File.WriteAllBytes(path, original);
        }
    }
}
