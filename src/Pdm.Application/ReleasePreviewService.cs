using System.Security.Cryptography;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

/// <summary>转图明细：发布包内每个需要转出的图档及其转出结果（STEP/PDF）。</summary>
public sealed record ReleasePreviewItem(
    Guid ReleasePackageId,
    string ReleasePackageNumber,
    Guid DocumentId,
    string DrawingNumber,
    string FileName,
    DocumentKind Kind,
    DocumentPreviewFormat Format,
    bool Succeeded,
    long FileLength,
    string PreviewRelativePath,
    string Sha256,
    string? Error);

/// <summary>
/// 发布预览（PDF/STEP 转换）的独立处理：使用审批冻结源图直接转换，PDF/STEP 失败不阻断发布，
/// 生成成功后补挂到正式版本并写入发布目录，结果通过站内消息反馈给相关人。
/// </summary>
public sealed class ReleasePreviewService(
    IPdmRepository repository,
    IServerPreviewConverter previewConverter,
    TimeProvider timeProvider,
    ReleaseDeliveryArchiveService? releaseDeliveryArchive = null,
    IPdfReleaseStampProcessor? pdfReleaseStamp = null)
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> PackageLocks = new();
    public const int MaxAttempts = 5;
    private const int BatchSize = 5;

    /// <summary>启动巡检历史成功状态，发现实际文件缺失或归档未完成时重新排队。</summary>
    public async Task<int> ReconcileDeliveryAsync(Guid packageId, CancellationToken cancellationToken)
    {
        var package = await repository.FindReleasePackageAsync(packageId, cancellationToken);
        if (package is null || package.State != ReleasePackageState.Published) return 0;
        var archived = await ArchiveDeliveryAsync(package, "system", cancellationToken);
        if (!ReleasePreviewPolicy.RequiresConversion(package))
        {
            if (package.PreviewState != ReleasePreviewState.None || package.PreviewAttempts != 0 || package.PreviewError is not null)
            {
                await repository.MarkReleasePreviewStateAsync(packageId, ReleasePreviewState.None, null, 0, timeProvider.GetUtcNow(), cancellationToken);
                await repository.AppendAuditAsync(new(Guid.NewGuid(), timeProvider.GetUtcNow(), "system", "release-package.preview-state-corrected",
                    nameof(ReleasePackage), packageId.ToString(), $"{package.Number}；{package.Scope}不产生转图任务，已撤销历史{package.PreviewState}状态，原尝试次数{package.PreviewAttempts}。"), cancellationToken);
            }
            return archived;
        }
        // 巡检不能把已耗尽次数的任务改回 Pending，造成“自动重试”假象。
        if (package.PreviewAttempts >= MaxAttempts)
        {
            if (package.PreviewState != ReleasePreviewState.Failed && package.PreviewState != ReleasePreviewState.Succeeded)
            {
                await repository.MarkReleasePreviewStateAsync(packageId, ReleasePreviewState.Failed,
                    package.PreviewError, package.PreviewAttempts, timeProvider.GetUtcNow(), cancellationToken);
                await repository.AppendAuditAsync(new(Guid.NewGuid(), timeProvider.GetUtcNow(), "system", "release-package.preview-state-corrected",
                    nameof(ReleasePackage), packageId.ToString(), $"{package.Number}；已尝试{package.PreviewAttempts}次，恢复为暂停状态，保留原失败原因。"), cancellationToken);
            }
            await NotifyIncompleteAsync(packageId, cancellationToken);
            return archived;
        }
        string? error = null;
        try
        {
            if ((await ListItemsAsync(packageId, cancellationToken)).Any(item => !item.Succeeded))
                error = "生产资料巡检发现预览缺失或尚未投放，已重新排队补齐。";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            error = await RecordFailureAsync(packageId, exception);
        }
        if (error is not null && package.PreviewState is not (ReleasePreviewState.Pending or ReleasePreviewState.WaitingForServer or ReleasePreviewState.Running))
            await repository.MarkReleasePreviewStateAsync(packageId, ReleasePreviewState.Pending, error,
                package.PreviewState is ReleasePreviewState.Succeeded or ReleasePreviewState.None ? 0 : package.PreviewAttempts,
                timeProvider.GetUtcNow(), cancellationToken);
        await NotifyIncompleteAsync(packageId, cancellationToken);
        return archived;
    }

    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        var pending = await repository.ListReleasePackagesAwaitingPreviewAsync(MaxAttempts, BatchSize, cancellationToken);
        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        await Parallel.ForEachAsync(pending, new ParallelOptions {
            MaxDegreeOfParallelism = Math.Clamp(settings.PreviewConversion.Servers.Count, 1, BatchSize), CancellationToken = cancellationToken
        }, async (package, token) => { await ProcessAsync(package, token); });
        return pending.Count;
    }

    /// <summary>处理单个发布包的转图；返回是否成功（失败会记录原因并按剩余次数继续排队）。</summary>
    public async Task<bool> ProcessAsync(ReleasePackage package, CancellationToken cancellationToken)
    {
        var gate = PackageLocks.GetOrAdd(package.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            package = await repository.FindReleasePackageAsync(package.Id, cancellationToken) ?? package;
            if (package.State != ReleasePackageState.Published) return false;
            if (!ReleasePreviewPolicy.RequiresConversion(package))
            {
                if (package.PreviewState != ReleasePreviewState.None || package.PreviewAttempts != 0 || package.PreviewError is not null)
                    await repository.MarkReleasePreviewStateAsync(package.Id, ReleasePreviewState.None, null, 0, timeProvider.GetUtcNow(), cancellationToken);
                return true;
            }
            if (package.PreviewAttempts >= MaxAttempts && package.PreviewState != ReleasePreviewState.Succeeded)
            {
                if (package.PreviewState != ReleasePreviewState.Failed)
                    await repository.MarkReleasePreviewStateAsync(package.Id, ReleasePreviewState.Failed, package.PreviewError,
                        package.PreviewAttempts, timeProvider.GetUtcNow(), cancellationToken);
                return false;
            }
            return await ProcessCoreAsync(package, cancellationToken);
        }
        finally { gate.Release(); }
    }

    private async Task<bool> ProcessCoreAsync(ReleasePackage package, CancellationToken cancellationToken)
    {
        var project = await repository.FindProjectAsync(package.ProjectId, cancellationToken);
        if (project is null) return false;
        var attempts = package.PreviewAttempts + 1;
        var now = timeProvider.GetUtcNow();
        await repository.MarkReleasePreviewStateAsync(package.Id, ReleasePreviewState.Running, package.PreviewError, package.PreviewAttempts, now, cancellationToken);
        try
        {
            var vaultRoot = StorageLocationPolicy.Normalize(project.VaultLocation);
            var completed = new HashSet<Guid>();
            foreach (var version in await ReleasedVersionsAsync(package.Id, cancellationToken))
            {
                if (!await PreviewReadableAsync(vaultRoot, version, cancellationToken)) continue;
                var bindingId = package.VersionBindings?.FirstOrDefault(binding => binding.DocumentId == version.DocumentId)?.VersionId
                    ?? version.SourceVersionId ?? version.Id;
                if (version.Preview?.Format == DocumentPreviewFormat.Pdf && pdfReleaseStamp is not null
                    && !pdfReleaseStamp.IsCurrent(StorageLocationPolicy.ResolveUnder(vaultRoot, version.Preview.StorageRelativePath), package, bindingId)) continue;
                completed.Add(version.DocumentId);
            }
            var sources = (await repository.ListReleasePreviewSourcesAsync(package.Id, cancellationToken))
                .Where(source => !completed.Contains(source.DocumentId)).ToArray();
            var stagingDirectory = StorageLocationPolicy.ResolveUnder(vaultRoot, Path.Combine(".release-staging", package.Number));
            Directory.CreateDirectory(stagingDirectory);
            var (previews, failures) = sources.Length == 0
                ? (new Dictionary<Guid, DocumentPreviewArtifact>(), (PartialPreviewConversionException?)null)
                : await ConvertPreviewsAsync(package, project, sources, stagingDirectory, cancellationToken);
            if (previews.Count > 0) await repository.AttachReleasePreviewArtifactsAsync(package.Id, previews, cancellationToken);
            package = await repository.FindReleasePackageAsync(package.Id, cancellationToken) ?? package;
            await CopyPreviewsIntoPublishedPathAsync(package, vaultRoot, await ReleasedVersionsAsync(package.Id, cancellationToken), cancellationToken);
            // 转图补齐后发布目录多了 STEP/PDF，这里顺带把新增成品登记到"机械发布/电气发布"目录。
            if (releaseDeliveryArchive is not null)
            {
                var archiveActor = package.ApprovalTasks
                    .Where(task => task.DecisionBy is not null)
                    .OrderBy(task => task.StepOrder)
                    .LastOrDefault()?.DecisionBy
                    ?? project.PrimaryProjectManager
                    ?? "system";
                await ArchiveDeliveryAsync(package, archiveActor, cancellationToken);
            }
            if (failures is not null) throw failures;
            if ((await ListItemsAsync(package.Id, cancellationToken)).Any(item => !item.Succeeded))
                throw new PdmRuleException("发布资料尚未齐套，请检查失败图档并重试转图。");
            await repository.MarkReleasePreviewStateAsync(package.Id, ReleasePreviewState.Succeeded, null, attempts, timeProvider.GetUtcNow(), cancellationToken);
            await NotifyAsync(package, project, "图纸转换已完成", $"STEP/PDF 已生成并绑定正式版本（第{attempts}次处理）", "result", cancellationToken);
            return true;
        }
        catch (PreviewServerUnavailableException exception)
        {
            if (package.PreviewState != ReleasePreviewState.WaitingForServer || package.PreviewError != exception.Message)
                await RecordFailureAsync(package.Id, exception, "release-package.preview-waiting");
            await repository.MarkReleasePreviewStateAsync(package.Id, ReleasePreviewState.WaitingForServer,
                exception.Message, package.PreviewAttempts, timeProvider.GetUtcNow(), CancellationToken.None);
            await NotifyIncompleteAsync(package.Id, cancellationToken);
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var exhausted = attempts >= MaxAttempts;
            var error = await RecordFailureAsync(package.Id, exception);
            await repository.MarkReleasePreviewStateAsync(package.Id,
                exhausted ? ReleasePreviewState.Failed : ReleasePreviewState.Pending, error, attempts, timeProvider.GetUtcNow(), CancellationToken.None);
            if (!exhausted) await NotifyIncompleteAsync(package.Id, cancellationToken);
            if (exhausted)
                await NotifyAsync(package, project, "图纸转换多次失败",
                    $"已重试{attempts}次，生产资料仍未全部投放：{error}；可在发布面板手动重试转图", "result", cancellationToken);
            return false;
        }
        catch (OperationCanceledException)
        {
            // 服务重启或整批运行超时会打断转图；必须落回可重排队的状态，否则发布面板会一直停在"转换中"并显示上一次的旧错误。
            // 打断不是转换本身失败，因此不消耗重试次数。
            await repository.MarkReleasePreviewStateAsync(package.Id, ReleasePreviewState.Pending,
                "转图处理被中断（服务停止或整批运行超时），已重新排队。",
                package.PreviewAttempts, timeProvider.GetUtcNow(), CancellationToken.None);
            throw;
        }
    }

    /// <summary>列出某个发布包的转图明细（图档 + 目标格式 + 成功/失败），用于发布总览的图纸转出列表。</summary>
    public async Task<IReadOnlyList<ReleasePreviewItem>> ListItemsAsync(Guid releasePackageId, CancellationToken cancellationToken)
    {
        var package = await repository.FindReleasePackageAsync(releasePackageId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包不存在。");
        if (!ReleasePreviewPolicy.RequiresConversion(package)) return [];
        var sources = await repository.ListReleasePreviewSourcesAsync(releasePackageId, cancellationToken);
        if (sources.Count == 0) return [];
        var project = await repository.FindProjectAsync(package.ProjectId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包对应的项目不存在。");
        var vaultRoot = StorageLocationPolicy.Normalize(project.VaultLocation);
        var released = (await ReleasedVersionsAsync(releasePackageId, cancellationToken)).ToDictionary(version => version.DocumentId);
        var items = new List<ReleasePreviewItem>();
        foreach (var source in sources)
        {
            released.TryGetValue(source.DocumentId, out var version);
            var preview = version?.Preview;
            var readable = await PreviewReadableAsync(vaultRoot, version, cancellationToken);
            var delivered = readable && !string.IsNullOrWhiteSpace(package.PublishedPath)
                && await ReleaseDeliveryFiles.IsDeliveredAsync(package.PublishedPath, preview!, cancellationToken);
            var error = delivered ? null
                : package.PreviewState == ReleasePreviewState.Running ? "正在转换…"
                : version is null ? "该图档没有随本次发布转为正式版本，需要重新发布后才能转出。"
                : readable ? "预览已生成，但生产目录尚未投放或文件校验未通过，请重试转图。"
                : preview is not null ? "预览文件缺失或内容校验未通过，请重试转图。"
                : ItemError(package.PreviewError, source.FileName);
            items.Add(new ReleasePreviewItem(package.Id, package.Number, source.DocumentId, source.DrawingNumber,
                source.FileName, source.Kind, ExpectedFormat(source.Kind), delivered, preview?.FileLength ?? 0,
                preview?.StorageRelativePath ?? string.Empty, preview?.Sha256 ?? string.Empty, error));
        }
        return items.OrderBy(item => item.DrawingNumber, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<IReadOnlyList<DocumentVersion>> ReleasedVersionsAsync(Guid packageId, CancellationToken cancellationToken)
    {
        var result = new List<DocumentVersion>();
        var package = await repository.FindReleasePackageAsync(packageId, cancellationToken) ?? throw new PdmNotFoundException("发布包不存在。");
        foreach (var source in await repository.ListReleasePreviewSourcesAsync(packageId, cancellationToken))
        {
            var version = (await repository.ListDocumentVersionsAsync(source.DocumentId, cancellationToken))
                .Where(item => item.ReleasePackageId == packageId || source.AlreadyReleased && item.Id == source.SourceVersionId)
                .OrderByDescending(item => item.ReleasePackageId == packageId).ThenByDescending(item => item.CreatedAt).FirstOrDefault();
            if (version is not null) result.Add(PdfTitleBlockSnapshotBuilder.DeliveryVersion(package, version));
        }
        return result;
    }

    private async Task<bool> PreviewReadableAsync(string vaultRoot, DocumentVersion? version, CancellationToken cancellationToken)
    {
        if (version?.Preview is not { } preview || version.Status != DocumentVersionStatus.Released) return false;
        var document = await repository.FindDocumentAsync(version.DocumentId, cancellationToken);
        if (document is null || preview.Format != ExpectedFormat(document.Kind)) return false;
        var legacy = false;
        if (document.Kind == DocumentKind.Drawing)
        {
            var original = version.SourceVersionId.HasValue
                ? await repository.FindDocumentVersionAsync(version.DocumentId, version.SourceVersionId.Value, cancellationToken) : null;
            legacy = original is null || string.Equals(original.StorageRelativePath, version.StorageRelativePath, StringComparison.OrdinalIgnoreCase);
        }
        if (!legacy && !string.Equals(preview.SourceSha256, version.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        return await ReleaseDeliveryFiles.MatchesAsync(StorageLocationPolicy.ResolveUnder(vaultRoot, preview.StorageRelativePath),
            preview.FileLength, preview.Sha256, cancellationToken);
    }

    /// <summary>按项目列出转图明细，可按发布包筛选（不传则列出该项目所有有转图记录的发布包）。</summary>
    public async Task<IReadOnlyList<ReleasePreviewItem>> ListProjectItemsAsync(Guid projectId, Guid? releasePackageId, CancellationToken cancellationToken)
    {
        var packages = (await repository.ListReleasePackagesAsync(projectId, cancellationToken))
            .Where(package => package.State == ReleasePackageState.Published && ReleasePreviewPolicy.RequiresConversion(package) && package.PreviewState != ReleasePreviewState.None)
            .Where(package => releasePackageId is null || package.Id == releasePackageId)
            .OrderByDescending(package => package.PublishedAt ?? package.CreatedAt)
            .ToArray();
        var items = new List<ReleasePreviewItem>();
        foreach (var package in packages) items.AddRange(await ListItemsAsync(package.Id, cancellationToken));
        return items;
    }

    /// <summary>
    /// 单项重试：只重新转换指定图档，保留同包其它已转好的预览；完成后按整体结果刷新发布包的转图状态。
    /// </summary>
    public async Task<ReleasePreviewItem> RetryItemAsync(Guid releasePackageId, Guid documentId, string actor, CancellationToken cancellationToken)
    {
        var items = await RetryItemsAsync(releasePackageId, [documentId], actor, cancellationToken);
        return items.First(item => item.DocumentId == documentId);
    }

    /// <summary>
    /// 批量重试：多个图档在同一个转换请求里一次性转出（引用树只下发、只解析一次），
    /// 比逐条重试快一个数量级；同样保留同包其它已转好的预览。
    /// </summary>
    public async Task<IReadOnlyList<ReleasePreviewItem>> RetryItemsAsync(Guid releasePackageId, IReadOnlyList<Guid> documentIds, string actor, CancellationToken cancellationToken)
    {
        var gate = PackageLocks.GetOrAdd(releasePackageId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try { return await RetryItemsCoreAsync(releasePackageId, documentIds, actor, cancellationToken); }
        catch (OperationCanceledException)
        {
            var package = await repository.FindReleasePackageAsync(releasePackageId, CancellationToken.None);
            if (package?.PreviewState == ReleasePreviewState.Running)
                await repository.MarkReleasePreviewStateAsync(releasePackageId, ReleasePreviewState.Pending,
                    "转图处理被中断（服务停止或整批运行超时），已重新排队。", package.PreviewAttempts, timeProvider.GetUtcNow(), CancellationToken.None);
            throw;
        }
        finally { gate.Release(); }
    }

    private async Task<IReadOnlyList<ReleasePreviewItem>> RetryItemsCoreAsync(Guid releasePackageId, IReadOnlyList<Guid> documentIds, string actor, CancellationToken cancellationToken)
    {
        var package = await repository.FindReleasePackageAsync(releasePackageId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包不存在。");
        if (package.State != ReleasePackageState.Published) throw new PdmRuleException("只有已发布的发布包可以重试转图。");
        if (!ReleasePreviewPolicy.RequiresConversion(package)) throw new PdmRuleException("该发布类型不支持转图。");
        var project = await repository.FindProjectAsync(package.ProjectId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包对应的项目不存在。");
        var sources = await repository.ListReleasePreviewSourcesAsync(releasePackageId, cancellationToken);
        var wanted = documentIds.ToHashSet();
        var targets = sources.Where(item => wanted.Contains(item.DocumentId)).ToArray();
        if (targets.Length == 0) throw new PdmRuleException("所选图档不在本次转图范围内。");
        var vaultRoot = StorageLocationPolicy.Normalize(project.VaultLocation);
        var stagingDirectory = StorageLocationPolicy.ResolveUnder(vaultRoot, Path.Combine(".release-staging", package.Number));
        Directory.CreateDirectory(stagingDirectory);
        var previousItems = await ListItemsAsync(package.Id, cancellationToken);
        PartialPreviewConversionException? failures;
        await repository.MarkReleasePreviewStateAsync(package.Id, ReleasePreviewState.Running, package.PreviewError, package.PreviewAttempts, timeProvider.GetUtcNow(), cancellationToken);
        try
        {
            IReadOnlyDictionary<Guid, DocumentPreviewArtifact> previews;
            var versions = (await ReleasedVersionsAsync(package.Id, cancellationToken)).ToDictionary(version => version.DocumentId);
            var missingTargets = new List<ReleasePreviewSource>();
            foreach (var target in targets)
                if (!versions.TryGetValue(target.DocumentId, out var version) || !await PreviewReadableAsync(vaultRoot, version, cancellationToken)
                    || version.Preview?.Format == DocumentPreviewFormat.Pdf && pdfReleaseStamp is not null
                        && !pdfReleaseStamp.IsCurrent(StorageLocationPolicy.ResolveUnder(vaultRoot, version.Preview.StorageRelativePath), package, target.SourceVersionId))
                    missingTargets.Add(target);
            (previews, failures) = missingTargets.Count == 0
                ? (new Dictionary<Guid, DocumentPreviewArtifact>(), (PartialPreviewConversionException?)null)
                : await ConvertPreviewsAsync(package, project, missingTargets, stagingDirectory, cancellationToken);
            var releasedVersions = await repository.AttachReleasePreviewArtifactsAsync(package.Id, previews, cancellationToken);
            if (missingTargets.Count > 0 && releasedVersions.Count == 0 && failures is null)
                throw new PdmRuleException("该图档还没有正式版本，无法挂接预览；请重新发布后再转图。");
            await CopyPreviewsIntoPublishedPathAsync(package, vaultRoot, await ReleasedVersionsAsync(package.Id, cancellationToken), cancellationToken);
            await ArchiveDeliveryAsync(package, actor, cancellationToken);
            var items = await ListItemsAsync(package.Id, cancellationToken);
            var pending = items.Where(item => !item.Succeeded).ToArray();
            var errors = pending.Select(item => $"{item.FileName}：{(failures?.Errors.GetValueOrDefault(item.DocumentId)
                ?? previousItems.FirstOrDefault(previous => previous.DocumentId == item.DocumentId)?.Error
                ?? "尚未生成预览，请单独重试。").ReplaceLineEndings(" ")}");
            await repository.MarkReleasePreviewStateAsync(package.Id,
                pending.Length == 0 ? ReleasePreviewState.Succeeded : ReleasePreviewState.Failed,
                pending.Length == 0 ? null : ItemErrorPrefix + string.Join("\n", errors),
                pending.Length == 0 ? 0 : package.PreviewAttempts,
                timeProvider.GetUtcNow(), cancellationToken);
            return await ListItemsAsync(package.Id, cancellationToken);
        }
        catch (PreviewServerUnavailableException exception)
        {
            await RecordFailureAsync(package.Id, exception, "release-package.preview-waiting");
            await repository.MarkReleasePreviewStateAsync(package.Id, ReleasePreviewState.WaitingForServer,
                exception.Message, package.PreviewAttempts, timeProvider.GetUtcNow(), CancellationToken.None);
            throw new PdmRuleException(exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var error = await RecordFailureAsync(package.Id, exception);
            await repository.MarkReleasePreviewStateAsync(package.Id, ReleasePreviewState.Failed, error, package.PreviewAttempts, timeProvider.GetUtcNow(), CancellationToken.None);
            throw new PdmRuleException(error);
        }
        catch (OperationCanceledException)
        {
            await repository.MarkReleasePreviewStateAsync(package.Id, ReleasePreviewState.Pending,
                "转图处理被中断（服务停止或整批运行超时），已重新排队。", package.PreviewAttempts, timeProvider.GetUtcNow(), CancellationToken.None);
            throw;
        }
    }

    private const string ItemErrorPrefix = PartialPreviewConversionException.ItemErrorPrefix;

    private async Task<string> RecordFailureAsync(Guid packageId, Exception exception, string action = "release-package.preview-diagnostic")
    {
        try
        {
            await repository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), timeProvider.GetUtcNow(), "system",
                action, nameof(ReleasePackage), packageId.ToString(), exception.ToString()), CancellationToken.None);
        }
        catch (Exception diagnosticFailure)
        {
            // 诊断写入失败也必须继续尝试恢复任务状态，避免一次瞬时数据库故障把任务留在 Running。
            System.Diagnostics.Trace.TraceError("发布包 {0} 转图异常：{1}；诊断写入失败：{2}", packageId, exception, diagnosticFailure);
        }
        return exception switch
        {
            PdmRuleException or PdmConflictException or PartialPreviewConversionException => exception.Message,
            IOException or UnauthorizedAccessException => "生产资料写入或读取失败，请检查磁盘空间、共享目录连接和读写权限后重试。",
            _ => "生产资料处理失败，请重试；如仍失败，请联系管理员检查转图诊断日志。"
        };
    }

    private static string ItemError(string? error, string fileName)
    {
        if (error is null) return "尚未生成预览。";
        if (!error.StartsWith(ItemErrorPrefix, StringComparison.Ordinal)) return error;
        var prefix = fileName + "：";
        return error[ItemErrorPrefix.Length..].Split('\n').FirstOrDefault(line => line.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..]
            ?? "该图档尚未完成转换，请单独重试。";
    }

    private async Task<(IReadOnlyDictionary<Guid, DocumentPreviewArtifact> Artifacts, PartialPreviewConversionException? Failure)> ConvertPreviewsAsync(
        ReleasePackage package, Project project, IReadOnlyList<ReleasePreviewSource> sources, string stagingDirectory, CancellationToken cancellationToken)
    {
        var artifacts = new Dictionary<Guid, DocumentPreviewArtifact>();
        var errors = new Dictionary<Guid, string>();
        foreach (var source in sources.Where(source => source.AlreadyReleased))
        {
            try { artifacts[source.DocumentId] = await ReusePublishedPreviewAsync(package, project, source, stagingDirectory, cancellationToken); }
            catch (Exception error) when (error is PdmRuleException or PdmConflictException or IOException)
            {
                errors[source.DocumentId] = error is IOException ? "原正式预览文件无法读取，未调用转图，请检查存储连接。" : error.Message;
            }
        }
        var pending = sources.Where(source => !source.AlreadyReleased).ToArray();
        try
        {
            if (pending.Length > 0)
                foreach (var pair in await previewConverter.GenerateAsync(package, project, pending, stagingDirectory, cancellationToken, keepExistingPreviews: true)) artifacts[pair.Key] = pair.Value;
        }
        catch (PartialPreviewConversionException exception)
        {
            foreach (var pair in exception.Artifacts) artifacts[pair.Key] = pair.Value;
            foreach (var pair in exception.Errors) errors[pair.Key] = pair.Value;
        }
        return (artifacts, errors.Count == 0 ? null : new PartialPreviewConversionException(artifacts, errors,
            string.Join("\n", sources.Where(source => errors.ContainsKey(source.DocumentId)).Select(source => $"{source.FileName}：{errors[source.DocumentId]}"))));
    }

    public async Task<DocumentPreviewArtifact> ReusePublishedPreviewAsync(ReleasePackage package, Project project,
        ReleasePreviewSource source, string stagingDirectory, CancellationToken cancellationToken)
    {
        var version = await repository.FindDocumentVersionAsync(source.DocumentId, source.SourceVersionId, cancellationToken)
            ?? throw new PdmRuleException("固定的正式图纸不存在，未调用转图，请核对发布来源。");
        if (version.Status != DocumentVersionStatus.Released || !string.Equals(version.Sha256, source.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new PdmConflictException("复用预览与固定正式来源不一致，未调用转图。");
        var preview = version.Preview;
        if (preview is null || preview.Format != ExpectedFormat(source.Kind)
            || !string.Equals(preview.SourceSha256, version.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("原正式预览尚未就绪，未调用转图，请先恢复原发布资料。");
        var vaultRoot = StorageLocationPolicy.Normalize(project.VaultLocation);
        var input = StorageLocationPolicy.ResolveUnder(vaultRoot, preview.StorageRelativePath);
        if (!await ReleaseDeliveryFiles.MatchesAsync(input, preview.FileLength, preview.Sha256, cancellationToken))
        {
            var original = version.ReleasePackageId.HasValue ? await repository.FindReleasePackageAsync(version.ReleasePackageId.Value, cancellationToken) : null;
            if (string.IsNullOrWhiteSpace(original?.PublishedPath)) throw new PdmRuleException("原正式预览文件不可用，未调用转图，请恢复原发布资料。");
            input = ReleaseDeliveryFiles.PreviewPath(original.PublishedPath, preview);
            if (!await ReleaseDeliveryFiles.MatchesAsync(input, preview.FileLength, preview.Sha256, cancellationToken))
                throw new PdmRuleException("原正式预览及交付副本校验未通过，未调用转图，请恢复原发布资料。");
        }
        Directory.CreateDirectory(stagingDirectory);
        var temporary = Path.Combine(stagingDirectory, $"reuse-{source.DocumentId:N}-{Guid.NewGuid():N}{(preview.Format == DocumentPreviewFormat.Pdf ? ".pdf" : ".step")}");
        try
        {
            File.Copy(input, temporary, false);
            if (preview.Format == DocumentPreviewFormat.Pdf && pdfReleaseStamp is not null)
                await pdfReleaseStamp.ReissueAsync(package, project, source, temporary, cancellationToken);
            else if (preview.Format == DocumentPreviewFormat.Pdf && package.PdfTitleBlockSnapshot is { Enabled: true })
                throw new PdmRuleException("PDF属性处理组件不可用，未调用转图，请联系管理员。");
            await using var stream = File.OpenRead(temporary);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            var relative = Path.Combine(".release-previews", package.Id.ToString("N"), hash,
                $"{source.DocumentId:N}_{Path.GetFileNameWithoutExtension(source.FileName)}{Path.GetExtension(temporary)}");
            var destination = StorageLocationPolicy.ResolveUnder(vaultRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(temporary, destination, true);
            return new(preview.Format, relative, stream.Length, hash, source.SourceSha256);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>转出目标格式：三维零件/装配体出STEP，2D工程图出PDF。</summary>
    private static DocumentPreviewFormat ExpectedFormat(DocumentKind kind) =>
        kind == DocumentKind.Drawing ? DocumentPreviewFormat.Pdf : DocumentPreviewFormat.Step;

    private async Task CopyPreviewsIntoPublishedPathAsync(ReleasePackage package, string vaultRoot, IReadOnlyList<DocumentVersion> releasedVersions, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(package.PublishedPath) || !Directory.Exists(package.PublishedPath))
            throw new PdmRuleException("生产发布目录不可访问，请检查共享目录连接和权限后重试转图。");
        var delivered = new Dictionary<Guid, DocumentPreviewArtifact>();
        foreach (var version in releasedVersions)
        {
            var preview = version.Preview;
            if (preview is null || !await PreviewReadableAsync(vaultRoot, version, cancellationToken)) continue;
            var source = StorageLocationPolicy.ResolveUnder(vaultRoot, preview.StorageRelativePath);
            var target = ReleaseDeliveryFiles.PreviewPath(package.PublishedPath, preview);
            if (!await ReleaseDeliveryFiles.MatchesAsync(target, preview.FileLength, preview.Sha256, cancellationToken))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temporary = target + $".{Guid.NewGuid():N}.tmp";
                try
                {
                    File.Copy(source, temporary, false);
                    if (!await ReleaseDeliveryFiles.MatchesAsync(temporary, preview.FileLength, preview.Sha256, cancellationToken))
                        throw new PdmRuleException("预览文件投放后校验不一致，请检查生产目录并重试转图。");
                    File.Move(temporary, target, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            delivered[version.DocumentId] = preview;
        }
        await ReleaseDeliveryFiles.RefreshManifestAsync(package, delivered, cancellationToken);
    }

    private async Task<int> ArchiveDeliveryAsync(ReleasePackage package, string actor, CancellationToken cancellationToken)
    {
        if (releaseDeliveryArchive is null) return 0;
        try { return await releaseDeliveryArchive.ArchiveAsync(package.Id, actor, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var error = await RecordFailureAsync(package.Id, exception, "release-package.delivery-archive-diagnostic");
            var project = await repository.FindProjectAsync(package.ProjectId, cancellationToken);
            if (project is not null)
                await NotifyAsync(package, project, "发布资料归档未完成", $"BOM及发布资料归档异常：{error}", "archive", cancellationToken);
            return 0;
        }
    }

    private async Task NotifyIncompleteAsync(Guid packageId, CancellationToken ct)
    {
        var package = await repository.FindReleasePackageAsync(packageId, ct);
        if (package is null) return;
        var project = await repository.FindProjectAsync(package.ProjectId, ct);
        if (project is not null) await ReleasePreviewNotifications.NotifyIncompleteAsync(repository, package, project, timeProvider.GetUtcNow(), ct);
    }

    private async Task NotifyAsync(
        ReleasePackage package, Project project, string title, string detail, string kind, CancellationToken cancellationToken)
    {
        var recipients = package.ApprovalTasks
            .Where(task => task.DecisionBy is not null)
            .Select(task => task.DecisionBy!)
            .Append(package.ApprovalTasks.OrderBy(task => task.StepOrder).FirstOrDefault()?.Assignee)
            .Append(project.PrimaryProjectManager)
            .Where(username => !string.IsNullOrWhiteSpace(username))
            .Select(username => username!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (kind != "archive") recipients = (await ReleasePreviewNotifications.RecipientsAsync(repository, package, project, cancellationToken)).ToArray();
        if (recipients.Length == 0) return;
        var createdAt = timeProvider.GetUtcNow();
        await repository.CreateUserNotificationsAsync(recipients.Select(recipient => new UserNotification(
            Guid.NewGuid(),
            recipient,
            kind == "archive" ? "ReleaseDeliveryResult" : "ReleasePreviewResult",
            title,
            $"{project.Code} · {package.Number} {detail}",
            project.Id,
            package.Id,
            kind == "archive" ? $"release-package:{package.Id:N}:archive" : $"release-package:{package.Id:N}:preview:{kind}:{(package.PreviewUpdatedAt ?? package.CreatedAt).UtcTicks}",
            createdAt,
            null)).ToArray(), cancellationToken);
    }
}
