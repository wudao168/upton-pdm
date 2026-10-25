using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class ReleaseBoundSourceReader
{
    public static string DrawingModel(ReleasePackage package, DocumentVersion version, IEnumerable<Guid> modelIds, string fallback)
    {
        var models = modelIds.ToHashSet();
        var names = ProductionDrawingQuantities.FrozenItems(package)
            .Where(item => item.SourceDocumentId.HasValue && models.Contains(item.SourceDocumentId.Value))
            .Select(item => item.Specification?.Trim()).Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var model = names.Length == 1 ? names[0] : version.PropertySnapshot
            .FirstOrDefault(item => item.Key.Split('/').Last().Equals("型号", StringComparison.OrdinalIgnoreCase)).Value;
        return string.IsNullOrWhiteSpace(model) ? fallback : model.Trim();
    }

    public static IEnumerable<Guid> ModelDocumentIds(DocumentReferenceNode node)
    {
        if (node.Kind is DocumentKind.Part or DocumentKind.Assembly && node.DocumentId.HasValue)
        {
            yield return node.DocumentId.Value;
            yield break;
        }
        foreach (var child in node.Children) foreach (var id in ModelDocumentIds(child)) yield return id;
    }

    public static string FileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', ':', '\0', '<', '>', '"', '|', '?', '*']) >= 0
            || name.Any(char.IsControl)) throw new PdmRuleException("发布源图档的文件名无效，请核对存档版本的文件信息；系统不会改名或忽略该文件。");
        return name;
    }
    public static async Task<IReadOnlyList<ReleasePreviewSource>> ReadAsync(IPdmRepository repository,
        ReleasePackage package, bool previewsOnly, CancellationToken ct)
    {
        var result = new List<ReleasePreviewSource>();
        var nativeIndexes = await repository.ListNativeReferenceIndexesAsync(package.ProjectId, ct);
        foreach (var binding in package.VersionBindings ?? [])
        {
            if (previewsOnly && !binding.Publish) continue;
            var document = await repository.FindDocumentAsync(binding.DocumentId, ct)
                ?? throw new PdmRuleException("发布绑定的图档不存在。");
            if (previewsOnly && document.Kind is not (DocumentKind.Part or DocumentKind.Drawing)) continue;
            var history = await repository.ListDocumentVersionsAsync(binding.DocumentId, ct);
            var original = history.FirstOrDefault(version => version.Id == binding.VersionId);
            if (original is null || original.Sha256 != binding.Sha256)
                throw new PdmConflictException("发布绑定的源版本内容不一致，已停止转图。");
            var source = package.State == ReleasePackageState.Published
                ? history.FirstOrDefault(version => version.ReleasePackageId == package.Id && version.SourceVersionId == binding.VersionId) ?? original
                : original;
            if (package.State == ReleasePackageState.Published && binding.Publish && source.Status != DocumentVersionStatus.Released)
                throw new PdmConflictException("发布图档尚无对应正式版本，不能标记资料齐套。");
            source.PropertySnapshot.TryGetValue("SourceFileSha256", out var originalHash);
            var independent = ArchiveSafety.NativeCopy(nativeIndexes, document.Id, source);
            result.Add(new(document.Id, source.Id, document.DrawingNumber, FileName(string.IsNullOrWhiteSpace(binding.FileName) ? document.FileName : binding.FileName),
                document.Kind, independent?.RelativePath ?? source.StorageRelativePath, independent?.Length ?? source.FileLength, independent?.Sha256 ?? source.Sha256,
                source.Status == DocumentVersionStatus.Released || string.IsNullOrWhiteSpace(originalHash) ? source.Sha256 : originalHash,
                source.Status == DocumentVersionStatus.Released ? source.Revision.Display : source.Revision.Release().Display,
                source.Status == DocumentVersionStatus.Released));
        }
        return result;
    }
}
