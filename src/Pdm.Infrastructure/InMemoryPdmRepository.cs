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
    private readonly ConcurrentDictionary<Guid, DocumentVersion> versions = new();
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

    public Task<PdmDocument> RegisterDocumentAsync(RegisterDocumentCommand command, string actor, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!projects.TryGetValue(command.ProjectId, out var project) || !project.IsActive)
            {
                throw new PdmNotFoundException("项目不存在或已停用。");
            }

            var existing = documents.Values.FirstOrDefault(document =>
                document.ProjectId == command.ProjectId
                && string.Equals(document.FileName, command.FileName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return Task.FromResult(existing);
            }

            var document = new PdmDocument(
                Guid.NewGuid(),
                command.ProjectId,
                command.DrawingNumber,
                command.Name,
                command.FileName,
                command.Kind,
                DocumentLifecycleState.Work,
                RevisionLabel.InitialWork(),
                null,
                DateTimeOffset.UtcNow);
            documents[document.Id] = document;
            return Task.FromResult(document);
        }
    }

    public Task<bool> HasDocumentReadAccessAsync(Guid documentId, string actor, UserRole role, CancellationToken cancellationToken) =>
        Task.FromResult(documents.ContainsKey(documentId));

    public Task<IReadOnlyList<DocumentVersion>> ListDocumentVersionsAsync(Guid documentId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DocumentVersion>>(versions.Values.Where(version => version.DocumentId == documentId).OrderByDescending(version => version.CreatedAt).ToArray());

    public Task<DocumentVersion?> FindDocumentVersionAsync(Guid documentId, Guid versionId, CancellationToken cancellationToken)
    {
        versions.TryGetValue(versionId, out var version);
        return Task.FromResult(version?.DocumentId == documentId ? version : null);
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

    public Task<PdmDocument> CompleteEditWithoutChangesAsync(Guid documentId, string actor, string sha256, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!documents.TryGetValue(documentId, out var document)) throw new PdmNotFoundException("图档不存在。");
            if (!string.Equals(document.CheckedOutBy, actor, StringComparison.OrdinalIgnoreCase)) throw new PdmConflictException("只有当前编辑人员可以结束编辑。");
            var latest = versions.Values.Where(version => version.DocumentId == documentId).OrderByDescending(version => version.CreatedAt).FirstOrDefault()
                ?? throw new PdmConflictException("图档尚无存档版本，必须先提交W1。");
            if (!string.Equals(latest.Sha256, sha256, StringComparison.OrdinalIgnoreCase)) throw new PdmConflictException("文件已经发生变更，请使用提交存档。");
            var updated = document with { CheckedOutBy = null, UpdatedAt = DateTimeOffset.UtcNow };
            documents[documentId] = updated;
            return Task.FromResult(updated);
        }
    }

    public Task<PdmDocument> DiscardCheckoutAsync(Guid documentId, string actor, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!documents.TryGetValue(documentId, out var document)) throw new PdmNotFoundException("图档不存在。");
            if (!string.Equals(document.CheckedOutBy, actor, StringComparison.OrdinalIgnoreCase)) throw new PdmConflictException("只有当前编辑人员可以放弃编辑。");
            var updated = document with { CheckedOutBy = null, UpdatedAt = DateTimeOffset.UtcNow };
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

    public Task<DocumentCheckInResult> CheckInVersionAsync(Guid documentId, string actor, DocumentVersionCommit commit, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!documents.TryGetValue(documentId, out var document)) throw new PdmNotFoundException("图档不存在。");
            if (!string.Equals(document.CheckedOutBy, actor, StringComparison.OrdinalIgnoreCase)) throw new PdmConflictException("只有当前编辑人员可以提交存档。");
            var latest = versions.Values.Where(version => version.DocumentId == documentId).OrderByDescending(version => version.CreatedAt).FirstOrDefault();
            if (latest is not null && string.Equals(latest.Sha256, commit.File.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                var unchanged = document with { CheckedOutBy = null, UpdatedAt = DateTimeOffset.UtcNow };
                documents[documentId] = unchanged;
                return Task.FromResult(new DocumentCheckInResult(unchanged, null, false));
            }
            var revision = versions.Values.Any(version => version.DocumentId == documentId) || document.Revision.IsReleased ? document.Revision.NextWork() : RevisionLabel.InitialWork();
            var version = new DocumentVersion(Guid.NewGuid(), documentId, revision, DocumentVersionStatus.Work, commit.File.RelativePath, commit.File.Length, commit.File.Sha256, actor, DateTimeOffset.UtcNow, commit.ChangeNote, commit.Properties, commit.ReferenceSnapshot.Root, commit.MechanicalBomSnapshot, commit.ElectricalBomSnapshot, commit.SourceVersionId, commit.SourceDescription, null, null);
            versions[version.Id] = version;
            var updated = document with { CheckedOutBy = null, Revision = revision, State = DocumentLifecycleState.Work, UpdatedAt = version.CreatedAt };
            documents[documentId] = updated;
            referenceTree = commit.ReferenceSnapshot.Root;
            return Task.FromResult(new DocumentCheckInResult(updated, version, true));
        }
    }

    public Task<(PdmDocument Document, DocumentVersion Version)> RestoreVersionAsync(Guid documentId, Guid sourceVersionId, string actor, StoredFile restoredFile, string changeNote, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!documents.TryGetValue(documentId, out var document)) throw new PdmNotFoundException("图档不存在。");
            if (!versions.TryGetValue(sourceVersionId, out var source) || source.DocumentId != documentId) throw new PdmNotFoundException("历史版本不存在。");
            var revision = document.Revision.NextWork();
            var restored = source with { Id = Guid.NewGuid(), Revision = revision, Status = DocumentVersionStatus.Work, StorageRelativePath = restoredFile.RelativePath, FileLength = restoredFile.Length, Sha256 = restoredFile.Sha256, CreatedBy = actor, CreatedAt = DateTimeOffset.UtcNow, ChangeNote = changeNote, SourceVersionId = source.Id, SourceDescription = $"由{source.Revision.Display}恢复生成{revision.Display}", ApprovalTaskId = null, ReleasePackageId = null };
            versions[restored.Id] = restored;
            var updated = document with { Revision = revision, State = DocumentLifecycleState.Work, CheckedOutBy = null, UpdatedAt = restored.CreatedAt };
            documents[documentId] = updated;
            return Task.FromResult((updated, restored));
        }
    }

    public Task<DocumentVersion> PublishDocumentVersionAsync(Guid documentId, Guid sourceVersionId, Guid releasePackageId, Guid approvalTaskId, string actor, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!documents.TryGetValue(documentId, out var document)) throw new PdmNotFoundException("图档不存在。");
            if (!versions.TryGetValue(sourceVersionId, out var source) || source.DocumentId != documentId) throw new PdmNotFoundException("待发布工作版本不存在。");
            if (source.Status != DocumentVersionStatus.Work) throw new PdmConflictException("只能从工作版本生成正式版本。");
            if (!string.Equals(source.Revision.Display, document.Revision.Display, StringComparison.OrdinalIgnoreCase)) throw new PdmConflictException("只能发布图档当前最新的工作版本。");
            if (!packages.TryGetValue(releasePackageId, out var package) || package.State is not (ReleasePackageState.Publishing or ReleasePackageState.Published)) throw new PdmConflictException("发布包尚未审批通过，不能生成正式版本。");
            if (!package.ApprovalTasks.Any(task => task.Id == approvalTaskId && task.Stage == ApprovalStage.Approval && task.Decision == ApprovalDecision.Approved)) throw new PdmConflictException("最终批准记录与发布包不匹配或尚未批准。");
            var revision = source.Revision.Release();
            var released = source with { Id = Guid.NewGuid(), Revision = revision, Status = DocumentVersionStatus.Released, CreatedBy = actor, CreatedAt = DateTimeOffset.UtcNow, ChangeNote = $"审批发布{revision.Display}", SourceVersionId = source.Id, SourceDescription = $"由{source.Revision.Display}审批发布", ApprovalTaskId = approvalTaskId, ReleasePackageId = releasePackageId };
            versions[released.Id] = released;
            documents[documentId] = document with { Revision = revision, State = DocumentLifecycleState.Released, CheckedOutBy = null, UpdatedAt = released.CreatedAt };
            return Task.FromResult(released);
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
