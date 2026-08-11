using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Upton.Pdm.SolidWorks;

internal static class RememberedCredentialsStore
{
    private const int FormatVersion = 1;
    private const string FileName = "solidworks-login.dat";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("UPTON PDM SolidWorks login");

    private static string StoragePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UPTON PDM",
        FileName);

    public static bool TryLoad(out string username, out string password)
    {
        username = string.Empty;
        password = string.Empty;
        if (!File.Exists(StoragePath))
        {
            return false;
        }

        byte[] clearBytes = null;
        try
        {
            var encryptedBytes = File.ReadAllBytes(StoragePath);
            clearBytes = ProtectedData.Unprotect(encryptedBytes, Entropy, DataProtectionScope.CurrentUser);
            using (var stream = new MemoryStream(clearBytes, false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                if (reader.ReadInt32() != FormatVersion)
                {
                    return false;
                }

                username = reader.ReadString();
                password = reader.ReadString();
                return !string.IsNullOrWhiteSpace(username);
            }
        }
        catch (Exception exception) when (
            exception is IOException ||
            exception is UnauthorizedAccessException ||
            exception is CryptographicException)
        {
            username = string.Empty;
            password = string.Empty;
            return false;
        }
        finally
        {
            if (clearBytes != null)
            {
                Array.Clear(clearBytes, 0, clearBytes.Length);
            }
        }
    }

    public static void Save(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("用户名不能为空。", nameof(username));
        }

        byte[] clearBytes;
        using (var stream = new MemoryStream())
        {
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(FormatVersion);
                writer.Write(username);
                writer.Write(password ?? string.Empty);
            }

            clearBytes = stream.ToArray();
        }

        byte[] encryptedBytes;
        try
        {
            encryptedBytes = ProtectedData.Protect(clearBytes, Entropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            Array.Clear(clearBytes, 0, clearBytes.Length);
        }

        var directory = Path.GetDirectoryName(StoragePath);
        Directory.CreateDirectory(directory);
        var temporaryPath = string.Concat(StoragePath, ".tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, encryptedBytes);
            if (File.Exists(StoragePath))
            {
                File.Replace(temporaryPath, StoragePath, null);
            }
            else
            {
                File.Move(temporaryPath, StoragePath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static void Clear()
    {
        if (File.Exists(StoragePath))
        {
            File.Delete(StoragePath);
        }
    }
}
