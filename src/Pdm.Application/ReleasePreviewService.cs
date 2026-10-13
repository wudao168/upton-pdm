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
/// 发布预览（PDF/STEP 转换）的独立处理：正式源图生成是发布前置条件，PDF/STEP 失败不阻断发布，
/// 生成成功后补挂到正式版本并写入发布目录，结果通过站内消息反馈给相关人。
/// </summary>
public sealed class ReleasePreviewService(
    IPdmRepository repository,
    IServerPreviewConverter previewConverter,
    TimeProvider timeProvider,
    ReleaseDeliveryArchiveService? releaseDeliveryArchive = null)
{
    public const int MaxAttempts = 5;
    private const int BatchSize = 5;

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
        var project = await repository.FindProjectAsync(package.ProjectId, cancellationToken);
        if (project is null) return false;
        var attempts = package.PreviewAttempts + 1;
        var now = timeProvider.GetUtcNow();
        await repository.MarkReleasePreviewStateAsync(package.Id, ReleasePreviewState.Running, package.PreviewError, package.PreviewAttempts, now, cancellationToken);
        try
        {
            var completed = (await ListItemsAsync(package.Id, cancellationToken)).Where(item => item.Succeeded).Select(item => item.DocumentId).ToHashSet();
            var sources = (await repository.ListReleasePreviewSourcesAsync(package.Id, cancellationToken))
                .Where(source => !completed.Contains(source.DocumentId)).ToArray();
            if (sources.Length == 0)
            {
                await repository.MarkReleasePreviewStateAsync(package.Id, ReleasePreviewState.Succeeded, null, attempts, timeProvider.GetUtcNow(), cancellationToken);
                return true;
            }
            var vaultRoot = StorageLocationPolicy.Normalize(project.VaultLocation);
            var stagingDirectory = StorageLocationPolicy.ResolveUnder(vaultRoot, Path.Combine(".release-staging", package.Number));
            Directory.CreateDirectory(stagingDirectory);
            var (previews, failures) = await ConvertPreviewsAsync(package, project, sources, stagingDirectory, cancellationToken);
            var releasedVersions = await repository.AttachReleasePreviewArtifactsAsync(package.Id, previews, cancellationToken);
            CopyPreviewsIntoPublishedPath(package, vaultRoot, releasedVersions);
            // 转图补齐后发布目录多了 STEP/PDF，这里顺带把新增成品登记到"机械发布/电气发布"目录。
            if (releaseDeliveryArchive is not null)
            {
                var archiveActor = package.ApprovalTasks
                    .Where(task => task.DecisionBy is not null)
                    .OrderBy(task => task.StepOrder)
                    .LastOrDefault()?.DecisionBy
                    ?? project.PrimaryProjectManager
                    ?? "system";
                await releaseDeliveryArchive.ArchiveAsync(package.Id, archiveActor, cancellationToken);
            }
            if (failures is not null) throw failures;
            await repository.MarkReleasePreviewStateAsync(package.Id, ReleasePreviewState.Succeeded, null, attempts, timeProvider.GetUtcNow(), cancellationToken);
            await NotifyAsync(package, project, "图纸转换已完成", $"STEP/PDF 已生成并绑定正式版本（第{attempts}次处理）", "result", cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var exhausted = attempts >= MaxAttempts;
            await repository.MarkReleasePreviewStateAsync(package.Id,
                exhausted ? ReleasePreviewState.Failed : ReleasePreviewState.Pending, exception.Message, attempts, timeProvider.GetUtcNow(), cancellationToken);
            if (exhausted)
                await NotifyAsync(package, project, "图纸转换多次失败",
                    $"已重试{attempts}次仍未生成 STEP/PDF：{exception.Message}；发布已完成，可在发布面板手动重试转图", "result", cancellationToken);
            return false;
        }
        catch (OperationCanceledException exception)
        {
            // 服务重启或整批运行超时会打断转图；必须落回可重排队的状态，否则发布面板会一直停在"转换中"并显示上一次的旧错误。
            // 打断不是转换本身失败，因此不消耗重试次数。
            await repository.MarkReleasePreviewStateAsync(package.Id, ReleasePreviewState.Pending,
                $"转图处理被中断（服务停止或整批运行超时），已重新排队：{(string.IsNullOrWhiteSpace(exception.Message) ? "未返回原因" : exception.Message)}",
                package.PreviewAttempts, timeProvider.GetUtcNow(), CancellationToken.None);
            throw;
        }
    }

    /// <summary>列出某个发布包的转图明细（图档 + 目标格式 + 成功/失败），用于发布总览的图纸转出列表。</summary>
    public async Task<IReadOnlyList<ReleasePreviewItem>> ListItemsAsync(Guid releasePackageId, CancellationToken cancellationToken)
    {
        var package = await repository.FindReleasePackageAsync(releasePackageId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包不存在。");
        var sources = await repository.ListReleasePreviewSourcesAsync(releasePackageId, cancellationToken);
        if (sources.Count == 0) return [];
        var released = new Dictionary<Guid, DocumentVersion>();
        foreach (var source in sources)
        {
            var version = (await repository.ListDocumentVersionsAsync(source.DocumentId, cancellationToken))
                .Where(item => item.ReleasePackageId == releasePackageId)
                .OrderByDescending(item => item.CreatedAt)
                .FirstOrDefault();
            if (version is not null) released[source.DocumentId] = version;
        }
        return sources
            .Select(source =>
            {
                released.TryGetValue(source.DocumentId, out var version);
                var preview = version?.Preview;
                var error = preview is not null
                    ? null
                    : version is null
                        ? "该图档没有随本次发布转为正式版本（发布时不在发布范围内），需要用新的非标件BOM+图纸发布后才能转出。"
                        : package.PreviewState == ReleasePreviewState.Running ? "正在转换…" : ItemError(package.PreviewError, source.FileName);
                return new ReleasePreviewItem(
                    package.Id,
                    package.Number,
                    source.DocumentId,
                    source.DrawingNumber,
                    source.FileName,
                    source.Kind,
                    ExpectedFormat(source.Kind),
                    preview is not null,
                    preview?.FileLength ?? 0,
                    preview?.StorageRelativePath ?? string.Empty,
                    preview?.Sha256 ?? string.Empty,
                    error);
            })
            .OrderBy(item => item.DrawingNumber, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>按项目列出转图明细，可按发布包筛选（不传则列出该项目所有有转图记录的发布包）。</summary>
    public async Task<IReadOnlyList<ReleasePreviewItem>> ListProjectItemsAsync(Guid projectId, Guid? releasePackageId, CancellationToken cancellationToken)
    {
        var packages = (await repository.ListReleasePackagesAsync(projectId, cancellationToken))
            .Where(package => package.State == ReleasePackageState.Published && package.PreviewState != ReleasePreviewState.None)
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
        var package = await repository.FindReleasePackageAsync(releasePackageId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包不存在。");
        if (package.State != ReleasePackageState.Published) throw new PdmRuleException("只有已发布的发布包可以重试转图。");
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
            (previews, failures) = await ConvertPreviewsAsync(package, project, targets, stagingDirectory, cancellationToken);
            var releasedVersions = await repository.AttachReleasePreviewArtifactsAsync(package.Id, previews, cancellationToken);
            if (releasedVersions.Count == 0 && failures is null)
                throw new PdmRuleException("该图档还没有正式版本，无法挂接预览；请重新发布后再转图。");
            CopyPreviewsIntoPublishedPath(package, vaultRoot, releasedVersions);
            if (releaseDeliveryArchive is not null) await releaseDeliveryArchive.ArchiveAsync(package.Id, actor, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await repository.MarkReleasePreviewStateAsync(package.Id, ReleasePreviewState.Failed, exception.Message, package.PreviewAttempts, timeProvider.GetUtcNow(), cancellationToken);
            throw;
        }
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

    private const string ItemErrorPrefix = PartialPreviewConversionException.ItemErrorPrefix;

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
        try
        {
            return (await previewConverter.GenerateAsync(package, project, sources, stagingDirectory, cancellationToken, keepExistingPreviews: true), null);
        }
        catch (PartialPreviewConversionException exception)
        {
            return (exception.Artifacts, exception);
        }
    }

    /// <summary>转出目标格式：三维零件/装配体出STEP，2D工程图出PDF。</summary>
    private static DocumentPreviewFormat ExpectedFormat(DocumentKind kind) =>
        kind == DocumentKind.Drawing ? DocumentPreviewFormat.Pdf : DocumentPreviewFormat.Step;

    private static void CopyPreviewsIntoPublishedPath(ReleasePackage package, string vaultRoot, IReadOnlyList<DocumentVersion> releasedVersions)
    {
        if (string.IsNullOrWhiteSpace(package.PublishedPath) || !Directory.Exists(package.PublishedPath)) return;
        foreach (var version in releasedVersions)
        {
            if (version.Preview is null) continue;
            try
            {
                var source = StorageLocationPolicy.ResolveUnder(vaultRoot, version.Preview.StorageRelativePath);
                if (!File.Exists(source)) continue;
                var target = Path.Combine(package.PublishedPath, Path.GetFileName(source));
                File.Copy(source, target, true);
            }
            catch (IOException) { /* 发布目录不可写时忽略：预览已绑定到正式版本，可后续重试。 */ }
            catch (UnauthorizedAccessException) { }
        }
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
        if (recipients.Length == 0) return;
        var createdAt = timeProvider.GetUtcNow();
        await repository.CreateUserNotificationsAsync(recipients.Select(recipient => new UserNotification(
            Guid.NewGuid(),
            recipient,
            "ReleasePreviewResult",
            title,
            $"{project.Code} · {package.Number} {detail}",
            project.Id,
            package.Id,
            $"release-package:{package.Id:N}:preview:{kind}",
            createdAt,
            null)).ToArray(), cancellationToken);
    }
}
