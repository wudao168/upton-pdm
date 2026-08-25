using System.Collections.Concurrent;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class InMemoryPdmRepository
{
    private readonly ConcurrentDictionary<Guid, DrawingReviewPackage> drawingReviewPackages = new();

    public Task<IReadOnlyList<DrawingReviewPackage>> ListDrawingReviewPackagesAsync(Guid projectId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DrawingReviewPackage>>(drawingReviewPackages.Values
            .Where(package => package.ProjectId == projectId)
            .OrderByDescending(package => package.CreatedAt)
            .ToArray());

    public Task<IReadOnlySet<Guid>> ListActiveDrawingReviewDocumentIdsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlySet<Guid>>(drawingReviewPackages.Values
                .Where(package => package.ProjectId == projectId && IsActiveDrawingReview(package))
                .SelectMany(package => package.Items.SelectMany(item => item.DrawingDocumentId.HasValue
                    ? new[] { item.ModelDocumentId, item.DrawingDocumentId.Value }
                    : new[] { item.ModelDocumentId }))
                .ToHashSet());
        }
    }

    public Task<bool> IsDocumentUnderActiveDrawingReviewAsync(Guid documentId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(IsDocumentUnderActiveDrawingReview(documentId));
        }
    }

    public Task<bool> IsActiveDrawingReviewWritebackAsync(Guid documentId, Guid writebackId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            return Task.FromResult(IsActiveDrawingReviewWriteback(documentId, writebackId));
        }
    }

    public Task<DrawingReviewPackage?> FindDrawingReviewPackageAsync(Guid packageId, CancellationToken cancellationToken)
    {
        drawingReviewPackages.TryGetValue(packageId, out var package);
        return Task.FromResult(package);
    }

    public Task<DrawingReviewPackage> CreateDrawingReviewPackageAsync(DrawingReviewPackage package, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var documentIds = package.Items
                .SelectMany(item => item.DrawingDocumentId.HasValue
                    ? new[] { item.ModelDocumentId, item.DrawingDocumentId.Value }
                    : new[] { item.ModelDocumentId })
                .Distinct()
                .ToArray();
            if (documentIds.Any(documentId => !documents.TryGetValue(documentId, out var document) || !string.IsNullOrWhiteSpace(document.CheckedOutBy)))
                throw new PdmConflictException("待审核图档仍处于签出编辑状态，请先提交存档或放弃编辑。");
            if (documentIds.Any(IsDocumentUnderActiveDrawingReview))
                throw new PdmConflictException("待审核图档已经处于图纸审核中，请刷新后重试。");
            if (!drawingReviewPackages.TryAdd(package.Id, package))
                throw new PdmConflictException("图纸审核单已经存在。");
            return Task.FromResult(package);
        }
    }

    public Task<DrawingReviewPackage> WithdrawDrawingReviewPackageAsync(Guid packageId, string actor, DateTimeOffset withdrawnAt, string reason, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!drawingReviewPackages.TryGetValue(packageId, out var package))
                throw new PdmNotFoundException("图纸审核单不存在。");
            if (package.State != DrawingReviewPackageState.InReview)
                throw new PdmConflictException("只有审核中的图纸审核单可以撤销。");
            package = package with
            {
                State = DrawingReviewPackageState.Withdrawn,
                WithdrawnBy = actor,
                WithdrawnAt = withdrawnAt,
                WithdrawalReason = reason
            };
            drawingReviewPackages[package.Id] = package;
            return Task.FromResult(package);
        }
    }

    private static bool IsActiveDrawingReview(DrawingReviewPackage package) =>
        package.State is DrawingReviewPackageState.InReview or DrawingReviewPackageState.WritingProperties;

    private bool IsDocumentUnderActiveDrawingReview(Guid documentId) => drawingReviewPackages.Values.Any(package =>
        IsActiveDrawingReview(package)
        && package.Items.Any(item => item.ModelDocumentId == documentId || item.DrawingDocumentId == documentId));

    private bool IsActiveDrawingReviewWriteback(Guid documentId, Guid writebackId) =>
        cadPropertyWritebacks.TryGetValue(writebackId, out var writeback)
        && writeback.SourceDocumentId == documentId
        && writeback.Status == CadPropertyWritebackStatus.InProgress
        && drawingReviewPackages.Values.Any(package =>
            package.State == DrawingReviewPackageState.WritingProperties
            && package.Items.Any(item =>
                item.ModelDocumentId == documentId && item.ModelWritebackId == writebackId
                || item.DrawingDocumentId == documentId && item.DrawingWritebackId == writebackId));

    public Task<DrawingReviewPackage> AddDrawingReviewMarkupAsync(DrawingReviewMarkup markup, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!drawingReviewPackages.TryGetValue(markup.PackageId, out var package))
                throw new PdmNotFoundException("图纸审核单不存在。");
            if (!package.Items.Any(item => item.Id == markup.ItemId))
                throw new PdmNotFoundException("图纸审核项不存在。");
            if (package.Markups.Any(item => item.Id == markup.Id))
                throw new PdmConflictException("图纸批注已经存在。");
            package = package with { Markups = package.Markups.Append(markup).ToArray() };
            drawingReviewPackages[package.Id] = package;
            return Task.FromResult(package);
        }
    }

    public Task<DrawingReviewPackage> ResolveDrawingReviewMarkupAsync(Guid markupId, string actor, DateTimeOffset resolvedAt, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var package = drawingReviewPackages.Values.FirstOrDefault(candidate => candidate.Markups.Any(markup => markup.Id == markupId))
                ?? throw new PdmNotFoundException("图纸批注不存在。");
            var current = package.Markups.Single(markup => markup.Id == markupId);
            if (current.State == DrawingReviewMarkupState.Resolved)
                throw new PdmConflictException("图纸批注已经关闭。");
            var markups = package.Markups.Select(markup => markup.Id != markupId
                ? markup
                : markup with { State = DrawingReviewMarkupState.Resolved, ResolvedBy = actor, ResolvedAt = resolvedAt }).ToArray();
            package = package with { Markups = markups };
            drawingReviewPackages[package.Id] = package;
            return Task.FromResult(package);
        }
    }

    public Task<DrawingReviewPackage> DecideDrawingReviewTargetAsync(
        Guid itemId,
        DrawingReviewTarget target,
        DrawingReviewTargetState state,
        string reviewer,
        string reviewerName,
        DateTimeOffset reviewedAt,
        string? comment,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var package = drawingReviewPackages.Values.FirstOrDefault(candidate => candidate.Items.Any(item => item.Id == itemId))
                ?? throw new PdmNotFoundException("图纸审核项不存在。");
            var items = package.Items.Select(item => item.Id != itemId ? item : target == DrawingReviewTarget.Model3D
                ? item with { ModelState = state, ModelReviewer = reviewer, ModelReviewerName = reviewerName, ModelReviewedAt = reviewedAt, ModelComment = comment }
                : item with { DrawingState = state, DrawingReviewer = reviewer, DrawingReviewerName = reviewerName, DrawingReviewedAt = reviewedAt, DrawingComment = comment }).ToArray();
            package = package with
            {
                Items = items,
                State = items.Any(item => item.ModelState == DrawingReviewTargetState.ChangesRequested || item.DrawingState == DrawingReviewTargetState.ChangesRequested)
                    ? DrawingReviewPackageState.ChangesRequested
                    : DrawingReviewPackageState.InReview
            };
            drawingReviewPackages[package.Id] = package;
            return Task.FromResult(package);
        }
    }

    public Task<DrawingReviewPackage> QueueDrawingReviewWritebacksAsync(Guid packageId, IReadOnlyList<DrawingReviewWritebackRequest> requests, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!drawingReviewPackages.TryGetValue(packageId, out var package))
                throw new PdmNotFoundException("图纸审核单不存在。");
            var byItem = requests.ToDictionary(request => request.ItemId);
            if (byItem.Count != package.Items.Count || package.Items.Any(item => !byItem.ContainsKey(item.Id)))
                throw new PdmRuleException("图纸审核属性写回必须同时覆盖审核单中的全部3D和2D图档。");
            foreach (var request in requests)
            {
                cadPropertyWritebacks[request.Model.Id] = request.Model;
                if (request.Drawing is not null)
                    cadPropertyWritebacks[request.Drawing.Id] = request.Drawing;
            }
            package = package with
            {
                State = DrawingReviewPackageState.WritingProperties,
                Items = package.Items.Select(item =>
                {
                    var request = byItem[item.Id];
                    if (item.RequiresDrawingReview != (request.Drawing is not null))
                        throw new PdmRuleException("图纸审核属性写回与审核目标不一致。");
                    return item with { ModelWritebackId = request.Model.Id, DrawingWritebackId = request.Drawing?.Id };
                }).ToArray()
            };
            drawingReviewPackages[package.Id] = package;
            return Task.FromResult(package);
        }
    }

    public Task<DrawingReviewPackage?> RecordDrawingReviewWritebackResultAsync(Guid writebackId, Guid? resultVersionId, bool succeeded, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var package = drawingReviewPackages.Values.FirstOrDefault(candidate => candidate.Items.Any(item => item.ModelWritebackId == writebackId || item.DrawingWritebackId == writebackId));
            if (package is null) return Task.FromResult<DrawingReviewPackage?>(null);
            var items = package.Items.Select(item =>
            {
                if (item.ModelWritebackId == writebackId)
                    return succeeded ? item with { ModelState = DrawingReviewTargetState.Marked, ModelResultVersionId = resultVersionId } : item;
                if (item.DrawingWritebackId == writebackId)
                    return succeeded ? item with { DrawingState = DrawingReviewTargetState.Marked, DrawingResultVersionId = resultVersionId } : item;
                return item;
            }).ToArray();
            var approved = succeeded && items.All(item => item.ModelState == DrawingReviewTargetState.Marked
                && item.DrawingState is DrawingReviewTargetState.Marked or DrawingReviewTargetState.NotRequired);
            package = package with
            {
                Items = items,
                State = succeeded ? approved ? DrawingReviewPackageState.Approved : DrawingReviewPackageState.WritingProperties : DrawingReviewPackageState.Stale,
                ApprovedAt = approved ? timeProvider.GetUtcNow() : null
            };
            drawingReviewPackages[package.Id] = package;
            return Task.FromResult<DrawingReviewPackage?>(package);
        }
    }

    public Task<ReleasePackage?> FindReleasePackageByApprovalTaskAsync(Guid taskId, CancellationToken cancellationToken) =>
        Task.FromResult(packages.Values.FirstOrDefault(package => package.ApprovalTasks.Any(task => task.Id == taskId)));
}
