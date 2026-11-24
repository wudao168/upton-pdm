using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private void ValidateArchiveToolboxReferences(IReadOnlyList<BatchOperationItem> selected, CancellationToken token, bool allowPendingInsertions)
    {
        var owners = selected.SelectMany(item => EnumerateArchiveNodes(item.Node))
            .Where(node => node.Kind == CadDocumentKind.Assembly && !node.IsStoredImport
                && node.Status != CadReferenceStatus.Virtual).Distinct().ToArray();
        foreach (var owner in owners)
            foreach (var manifest in controlledOpenManifests.Values)
                if (manifest.VerifiedMissingToolboxReferences.TryGetValue(owner.FullPath, out var verified)
                    && owner.DocumentId.HasValue && manifest.Files.Any(file => file.DocumentId == owner.DocumentId.Value)
                    && File.Exists(ArchiveFileReadPath(owner)))
                {
                    token.ThrowIfCancellationRequested();
                    var values = ArchiveReadContext.Read(owner, "复核工作副本的Toolbox缺件",
                        () => application.GetDocumentDependencies2(ArchiveFileReadPath(owner), false, false, false)) as Array;
                    var saved = new HashSet<string>(PdmDependencyEntries(values, owner.FullPath).SelectMany(entry => entry.Value),
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var reference in verified.Where(reference => saved.Contains(reference.SourcePath)))
                        ArchiveMissingReferencePolicy.ApplyToolboxEvidence(owner, reference);
                }
        var parents = owners.Where(node => !node.IsSuppressed && node.Children.Any(IsArchivePart)).ToArray();
        if (parents.Length == 0) return;
        var browser = SolidWorksToolboxPaths.BrowserDirectory(application);
        if (browser == null) return;
        var savedByOwner = new Dictionary<string, IReadOnlyList<KeyValuePair<string, string[]>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var parent in parents)
        {
            token.ThrowIfCancellationRequested();
            var fromToolbox = parent.Children.Where(child => IsArchivePart(child)
                && SolidWorksToolboxPaths.IsBrowserPart(child.FullPath, browser)).ToArray();
            if (fromToolbox.Length == 0 || !File.Exists(parent.FullPath)) continue;
            var readPath = ArchiveFileReadPath(parent);
            if (!savedByOwner.TryGetValue(readPath, out var saved))
            {
                var paths = ArchiveReadContext.Read(parent, "提前核对Toolbox原始引用",
                    () => application.GetDocumentDependencies2(readPath, false, false, false)) as Array;
                savedByOwner[readPath] = saved = PdmDependencyEntries(paths, parent.FullPath);
            }
            var pending = allowPendingInsertions && parent.IsModifiedInSolidWorks && PathsEqual(readPath, parent.FullPath);
            foreach (var child in fromToolbox)
            {
                var instance = Path.GetFileName(child.InstancePath.Replace('\\', '/'));
                var original = FindSavedToolboxReferencePaths(instance, child.Configuration, saved);
                if (child.Status == CadReferenceStatus.Missing
                    || !MatchesSavedToolboxReference(parent.FullPath, child.FullPath, original, saved, token, pending))
                    ArchiveMissingReferencePolicy.WarnToolbox(parent, child, original);
            }
        }
        foreach (var group in owners.GroupBy(owner => owner.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            var evidence = group.SelectMany(owner => owner.ToolboxEvidence).Distinct().ToArray();
            foreach (var owner in group)
                foreach (var reference in evidence) ArchiveMissingReferencePolicy.ApplyToolboxEvidence(owner, reference);
        }
    }

    private void PreserveVerifiedToolboxEvidence(CadTreeNode previous, CadTreeNode current, CancellationToken token)
    {
        var known = ArchiveMissingReferencePolicy.Walk(previous).Where(node => node.ToolboxEvidence.Count > 0)
            .ToLookup(node => node.FullPath, StringComparer.OrdinalIgnoreCase);
        var owners = ArchiveMissingReferencePolicy.Walk(current).Where(node => node.Kind == CadDocumentKind.Assembly
            && !node.IsStoredImport && node.Status != CadReferenceStatus.Virtual && known.Contains(node.FullPath));
        foreach (var group in owners.GroupBy(node => node.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var owner = group.First();
            var path = ArchiveFileReadPath(owner);
            if (!File.Exists(path)) continue;
            var values = ArchiveReadContext.Read(owner, "复核已识别的Toolbox原始引用",
                () => application.GetDocumentDependencies2(path, false, false, false)) as Array;
            var saved = new HashSet<string>(PdmDependencyEntries(values, owner.FullPath).SelectMany(entry => entry.Value),
                StringComparer.OrdinalIgnoreCase);
            var evidence = known[group.Key].Where(prior => prior.DocumentId == owner.DocumentId)
                .SelectMany(prior => prior.ToolboxEvidence).Where(reference => saved.Contains(reference.SourcePath)).Distinct().ToArray();
            foreach (var occurrence in group)
                foreach (var reference in evidence) ArchiveMissingReferencePolicy.ApplyToolboxEvidence(occurrence, reference);
        }
    }

    private void RestoreArchiveToolboxCopyReferences(CadTreeNode owner, string copy, CancellationToken token)
    {
        if (owner.ToolboxEvidence.Count == 0) return;
        var references = ArchiveReadContext.Read(owner, "核对提交副本的Toolbox引用", ReadSavedPaths);
        var replacements = owner.ToolboxEvidence.Where(evidence => !PathsEqual(evidence.ActualPath, evidence.SourcePath)
            && references.Contains(evidence.ActualPath)).GroupBy(evidence => evidence.ActualPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Current: group.Key, Original: group.Select(value => value.SourcePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()))
            .ToArray();
        foreach (var pair in replacements)
        {
            token.ThrowIfCancellationRequested();
            if (pair.Original.Length != 1 || !Path.IsPathRooted(pair.Original[0]) || !IsArchiveNativePartPath(pair.Original[0]))
            {
                PluginDiagnosticLog.Record("Toolbox引用提醒", owner.FileName + "：本机模板对应多个原引用或原引用无法定位，原地址见引用提示。");
                continue;
            }
            var backup = copy + ".toolbox.bak";
            var attributes = File.GetAttributes(copy);
            File.Copy(copy, backup, false);
            try
            {
                File.SetAttributes(backup, attributes & ~FileAttributes.ReadOnly);
                File.SetAttributes(copy, attributes & ~FileAttributes.ReadOnly);
                // Native replacement applies only to this closed submission copy.
                // SolidWorks permits a missing destination of the same file type.
                if (!application.ReplaceReferencedDocument(copy, pair.Current, pair.Original[0]))
                    throw new IOException("SolidWorks未确认提交副本的原引用已保留。");
                var verified = ReadSavedPaths();
                if (verified.Contains(pair.Current) || !verified.Contains(pair.Original[0])
                    || references.Where(path => !PathsEqual(path, pair.Current)).Any(path => !verified.Contains(path)))
                    throw new IOException("提交副本的原引用或其他引用核对未通过。");
                references = verified;
                PluginDiagnosticLog.Record("Toolbox引用核对", owner.FileName + "：提交副本已核对并保留原地址 " + pair.Original[0]);
            }
            catch (Exception error) when (!(error is OperationCanceledException))
            {
                File.SetAttributes(copy, File.GetAttributes(copy) & ~FileAttributes.ReadOnly);
                File.Copy(backup, copy, true);
                LogDiagnostic("Toolbox提交副本的原地址尚待核对：" + owner.FileName, error);
            }
            finally
            {
                File.SetAttributes(copy, attributes);
                try { File.Delete(backup); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                { LogDiagnostic("清理Toolbox提交临时备份：" + owner.FileName, error); }
            }
        }

        HashSet<string> ReadSavedPaths() => new HashSet<string>(PdmDependencyEntries(
            application.GetDocumentDependencies2(copy, false, false, false) as Array, owner.FullPath)
            .SelectMany(entry => entry.Value), StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsArchiveNativePartPath(string path) => string.Equals(Path.GetExtension(path), ".SLDPRT", StringComparison.OrdinalIgnoreCase);

    private static bool IsArchivePart(CadTreeNode node) => node.Kind == CadDocumentKind.Part
        && node.Role == CadReferenceRole.Component && !node.IsSuppressed && !node.IsStoredImport
        && node.Status != CadReferenceStatus.Virtual
        && !string.IsNullOrWhiteSpace(node.FullPath);

    private static string[] FindSavedToolboxReferencePaths(string instance, string configuration,
        IReadOnlyList<KeyValuePair<string, string[]>> saved)
    {
        var separator = instance.LastIndexOf('-');
        var instanceFile = separator >= 0 && int.TryParse(instance.Substring(separator + 1), out _)
            ? instance.Substring(0, separator) : instance;
        string Leaf(string name)
        {
            var file = Path.GetFileName(name);
            return file.EndsWith(".SLDPRT", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(file) : file;
        }
        bool Matches(string name) => string.Equals(Leaf(name), instance, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Leaf(name), instanceFile, StringComparison.OrdinalIgnoreCase);
        string[] Recorded(Func<string, bool> matches) => saved.Where(entry => matches(entry.Key) || entry.Value.Any(matches))
            .SelectMany(entry => entry.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var recorded = Recorded(Matches);
        // The native instance name can be replaced by the template's file name.
        // Generated Toolbox parts often carry their saved file stem as configuration.
        if (!string.IsNullOrWhiteSpace(configuration))
        {
            var configured = Recorded(name => string.Equals(Leaf(name), configuration, StringComparison.OrdinalIgnoreCase));
            if (configured.Length != 0) return recorded.Concat(configured).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        return recorded;
    }

    private static bool MatchesSavedToolboxReference(string ownerPath, string loaded, string[] recorded,
        IReadOnlyList<KeyValuePair<string, string[]>> saved, CancellationToken token, bool allowPendingInsertions)
    {
        if (recorded.Length == 0)
        {
            // Instances can be renamed. An exact saved path still proves a normal library reference.
            if (saved.SelectMany(entry => entry.Value).Any(path => PathsEqual(path, loaded))) return true;
            // A genuinely new instance has no saved entry yet. The final pre-upload check
            // reads its saved submission copy, while known nonmatching references still fail here.
            return allowPendingInsertions;
        }
        if (recorded.Length == 1)
        {
            var original = recorded[0];
            if (PathsEqual(original, loaded)) return true;
            if (string.Equals(Path.GetFileName(original), Path.GetFileName(loaded), StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var source = File.Exists(original) ? original : Path.Combine(Path.GetDirectoryName(ownerPath), Path.GetFileName(original));
                    if (File.Exists(source) && File.Exists(loaded)
                        && new FileInfo(source).Length == new FileInfo(loaded).Length
                        && string.Equals(ArchiveFileFingerprint.Read(source, token), ArchiveFileFingerprint.Read(loaded, token), StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                { PluginDiagnosticLog.Record("Toolbox引用核对", ownerPath + "：" + error); }
            }
        }
        return false;
    }
}
