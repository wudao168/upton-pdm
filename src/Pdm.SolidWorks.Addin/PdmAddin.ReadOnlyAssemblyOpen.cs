using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using SolidWorks.Interop.sldworks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    // The native document object owns this observation. A later open of the same
    // path does not inherit the previous window's occurrence inventory.
    private static readonly ConditionalWeakTable<IModelDoc2, ReadOnlyAssemblyOpen> readOnlyAssemblyOpens =
        new ConditionalWeakTable<IModelDoc2, ReadOnlyAssemblyOpen>();

    private sealed class ReadOnlyAssemblyOpen
    {
        internal Guid Project, Document, Version;
        internal string Path, Configuration, Hash;
        internal Dictionary<int, OpenedOccurrence> Occurrences;
    }

    private sealed class OpenedOccurrence
    {
        internal string Name, Configuration, SavedPath, ControlledPath;
        internal Guid Document, Version;
        internal bool Virtual, Suppressed;

        internal bool Matches(OpenedOccurrence current) =>
            Document == current.Document && Version == current.Version && Virtual == current.Virtual
            && Suppressed == current.Suppressed && StringComparer.OrdinalIgnoreCase.Equals(Name, current.Name)
            && StringComparer.OrdinalIgnoreCase.Equals(Configuration, current.Configuration)
            && StringComparer.OrdinalIgnoreCase.Equals(ControlledPath, current.ControlledPath)
            && (StringComparer.OrdinalIgnoreCase.Equals(SavedPath, current.SavedPath)
                || ControlledPath != null && PathsEqual(ControlledPath, current.SavedPath));
    }

    private static Dictionary<int, OpenedOccurrence> ReadOpenedOccurrences(IComponent2[] components,
        string directory, ControlledOpenManifestDto manifest)
    {
        var files = manifest.Files.ToDictionary(file => file.FileName, StringComparer.OrdinalIgnoreCase);
        var verified = new HashSet<Guid>();
        var occurrences = new Dictionary<int, OpenedOccurrence>();
        foreach (var component in components)
        {
            var id = component.GetID();
            var occurrence = new OpenedOccurrence { Name = component.Name2, Configuration = component.ReferencedConfiguration,
                SavedPath = component.GetPathName(), Virtual = component.IsVirtual, Suppressed = component.IsSuppressed() };
            if (string.IsNullOrWhiteSpace(occurrence.Name) || occurrences.ContainsKey(id)) return null;
            if (!occurrence.Virtual)
            {
                if (string.IsNullOrWhiteSpace(occurrence.SavedPath) || !Path.IsPathRooted(occurrence.SavedPath)) return null;
                var loaded = component.GetModelDoc2() as IModelDoc2;
                if (files.TryGetValue(Path.GetFileName(occurrence.SavedPath), out var child))
                {
                    occurrence.ControlledPath = ControlledReferenceMaterializer.ManifestPath(directory, child);
                    if (loaded != null && !PathsEqual(loaded.GetPathName(), occurrence.ControlledPath)) return null;
                    if (verified.Add(child.DocumentId) && !WorkingReuseBindingMatches(occurrence.ControlledPath, manifest.ProjectId, child))
                        return null;
                    occurrence.Document = child.DocumentId;
                    occurrence.Version = child.VersionId;
                }
                else if (loaded != null) return null;
            }
            occurrences.Add(id, occurrence);
        }
        return occurrences;
    }

    private void RememberReadOnlyAssemblyOpen(IModelDoc2 owner, string path, ControlledOpenManifestDto manifest)
    {
        try
        {
            if (!(owner is IAssemblyDoc assembly) || manifest.ForEdit || manifest.ResumedDocumentIds.Count != 0
                || manifest.RetainedWorkingHashes.Count != 0 || manifest.RetainedLocalReferences.Count != 0) return;
            readOnlyAssemblyOpens.Remove(owner);
            var root = manifest.Files.Single(file => file.DocumentId == manifest.RootDocumentId);
            var directory = Path.GetDirectoryName(path);
            if (manifest.ProjectId == Guid.Empty || root.VersionId == Guid.Empty || root.VersionId != manifest.RootVersionId
                || !PathsEqual(path, ControlledReferenceMaterializer.ManifestPath(directory, root))
                || !ReferenceEquals(application.ActiveDoc, owner) || !PathsEqual(owner.GetPathName(), path)
                || !owner.IsOpenedReadOnly() || owner.GetSaveFlag() || !WorkingReuseBindingMatches(path, manifest.ProjectId, root)
                || !ControlledReferenceMaterializer.TryReadVerifiedCopyHash(path, root, out var hash)) return;
            var configuration = owner.ConfigurationManager?.ActiveConfiguration?.Name;
            if (string.IsNullOrWhiteSpace(configuration) || !string.IsNullOrWhiteSpace(root.Configuration)
                && !StringComparer.OrdinalIgnoreCase.Equals(configuration, root.Configuration)) return;
            var stamp = owner.GetUpdateStamp();
            var values = assembly.GetComponents(true) as Array;
            var components = values?.OfType<IComponent2>().ToArray();
            if (components == null || components.Length == 0 || components.Length != values.Length) return;
            var occurrences = ReadOpenedOccurrences(components, directory, manifest);
            if (occurrences == null || !ReferenceEquals(application.ActiveDoc, owner) || !owner.IsOpenedReadOnly()
                || !StringComparer.OrdinalIgnoreCase.Equals(configuration, owner.ConfigurationManager?.ActiveConfiguration?.Name)
                || owner.GetSaveFlag() || owner.GetUpdateStamp() != stamp) return;
            readOnlyAssemblyOpens.Add(owner, new ReadOnlyAssemblyOpen { Project = manifest.ProjectId, Document = root.DocumentId,
                Version = root.VersionId, Path = path, Configuration = configuration, Hash = hash, Occurrences = occurrences });
        }
        catch (Exception error) { LogDiagnostic("记录只读总装的直属实例", error); }
    }

    private static bool MatchesReadOnlyAssemblyOpen(IModelDoc2 owner, string path, ControlledOpenManifestDto manifest,
        ControlledOpenFileDto file, string configuration, IComponent2[] components)
    {
        try
        {
            if (owner == null || !readOnlyAssemblyOpens.TryGetValue(owner, out var observed)
                || observed.Project != manifest.ProjectId || observed.Document != file.DocumentId
                || observed.Version != file.VersionId || observed.Document != manifest.RootDocumentId
                || observed.Version != manifest.RootVersionId || !PathsEqual(path, observed.Path)
                || !PathsEqual(path, owner.GetPathName()) || !owner.IsOpenedReadOnly()
                || !StringComparer.OrdinalIgnoreCase.Equals(configuration, observed.Configuration)
                || !StringComparer.OrdinalIgnoreCase.Equals(configuration, owner.ConfigurationManager?.ActiveConfiguration?.Name)
                || components.Length != observed.Occurrences.Count || !WorkingReuseBindingMatches(path, manifest.ProjectId, file)
                || !ControlledReferenceMaterializer.TryReadVerifiedCopyHash(path, file, out var hash)
                || !StringComparer.OrdinalIgnoreCase.Equals(hash, observed.Hash)) return false;
            var stamp = owner.GetUpdateStamp();
            var current = ReadOpenedOccurrences(components, Path.GetDirectoryName(path), manifest);
            return current != null && current.All(pair => observed.Occurrences.TryGetValue(pair.Key, out var previous)
                && previous.Matches(pair.Value)) && owner.IsOpenedReadOnly()
                && StringComparer.OrdinalIgnoreCase.Equals(configuration, owner.ConfigurationManager?.ActiveConfiguration?.Name)
                && owner.GetUpdateStamp() == stamp;
        }
        catch (Exception error) { LogDiagnostic("核对只读总装的直属实例", error); return false; }
    }
}
