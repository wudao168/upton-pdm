using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

/// <summary>
/// 发布预览（转图）的独立处理：发布不再因转图失败而失败，转图作为单独事项由后台重试，
/// 生成成功后补挂到正式版本并写入发布目录，结果通过站内消息反馈给相关人。
/// </summary>
public sealed class ReleasePreviewService(
    IPdmRepository repository,
    IServerPreviewConverter previewConverter,
    TimeProvider timeProvider)
{
    public const int MaxAttempts = 5;
    private const int BatchSize = 5;

    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        var pending = await repository.ListReleasePackagesAwaitingPreviewAsync(MaxAttempts, BatchSize, cancellationToken);
        var processed = 0;
        foreach (var package in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessAsync(package, cancellationToken);
            processed++;
        }
        return processed;
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
            var sources = await repository.ListReleasePreviewSourcesAsync(package.Id, cancellationToken);
            if (sources.Count == 0)
            {
                await repository.MarkReleasePreviewStateAsync(package.Id, ReleasePreviewState.Succeeded, null, attempts, timeProvider.GetUtcNow(), cancellationToken);
                return true;
            }
            var vaultRoot = StorageLocationPolicy.Normalize(project.VaultLocation);
            var stagingDirectory = StorageLocationPolicy.ResolveUnder(vaultRoot, Path.Combine(".release-staging", package.Number));
            Directory.CreateDirectory(stagingDirectory);
            var previews = await previewConverter.GenerateAsync(package, project, sources, stagingDirectory, cancellationToken);
            var releasedVersions = await repository.AttachReleasePreviewArtifactsAsync(package.Id, previews, cancellationToken);
            CopyPreviewsIntoPublishedPath(package, vaultRoot, releasedVersions);
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
    }

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
