using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Upton.Pdm.Desktop;

internal static class RememberedCredentialsStore
{
    private const int LegacyFormatVersion = 1;
    private const int FormatVersion = 2;
    private const string FileName = "desktop-login.dat";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("UPTON PDM Desktop login");

    private static string StoragePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UPTON PDM",
        FileName);

    public static bool TryLoadUsername(out string username)
    {
        username = string.Empty;
        if (!File.Exists(StoragePath))
        {
            return false;
        }

        byte[]? clearBytes = null;
        var requiresMigration = false;
        try
        {
            var encryptedBytes = File.ReadAllBytes(StoragePath);
            clearBytes = ProtectedData.Unprotect(encryptedBytes, Entropy, DataProtectionScope.CurrentUser);
            using var stream = new MemoryStream(clearBytes, false);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            var formatVersion = reader.ReadInt32();
            if (formatVersion != LegacyFormatVersion && formatVersion != FormatVersion)
            {
                return false;
            }

            username = reader.ReadString();
            requiresMigration = formatVersion == LegacyFormatVersion;
        }
        catch (Exception exception) when (
            exception is IOException
            || exception is UnauthorizedAccessException
            || exception is CryptographicException)
        {
            username = string.Empty;
            return false;
        }
        finally
        {
            if (clearBytes is not null)
            {
                Array.Clear(clearBytes, 0, clearBytes.Length);
            }
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            return false;
        }

        if (requiresMigration)
        {
            try
            {
                SaveUsername(username);
            }
            catch (Exception exception) when (
                exception is IOException
                || exception is UnauthorizedAccessException
                || exception is CryptographicException)
            {
                username = string.Empty;
                return false;
            }
        }

        return true;
    }

    public static void SaveUsername(string username)
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
                writer.Write(username.Trim());
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

        var directory = Path.GetDirectoryName(StoragePath)!;
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
