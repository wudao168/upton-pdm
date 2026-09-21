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
                .SelectMany(package => package.Items
                    // 已驳回的图档立即释放编辑锁，该审核单其余图档仍保持锁定。
                    .Where(item => item.DrawingDocumentId.HasValue && !IsChangesRequested(item))
                    .Select(item => item.DrawingDocumentId!.Value))
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
                .Select(item => item.DrawingDocumentId ?? throw new PdmRuleException("非标件缺少唯一关联的2D工程图。"))
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
            if (package.State is not (DrawingReviewPackageState.InReview
                or DrawingReviewPackageState.PendingSupervisorApproval
                or DrawingReviewPackageState.ChangesRequested))
                throw new PdmConflictException("只有审核中或已驳回的图纸审核单可以撤销。");
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
        package.State is DrawingReviewPackageState.InReview or DrawingReviewPackageState.PendingSupervisorApproval or DrawingReviewPackageState.WritingProperties;

    private bool IsDocumentUnderActiveDrawingReview(Guid documentId) => drawingReviewPackages.Values.Any(package =>
        IsActiveDrawingReview(package)
        && package.Items.Any(item => item.DrawingDocumentId == documentId && !IsChangesRequested(item)));

    private static bool IsChangesRequested(DrawingReviewItem item) =>
        item.DrawingState == DrawingReviewTargetState.ChangesRequested;

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
            var supervisorNode = package.State == DrawingReviewPackageState.PendingSupervisorApproval;
            if (!supervisorNode && package.State != DrawingReviewPackageState.InReview)
                throw new PdmConflictException("当前图纸审核单不允许继续审核。");
            var items = package.Items.Select(item => item.Id != itemId ? item : target == DrawingReviewTarget.Model3D
                ? item with { ModelState = state, ModelReviewer = reviewer, ModelReviewerName = reviewerName, ModelReviewedAt = reviewedAt, ModelComment = comment }
                : item with { DrawingState = state, DrawingReviewer = reviewer, DrawingReviewerName = reviewerName, DrawingReviewedAt = reviewedAt, DrawingComment = comment }).ToArray();
            package = package with
            {
                Items = items,
                // 单张图纸驳回只影响该图纸，审核单其余图纸继续并行审核；
                // 机械主管按图驳回后审核单回到审图节点重新审核。
                State = DrawingReviewPackageState.InReview,
                SupervisorReviewedBy = supervisorNode ? null : package.SupervisorReviewedBy,
                SupervisorReviewedByName = supervisorNode ? null : package.SupervisorReviewedByName,
                SupervisorReviewedAt = supervisorNode ? null : package.SupervisorReviewedAt,
                SupervisorComment = supervisorNode ? null : package.SupervisorComment
            };
            drawingReviewPackages[package.Id] = package;
            return Task.FromResult(package);
        }
    }

    public Task<DrawingReviewPackage> RevokeDrawingReviewTargetAsync(Guid itemId, DrawingReviewTarget target, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var package = drawingReviewPackages.Values.FirstOrDefault(candidate => candidate.Items.Any(item => item.Id == itemId))
                ?? throw new PdmNotFoundException("图纸审核项不存在。");
            if (package.State is not (DrawingReviewPackageState.InReview or DrawingReviewPackageState.PendingSupervisorApproval))
                throw new PdmConflictException("当前图纸审核单不允许撤销审核通过。");
            var currentItem = package.Items.Single(item => item.Id == itemId);
            var current = target == DrawingReviewTarget.Model3D ? currentItem.ModelState : currentItem.DrawingState;
            if (current is not (DrawingReviewTargetState.Approved or DrawingReviewTargetState.Marked or DrawingReviewTargetState.ChangesRequested))
                throw new PdmConflictException("该图档没有可撤销的审核结论。");
            var items = package.Items.Select(item => item.Id != itemId ? item : target == DrawingReviewTarget.Model3D
                ? item with { ModelState = DrawingReviewTargetState.Pending, ModelReviewer = null, ModelReviewerName = null, ModelReviewedAt = null, ModelComment = null }
                : item with { DrawingState = DrawingReviewTargetState.Pending, DrawingReviewer = null, DrawingReviewerName = null, DrawingReviewedAt = null, DrawingComment = null }).ToArray();
            package = package with
            {
                Items = items,
                State = DrawingReviewPackageState.InReview,
                SupervisorReviewedBy = null,
                SupervisorReviewedByName = null,
                SupervisorReviewedAt = null,
                SupervisorComment = null
            };
            drawingReviewPackages[package.Id] = package;
            return Task.FromResult(package);
        }
    }

    public Task<DrawingReviewPackage> ResubmitDrawingReviewItemAsync(
        Guid itemId,
        Guid drawingVersionId,
        string drawingRevision,
        string drawingSha256,
        string drawingCreatedBy,
        CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var package = drawingReviewPackages.Values.FirstOrDefault(candidate => candidate.Items.Any(item => item.Id == itemId))
                ?? throw new PdmNotFoundException("图纸审核项不存在。");
            if (package.State is not (DrawingReviewPackageState.InReview or DrawingReviewPackageState.PendingSupervisorApproval))
                throw new PdmConflictException("当前图纸审核单不允许重新提交审核。");
            if (package.Items.Single(item => item.Id == itemId).DrawingState != DrawingReviewTargetState.ChangesRequested)
                throw new PdmConflictException("只有已驳回（待修改）的图档可以重新提交审核。");
            var items = package.Items.Select(item => item.Id != itemId ? item : item with
            {
                DrawingState = DrawingReviewTargetState.Pending,
                DrawingVersionId = drawingVersionId,
                DrawingRevision = drawingRevision,
                DrawingSha256 = drawingSha256,
                DrawingCreatedBy = drawingCreatedBy,
                DrawingReviewer = null,
                DrawingReviewerName = null,
                DrawingReviewedAt = null,
                DrawingComment = null,
                DrawingResultVersionId = null,
                DrawingWritebackId = null
            }).ToArray();
            package = package with
            {
                Items = items,
                State = DrawingReviewPackageState.InReview,
                SupervisorReviewedBy = null,
                SupervisorReviewedByName = null,
                SupervisorReviewedAt = null,
                SupervisorComment = null
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
                throw new PdmRuleException("图纸审核属性写回必须覆盖审核单中的全部2D图纸。");
            foreach (var request in requests)
            {
                cadPropertyWritebacks[request.Drawing.Id] = request.Drawing;
            }
            package = package with
            {
                State = DrawingReviewPackageState.WritingProperties,
                Items = package.Items.Select(item =>
                {
                    var request = byItem[item.Id];
                    if (!item.RequiresDrawingReview || item.DrawingState != DrawingReviewTargetState.Approved)
                        throw new PdmRuleException("图纸审核属性写回与审核目标不一致。");
                    return item with { DrawingWritebackId = request.Drawing.Id };
                }).ToArray()
            };
            drawingReviewPackages[package.Id] = package;
            return Task.FromResult(package);
        }
    }

    public Task<DrawingReviewPackage> AdvanceDrawingReviewToSupervisorAsync(Guid packageId, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!drawingReviewPackages.TryGetValue(packageId, out var package)) throw new PdmNotFoundException("图纸审核单不存在。");
            if (package.State != DrawingReviewPackageState.InReview
                || string.IsNullOrWhiteSpace(package.Supervisor) || package.Items.Any(item => item.DrawingState != DrawingReviewTargetState.Approved))
                throw new PdmConflictException("图纸尚未全部通过审图节点，不能提交机械主管批准。");
            package = package with { State = DrawingReviewPackageState.PendingSupervisorApproval };
            drawingReviewPackages[package.Id] = package;
            return Task.FromResult(package);
        }
    }

    public Task<DrawingReviewPackage> DecideDrawingReviewSupervisorAsync(Guid packageId, DrawingReviewDecision decision, string reviewer, string reviewerName, DateTimeOffset reviewedAt, string? comment, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!drawingReviewPackages.TryGetValue(packageId, out var package)) throw new PdmNotFoundException("图纸审核单不存在。");
            if (package.State != DrawingReviewPackageState.PendingSupervisorApproval || package.SupervisorReviewedAt.HasValue)
                throw new PdmConflictException("机械主管批准任务已处理或当前状态已变化，请刷新后重试。");
            package = package with
            {
                State = decision == DrawingReviewDecision.Approve ? DrawingReviewPackageState.PendingSupervisorApproval : DrawingReviewPackageState.ChangesRequested,
                SupervisorReviewedBy = reviewer,
                SupervisorReviewedByName = reviewerName,
                SupervisorReviewedAt = reviewedAt,
                SupervisorComment = comment,
                // 机械主管整单驳回：单内仍为已通过的图纸一并置为待修改，设计者可直接改图后重新提交。
                Items = decision == DrawingReviewDecision.RequestChanges
                    ? package.Items.Select(item => item.DrawingState is DrawingReviewTargetState.Approved or DrawingReviewTargetState.Marked
                        ? item with { DrawingState = DrawingReviewTargetState.ChangesRequested, DrawingReviewer = reviewer, DrawingReviewerName = reviewerName, DrawingReviewedAt = reviewedAt, DrawingComment = comment }
                        : item).ToArray()
                    : package.Items
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
            var approved = succeeded && items.All(item => item.DrawingState is DrawingReviewTargetState.Marked or DrawingReviewTargetState.NotRequired);
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

    public Task<DrawingReviewPackage> AbandonDrawingReviewWritebacksAsync(Guid packageId, string actor, string reason, DateTimeOffset completedAt, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!drawingReviewPackages.TryGetValue(packageId, out var package)) throw new PdmNotFoundException("图纸审核单不存在。");
            if (package.State != DrawingReviewPackageState.WritingProperties)
                throw new PdmConflictException("只有正在写入审核标记的图纸审核单可以放弃写入。");
            foreach (var item in package.Items)
            {
                if (item.DrawingWritebackId is not Guid writebackId || !cadPropertyWritebacks.TryGetValue(writebackId, out var writeback)) continue;
                if (writeback.Status is not (CadPropertyWritebackStatus.Pending or CadPropertyWritebackStatus.InProgress)) continue;
                cadPropertyWritebacks[writebackId] = writeback with { Status = CadPropertyWritebackStatus.Superseded, CompletedAt = completedAt, LastError = reason };
            }
            package = package with { State = DrawingReviewPackageState.Approved, ApprovedAt = completedAt };
            drawingReviewPackages[package.Id] = package;
            return Task.FromResult(package);
        }
    }

    public Task<IReadOnlyList<Guid>> ListTimedOutDrawingReviewWritebackPackageIdsAsync(DateTimeOffset requestedBefore, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var result = new List<Guid>();
            foreach (var package in drawingReviewPackages.Values)
            {
                if (package.State != DrawingReviewPackageState.WritingProperties) continue;
                var writebacks = package.Items
                    .Where(item => item.DrawingWritebackId.HasValue)
                    .Select(item => cadPropertyWritebacks.TryGetValue(item.DrawingWritebackId!.Value, out var writeback) ? writeback : null)
                    .ToArray();
                if (writebacks.Length == 0 || writebacks.Any(writeback => writeback is null)) continue;
                if (writebacks.All(writeback => writeback!.Status is CadPropertyWritebackStatus.Pending or CadPropertyWritebackStatus.InProgress)
                    && writebacks.Max(writeback => writeback!.RequestedAt) < requestedBefore)
                    result.Add(package.Id);
            }
            return Task.FromResult<IReadOnlyList<Guid>>(result);
        }
    }

    public Task<ReleasePackage?> FindReleasePackageByApprovalTaskAsync(Guid taskId, CancellationToken cancellationToken) =>
        Task.FromResult(packages.Values.FirstOrDefault(package => package.ApprovalTasks.Any(task => task.Id == taskId)));
}
