using System.Collections.Concurrent;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryPdmRepository : IPdmRepository
{
    private readonly object gate = new();
    private readonly ConcurrentDictionary<Guid, Project> projects = new();
    private readonly ConcurrentDictionary<Guid, PdmDocument> documents = new();
    private readonly ConcurrentDictionary<Guid, UserAccount> users = new();
    private readonly ConcurrentDictionary<Guid, ReleasePackage> packages = new();
    private readonly ConcurrentQueue<AuditEntry> audits = new();
    private readonly IReadOnlyList<BomItem> bomItems;
    private DocumentReferenceNode referenceTree;

    public InMemoryPdmRepository(TimeProvider timeProvider)
    {
        var project = SeedData.Project();
        projects[project.Id] = project;
        foreach (var document in SeedData.Documents(timeProvider.GetUtcNow()))
        {
            documents[document.Id] = document;
        }

        referenceTree = SeedData.Tree(documents);
        bomItems = SeedData.Bom();
        var package = SeedData.ReleasePackage(timeProvider.GetUtcNow());
        packages[package.Id] = package;
    }

    public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Project>>(projects.Values.OrderBy(project => project.Code).ToArray());

    public Task<Project?> FindProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        projects.TryGetValue(projectId, out var project);
        return Task.FromResult(project);
    }

    public Task<IReadOnlyList<PdmDocument>> ListDocumentsAsync(Guid projectId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PdmDocument>>(documents.Values.Where(document => document.ProjectId == projectId).OrderBy(document => document.DrawingNumber).ThenBy(document => document.Kind).ToArray());

    public Task<PdmDocument?> FindDocumentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        documents.TryGetValue(documentId, out var document);
        return Task.FromResult(document);
    }

    public Task<DocumentReferenceNode?> GetReferenceTreeAsync(Guid projectId, CancellationToken cancellationToken) =>
        Task.FromResult<DocumentReferenceNode?>(projectId == SeedData.ProjectId ? referenceTree : null);

    public Task<IReadOnlyList<BomItem>> GetBomAsync(Guid projectId, BomKind kind, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BomItem>>(bomItems.Where(item => item.ProjectId == projectId && item.Kind == kind).OrderBy(item => item.Sequence).ToArray());

    public Task<IReadOnlyList<ReleasePackage>> ListReleasePackagesAsync(Guid projectId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ReleasePackage>>(packages.Values.Where(package => package.ProjectId == projectId).OrderByDescending(package => package.CreatedAt).ToArray());

    public Task<ReleasePackage?> FindReleasePackageAsync(Guid releasePackageId, CancellationToken cancellationToken)
    {
        packages.TryGetValue(releasePackageId, out var package);
        return Task.FromResult(package);
    }

    public Task<PdmDocument> CheckoutAsync(Guid documentId, string actor, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!documents.TryGetValue(documentId, out var document))
            {
                throw new PdmNotFoundException("图档不存在。 ");
            }

            if (document.CheckedOutBy is not null && !string.Equals(document.CheckedOutBy, actor, StringComparison.OrdinalIgnoreCase))
            {
                throw new PdmConflictException($"图档正在由{document.CheckedOutBy}编辑。 ");
            }

            var updated = document with { CheckedOutBy = actor, UpdatedAt = DateTimeOffset.UtcNow };
            documents[documentId] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<PdmDocument> CheckInAsync(Guid documentId, string actor, RevisionLabel nextRevision, CadReferenceSnapshot snapshot, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!documents.TryGetValue(documentId, out var document))
            {
                throw new PdmNotFoundException("图档不存在。 ");
            }

            if (!string.Equals(document.CheckedOutBy, actor, StringComparison.OrdinalIgnoreCase))
            {
                throw new PdmConflictException("只有当前编辑人员可以提交存档。 ");
            }

            var updated = document with { CheckedOutBy = null, Revision = nextRevision, UpdatedAt = DateTimeOffset.UtcNow };
            documents[documentId] = updated;
            referenceTree = snapshot.Root;
            return Task.FromResult(updated);
        }
    }

    public Task<ReleasePackage> CreateReleasePackageAsync(ReleasePackage package, CancellationToken cancellationToken)
    {
        if (!packages.TryAdd(package.Id, package))
        {
            throw new PdmConflictException("发布包编号已经存在。 ");
        }

        return Task.FromResult(package);
    }

    public Task<ReleasePackage> DecideApprovalAsync(Guid taskId, string actor, ApprovalDecision decision, string? comment, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var package = packages.Values.FirstOrDefault(candidate => candidate.ApprovalTasks.Any(task => task.Id == taskId))
                ?? throw new PdmNotFoundException("审批任务不存在。 ");
            var task = package.ApprovalTasks.Single(item => item.Id == taskId);
            if (task.Decision is not null)
            {
                throw new PdmConflictException("审批任务已经处理。 ");
            }

            if (!string.Equals(task.Assignee, actor, StringComparison.OrdinalIgnoreCase) && !string.Equals(actor, "admin", StringComparison.OrdinalIgnoreCase))
            {
                throw new PdmRuleException("只能处理分配给自己的审批任务。 ");
            }

            var expectedState = task.Stage == ApprovalStage.ProcessReview ? ReleasePackageState.ProcessReview : ReleasePackageState.Approval;
            if (package.State != expectedState)
            {
                throw new PdmConflictException("当前发布包尚未到达该审批节点。 ");
            }

            var updatedTask = task with
            {
                Decision = decision,
                DecisionBy = actor,
                Comment = comment,
                DecidedAt = DateTimeOffset.UtcNow
            };
            var updatedTasks = package.ApprovalTasks.Select(item => item.Id == taskId ? updatedTask : item).ToArray();
            var nextState = decision == ApprovalDecision.Rejected
                ? ReleasePackageState.Rejected
                : task.Stage == ApprovalStage.ProcessReview
                    ? ReleasePackageState.Approval
                    : ReleasePackageState.Publishing;
            var updated = package with { ApprovalTasks = updatedTasks, State = nextState };
            packages[package.Id] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task MarkPublishedAsync(Guid releasePackageId, string publishedPath, DateTimeOffset publishedAt, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!packages.TryGetValue(releasePackageId, out var package))
            {
                throw new PdmNotFoundException("发布包不存在。 ");
            }

            packages[releasePackageId] = package with
            {
                State = ReleasePackageState.Published,
                PublishedPath = publishedPath,
                PublishedAt = publishedAt
            };
            return Task.CompletedTask;
        }
    }

    public Task MarkPublishFailedAsync(Guid releasePackageId, string error, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!packages.TryGetValue(releasePackageId, out var package))
            {
                throw new PdmNotFoundException("发布包不存在。 ");
            }

            packages[releasePackageId] = package with { State = ReleasePackageState.PublishFailed };
            return Task.CompletedTask;
        }
    }

    public Task<UserAccount?> FindUserAsync(string username, CancellationToken cancellationToken)
    {
        var user = users.Values.FirstOrDefault(item => string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(user);
    }

    public Task<int> CountUsersAsync(CancellationToken cancellationToken) => Task.FromResult(users.Count);

    public Task CreateUserAsync(UserAccount user, CancellationToken cancellationToken)
    {
        if (users.Values.Any(item => string.Equals(item.Username, user.Username, StringComparison.OrdinalIgnoreCase)) || !users.TryAdd(user.Id, user))
        {
            throw new PdmConflictException("用户名已经存在。 ");
        }

        return Task.CompletedTask;
    }

    public Task AppendAuditAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        audits.Enqueue(entry);
        return Task.CompletedTask;
    }
}
