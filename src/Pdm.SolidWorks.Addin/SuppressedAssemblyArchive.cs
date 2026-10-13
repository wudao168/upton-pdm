using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Upton.Pdm.SolidWorks;

internal static class SuppressedAssemblyArchive
{
    public static bool IsUnexpanded(CadTreeNode node) => node != null
        && node.Kind == CadDocumentKind.Assembly && node.Status == CadReferenceStatus.Suppressed
        && !node.Children.Any(child => child.Kind != CadDocumentKind.Drawing);

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
            || !string.Equals(node.CurrentRevision, version.Revision?.Display, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(node.SuppressedArchiveSha256) || !File.Exists(node.FullPath)) return false;
        using (var stream = new FileStream(node.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var hash = SHA256.Create())
            return string.Equals(BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", ""), node.SuppressedArchiveSha256, StringComparison.OrdinalIgnoreCase);
    }
}
