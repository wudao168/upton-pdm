using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Upton.Pdm.PreviewAgent.Setup;

internal static class SetupPreferences
{
    internal static string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UPLM", "preview-agent-setup.json");

    public static SetupOptions Load()
    {
        if (!File.Exists(FilePath)) return new SetupOptions();
        try
        {
            var options = new JavaScriptSerializer().Deserialize<SetupOptions>(File.ReadAllText(FilePath)) ?? new SetupOptions();
            options.Password = Decrypt(options.Password);
            options.Token = Decrypt(options.Token);
            return options;
        }
        catch (Exception exception) when (exception is IOException || exception is ArgumentException || exception is CryptographicException || exception is FormatException)
        {
            return new SetupOptions();
        }
    }

    public static void Save(SetupOptions options)
    {
        var saved = new SetupOptions {
            Name = options.Name, ServerUrl = options.ServerUrl, Username = options.Username, Password = Encrypt(options.Password),
            Token = Encrypt(options.Token), Port = options.Port, TimeoutMinutes = options.TimeoutMinutes,
            InstallDirectory = options.InstallDirectory, WorkerPath = options.WorkerPath, WorkRoot = options.WorkRoot,
            DedicatedConversionHost = options.DedicatedConversionHost
        };
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(saved), new UTF8Encoding(false));
        if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
        else File.Move(temporary, FilePath);
    }

    private static string Encrypt(string value) => string.IsNullOrEmpty(value) ? string.Empty
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
    private static string Decrypt(string value) => string.IsNullOrEmpty(value) ? string.Empty
        : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
}
