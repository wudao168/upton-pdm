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
    private readonly IPdmRepository? repository;

    public AtomicReleasePackagePublisher(TimeProvider timeProvider, IServerPreviewConverter previewConverter, IPdmRepository? repository = null)
    {
        this.timeProvider = timeProvider;
        this.previewConverter = previewConverter;
        this.repository = repository;
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
        standard = BomDrawingRevision.ForPackage(standard, package, preparingPublication: true).ToArray();
        nonStandard = BomDrawingRevision.ForPackage(nonStandard, package, preparingPublication: true).ToArray();
        electrical = BomDrawingRevision.ForPackage(electrical, package, preparingPublication: true).ToArray();
        switch (package.Scope)
        {
            case ReleaseScope.StandardLongLead:
                await File.WriteAllBytesAsync(Path.Combine(stagingDirectory, "long-lead-standard-parts-bom.xlsx"), BomWorkbook.Write(standard), cancellationToken);
                break;
            case ReleaseScope.StandardFormal:
                var history = repository is null ? [] : await repository.ListReleasePackagesAsync(package.ProjectId, cancellationToken);
                if (history.Any(previous => previous.Scope == ReleaseScope.StandardLongLead
                    && previous.State is not (ReleasePackageState.Published or ReleasePackageState.Rejected)))
                    throw new PdmRuleException("存在尚未完成的长交期发布，请完成或撤销后再进行正式发布。");
                var prior = BomReleaseAggregation.PriorQuantities(BomReleaseAggregation.PriorLongLeadItems(package, history), standard);
                await File.WriteAllBytesAsync(Path.Combine(stagingDirectory, "standard-parts-bom.xlsx"), BomWorkbook.WriteStandardRelease(standard, prior), cancellationToken);
                break;
            case ReleaseScope.StandardSupplement:
                await File.WriteAllBytesAsync(Path.Combine(stagingDirectory, "standard-parts-bom.xlsx"), BomWorkbook.Write(standard), cancellationToken);
                break;
            case ReleaseScope.NonStandardLongLead:
                await File.WriteAllBytesAsync(Path.Combine(stagingDirectory, "long-lead-nonstandard-parts-bom.xlsx"), BomWorkbook.Write(nonStandard), cancellationToken);
                break;
            case ReleaseScope.ElectricalLongLead:
                await File.WriteAllBytesAsync(Path.Combine(stagingDirectory, "long-lead-electrical-bom.xlsx"), BomWorkbook.Write(electrical), cancellationToken);
                break;
            case ReleaseScope.NonStandardSupplement:
                await File.WriteAllBytesAsync(Path.Combine(stagingDirectory, "nonstandard-parts-bom.xlsx"), BomWorkbook.Write(nonStandard), cancellationToken);
                break;
            case ReleaseScope.ElectricalSupplement:
                await File.WriteAllBytesAsync(Path.Combine(stagingDirectory, "electrical-bom.xlsx"), BomWorkbook.Write(electrical), cancellationToken);
                break;
            case ReleaseScope.ElectricalFormal:
                var electricalHistory = repository is null ? [] : await repository.ListReleasePackagesAsync(package.ProjectId, cancellationToken);
                if (electricalHistory.Any(previous => previous.Scope == ReleaseScope.ElectricalLongLead
                    && previous.State is not (ReleasePackageState.Published or ReleasePackageState.Rejected)))
                    throw new PdmRuleException("存在尚未完成的电气件长交期发布，请完成或撤销后再进行正式发布。");
                var priorElectrical = BomReleaseAggregation.PriorQuantities(BomReleaseAggregation.PriorLongLeadItems(package, electricalHistory), electrical);
                await File.WriteAllBytesAsync(Path.Combine(stagingDirectory, "electrical-bom.xlsx"), BomWorkbook.WriteStandardRelease(electrical, priorElectrical), cancellationToken);
                break;
            case ReleaseScope.NonStandardWithDrawing:
                var nonStandardHistory = repository is null ? [] : await repository.ListReleasePackagesAsync(package.ProjectId, cancellationToken);
                if (nonStandardHistory.Any(previous => previous.Scope == ReleaseScope.NonStandardLongLead
                    && previous.State is not (ReleasePackageState.Published or ReleasePackageState.Rejected)))
                    throw new PdmRuleException("存在尚未完成的非标件长交期发布，请完成或撤销后再进行正式发布。");
                var priorNonStandard = BomReleaseAggregation.PriorQuantities(BomReleaseAggregation.PriorLongLeadItems(package, nonStandardHistory), nonStandard);
                await File.WriteAllBytesAsync(Path.Combine(stagingDirectory, "nonstandard-parts-bom.xlsx"), BomWorkbook.WriteStandardRelease(nonStandard, priorNonStandard), cancellationToken);
                break;
            default:
                await File.WriteAllBytesAsync(Path.Combine(stagingDirectory, "standard-parts-bom.xlsx"), BomWorkbook.Write(standard), cancellationToken);
                await File.WriteAllBytesAsync(Path.Combine(stagingDirectory, "nonstandard-parts-bom.xlsx"), BomWorkbook.Write(nonStandard), cancellationToken);
                await File.WriteAllBytesAsync(Path.Combine(stagingDirectory, "electrical-bom.xlsx"), BomWorkbook.Write(electrical), cancellationToken);
                break;
        }
        var main = repository is null ? project : await repository.FindProjectAsync(project.RootProjectId ?? project.ParentProjectId ?? project.Id, cancellationToken) ?? project;
        var directory = repository is null ? null : await repository.GetOrganizationDirectoryAsync(cancellationToken);
        foreach (var path in Directory.GetFiles(stagingDirectory, "*.xlsx", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(path);
            var title = fileName.Contains("nonstandard", StringComparison.Ordinal) ? "非标件BOM"
                : fileName.Contains("electrical", StringComparison.Ordinal) ? "电气件BOM" : "标准件BOM";
            var metadata = ProjectWorkbookMetadata.ForProject(project, main, directory, title, "发布包版本", package.Number,
                timeProvider.GetUtcNow().ToOffset(TimeSpan.FromHours(8)), "—", "发布冻结BOM");
            metadata = metadata with { Fields = metadata.Fields.Select(field => field.Key == "整套数量"
                ? new KeyValuePair<string, string>(field.Key, $"{Math.Max(1, package.WholeSetMultiplier)}套") : field).ToArray() };
            var content = await File.ReadAllBytesAsync(path, cancellationToken);
            await File.WriteAllBytesAsync(path, ProjectWorkbookHeader.Apply(content, metadata), cancellationToken);
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
        var finalDirectory = StorageLocationPolicy.ResolveUnder(releaseRoot, package.Number);
        if (Directory.Exists(finalDirectory))
            return await RestorePublicationAsync(package, vaultRoot, finalDirectory, previewSources, cancellationToken);
        var stagingDirectory = StorageLocationPolicy.ResolveUnder(vaultRoot, Path.Combine(".release-staging", package.Number));
        if (!Directory.Exists(stagingDirectory))
        {
            throw new PdmRuleException($"发布暂存目录不存在：{stagingDirectory}");
        }

        var publishedAt = package.PublishedAt ?? timeProvider.GetUtcNow();
        var conversionPackage = package with { PublishedAt = publishedAt };
        // Refresh legacy in-review packages as well; the approved full BOM snapshot is unchanged.
        ReleasePublishingProgress.Set(package.Id, "生成BOM发布文件");
        await PrepareAsync(package with { State = ReleasePackageState.Publishing }, project, cancellationToken);

        // 核对审批冻结的工程图，原文件直接作为正式版本和PDF转换来源。
        Dictionary<Guid, FormalDrawingSource> formalDrawings;
        ReleasePublishingProgress.Set(package.Id, "核对审批冻结工程图");
        try
        {
            var workSources = previewSources.Where(source => !source.AlreadyReleased).ToArray();
            formalDrawings = workSources.Length == 0
                ? new Dictionary<Guid, FormalDrawingSource>()
                : new Dictionary<Guid, FormalDrawingSource>(await previewConverter.FinalizeDrawingsAsync(
                    package, project, workSources, stagingDirectory, cancellationToken));
        }
        catch (Exception exception) when (exception is not PdmConflictException && !cancellationToken.IsCancellationRequested)
        {
            throw new ReleaseConversionPendingException(exception.Message, exception);
        }
        var conversionSources = previewSources.Select(source => formalDrawings.TryGetValue(source.DocumentId, out var formal)
            ? source with { StorageRelativePath = formal.StorageRelativePath, FileLength = formal.FileLength, Sha256 = formal.Sha256, SourceSha256 = formal.Sha256 }
            : source).ToArray();
        string? previewError = null;
        var previews = new Dictionary<Guid, DocumentPreviewArtifact>();
        if (repository is not null)
        foreach (var source in conversionSources.Where(source => source.AlreadyReleased))
        {
            var version = await repository.FindDocumentVersionAsync(source.DocumentId, source.SourceVersionId, cancellationToken);
            if (version?.Preview is not { } existing || existing.Format != (source.Kind == DocumentKind.Drawing ? DocumentPreviewFormat.Pdf : DocumentPreviewFormat.Step)
                || !string.Equals(existing.SourceSha256, source.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
            var sourcePath = StorageLocationPolicy.ResolveUnder(vaultRoot, existing.StorageRelativePath);
            if (!await ReleaseDeliveryFiles.MatchesAsync(sourcePath, existing.FileLength, existing.Sha256, cancellationToken)) continue;
            var targetPath = ReleaseDeliveryFiles.PreviewPath(stagingDirectory, existing);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath, true);
            if (!await ReleaseDeliveryFiles.MatchesAsync(targetPath, existing.FileLength, existing.Sha256, cancellationToken)) throw new PdmConflictException("已发布图纸的复制校验失败，未生成新版本。");
            previews[source.DocumentId] = existing;
        }
        try
        {
            var pendingSources = conversionSources.Where(source => !previews.ContainsKey(source.DocumentId)).ToArray();
            if (pendingSources.Length > 0)
                foreach (var preview in await previewConverter.GenerateAsync(conversionPackage, project, pendingSources, stagingDirectory, cancellationToken)) previews[preview.Key] = preview.Value;
        }
        catch (PartialPreviewConversionException exception) when (!cancellationToken.IsCancellationRequested)
        {
            previewError = exception.Message;
            foreach (var preview in exception.Artifacts) previews[preview.Key] = preview.Value;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            previewError = exception.Message;
        }
        var activePreviewFiles = previews.Values.Select(preview =>
        {
            var path = ReleaseDeliveryFiles.PreviewPath(stagingDirectory, preview);
            return File.Exists(path) ? path : Path.Combine(stagingDirectory, "previews", Path.GetFileName(preview.StorageRelativePath));
        }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sourceFiles = Directory.GetFiles(stagingDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !ReleaseDeliveryFiles.IsPreview(path) || activePreviewFiles.Contains(path))
            .Where(path => !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)).ToArray();
        ValidateFiles(package.Scope, sourceFiles, previewSources, previews, requirePreviews: previewError is null);

        Directory.CreateDirectory(releaseRoot);
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
                package.ConfirmedRemovedBomItemIds,
                package.VersionBindings,
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
                Previews = previews,
                FormalDrawings = formalDrawings,
                PreviewError = previewError,
                PublishedAt = publishedAt,
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
            return new ReleasePublication(finalDirectory, previews, previewError, formalDrawings) { PublishedAt = publishedAt };
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

    private async Task<ReleasePublication> RestorePublicationAsync(ReleasePackage package, string vaultRoot, string directory,
        IReadOnlyList<ReleasePreviewSource> sources, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(manifestPath)) throw new PdmConflictException("生产目录已存在，但缺少发布包清单，不能安全恢复发布。");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken));
        var manifest = document.RootElement;
        if (!manifest.TryGetProperty("id", out var id) || !id.TryGetGuid(out var savedId) || savedId != package.Id
            || !manifest.TryGetProperty("projectId", out var projectId) || projectId.GetGuid() != package.ProjectId)
            throw new PdmConflictException("生产目录已存在同名但身份不同的发布包。");
        await ReleaseDeliveryFiles.VerifyChecksumsAsync(directory, cancellationToken);
        var previews = manifest.TryGetProperty("previews", out var previewNode)
            ? previewNode.Deserialize<Dictionary<Guid, DocumentPreviewArtifact>>(ReleaseDeliveryFiles.JsonOptions) ?? [] : [];
        var formal = manifest.TryGetProperty("formalDrawings", out var formalNode)
            ? formalNode.Deserialize<Dictionary<Guid, FormalDrawingSource>>(ReleaseDeliveryFiles.JsonOptions) ?? [] : [];
        foreach (var source in sources)
        {
            // 兼容升级前生成的清单：只复用经过指纹核对的正式版本和正式源图旁清单。
            if (repository is not null && !previews.ContainsKey(source.DocumentId))
            {
                var released = (await repository.ListDocumentVersionsAsync(source.DocumentId, cancellationToken))
                    .FirstOrDefault(version => (version.ReleasePackageId == package.Id || source.AlreadyReleased && version.Id == source.SourceVersionId) && version.Preview is not null);
                if (released?.Preview is not null) previews[source.DocumentId] = released.Preview;
            }
            if (source.Kind == DocumentKind.Drawing && !source.AlreadyReleased && !formal.ContainsKey(source.DocumentId))
            {
                var formalManifest = StorageLocationPolicy.ResolveUnder(vaultRoot, Path.Combine(".release-formal", package.Id.ToString("N"), $"{source.DocumentId:N}.slddrw.json"));
                if (!File.Exists(formalManifest)) throw new PdmConflictException($"工程图{source.DrawingNumber}缺少正式源图清单，不能安全恢复发布。");
                using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(formalManifest, cancellationToken));
                formal[source.DocumentId] = saved.RootElement.GetProperty("formal").Deserialize<FormalDrawingSource>(ReleaseDeliveryFiles.JsonOptions)
                    ?? throw new PdmConflictException("正式源图清单无效，不能安全恢复发布。");
            }
            if (formal.TryGetValue(source.DocumentId, out var drawing)
                && !await ReleaseDeliveryFiles.MatchesAsync(StorageLocationPolicy.ResolveUnder(vaultRoot, drawing.StorageRelativePath), drawing.FileLength, drawing.Sha256, cancellationToken))
                throw new PdmConflictException($"工程图{source.DrawingNumber}的正式源文件缺失或校验失败，已停止恢复发布。");
            if (previews.TryGetValue(source.DocumentId, out var preview))
            {
                var target = ReleaseDeliveryFiles.PreviewPath(directory, preview);
                if (!File.Exists(target)) target = Path.Combine(directory, "previews", Path.GetFileName(preview.StorageRelativePath));
                if (!await ReleaseDeliveryFiles.MatchesAsync(StorageLocationPolicy.ResolveUnder(vaultRoot, preview.StorageRelativePath), preview.FileLength, preview.Sha256, cancellationToken)
                    || !await ReleaseDeliveryFiles.MatchesAsync(target, preview.FileLength, preview.Sha256, cancellationToken))
                    throw new PdmConflictException($"图档{source.DrawingNumber}的预览与生产目录不一致，已停止恢复发布。");
            }
        }
        var error = sources.Any(source => !previews.ContainsKey(source.DocumentId)) ? "发布资料尚未齐套，等待后台补齐预览。" : null;
        return new ReleasePublication(directory, previews, error, formal)
        {
            PublishedAt = manifest.TryGetProperty("publishedAt", out var publishedAt) && publishedAt.TryGetDateTimeOffset(out var value) ? value : null
        };
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
        IReadOnlyDictionary<Guid, DocumentPreviewArtifact> previews,
        bool requirePreviews = true)
    {
        if (sourceFiles.Any(path => NativeExtensions.Contains(Path.GetExtension(path))))
            throw new PdmRuleException("生产发布包不能包含SolidWorks源文件。 ");
        if (sourceFiles.Any(path => RetiredReleaseExtensions.Contains(Path.GetExtension(path))))
            throw new PdmRuleException("新发布包不能包含DWG文件；历史DWG保留在原位置且不会自动删除。");
        foreach (var source in previewSources)
        {
            if (!previews.TryGetValue(source.DocumentId, out var preview))
            {
                // 转图失败时允许先发布：预览留待后台转图任务补齐，不阻塞发布流程。
                if (requirePreviews) throw new PdmRuleException($"图档{source.DrawingNumber}缺少服务器生成的发布预览。");
                continue;
            }
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
        if (scope == ReleaseScope.NonStandardLongLead)
            RequireFile(sourceFiles, path => string.Equals(Path.GetFileName(path), "long-lead-nonstandard-parts-bom.xlsx", StringComparison.OrdinalIgnoreCase), "长交期非标件BOM XLSX");
        if (scope == ReleaseScope.ElectricalLongLead)
            RequireFile(sourceFiles, path => string.Equals(Path.GetFileName(path), "long-lead-electrical-bom.xlsx", StringComparison.OrdinalIgnoreCase), "长交期电气件BOM XLSX");
        if (scope is ReleaseScope.LegacyCombined or ReleaseScope.StandardFormal or ReleaseScope.StandardSupplement)
            RequireFile(sourceFiles, path => string.Equals(Path.GetFileName(path), "standard-parts-bom.xlsx", StringComparison.OrdinalIgnoreCase), "标准件BOM XLSX");
        if (scope is ReleaseScope.LegacyCombined or ReleaseScope.NonStandardWithDrawing or ReleaseScope.NonStandardSupplement)
            RequireFile(sourceFiles, path => string.Equals(Path.GetFileName(path), "nonstandard-parts-bom.xlsx", StringComparison.OrdinalIgnoreCase), "非标件BOM XLSX");
        if (scope is ReleaseScope.LegacyCombined or ReleaseScope.ElectricalFormal or ReleaseScope.ElectricalSupplement)
            RequireFile(sourceFiles, path => string.Equals(Path.GetFileName(path), "electrical-bom.xlsx", StringComparison.OrdinalIgnoreCase), "电气件BOM XLSX");
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
