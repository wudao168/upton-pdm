using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SolidWorks.Interop.sldworks;

namespace Upton.Pdm.SolidWorks;

public sealed partial class PdmAddin
{
    private async Task<ControlledOpenManifestDto> IncludeVerifiedInsertedPartsAsync(string directory,
        ControlledOpenManifestDto manifest, ISet<Guid> editableAssemblyIds, IReadOnlyDictionary<Guid, DocumentDto> documents)
    {
        if (editableAssemblyIds.Count == 0) return manifest;
        var files = manifest.Files.ToList();
        foreach (var id in editableAssemblyIds)
        {
            var owner = files.Single(file => file.DocumentId == id);
            var loaded = FindLoadedDocument(ControlledReferenceMaterializer.LocalPath(directory, owner));
            if (!(loaded is IAssemblyDoc assembly)) throw new InvalidOperationException("所属装配体尚未加载，未继续获取权限。");
            var raw = assembly.GetComponents(true) as Array;
            if (raw == null) throw new InvalidOperationException("当前新增装配实例尚未读取完整，修改已保留，请稍后重试。");
            foreach (var component in raw.OfType<IComponent2>())
            {
                if (component.IsVirtual) continue;
                var path = component.GetPathName();
                if (files.Any(file => PathsEqual(path, Path.Combine(directory, file.RelativePath ?? file.FileName)))) continue;
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
