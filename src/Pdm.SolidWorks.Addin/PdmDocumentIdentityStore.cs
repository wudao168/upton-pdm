#nullable disable
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;
using Upton.Pdm.LocalSettings;

namespace Upton.Pdm.SolidWorks;

internal static class PdmDocumentIdentityStore
{
    private const string StreamName = ":UptonPdm.DocumentId";
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint CreateAlways = 2;
    private const uint OpenExisting = 3;
    private static string bindingDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UPTON PDM", "document-bindings");
    private static Func<string> workspaceRootResolver = WorkspaceSettingsStore.GetWorkspaceRoot;
    private static readonly object bindingSync = new object();

    public static bool TryRead(string filePath, out Guid documentId)
    {
        var binding = IsControlledWorkspacePath(filePath) ? ReadBinding(filePath) : null;
        documentId = binding?.DocumentId ?? Guid.Empty;
        return documentId != Guid.Empty;
    }

    public static Guid? ReadProjectId(string filePath) =>
        IsControlledWorkspacePath(filePath) ? ReadBinding(filePath)?.ProjectId : null;

    public static bool TryReadControlledMetadata(string filePath, out PdmControlledFileMetadata metadata)
    {
        metadata = null;
        var binding = IsControlledWorkspacePath(filePath) ? ReadBinding(filePath) : null;
        if (binding == null || binding.DocumentId == Guid.Empty) return false;
        metadata = new PdmControlledFileMetadata
        {
            DocumentId = binding.DocumentId,
            ProjectId = binding.ProjectId,
            VersionId = binding.VersionId,
            Revision = binding.Revision ?? string.Empty,
            Sha256 = binding.Sha256 ?? string.Empty,
            FileLength = binding.FileLength
        };
        return true;
    }

    public static bool TryReadProvenance(string filePath, out Guid documentId, out Guid? projectId)
    {
        var binding = ReadBinding(filePath);
        documentId = binding?.DocumentId ?? Guid.Empty;
        projectId = binding?.ProjectId;
        if (documentId != Guid.Empty)
        {
            return true;
        }

        projectId = null;
        return TryReadUnverifiedIdentity(filePath, out documentId);
    }

    public static bool IsControlledWorkspacePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        try
        {
            var workspaceRoot = Path.GetFullPath(workspaceRootResolver())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(filePath);
            return fullPath.StartsWith(workspaceRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
        {
            return false;
        }
    }

    // A stream may travel with a copied/packed CAD file. It is provenance only, not a binding.
    public static bool TryReadUnverifiedIdentity(string filePath, out Guid documentId)
    {
        documentId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            using (var handle = CreateFile(
                filePath + StreamName,
                GenericRead,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    return false;
                }

                using (var reader = new StreamReader(new FileStream(handle, FileAccess.Read), Encoding.ASCII, false))
                {
                    return Guid.TryParse(reader.ReadToEnd()?.Trim(), out documentId) && documentId != Guid.Empty;
                }
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    public static bool TryWrite(string filePath, Guid documentId, Guid? projectId = null)
    {
        return TryWriteBinding(filePath, documentId, projectId, null, null, null, null);
    }

    public static bool TryWriteControlledVersion(
        string filePath,
        Guid documentId,
        Guid? projectId,
        Guid versionId,
        string revision,
        string sha256,
        long fileLength)
    {
        return TryWriteBinding(filePath, documentId, projectId, versionId, revision, sha256, fileLength);
    }

    private static bool TryWriteBinding(
        string filePath,
        Guid documentId,
        Guid? projectId,
        Guid? versionId,
        string revision,
        string sha256,
        long? fileLength)
    {
        if (documentId == Guid.Empty || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;
        lock (bindingSync)
        {
            string temporaryPath = null;
            try
            {
                var previous = ReadBinding(filePath);
                var retainedProjectId = projectId ?? (previous?.DocumentId == documentId ? previous.ProjectId : null);
                var retainedVersionId = versionId ?? (previous?.DocumentId == documentId ? previous.VersionId : null);
                var retainedRevision = revision ?? (previous?.DocumentId == documentId ? previous.Revision : null);
                var retainedSha256 = sha256 ?? (previous?.DocumentId == documentId ? previous.Sha256 : null);
                var retainedFileLength = fileLength ?? (previous?.DocumentId == documentId ? previous.FileLength : null);
                if (previous?.DocumentId == documentId
                    && previous.ProjectId == retainedProjectId
                    && previous.VersionId == retainedVersionId
                    && string.Equals(previous.Revision ?? string.Empty, retainedRevision ?? string.Empty, StringComparison.Ordinal)
                    && string.Equals(previous.Sha256 ?? string.Empty, retainedSha256 ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                    && previous.FileLength == retainedFileLength)
                    return true;
                var binding = new DocumentBinding
                {
                    SchemaVersion = 1,
                    FullPath = Path.GetFullPath(filePath),
                    MachineName = Environment.MachineName,
                    DocumentId = documentId,
                    ProjectId = retainedProjectId,
                    VersionId = retainedVersionId,
                    Revision = retainedRevision,
                    Sha256 = retainedSha256,
                    FileLength = retainedFileLength
                };
                Directory.CreateDirectory(bindingDirectory);
                var path = BindingPath(filePath);
                temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporaryPath, new JavaScriptSerializer().Serialize(binding), Encoding.UTF8);
                if (File.Exists(path)) File.Replace(temporaryPath, path, null);
                else File.Move(temporaryPath, path);
                TryWriteIdentityStream(filePath, documentId);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
            {
                return false;
            }
            finally
            {
                if (temporaryPath != null && File.Exists(temporaryPath))
                {
                    try { File.Delete(temporaryPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }
    }

    private static DocumentBinding ReadBinding(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;
        try
        {
            var path = BindingPath(filePath);
            if (!File.Exists(path)) return null;
            var binding = new JavaScriptSerializer().Deserialize<DocumentBinding>(File.ReadAllText(path, Encoding.UTF8));
            return binding != null && binding.SchemaVersion == 1 && binding.DocumentId != Guid.Empty
                && string.Equals(binding.FullPath, Path.GetFullPath(filePath), StringComparison.OrdinalIgnoreCase)
                && string.Equals(binding.MachineName, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                    ? binding : null;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException || e is InvalidOperationException)
        {
            return null;
        }
    }

    private static string BindingPath(string filePath)
    {
        using (var hash = SHA256.Create())
        {
            var key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(filePath).ToUpperInvariant()))).Replace("-", string.Empty);
            return Path.Combine(bindingDirectory, key + ".json");
        }
    }

    private sealed class DocumentBinding
    {
        public int SchemaVersion { get; set; }
        public string FullPath { get; set; }
        public string MachineName { get; set; }
        public Guid DocumentId { get; set; }
        public Guid? ProjectId { get; set; }
        public Guid? VersionId { get; set; }
        public string Revision { get; set; }
        public string Sha256 { get; set; }
        public long? FileLength { get; set; }
    }

    private static bool TryWriteIdentityStream(string filePath, Guid documentId)
    {
        if (documentId == Guid.Empty || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        FileAttributes? originalAttributes = null;
        try
        {
            originalAttributes = File.GetAttributes(filePath);
            if ((originalAttributes.Value & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(filePath, originalAttributes.Value & ~FileAttributes.ReadOnly);
            }

            using (var handle = CreateFile(
                filePath + StreamName,
                GenericWrite,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                CreateAlways,
                0,
                IntPtr.Zero))
            {
                if (handle.IsInvalid)
                {
                    return false;
                }

                using (var writer = new StreamWriter(new FileStream(handle, FileAccess.Write), Encoding.ASCII))
                {
                    writer.Write(documentId.ToString("D"));
                }
            }
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
        finally
        {
            if (originalAttributes.HasValue && File.Exists(filePath))
            {
                try
                {
                    File.SetAttributes(filePath, originalAttributes.Value);
                }
                catch
                {
                    // The identity stream is optional on file systems without writable attributes.
                }
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}

internal sealed class PdmControlledFileMetadata
{
    public Guid DocumentId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? VersionId { get; set; }
    public string Revision { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long? FileLength { get; set; }
}
