#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;
using Upton.Pdm.LocalSettings;

namespace Upton.Pdm.SolidWorks;

internal static class PdmDocumentIdentityStore
{
    internal static void ConfigureBrowserReadScope(string root)
    {
        bindingDirectory = Path.Combine(root, "_bindings");
        workspaceRootResolver = () => root;
        isolatedReadScope = true;
    }

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
    private static bool isolatedReadScope;
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
        if (IsControlledWorkspacePath(filePath)) return TryReadLocalMetadata(filePath, out metadata);
        metadata = null;
        return false;
    }

    // A machine/path binding also records the base of a previous source-directory import.
    // Copied identity streams never provide a trusted base version.
    public static bool TryReadLocalMetadata(string filePath, out PdmControlledFileMetadata metadata)
    {
        metadata = null;
        var binding = ReadBinding(filePath);
        if (binding == null || binding.DocumentId == Guid.Empty) return false;
        metadata = new PdmControlledFileMetadata
        {
            DocumentId = binding.DocumentId,
            ProjectId = binding.ProjectId,
            VersionId = binding.VersionId,
            Revision = binding.Revision ?? string.Empty,
            Sha256 = binding.Sha256 ?? string.Empty,
            ResolvedReferenceSha256 = binding.ResolvedReferenceSha256 ?? string.Empty,
            ServerMaterializedAtUtc = binding.ServerMaterializedAtUtc,
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
            var fullPath = Path.GetFullPath(filePath);
            return ManagedWorkspaceRoots(workspaceRootResolver()).Any(root => fullPath.StartsWith(
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
        {
            return false;
        }
    }

    internal static string[] ManagedWorkspaceRoots(string configuredRoot) =>
        (isolatedReadScope ? new[] { configuredRoot } : new[] { configuredRoot,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UPTON PDM", "Workspace"),
            Path.Combine(Path.GetTempPath(), "UPLM", "ManagedWorkspace") })
        .Where(root => !string.IsNullOrWhiteSpace(root)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

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

    internal static bool TryRecoverControlledVersion(string filePath, PdmControlledFileMetadata expected,
        Guid projectId, Guid versionId, string revision, string sha256, long fileLength)
    {
        return expected != null && TryWriteBinding(filePath, expected.DocumentId, projectId, versionId,
            revision, sha256, fileLength, expected);
    }

    internal static bool MatchesResolvedVersion(string path, Guid versionId, string archivedHash, string actualHash)
    {
        return !string.IsNullOrWhiteSpace(actualHash) && !string.IsNullOrWhiteSpace(archivedHash)
            && TryReadLocalMetadata(path, out var metadata) && metadata.VersionId == versionId
            && string.Equals(metadata.Sha256, archivedHash, StringComparison.OrdinalIgnoreCase)
            && string.Equals(metadata.ResolvedReferenceSha256, actualHash, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryRecordResolvedReferences(string path, PdmControlledFileMetadata expected, string actualHash) =>
        expected?.VersionId != null && !string.IsNullOrWhiteSpace(actualHash)
        && TryWriteBinding(path, expected.DocumentId, expected.ProjectId, expected.VersionId, expected.Revision,
            expected.Sha256, expected.FileLength, expected, actualHash);

    internal static bool TryRecordServerMaterialization(string path, DateTime atUtc) =>
        TryReadLocalMetadata(path, out var metadata) && TryWriteBinding(path, metadata.DocumentId, metadata.ProjectId,
            metadata.VersionId, metadata.Revision, metadata.Sha256, metadata.FileLength, metadata,
            metadata.ResolvedReferenceSha256, atUtc);

    internal static void RestoreLocalMetadata(string path, PdmControlledFileMetadata previous)
    {
        if (previous != null)
        {
            if (!TryWriteBinding(path, previous.DocumentId, previous.ProjectId, previous.VersionId, previous.Revision,
                previous.Sha256, previous.FileLength, null, previous.ResolvedReferenceSha256))
                throw new IOException("工作区文件已回滚，但原始版本记录未能恢复：" + Path.GetFileName(path));
            return;
        }
        lock (bindingSync)
        {
            var record = BindingPath(path);
            if (File.Exists(record)) File.Delete(record);
        }
    }

    private static bool TryWriteBinding(
        string filePath,
        Guid documentId,
        Guid? projectId,
        Guid? versionId,
        string revision,
        string sha256,
        long? fileLength,
        PdmControlledFileMetadata expected = null, string resolvedReferenceSha256 = null, DateTime? serverMaterializedAtUtc = null)
    {
        if (documentId == Guid.Empty || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;
        lock (bindingSync)
        {
            string temporaryPath = null;
            Mutex bindingMutex = null;
            var ownsMutex = false;
            try
            {
                // Desktop verification and the add-in write the same per-user binding files.
                bindingMutex = new Mutex(false, @"Local\UPTON-PDM-Binding-" + Path.GetFileNameWithoutExtension(BindingPath(filePath)));
                try { ownsMutex = bindingMutex.WaitOne(TimeSpan.FromSeconds(5)); }
                catch (AbandonedMutexException) { ownsMutex = true; }
                if (!ownsMutex) return false;
                var previous = ReadBinding(filePath);
                if (expected != null && (previous == null || previous.DocumentId != expected.DocumentId
                    || previous.ProjectId != expected.ProjectId || previous.VersionId != expected.VersionId
                    || previous.Revision != expected.Revision || previous.FileLength != expected.FileLength
                    || !string.Equals(previous.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(previous.ResolvedReferenceSha256 ?? string.Empty, expected.ResolvedReferenceSha256 ?? string.Empty, StringComparison.OrdinalIgnoreCase))) return false;
                var retainedProjectId = projectId ?? (previous?.DocumentId == documentId ? previous.ProjectId : null);
                var retainedVersionId = versionId ?? (previous?.DocumentId == documentId ? previous.VersionId : null);
                var retainedRevision = revision ?? (previous?.DocumentId == documentId ? previous.Revision : null);
                var retainedSha256 = sha256 ?? (previous?.DocumentId == documentId ? previous.Sha256 : null);
                var retainedFileLength = fileLength ?? (previous?.DocumentId == documentId ? previous.FileLength : null);
                var retainedResolvedHash = resolvedReferenceSha256
                    ?? (previous?.DocumentId == documentId && previous.VersionId == retainedVersionId
                        && string.Equals(previous.Sha256, retainedSha256, StringComparison.OrdinalIgnoreCase)
                        ? previous.ResolvedReferenceSha256 : null);
                var materializedAt = serverMaterializedAtUtc ?? (previous?.DocumentId == documentId ? previous.ServerMaterializedAtUtc : null);
                if (previous?.DocumentId == documentId
                    && previous.ProjectId == retainedProjectId
                    && previous.VersionId == retainedVersionId
                    && string.Equals(previous.Revision ?? string.Empty, retainedRevision ?? string.Empty, StringComparison.Ordinal)
                    && string.Equals(previous.Sha256 ?? string.Empty, retainedSha256 ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                    && previous.FileLength == retainedFileLength
                    && string.Equals(previous.ResolvedReferenceSha256, retainedResolvedHash, StringComparison.OrdinalIgnoreCase)
                    && previous.ServerMaterializedAtUtc == materializedAt)
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
                    ResolvedReferenceSha256 = retainedResolvedHash,
                    ServerMaterializedAtUtc = materializedAt,
                    FileLength = retainedFileLength
                };
                Directory.CreateDirectory(bindingDirectory);
                var path = BindingPath(filePath);
                temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporaryPath, new JavaScriptSerializer().Serialize(binding), Encoding.UTF8);
                if (File.Exists(path)) File.Replace(temporaryPath, path, null);
                else File.Move(temporaryPath, path);
                if (IsControlledWorkspacePath(filePath)) TryWriteIdentityStream(filePath, documentId);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
            {
                return false;
            }
            finally
            {
                if (ownsMutex) bindingMutex.ReleaseMutex();
                bindingMutex?.Dispose();
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
        public string ResolvedReferenceSha256 { get; set; }
        public DateTime? ServerMaterializedAtUtc { get; set; }
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
    public string ResolvedReferenceSha256 { get; set; } = string.Empty;
    public DateTime? ServerMaterializedAtUtc { get; set; }
    public long? FileLength { get; set; }
}
