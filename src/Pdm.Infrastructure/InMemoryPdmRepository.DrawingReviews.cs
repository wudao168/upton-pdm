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

    public Task<DrawingReviewPackage?> FindDrawingReviewPackageAsync(Guid packageId, CancellationToken cancellationToken)
    {
        drawingReviewPackages.TryGetValue(packageId, out var package);
        return Task.FromResult(package);
    }

    public Task<DrawingReviewPackage> CreateDrawingReviewPackageAsync(DrawingReviewPackage package, CancellationToken cancellationToken)
    {
        if (!drawingReviewPackages.TryAdd(package.Id, package))
            throw new PdmConflictException("图纸审核单已经存在。");
        return Task.FromResult(package);
    }

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
                cadPropertyWritebacks[request.Drawing.Id] = request.Drawing;
            }
            package = package with
            {
                State = DrawingReviewPackageState.WritingProperties,
                Items = package.Items.Select(item =>
                {
                    var request = byItem[item.Id];
                    return item with { ModelWritebackId = request.Model.Id, DrawingWritebackId = request.Drawing.Id };
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
            var approved = succeeded && items.All(item => item.ModelState == DrawingReviewTargetState.Marked && item.DrawingState == DrawingReviewTargetState.Marked);
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
