using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private static bool UsesDeferredComponentPath(swComponentSuppressionState_e state) =>
        state is swComponentSuppressionState_e.swComponentSuppressed
            or swComponentSuppressionState_e.swComponentLightweight or swComponentSuppressionState_e.swComponentFullyLightweight;

    private sealed class WorkingAssemblyReferenceScope
    {
        public ControlledOpenFileDto Owner;
        public IComponent2[] Components;
        public string Key;
        public string Configuration;
        public string[] HistoricalMissingPaths = Array.Empty<string>();
        public IDictionary<string, ArchivedMissingOccurrence[]> ArchivedOccurrences;
    }

    private sealed class WorkingAssemblyReferenceProof
    {
        public Guid OwnerId;
        public readonly HashSet<string> RetainedMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, Guid> DeferredReferences = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
    }

    private static WorkingAssemblyReferenceScope WorkingReferenceScope(ControlledOpenFileDto owner,
        string configuration, IComponent2[] components)
    {
        // Evidence belongs to the actual occurrence, including its configuration and
        // multiplicity. Never reuse it for another instance solely by document id.
        string Field(string value) => (value ?? string.Empty).Length + ":" + value;
        var rows = components.Select(component => Field(component.GetPathName()) + Field(component.Name2)
            + Field(component.ReferencedConfiguration) + Field(component.IsVirtual.ToString())
            + Field(component.GetSuppression().ToString())
            + Field((component.GetModelDoc2() as IModelDoc2)?.GetPathName())).OrderBy(value => value, StringComparer.Ordinal);
        return new WorkingAssemblyReferenceScope { Owner = owner, Components = components, Configuration = configuration,
            Key = owner.DocumentId + ":" + owner.VersionId + ":" + Field(configuration) + string.Concat(rows.Select(Field)) };
    }

    private async Task<ControlledOpenManifestDto> VerifyWorkingAssemblyReferencesAsync(string directory,
        ControlledOpenManifestDto manifest, WorkingAssemblyReferenceScope scope, ISet<Guid> editableAssemblyIds,
        IReadOnlyDictionary<Guid, DocumentDto> documents,
        IReadOnlyDictionary<Guid, ControlledOpenFileDto> latestFiles, WorkingAssemblyReferenceProof proof,
        IDictionary<Guid, HashSet<string>> archivedReferenceCache)
    {
        // Evidence for an archived reference does not depend on the edit session.
        // Adopting new references still requires the existing edit eligibility.
        var files = manifest.Files.ToList();
        var id = scope.Owner.DocumentId;
        {
            var owner = files.Single(file => file.DocumentId == id);
            var components = scope.Components;
            if (latestFiles.TryGetValue(id, out var exactOwner) && exactOwner.VersionId == owner.VersionId)
                await VerifyArchivedMissingOccurrencesAsync(scope, exactOwner, files, proof, archivedReferenceCache);
            foreach (var component in components)
            {
                if (component.IsVirtual) continue;
                var path = component.GetPathName();
                if (proof.RetainedMissing.Contains(path)) continue;
                if (files.Any(file => PathsEqual(path, Path.Combine(directory, file.RelativePath ?? file.FileName)))) continue;
                var name = Path.GetFileName(path);
                var candidates = files.Where(file => string.Equals(file.FileName, name, StringComparison.OrdinalIgnoreCase)).ToArray();
                // A suppressed, unavailable reference can predate the indexed tree.
                // A managed, unloaded reference can also report its old path.
                if (Path.IsPathRooted(path) && !File.Exists(path) && IsNativeModelReference(path)
                    && !IsSolidWorksTemporaryVirtualComponentPath(path) && !NativeArchiveManifest.IsInterconnectCachePath(path)
                    && latestFiles.TryGetValue(id, out var storedOwner) && storedOwner.VersionId == owner.VersionId)
                {
                    var state = (swComponentSuppressionState_e)component.GetSuppression();
                    var managedDeferred = candidates.Length == 1 && UsesDeferredComponentPath(state);
                    if (managedDeferred && component.GetModelDoc2() == null)
                    {
                        if (!archivedReferenceCache.TryGetValue(id, out var archivedReferences))
                            archivedReferenceCache[id] = archivedReferences = await ReadArchivedDirectReferencesAsync(storedOwner);
                        if (archivedReferences.Contains(path))
                        {
                            var child = candidates[0];
                            var local = ControlledReferenceMaterializer.LocalPath(directory, child);
                            if (!WorkingReuseBindingMatches(local, manifest.ProjectId, child)
                                || !ControlledReferenceMaterializer.Matches(local, child))
                                throw new InvalidOperationException("已存档引用的受控副本需要核对，尚未获取装配体权限：" + name);
                            var aliases = proof.DeferredReferences;
                            if (!aliases.ContainsKey(path))
                                LogOperation("获取装配权限已核对未加载的原有受控引用：" + name);
                            aliases[path] = child.DocumentId;
                            continue;
                        }
                    }
                }
                if (!editableAssemblyIds.Contains(id)) continue;
                if (DocumentKindFromPath(path) != CadDocumentKind.Part)
                    throw new InvalidOperationException("当前新增子装配尚未纳入原工作基准。已保留装配关系，请先核对该子装配，再获取所属装配体权限：" + Path.GetFileName(path));
                if (!PdmDocumentIdentityStore.TryReadLocalMetadata(path, out var binding) || binding.ProjectId != manifest.ProjectId
                    || !binding.VersionId.HasValue || !documents.TryGetValue(binding.DocumentId, out var document)
                    || document.ProjectId != manifest.ProjectId || document.Kind != (int)CadDocumentKind.Part
                    || !PathsEqual(path, Path.Combine(directory, Path.GetFileName(path)))
                    || !string.Equals(document.FileName, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("新增零件的项目身份或受控位置无法核对，未替换文件或获取权限：" + Path.GetFileName(path)
                        + "。请从当前PLM项目工作区取回该零件后再装配。");
                var latest = (await apiClient.GetVersionsAsync(binding.DocumentId, lifetime.Token, 1)).FirstOrDefault();
                if (latest == null || latest.Id != binding.VersionId || latest.ReferenceSnapshot?.DocumentId != binding.DocumentId
                    || latest.ReferenceSnapshot.Kind != (int)CadDocumentKind.Part
                    || !string.Equals(latest.ReferenceSnapshot.FileName, document.FileName, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("新增零件与PLM当前版本不一致，装配关系和文件已保留：" + Path.GetFileName(path));
                var model = component.GetModelDoc2() as IModelDoc2;
                var configuration = component.ReferencedConfiguration;
                if (string.IsNullOrWhiteSpace(configuration) || model == null || !PathsEqual(model.GetPathName(), path)
                    || model.GetSaveFlag() && !model.IsOpenedReadOnly() || model.GetConfigurationByName(configuration) == null)
                    throw new InvalidOperationException("新增零件的加载来源、配置或修改状态尚未核对，已保留当前装配：" + Path.GetFileName(path));
                var source = new ControlledOpenFileDto { DocumentId = binding.DocumentId, VersionId = binding.VersionId.Value,
                    FileName = Path.GetFileName(path), RelativePath = Path.GetFileName(path), Configuration = configuration,
                    Sha256 = binding.Sha256, FileLength = binding.FileLength ?? 0, Revision = binding.Revision };
                var stored = new ControlledOpenFileDto { DocumentId = source.DocumentId, VersionId = latest.Id, Sha256 = latest.Sha256, FileLength = latest.FileLength };
                if (!WorkingReuseBindingMatches(path, manifest.ProjectId, source) || !ControlledReferenceMaterializer.Matches(path, source)
                    || !WorkingReuseFileBytesMatch(source, stored) && !WorkingReuseStoredSourceVersionMatches(source, stored, latest))
                    throw new InvalidOperationException("新增零件内容与已存档版本不符，未扩大编辑权限，当前装配已保留：" + source.FileName);
                files.Add(source);
                LogOperation("已核对获取前新增的受控零件 file=" + source.FileName + " version=" + source.VersionId + " childCheckout=0");
            }
        }
        return files.Count == manifest.Files.Count ? manifest : WithWorkingFiles(manifest, files);
    }

    private Func<ISldWorks, string, string, string, System.Threading.CancellationToken, Task<ArchivedMissingOccurrence[]>> readArchivedOccurrences
        = ArchiveNativeProcessWorkspace.ReadSavedOccurrencesAsync;

    private async Task VerifyArchivedMissingOccurrencesAsync(WorkingAssemblyReferenceScope scope, ControlledOpenFileDto owner,
        IReadOnlyList<ControlledOpenFileDto> files, WorkingAssemblyReferenceProof proof, IDictionary<Guid, HashSet<string>> directReferenceCache)
    {
        var components = scope.Components;
        var unknown = new HashSet<string>(components.Where(component => !component.IsVirtual && component.GetModelDoc2() == null
            && component.GetSuppression() == (int)swComponentSuppressionState_e.swComponentSuppressed).Select(component => component.GetPathName())
            .Where(path => !string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) && IsNativeModelReference(path)
                && !IsSolidWorksTemporaryVirtualComponentPath(path) && !NativeArchiveManifest.IsInterconnectCachePath(path)
                && !File.Exists(path) && !files.Any(file => string.Equals(file.FileName, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase))),
            StringComparer.OrdinalIgnoreCase);
        if (unknown.Count == 0 && scope.HistoricalMissingPaths.Length == 0) return;
        string canonical = null;
        if (!directReferenceCache.TryGetValue(owner.DocumentId, out var direct))
        {
            canonical = await DownloadArchivedReferenceBaselineAsync(owner);
            directReferenceCache[owner.DocumentId] = direct = ReadArchivedDirectReferencePaths(canonical, owner.FileName);
        }
        if (!unknown.IsSubsetOf(direct))
            throw new InvalidOperationException("当前装配包含存档原件没有的缺失引用，未获取权限：" + owner.FileName + " → "
                + unknown.First(path => !direct.Contains(path)));
        // An ancestor warning may refer to a descendant's omission. Read only the actual owner.
        unknown.UnionWith(scope.HistoricalMissingPaths);
        unknown.IntersectWith(direct);
        if (unknown.Count == 0) return;
        var key = owner.VersionId + "|" + scope.Configuration;
        var cache = scope.ArchivedOccurrences ?? new Dictionary<string, ArchivedMissingOccurrence[]>(StringComparer.OrdinalIgnoreCase);
        if (!cache.TryGetValue(key, out var original))
        {
            canonical ??= await DownloadArchivedReferenceBaselineAsync(owner);
            cache[key] = original = await readArchivedOccurrences(application, canonical, scope.Configuration, owner.Sha256, lifetime.Token);
        }
        foreach (var path in unknown)
        {
            var matches = original.Where(row => PathsEqual(row.Path, path)).ToArray();
            if (matches.Length == 0 && !components.Any(component => PathsEqual(component.GetPathName(), path))) continue;
            if (!ArchivedMissingOccurrencesMatch(path, components, matches))
                throw new InvalidOperationException("历史缺件的实际实例、配置或抑制状态与存档原件不符，未获取权限：" + owner.FileName + " → " + path);
            proof.RetainedMissing.Add(path);
            LogOperation("获取装配权限已按父版本与配置核对缺件实例：" + owner.FileName + " → " + path + "，实例数 " + matches.Length);
        }
    }

    private static bool WorkingReuseChildrenAreKnownParts(IEnumerable<DocumentReferenceNodeDto> expected,
        IEnumerable<CadTreeNode> actual, IEnumerable<ControlledOpenFileDto> files)
    {
        var expectedKeys = new HashSet<string>(expected.Select(child => WorkingReuseReferenceKey(child.FileName, child.Configuration,
            child.Status == (int)CadReferenceStatus.Virtual)));
        var knownParts = new HashSet<string>(files.Where(file => DocumentKindFromPath(file.FileName) == CadDocumentKind.Part)
            .Select(file => WorkingReuseReferenceKey(file.FileName, file.Configuration, false)));
        return actual.All(child => expectedKeys.Contains(WorkingReuseReferenceKey(child.FileName, child.Configuration, child.Status == CadReferenceStatus.Virtual))
            || child.Status != CadReferenceStatus.Virtual && knownParts.Contains(WorkingReuseReferenceKey(child.FileName, child.Configuration, false)));
    }
}
