using System.Security.Cryptography;
using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class AtomicReleasePackagePublisher : IReleasePackagePublisher
{
    private static readonly HashSet<string> NativeExtensions = new(StringComparer.OrdinalIgnoreCase) { ".SLDPRT", ".SLDASM", ".SLDDRW" };
    private static readonly HashSet<string> RetiredReleaseExtensions = new(StringComparer.OrdinalIgnoreCase) { ".DWG" };
    private readonly TimeProvider timeProvider;
    private readonly IServerPreviewConverter previewConverter;

    public AtomicReleasePackagePublisher(TimeProvider timeProvider, IServerPreviewConverter previewConverter)
    {
        this.timeProvider = timeProvider;
        this.previewConverter = previewConverter;
    }

    public AtomicReleasePackagePublisher(TimeProvider timeProvider)
        : this(timeProvider, new MissingServerPreviewConverter())
    {
    }

    public async Task PrepareAsync(ReleasePackage package, Project project, CancellationToken cancellationToken)
    {
        var vaultRoot = StorageLocationPolicy.Normalize(project.VaultLocation);
        var stagingDirectory = StorageLocationPolicy.ResolveUnder(vaultRoot, Path.Combine(".release-staging", package.Number));
        Directory.CreateDirectory(stagingDirectory);
        var standard = package.StandardBomVersionId.HasValue
            ? package.StandardBomSnapshot.ToArray()
            : package.MechanicalBomSnapshot.Where(item => item.Kind == BomKind.Standard).ToArray();
        var nonStandard = package.NonStandardBomVersionId.HasValue
            ? package.NonStandardBomSnapshot.ToArray()
            : package.MechanicalBomSnapshot.Where(item => item.Kind is BomKind.NonStandard or BomKind.Mechanical).ToArray();
        static BomItem[] ReleaseQuantities(IEnumerable<BomItem> items, int multiplier) => items
            .Where(item => !item.IsManuallyExcluded && !item.IsReleaseExcluded && !item.IsPendingRemoval)
            .Select(item => item with { Quantity = item.Quantity * Math.Max(1, multiplier) })
            .ToArray();
        standard = ReleaseQuantities(standard, package.WholeSetMultiplier);
        nonStandard = ReleaseQuantities(nonStandard, package.WholeSetMultiplier);
        var electrical = ReleaseQuantities(package.ElectricalBomSnapshot, package.WholeSetMultiplier);
        switch (package.Scope)
        {
            case ReleaseScope.StandardLongLead:
                await File.WriteAllBytesAsync(Path.Combine(stagingDirectory, "long-lead-standard-parts-bom.xlsx"), BomWorkbook.Write(standard), cancellationToken);
                break;
            case ReleaseScope.StandardFormal:
            case ReleaseScope.StandardSupplement:
                await File.WriteAllBytesAsync(Path.Combine(stagingDirectory, "standard-parts-bom.xlsx"), BomWorkbook.Write(standard), cancellationToken);
                break;
            case ReleaseScope.ElectricalFormal:
            case ReleaseScope.ElectricalSupplement:
                await File.WriteAllBytesAsync(Path.Combine(stagingDirectory, "electrical-bom.xlsx"), BomWorkbook.Write(electrical), cancellationToken);
                break;
            case ReleaseScope.NonStandardWithDrawing:
                await File.WriteAllBytesAsync(Path.Combine(stagingDirectory, "nonstandard-parts-bom.xlsx"), BomWorkbook.Write(nonStandard), cancellationToken);
                break;
            default:
                await File.WriteAllBytesAsync(Path.Combine(stagingDirectory, "standard-parts-bom.xlsx"), BomWorkbook.Write(standard), cancellationToken);
                await File.WriteAllBytesAsync(Path.Combine(stagingDirectory, "nonstandard-parts-bom.xlsx"), BomWorkbook.Write(nonStandard), cancellationToken);
                await File.WriteAllBytesAsync(Path.Combine(stagingDirectory, "electrical-bom.xlsx"), BomWorkbook.Write(electrical), cancellationToken);
                break;
        }
    }

    public Task DiscardDraftAsync(ReleasePackage package, Project project, CancellationToken cancellationToken)
    {
        var vaultRoot = StorageLocationPolicy.Normalize(project.VaultLocation);
        var stagingDirectory = StorageLocationPolicy.ResolveUnder(vaultRoot, Path.Combine(".release-staging", package.Number));
        if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, true);
        return Task.CompletedTask;
    }

    public Task ValidateAsync(ReleasePackage package, Project project, CancellationToken cancellationToken)
    {
        var stagingDirectory = StorageLocationPolicy.ResolveUnder(StorageLocationPolicy.Normalize(project.VaultLocation), Path.Combine(".release-staging", package.Number));
        if (!Directory.Exists(stagingDirectory)) throw new PdmRuleException("发布包暂存目录不存在，请重新准备发布包。");
        ValidateFiles(
            package.Scope,
            Directory.GetFiles(stagingDirectory, "*", SearchOption.AllDirectories),
            [],
            new Dictionary<Guid, DocumentPreviewArtifact>());
        return Task.CompletedTask;
    }

    public async Task<ReleasePublication> PublishAsync(
        ReleasePackage package,
        Project project,
        IReadOnlyList<ReleasePreviewSource> previewSources,
        CancellationToken cancellationToken)
    {
        var vaultRoot = StorageLocationPolicy.Normalize(project.VaultLocation);
        var releaseRoot = StorageLocationPolicy.Normalize(project.ReleaseLocation);
        var stagingDirectory = StorageLocationPolicy.ResolveUnder(vaultRoot, Path.Combine(".release-staging", package.Number));
        if (!Directory.Exists(stagingDirectory))
        {
            throw new PdmRuleException($"发布暂存目录不存在：{stagingDirectory}");
        }

        var previews = previewSources.Count == 0
            ? new Dictionary<Guid, DocumentPreviewArtifact>()
            : new Dictionary<Guid, DocumentPreviewArtifact>(await previewConverter.GenerateAsync(package, project, previewSources, stagingDirectory, cancellationToken));
        var sourceFiles = Directory.GetFiles(stagingDirectory, "*", SearchOption.AllDirectories);
        ValidateFiles(package.Scope, sourceFiles, previewSources, previews);

        Directory.CreateDirectory(releaseRoot);
        var finalDirectory = StorageLocationPolicy.ResolveUnder(releaseRoot, package.Number);
        if (Directory.Exists(finalDirectory))
        {
            var existingManifest = Path.Combine(finalDirectory, "manifest.json");
            if (File.Exists(existingManifest) && (await File.ReadAllTextAsync(existingManifest, cancellationToken)).Contains(package.Id.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return new ReleasePublication(finalDirectory, previews);
            }

            throw new PdmConflictException("生产目录已存在同名但内容不同的发布包。 ");
        }

        var temporaryDirectory = StorageLocationPolicy.ResolveUnder(releaseRoot, $".{package.Number}.publishing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            foreach (var sourceFile in sourceFiles)
            {
                var relative = Path.GetRelativePath(stagingDirectory, sourceFile);
                var target = StorageLocationPolicy.ResolveUnder(temporaryDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(sourceFile, target, false);
            }

            var manifest = new
            {
                package.Id,
                package.Number,
                package.ProjectId,
                package.ReferenceSnapshotId,
                package.Scope,
                package.WorkflowCode,
                package.WorkflowVersion,
                package.WholeSetMultiplier,
                package.CreatesManufacturingBaseline,
                package.LocksDocuments,
                package.SelectedBomItemIds,
                package.ChangeNumber,
                package.ChangeReason,
                package.EffectiveSerialFrom,
                package.EffectiveSerialTo,
                package.StandardBomVersionId,
                package.NonStandardBomVersionId,
                package.ElectricalBomVersionId,
                package.StandardBomRevision,
                package.NonStandardBomRevision,
                package.MechanicalBomRevision,
                package.ElectricalBomRevision,
                PublishedAt = timeProvider.GetUtcNow(),
                Files = sourceFiles.Select(path => Path.GetRelativePath(stagingDirectory, path).Replace('\\', '/')).OrderBy(path => path).ToArray()
            };
            await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(temporaryDirectory, "approval.json"), JsonSerializer.Serialize(package.ApprovalTasks, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }), cancellationToken);

            var checksums = new List<string>();
            foreach (var file in Directory.GetFiles(temporaryDirectory, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                await using var input = File.OpenRead(file);
                var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
                checksums.Add($"{sha256}  {Path.GetRelativePath(temporaryDirectory, file).Replace('\\', '/')}");
            }

            await File.WriteAllLinesAsync(Path.Combine(temporaryDirectory, "checksums.sha256"), checksums, cancellationToken);
            Directory.Move(temporaryDirectory, finalDirectory);
            return new ReleasePublication(finalDirectory, previews);
        }
        catch
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, true);
            }

            throw;
        }
    }

    private static void RequireFile(IEnumerable<string> files, Func<string, bool> predicate, string description)
    {
        if (!files.Any(predicate))
        {
            throw new PdmRuleException($"发布暂存目录缺少{description}。 ");
        }
    }

    private static void ValidateFiles(
        ReleaseScope scope,
        IReadOnlyList<string> sourceFiles,
        IReadOnlyList<ReleasePreviewSource> previewSources,
        IReadOnlyDictionary<Guid, DocumentPreviewArtifact> previews)
    {
        if (sourceFiles.Any(path => NativeExtensions.Contains(Path.GetExtension(path))))
            throw new PdmRuleException("生产发布包不能包含SolidWorks源文件。 ");
        if (sourceFiles.Any(path => RetiredReleaseExtensions.Contains(Path.GetExtension(path))))
            throw new PdmRuleException("新发布包不能包含DWG文件；历史DWG保留在原位置且不会自动删除。");
        foreach (var source in previewSources)
        {
            if (!previews.TryGetValue(source.DocumentId, out var preview))
                throw new PdmRuleException($"图档{source.DrawingNumber}缺少服务器生成的发布预览。");
            var requiredFormat = source.Kind == DocumentKind.Drawing ? DocumentPreviewFormat.Pdf : DocumentPreviewFormat.Step;
            if (preview.Format != requiredFormat)
                throw new PdmRuleException($"图档{source.DrawingNumber}的服务器预览格式不正确。");
            var requiredExtension = requiredFormat == DocumentPreviewFormat.Pdf ? ".pdf" : ".step";
            RequireFile(sourceFiles, path => string.Equals(Path.GetExtension(path), requiredExtension, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileNameWithoutExtension(path).Contains(source.DocumentId.ToString("N"), StringComparison.OrdinalIgnoreCase),
                $"{source.DrawingNumber}的{requiredExtension.TrimStart('.').ToUpperInvariant()}文件");
        }
        if (scope == ReleaseScope.StandardLongLead)
            RequireFile(sourceFiles, path => string.Equals(Path.GetFileName(path), "long-lead-standard-parts-bom.xlsx", StringComparison.OrdinalIgnoreCase), "长交期标准件BOM XLSX");
        if (scope is ReleaseScope.LegacyCombined or ReleaseScope.StandardFormal or ReleaseScope.StandardSupplement)
            RequireFile(sourceFiles, path => string.Equals(Path.GetFileName(path), "standard-parts-bom.xlsx", StringComparison.OrdinalIgnoreCase), "标准件BOM XLSX");
        if (scope is ReleaseScope.LegacyCombined or ReleaseScope.NonStandardWithDrawing)
            RequireFile(sourceFiles, path => string.Equals(Path.GetFileName(path), "nonstandard-parts-bom.xlsx", StringComparison.OrdinalIgnoreCase), "非标件BOM XLSX");
        if (scope is ReleaseScope.LegacyCombined or ReleaseScope.ElectricalFormal or ReleaseScope.ElectricalSupplement)
            RequireFile(sourceFiles, path => string.Equals(Path.GetFileName(path), "electrical-bom.xlsx", StringComparison.OrdinalIgnoreCase), "电气BOM XLSX");
    }

    private sealed class MissingServerPreviewConverter : IServerPreviewConverter
    {
        public Task<IReadOnlyDictionary<Guid, DocumentPreviewArtifact>> GenerateAsync(
            ReleasePackage package,
            Project project,
            IReadOnlyList<ReleasePreviewSource> sources,
            string stagingDirectory,
            CancellationToken cancellationToken) =>
            throw new PdmRuleException("服务器未配置SolidWorks发布转换进程，不能生成STEP/PDF。");
    }
}
