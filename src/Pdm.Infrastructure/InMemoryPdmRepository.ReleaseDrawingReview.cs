using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class InMemoryPdmRepository
{
    public Task<ReleasePackage> SaveReleaseDrawingReviewAsync(Guid packageId, ReleaseDrawingReview review,
        int expectedRevision, Guid? approvalTaskId, CancellationToken ct, Guid? documentId = null)
    {
        lock(gate)
        {
            var package=packages.GetValueOrDefault(packageId) ?? throw new PdmNotFoundException("发布包不存在。");
            var current=package.ApprovalTasks.OrderBy(t=>t.StepOrder).FirstOrDefault(t=>t.Decision is null);
            if ((package.DrawingReview?.Revision ?? 0)!=expectedRevision
                || (approvalTaskId is null ? package.State is not (ReleasePackageState.Draft or ReleasePackageState.Rejected)
                    : package.State is not (ReleasePackageState.ProcessReview or ReleasePackageState.Approval) || current?.Id!=approvalTaskId))
                throw new PdmConflictException("图纸审核资料或审批节点已变化，请刷新后重试。");
            if (documentId is Guid id && current is not null && package.LocksDocuments
                && package.VersionBindings?.Any(binding => binding.DocumentId == id && binding.Publish && binding.RequiresPromotion) == true)
            {
                if (!documents.TryGetValue(id, out var drawing) || drawing.DeletedAt is not null || drawing.State == DocumentLifecycleState.Obsolete)
                    throw new PdmConflictException("送审图纸已失效，请核对当前发布包。");
                var item = review.Items.Single(value => value.DocumentId == id);
                var hasIssue = ReleaseDrawingReview.HasIssue(review, item, current.Id);
                if (!hasIssue && drawing.CheckedOutBy is not null)
                    throw new PdmConflictException("图纸正在编辑，请先结束编辑，再撤回退回。");
                documents[id] = drawing with { State = hasIssue ? DocumentLifecycleState.Work : DocumentLifecycleState.InReview };
            }
            packages[packageId]=package with {DrawingReview=review};
            return Task.FromResult(packages[packageId]);
        }
    }

    private PdmDocument AdoptArchivedReviewDrawing(PdmDocument document, DocumentVersion version)
    {
        var replacements = new List<ReleasePackage>();
        foreach (var package in packages.Values.Where(value => value.ProjectId == document.ProjectId))
        {
            var task = ReleaseDrawingRevision.CurrentTask(package);
            var review = package.DrawingReview;
            var drawing = review?.Items.SingleOrDefault(item => item.DocumentId == document.Id);
            if (task is null || drawing is null || !ReleaseDrawingReview.HasIssue(review!, drawing, task.Id)) continue;
            if (document.CheckedOutBy is not null)
                throw new PdmRuleException("退回图纸须结束编辑并提交存档，才能自动进入复审。");
            var designer = users.Values.FirstOrDefault(user => user.Username.Equals(version.CreatedBy, StringComparison.OrdinalIgnoreCase))?.DisplayName;
            replacements.Add(ReleaseDrawingRevision.Replace(package, task, drawing, versions[drawing.VersionId], version, designer));
        }
        foreach (var replacement in replacements) packages[replacement.Id] = replacement;
        return replacements.Any(value => value.LocksDocuments && value.VersionBindings!.Any(binding => binding.DocumentId == document.Id && binding.Publish))
            ? document with { State = DocumentLifecycleState.InReview } : document;
    }
}
