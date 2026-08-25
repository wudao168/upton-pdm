using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryProgramTemplateRepository(TimeProvider timeProvider) : IProgramTemplateRepository
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, ProgramTemplate> templates = [];
    private readonly Dictionary<Guid, ProgramTemplateApprovalTask> tasks = [];
    private readonly Dictionary<ProgramTemplateAssetType, int> counters = [];

    public Task<string> ReserveCodeAsync(ProgramTemplateAssetType assetType, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var next = counters.GetValueOrDefault(assetType) + 1;
            counters[assetType] = next;
            return Task.FromResult($"{Prefix(assetType)}-{next:D4}");
        }
    }

    public Task<IReadOnlyList<ProgramTemplate>> ListPublishedAsync(CancellationToken cancellationToken)
    {
        lock (gate)
            return Task.FromResult<IReadOnlyList<ProgramTemplate>>(templates.Values
                .Where(item => !item.IsArchived && item.CurrentPublishedRevisionId is not null)
                .OrderBy(item => item.Code).ToArray());
    }

    public Task<IReadOnlyList<ProgramTemplate>> ListMineAsync(string actor, CancellationToken cancellationToken)
    {
        lock (gate)
            return Task.FromResult<IReadOnlyList<ProgramTemplate>>(templates.Values
                .Where(item => item.Revisions.Any(revision => string.Equals(revision.CreatedBy, actor, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(item => item.Code).ToArray());
    }

    public Task<ProgramTemplate?> FindAsync(Guid templateId, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult(templates.GetValueOrDefault(templateId));
    }

    public Task<ProgramTemplateRevision?> FindRevisionAsync(Guid revisionId, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult(templates.Values.SelectMany(item => item.Revisions).FirstOrDefault(item => item.Id == revisionId));
    }

    public Task<ProgramTemplate> CreateAsync(ProgramTemplate template, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            templates.Add(template.Id, template);
            return Task.FromResult(template);
        }
    }

    public Task<ProgramTemplateRevision> CreateRevisionAsync(ProgramTemplateRevision revision, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var template = RequireTemplate(revision.TemplateId);
            templates[template.Id] = template with { Revisions = template.Revisions.Append(revision).ToArray() };
            return Task.FromResult(revision);
        }
    }

    public Task<ProgramTemplateRevision> UpdateDraftAsync(ProgramTemplateRevision revision, long expectedRowVersion, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var current = RequireRevision(revision.Id);
            RequireRowVersion(current.RowVersion, expectedRowVersion);
            if (current.State != ProgramTemplateRevisionState.Draft) throw new PdmConflictException("程序模板草稿状态已变化。");
            var updated = revision with { RowVersion = current.RowVersion + 1 };
            ReplaceRevision(updated);
            return Task.FromResult(updated);
        }
    }

    public Task<ProgramTemplateRevision> AttachFileAsync(Guid revisionId, StoredProgramTemplateFile file, long expectedRowVersion, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var current = RequireRevision(revisionId);
            RequireRowVersion(current.RowVersion, expectedRowVersion);
            if (current.State != ProgramTemplateRevisionState.Draft) throw new PdmConflictException("程序模板草稿状态已变化。");
            var updated = file.Kind == ProgramTemplateAttachmentKind.Package
                ? current with
                {
                    PackageFileName = file.OriginalFileName,
                    PackageStoragePath = file.RelativePath,
                    PackageFileLength = file.Length,
                    PackageSha256 = file.Sha256,
                    RowVersion = current.RowVersion + 1
                }
                : current with
                {
                    EvidenceFileName = file.OriginalFileName,
                    EvidenceStoragePath = file.RelativePath,
                    EvidenceFileLength = file.Length,
                    EvidenceSha256 = file.Sha256,
                    RowVersion = current.RowVersion + 1
                };
            ReplaceRevision(updated);
            return Task.FromResult(updated);
        }
    }

    public Task<ProgramTemplateRevision> SubmitAsync(Guid revisionId, ProgramTemplateApprovalTask reviewTask, ProgramTemplateApprovalTask approvalTask, long expectedRowVersion, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var current = RequireRevision(revisionId);
            RequireRowVersion(current.RowVersion, expectedRowVersion);
            if (current.State != ProgramTemplateRevisionState.Draft) throw new PdmConflictException("程序模板草稿状态已变化。");
            var updated = current with
            {
                State = ProgramTemplateRevisionState.PendingReview,
                SubmittedAt = timeProvider.GetUtcNow(),
                RowVersion = current.RowVersion + 1
            };
            tasks.Add(reviewTask.Id, reviewTask);
            tasks.Add(approvalTask.Id, approvalTask);
            ReplaceRevision(updated);
            return Task.FromResult(updated);
        }
    }

    public Task<ProgramTemplateApprovalTask?> FindTaskAsync(Guid taskId, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult(tasks.GetValueOrDefault(taskId));
    }

    public Task<IReadOnlyList<ProgramTemplateApprovalTask>> ListRevisionTasksAsync(Guid revisionId, CancellationToken cancellationToken)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<ProgramTemplateApprovalTask>>(tasks.Values.Where(item => item.RevisionId == revisionId).OrderBy(item => item.Stage).ToArray());
    }

    public Task<IReadOnlyList<ProgramTemplateApprovalTask>> ListTasksAsync(string actor, string roleCode, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var visible = tasks.Values.Where(task =>
            {
                if (task.Decision is not null) return false;
                var revision = RequireRevision(task.RevisionId);
                return task.Stage == ProgramTemplateApprovalStage.Review
                    ? revision.State == ProgramTemplateRevisionState.PendingReview && string.Equals(task.Assignee, actor, StringComparison.OrdinalIgnoreCase)
                    : revision.State == ProgramTemplateRevisionState.PendingApproval && string.Equals(task.AssigneeRoleCode, roleCode, StringComparison.OrdinalIgnoreCase);
            }).OrderBy(item => item.CreatedAt).ToArray();
            return Task.FromResult<IReadOnlyList<ProgramTemplateApprovalTask>>(visible);
        }
    }

    public Task<ProgramTemplateDecisionResult> DecideAsync(Guid taskId, string actor, ProgramTemplateApprovalDecision decision, string? comment, IReadOnlyList<string> checklistItems, long expectedRowVersion, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var task = tasks.GetValueOrDefault(taskId) ?? throw new PdmNotFoundException("程序模板审批任务不存在。");
            RequireRowVersion(task.RowVersion, expectedRowVersion);
            if (task.Decision is not null) throw new PdmConflictException("程序模板审批任务已经处理。");
            var current = RequireRevision(task.RevisionId);
            var expectedState = task.Stage == ProgramTemplateApprovalStage.Review
                ? ProgramTemplateRevisionState.PendingReview
                : ProgramTemplateRevisionState.PendingApproval;
            if (current.State != expectedState) throw new PdmConflictException("程序模板审批状态已变化。");
            var decidedAt = timeProvider.GetUtcNow();
            var decidedTask = task with
            {
                Decision = decision,
                DecisionBy = actor,
                Comment = comment,
                ChecklistItems = checklistItems.ToArray(),
                DecidedAt = decidedAt,
                RowVersion = task.RowVersion + 1
            };
            tasks[task.Id] = decidedTask;
            var state = decision == ProgramTemplateApprovalDecision.Rejected
                ? ProgramTemplateRevisionState.Rejected
                : task.Stage == ProgramTemplateApprovalStage.Review
                    ? ProgramTemplateRevisionState.PendingApproval
                    : ProgramTemplateRevisionState.Published;
            var updated = current with
            {
                State = state,
                PublishedAt = state == ProgramTemplateRevisionState.Published ? decidedAt : current.PublishedAt,
                RowVersion = current.RowVersion + 1
            };
            if (state == ProgramTemplateRevisionState.Published)
            {
                var template = RequireTemplate(current.TemplateId);
                var revisions = template.Revisions.Select(item => item.Id == updated.Id
                    ? updated
                    : item.Id == template.CurrentPublishedRevisionId
                        ? item with { State = ProgramTemplateRevisionState.Superseded, RowVersion = item.RowVersion + 1 }
                        : item).ToArray();
                templates[template.Id] = template with { CurrentPublishedRevisionId = updated.Id, Revisions = revisions };
            }
            else ReplaceRevision(updated);
            return Task.FromResult(new ProgramTemplateDecisionResult(updated, decidedTask));
        }
    }

    public Task<ProgramTemplate> SetArchivedAsync(Guid templateId, bool archived, string actor, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var template = RequireTemplate(templateId);
            var revisions = template.Revisions.Select(item => item.Id == template.CurrentPublishedRevisionId
                ? item with { State = archived ? ProgramTemplateRevisionState.Archived : ProgramTemplateRevisionState.Published, RowVersion = item.RowVersion + 1 }
                : item).ToArray();
            var updated = template with { IsArchived = archived, Revisions = revisions };
            templates[template.Id] = updated;
            return Task.FromResult(updated);
        }
    }

    private ProgramTemplate RequireTemplate(Guid templateId) =>
        templates.GetValueOrDefault(templateId) ?? throw new PdmNotFoundException("程序模板不存在。");

    private ProgramTemplateRevision RequireRevision(Guid revisionId) =>
        templates.Values.SelectMany(item => item.Revisions).FirstOrDefault(item => item.Id == revisionId)
        ?? throw new PdmNotFoundException("程序模板候选版本不存在。");

    private void ReplaceRevision(ProgramTemplateRevision revision)
    {
        var template = RequireTemplate(revision.TemplateId);
        templates[template.Id] = template with { Revisions = template.Revisions.Select(item => item.Id == revision.Id ? revision : item).ToArray() };
    }

    private static void RequireRowVersion(long current, long expected)
    {
        if (current != expected) throw new PdmConflictException("数据已被其他用户更新，请刷新后重试。");
    }

    private static string Prefix(ProgramTemplateAssetType type) => type switch
    {
        ProgramTemplateAssetType.PlcFunctionBlock => "PT-FB",
        ProgramTemplateAssetType.PlcProgram => "PT-PLC",
        _ => "PT-HMI"
    };
}
