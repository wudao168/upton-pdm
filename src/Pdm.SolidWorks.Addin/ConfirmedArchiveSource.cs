using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Upton.Pdm.SolidWorks;

// A submission receipt describes the old disk source separately from the archived
// bytes. It never relabels an unchanged source file as the newly stored version.
internal static class ConfirmedArchiveSource
{
    internal static string UserScope(string server, string user) => string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(user)
        ? null : server + "|" + user.ToLowerInvariant();
    internal sealed class Receipt
    {
        public string Path { get; set; }
        public string Machine { get; set; }
        public string SourceSha256 { get; set; }
        public long SourceLength { get; set; }
        public Guid? SourceBaseVersionId { get; set; }
        public string SourceBaseSha256 { get; set; }
        public string SourceResolvedSha256 { get; set; }
        public Guid DocumentId { get; set; }
        public Guid ProjectId { get; set; }
        public Guid VersionId { get; set; }
        public string StoredSha256 { get; set; }
        public long StoredLength { get; set; }
        public DateTime ConfirmedAtUtc { get; set; }
    }

    private static string ReceiptPath(string root, string scope, string path)
    {
        using var sha = SHA256.Create();
        var key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(scope + "|" + System.IO.Path.GetFullPath(path).ToUpperInvariant()))).Replace("-", "");
        return System.IO.Path.Combine(root, ".uplm", "ConfirmedSources", key + ".json");
    }

    internal static bool Record(string root, string scope, string path, Guid documentId, Guid projectId,
        DocumentVersionDto version, string sourceHash)
    {
        if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(sourceHash) || version == null
            || !File.Exists(path) || !string.Equals(ArchiveFileFingerprint.Read(path), sourceHash, StringComparison.OrdinalIgnoreCase)) return false;
        if (!System.IO.Path.GetFullPath(path).StartsWith(System.IO.Path.Combine(System.IO.Path.GetFullPath(root), "View")
            + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var binding);
        var receipt = new Receipt { Path = System.IO.Path.GetFullPath(path), Machine = Environment.MachineName,
            DocumentId = documentId, ProjectId = projectId, VersionId = version.Id, StoredSha256 = version.Sha256,
            StoredLength = version.FileLength, SourceSha256 = sourceHash, SourceLength = new FileInfo(path).Length,
            SourceBaseVersionId = binding?.VersionId, SourceBaseSha256 = binding?.Sha256, SourceResolvedSha256 = binding?.ResolvedReferenceSha256,
            ConfirmedAtUtc = DateTime.UtcNow };
        var target = ReceiptPath(root, scope, path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
        var pending = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(pending, new JavaScriptSerializer().Serialize(receipt), Encoding.UTF8);
            if (File.Exists(target)) File.Replace(pending, target, null); else File.Move(pending, target);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
        return true;
    }

    internal static bool Matches(string root, string scope, string path, Guid projectId, ControlledOpenFileDto file, string actual)
    {
        if (string.IsNullOrWhiteSpace(scope)) return false;
        try
        {
            var target = ReceiptPath(root, scope, path);
            if (!File.Exists(target)) return false;
            var receipt = new JavaScriptSerializer().Deserialize<Receipt>(File.ReadAllText(target, Encoding.UTF8));
            if (!PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var binding)) return false;
            return receipt != null && string.Equals(receipt.Path, System.IO.Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)
                && receipt.Machine == Environment.MachineName && receipt.ProjectId == projectId && receipt.DocumentId == file.DocumentId
                && receipt.VersionId == file.VersionId && receipt.StoredLength == file.FileLength
                && string.Equals(receipt.StoredSha256, file.Sha256, StringComparison.OrdinalIgnoreCase)
                && binding.VersionId == receipt.SourceBaseVersionId && binding.DocumentId == receipt.DocumentId && binding.ProjectId == receipt.ProjectId
                && string.Equals(binding.Sha256, receipt.SourceBaseSha256, StringComparison.OrdinalIgnoreCase)
                && string.Equals(binding.ResolvedReferenceSha256, receipt.SourceResolvedSha256, StringComparison.OrdinalIgnoreCase)
                && receipt.SourceLength == new FileInfo(path).Length && string.Equals(receipt.SourceSha256, actual, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is InvalidOperationException)
        { return false; }
    }
}
