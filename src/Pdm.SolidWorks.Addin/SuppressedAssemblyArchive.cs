using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Upton.Pdm.SolidWorks;

internal static class SuppressedAssemblyArchive
{
    internal static bool HasCompleteReferences(DocumentReferenceNodeDto node, bool isRoot = true)
    {
        if (node == null) return false;
        if (node.Role == (int)CadReferenceRole.ExternalFileDependency) return true;
        if (node.Kind == (int)CadDocumentKind.Drawing) return true;
        if (node.Status != (int)CadReferenceStatus.Virtual
            && (!node.DocumentId.HasValue || !isRoot && !PdmApiClient.HasResolvedRevision(node.Revision?.Display))) return false;
        return (node.Children ?? new System.Collections.Generic.List<DocumentReferenceNodeDto>()).All(child => HasCompleteReferences(child, false));
    }

    public static bool IsUnexpanded(CadTreeNode node) => node != null
        && node.Kind == CadDocumentKind.Assembly
        && (node.Status == CadReferenceStatus.Suppressed || node.Status == CadReferenceStatus.Lightweight)
        && !node.Children.Any(child => child.Kind != CadDocumentKind.Drawing && child.Role != CadReferenceRole.ExternalFileDependency);

    public static bool MatchesConfiguration(CadTreeNode node, DocumentVersionDto version)
    {
        if (version?.ReferenceSnapshot == null || string.IsNullOrWhiteSpace(node.Configuration)) return false;
        var configuration = version.ReferenceSnapshot.Configuration;
        if (string.IsNullOrWhiteSpace(configuration))
        {
            // Older standalone snapshots omitted the root configuration. Property capture
            // records only the active configuration, so a unique recorded scope is usable.
            var scopes = (version.PropertySnapshot?.Keys.AsEnumerable() ?? Enumerable.Empty<string>())
                .Where(key => key.StartsWith("配置:", StringComparison.Ordinal) && key.IndexOf('/') > 3)
                .Select(key => key.Substring(3, key.IndexOf('/') - 3)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            configuration = scopes.Length == 1 ? scopes[0] : null;
        }
        return string.Equals(configuration, node.Configuration, StringComparison.OrdinalIgnoreCase);
    }

    public static bool CanReuse(CadTreeNode node)
    {
        var version = node?.SuppressedArchiveVersion;
        if (!IsUnexpanded(node) || node.IsModifiedInSolidWorks || node.IsRenamePendingSave || !node.DocumentId.HasValue
            || version?.ReferenceSnapshot?.DocumentId != node.DocumentId
            || !string.Equals(version.ReferenceSnapshot.FileName, node.FileName, StringComparison.OrdinalIgnoreCase)
            || !MatchesConfiguration(node, version)
            || version.ReferenceSnapshot.Children == null
            || !version.ReferenceSnapshot.Children.Any(child => child.Kind != (int)CadDocumentKind.Drawing && child.Role != (int)CadReferenceRole.ExternalFileDependency)
            || !HasCompleteReferences(version.ReferenceSnapshot)
            || !string.Equals(node.CurrentRevision, version.Revision?.Display, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(node.SuppressedArchiveSha256) || !File.Exists(node.FullPath)) return false;
        using (var stream = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var hash = SHA256.Create())
            return string.Equals(BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", ""), node.SuppressedArchiveSha256, StringComparison.OrdinalIgnoreCase);
    }
}
