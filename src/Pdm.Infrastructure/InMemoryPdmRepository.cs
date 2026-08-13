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
    private readonly ConcurrentDictionary<Guid, PdmCustomer> customers = new();
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<string>> projectResponsibles = new();
    private readonly ConcurrentDictionary<int, EquipmentTypeDefinition> equipmentTypes = new();
    private readonly ConcurrentDictionary<Guid, ReleasePackage> packages = new();
    private readonly ConcurrentDictionary<Guid, DocumentVersion> versions = new();
    private readonly ConcurrentQueue<AuditEntry> audits = new();
    private readonly Dictionary<Guid, int> projectCounters = new();
    private readonly Dictionary<Guid, int> serialCounters = new();
    private readonly Dictionary<(Guid OrganizationId, string CustomerCode), int> customerCounters = new();
    private readonly List<BomItem> bomItems;
    private DocumentReferenceNode referenceTree;
    private Guid referenceRootDocumentId = SeedData.RootDocumentId;
    private PdmSystemSettings systemSettings = new(@"D:\PDM\Vault", @"D:\PDM\Release");

    public InMemoryPdmRepository(TimeProvider timeProvider)
    {
        var project = SeedData.Project();
        project = project with { ResponsibleUsers = [project.Owner] };
        projects[project.Id] = project;
        projectResponsibles[project.Id] = [project.Owner];
        customers[Guid.Parse("c0046500-0000-0000-0000-000000000001")] = new(Guid.Parse("c0046500-0000-0000-0000-000000000001"), "C00465", "中山比亚迪电子有限公司", true);
        foreach (var code in Enumerable.Range(0, 100)) equipmentTypes[code] = new(code, $"类型{code:D2}", true);
        foreach (var document in SeedData.Documents(timeProvider.GetUtcNow()))
        {
            documents[document.Id] = document;
        }

        referenceTree = SeedData.Tree(documents);
        bomItems = SeedData.Bom().ToList();
        var package = SeedData.ReleasePackage(timeProvider.GetUtcNow());
        packages[package.Id] = package;
    }

    public Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Project>>(projects.Values.OrderBy(project => project.Code).ToArray());

    public Task<IReadOnlyList<Project>> ListProjectsForUserAsync(string actor, UserRole role, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Project>>(projects.Values
            .Where(project => role == UserRole.Administrator
                || project.ResponsibleUsers.Contains(actor, StringComparer.OrdinalIgnoreCase))
            .OrderBy(project => project.Code)
            .ToArray());

    public Task<Project?> FindProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        projects.TryGetValue(projectId, out var project);
        return Task.FromResult(project);
    }

    public Task<bool> HasProjectReadAccessAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken) =>
        Task.FromResult(role == UserRole.Administrator
            || (projects.TryGetValue(projectId, out var project)
                && project.ResponsibleUsers.Contains(actor, StringComparer.OrdinalIgnoreCase)));

    public Task<Project> CreateProjectAsync(CreateProjectCommand command, string actor, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (projects.Values.Any(project => string.Equals(project.Code, command.Code, StringComparison.OrdinalIgnoreCase)))
            {
                throw new PdmConflictException("项目编码已经存在。");
            }

            var project = new Project(
                Guid.NewGuid(),
                command.Code,
                command.Name,
                command.Owner,
                command.VaultLocation,
                command.ReleaseLocation,
                true) { ResponsibleUsers = [actor] };
            projects[project.Id] = project;
            projectResponsibles[project.Id] = [actor];
            return Task.FromResult(project);
        }
    }

    public Task<ProjectNumberingOptions> GetProjectNumberingOptionsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(NumberingOptions());

    public Task<ProjectNumberingOptions> AdvanceOrganizationCountersAsync(Guid organizationId, int currentProjectSequence, int currentSerialSequence, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!NumberingOptions().Organizations.Any(item => item.Id == organizationId)) throw new PdmNotFoundException("组织不存在。");
            if (currentProjectSequence < projectCounters.GetValueOrDefault(organizationId) || currentSerialSequence < serialCounters.GetValueOrDefault(organizationId))
                throw new PdmRuleException("流水基线只能向前调整，不能小于系统当前值。");
            projectCounters[organizationId] = currentProjectSequence;
            serialCounters[organizationId] = currentSerialSequence;
            return Task.FromResult(NumberingOptions());
        }
    }

    public Task<IReadOnlyList<PdmCustomer>> ListCustomersAsync(bool includeInactive, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PdmCustomer>>(customers.Values.Where(customer => includeInactive || customer.IsActive).OrderBy(customer => customer.Code).ToArray());

    public Task<PdmCustomer?> FindCustomerAsync(Guid customerId, CancellationToken cancellationToken)
    {
        customers.TryGetValue(customerId, out var customer);
        return Task.FromResult(customer);
    }

    public Task<PdmCustomer> SaveCustomerAsync(Guid? customerId, string code, string name, bool isActive, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (customers.Values.Any(customer => customer.Id != customerId && string.Equals(customer.Code, code, StringComparison.OrdinalIgnoreCase)))
                throw new PdmConflictException("客户编码已经存在。");
            if (customerId is not null && !customers.ContainsKey(customerId.Value)) throw new PdmNotFoundException("客户不存在。");
            var customer = new PdmCustomer(customerId ?? Guid.NewGuid(), code, name, isActive);
            customers[customer.Id] = customer;
            return Task.FromResult(customer);
        }
    }

    public Task<IReadOnlyList<EquipmentTypeDefinition>> ListEquipmentTypesAsync(bool includeInactive, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EquipmentTypeDefinition>>(equipmentTypes.Values.Where(item => includeInactive || item.IsActive).OrderBy(item => item.Code).ToArray());

    public Task<EquipmentTypeDefinition> SaveEquipmentTypeAsync(int code, string name, bool isActive, CancellationToken cancellationToken)
    {
        var item = new EquipmentTypeDefinition(code, name, isActive);
        equipmentTypes[code] = item;
        return Task.FromResult(item);
    }

    public Task<PdmSystemSettings> GetSystemSettingsAsync(CancellationToken cancellationToken) => Task.FromResult(systemSettings);

    public Task<PdmSystemSettings> UpdateSystemSettingsAsync(PdmSystemSettings settings, CancellationToken cancellationToken)
    {
        systemSettings = settings;
        return Task.FromResult(settings);
    }

    public Task<IReadOnlyList<UserAccount>> ListUsersAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UserAccount>>(users.Values.OrderBy(user => user.Username).ToArray());

    public Task<Project> SetProjectResponsibleUsersAsync(Guid projectId, IReadOnlyList<string> usernames, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!projects.TryGetValue(projectId, out var project)) throw new PdmNotFoundException("项目不存在。");
            var responsibleUsers = usernames.ToArray();
            projectResponsibles[projectId] = responsibleUsers;
            project = project with { Owner = responsibleUsers[0], ResponsibleUsers = responsibleUsers };
            projects[projectId] = project;
            return Task.FromResult(project);
        }
    }

    public Task<Project> CreateNumberedProjectAsync(CreateNumberedProjectCommand command, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            var organization = NumberingOptions().Organizations.Single(item => item.Id == command.OrganizationId);
            if (!customers.TryGetValue(command.CustomerId, out var customer) || !customer.IsActive) throw new PdmRuleException("所选客户不存在或已停用。");
            var projectSequence = Next(projectCounters, organization.Id);
            var customerKey = (organization.Id, customer.Code.ToUpperInvariant());
            var customerSequence = Next(customerCounters, customerKey);
            var serialStart = ReserveSerials(organization.Id, command.Quantity);
            var code = $"{command.ProjectTypeCode}{organization.ProjectCompanyCode}{projectSequence:D5}";
            var model = $"{organization.ModelCompanyCode}-{command.EquipmentTypeCode}-{customer.Code}-{customerSequence:D3}-00";
            var project = BuildNumberedProject(Guid.NewGuid(), code, command.Name, command.ProjectAlias, organization, command.ProjectTypeCode,
                command.EquipmentTypeCode, customer.Code, customer.Name, customerSequence, model, command.SignedDate,
                command.Quantity, null, null, command.Owner, Path.Combine(command.VaultLocation, code), Path.Combine(command.ReleaseLocation, code), serialStart);
            project = project with { ResponsibleUsers = [command.Owner] };
            projects[project.Id] = project;
            projectResponsibles[project.Id] = project.ResponsibleUsers;
            return Task.FromResult(project);
        }
    }

    public Task<Project> CreateSubprojectAsync(CreateSubprojectCommand command, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!projects.TryGetValue(command.ParentProjectId, out var parent)) throw new PdmNotFoundException("主项目不存在。");
            if (parent.ParentProjectId is not null) throw new PdmRuleException("只能在主项目下创建子项目。");
            if (parent.OrganizationId is null || parent.EquipmentTypeCode is null || parent.CustomerProjectSequence is null
                || parent.SignedDate is null || string.IsNullOrWhiteSpace(parent.ProjectTypeCode)
                || string.IsNullOrWhiteSpace(parent.CustomerCode) || string.IsNullOrWhiteSpace(parent.CustomerName))
                throw new PdmRuleException("旧项目缺少自动编号资料，不能直接创建子项目。");
            var organization = NumberingOptions().Organizations.Single(item => item.Id == parent.OrganizationId);
            var childSequence = projects.Values.Where(item => item.ParentProjectId == parent.Id).Select(item => item.ChildSequence ?? 0).DefaultIfEmpty().Max() + 1;
            var serialStart = ReserveSerials(organization.Id, command.Quantity);
            var code = $"{parent.Code}-{childSequence}";
            var model = $"{organization.ModelCompanyCode}-{parent.EquipmentTypeCode.Value}-{parent.CustomerCode}-{parent.CustomerProjectSequence.Value:D3}-{childSequence:D2}";
            var project = BuildNumberedProject(Guid.NewGuid(), code, command.Name, command.ProjectAlias, organization, parent.ProjectTypeCode,
                parent.EquipmentTypeCode.Value, parent.CustomerCode, parent.CustomerName, parent.CustomerProjectSequence.Value, model,
                parent.SignedDate.Value, command.Quantity, parent.Id, childSequence, parent.Owner,
                Path.Combine(command.VaultRoot ?? systemSettings.VaultRoot, code), Path.Combine(command.ReleaseRoot ?? systemSettings.ReleaseRoot, code), serialStart);
            project = project with { ResponsibleUsers = parent.ResponsibleUsers };
            projects[project.Id] = project;
            projectResponsibles[project.Id] = project.ResponsibleUsers;
            return Task.FromResult(project);
        }
    }

    private ProjectNumberingOptions NumberingOptions() => new(
        [
            Organization(Guid.Parse("70000000-0000-0000-0000-000000000001"), "昆山阿普顿自动化系统有限公司", "7", "AK"),
            Organization(Guid.Parse("30000000-0000-0000-0000-000000000001"), "广州阿普顿自动化系统有限公司", "3", "AG"),
            Organization(Guid.Parse("90000000-0000-0000-0000-000000000001"), "南京阿普顿自动化系统有限公司", "9", "AN")
        ],
        [new("P", "标准项目", true), new("W", "外发项目", true), new("R", "研发项目", true), new("S", "售后项目", true)],
        equipmentTypes.Values.Where(item => item.IsActive).OrderBy(item => item.Code).ToArray());

    private ProjectOrganization Organization(Guid id, string name, string projectCode, string modelCode) =>
        new(id, name, projectCode, modelCode, name, true,
            projectCounters.GetValueOrDefault(id), serialCounters.GetValueOrDefault(id));

    private static int Next<TKey>(Dictionary<TKey, int> counters, TKey key) where TKey : notnull
    {
        var next = counters.GetValueOrDefault(key) + 1;
        counters[key] = next;
        return next;
    }

    private int ReserveSerials(Guid organizationId, int quantity)
    {
        var start = serialCounters.GetValueOrDefault(organizationId) + 1;
        serialCounters[organizationId] = start + quantity - 1;
        return start;
    }

    private static Project BuildNumberedProject(
        Guid id, string code, string name, string? projectAlias, ProjectOrganization organization, string projectTypeCode,
        int equipmentTypeCode, string customerCode, string customerName, int customerProjectSequence, string deviceModel,
        DateOnly signedDate, int quantity, Guid? parentProjectId, int? childSequence, string owner,
        string vaultLocation, string releaseLocation, int serialStart) =>
        new(id, code, name, owner, vaultLocation, releaseLocation, true)
        {
            ProjectAlias = projectAlias,
            OrganizationId = organization.Id,
            OrganizationName = organization.Name,
            ProjectTypeCode = projectTypeCode,
            EquipmentTypeCode = equipmentTypeCode,
            CustomerCode = customerCode,
            CustomerName = customerName,
            CustomerProjectSequence = customerProjectSequence,
            DeviceModel = deviceModel,
            SignedDate = signedDate,
            Quantity = quantity,
            ParentProjectId = parentProjectId,
            ChildSequence = childSequence,
            SerialNumbers = Enumerable.Range(serialStart, quantity).Select(value => $"{organization.ProjectCompanyCode}{value:D7}").ToArray()
        };

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
        Task.FromResult(role == UserRole.Administrator
            || (documents.TryGetValue(documentId, out var document)
                && projects.TryGetValue(document.ProjectId, out var project)
                && (project.ResponsibleUsers.Contains(actor, StringComparer.OrdinalIgnoreCase)
                    || string.Equals(document.CheckedOutBy, actor, StringComparison.OrdinalIgnoreCase))));

    public Task<IReadOnlyList<DocumentVersion>> ListDocumentVersionsAsync(Guid documentId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DocumentVersion>>(versions.Values.Where(version => version.DocumentId == documentId).OrderByDescending(version => version.CreatedAt).ToArray());

    public Task<DocumentVersion?> FindDocumentVersionAsync(Guid documentId, Guid versionId, CancellationToken cancellationToken)
    {
        versions.TryGetValue(versionId, out var version);
        return Task.FromResult(version?.DocumentId == documentId ? version : null);
    }

    public Task<DocumentReferenceNode?> GetReferenceTreeAsync(Guid projectId, CancellationToken cancellationToken) =>
        Task.FromResult<DocumentReferenceNode?>(projectId == SeedData.ProjectId ? referenceTree : null);

    public Task<CadReferenceSnapshot?> GetLatestReferenceSnapshotAsync(Guid projectId, CancellationToken cancellationToken) =>
        Task.FromResult<CadReferenceSnapshot?>(projectId == SeedData.ProjectId
            ? new CadReferenceSnapshot(SeedData.SnapshotId, projectId, referenceRootDocumentId, DateTimeOffset.UtcNow, "seed", referenceTree, string.Empty)
            : null);

    public Task<IReadOnlyList<BomItem>> GetBomAsync(Guid projectId, BomKind kind, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<BomItem>>(bomItems.Where(item => item.ProjectId == projectId && item.Kind == kind).OrderBy(item => item.Sequence).ToArray());

    public Task<IReadOnlyList<BomItem>> ReplaceBomAsync(Guid projectId, BomKind kind, IReadOnlyList<BomItem> items, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            bomItems.RemoveAll(item => item.ProjectId == projectId && item.Kind == kind);
            bomItems.AddRange(items);
            return Task.FromResult<IReadOnlyList<BomItem>>(items.OrderBy(item => item.Sequence).ToArray());
        }
    }

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
            string? latestSourceSha256 = null;
            latest?.PropertySnapshot.TryGetValue("SourceFileSha256", out latestSourceSha256);
            commit.Properties.TryGetValue("SourceFileSha256", out var sourceFileSha256);
            var sameFile = latest is not null
                && (string.Equals(latest.Sha256, commit.File.Sha256, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(latestSourceSha256)
                        && !string.IsNullOrWhiteSpace(sourceFileSha256)
                        && string.Equals(latestSourceSha256, sourceFileSha256, StringComparison.OrdinalIgnoreCase)));
            if (!commit.ForceVersion
                && sameFile)
            {
                if (commit.IsProjectRoot)
                {
                    referenceTree = commit.ReferenceSnapshot.Root;
                    referenceRootDocumentId = documentId;
                }

                var unchanged = document with { CheckedOutBy = null, UpdatedAt = DateTimeOffset.UtcNow };
                documents[documentId] = unchanged;
                return Task.FromResult(new DocumentCheckInResult(unchanged, null, false));
            }
            var revision = versions.Values.Any(version => version.DocumentId == documentId) || document.Revision.IsReleased ? document.Revision.NextWork() : RevisionLabel.InitialWork();
            var version = new DocumentVersion(Guid.NewGuid(), documentId, revision, DocumentVersionStatus.Work, commit.File.RelativePath, commit.File.Length, commit.File.Sha256, actor, DateTimeOffset.UtcNow, commit.ChangeNote, commit.Properties, commit.ReferenceSnapshot.Root, commit.MechanicalBomSnapshot, commit.ElectricalBomSnapshot, commit.SourceVersionId, commit.SourceDescription, null, null);
            versions[version.Id] = version;
            var updated = document with { CheckedOutBy = null, Revision = revision, State = DocumentLifecycleState.Work, UpdatedAt = version.CreatedAt };
            documents[documentId] = updated;
            if (commit.IsProjectRoot)
            {
                referenceTree = commit.ReferenceSnapshot.Root;
                referenceRootDocumentId = documentId;
            }

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

    public Task<IReadOnlyList<DocumentVersion>> PublishReleasePackageVersionsAsync(Guid releasePackageId, Guid approvalTaskId, string actor, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!packages.TryGetValue(releasePackageId, out var package) || package.State != ReleasePackageState.Publishing)
                throw new PdmConflictException("发布包尚未进入发布状态。");
            if (!package.ApprovalTasks.Any(task => task.Id == approvalTaskId && task.Stage == ApprovalStage.Approval && task.Decision == ApprovalDecision.Approved))
                throw new PdmConflictException("最终批准记录无效。");
            var released = new List<DocumentVersion>();
            foreach (var documentId in EnumerateDocumentIds(referenceTree).Distinct())
            {
                if (!documents.TryGetValue(documentId, out var document)) continue;
                var source = versions.Values.Where(version => version.DocumentId == documentId).OrderByDescending(version => version.CreatedAt).FirstOrDefault();
                if (source is null || source.Status != DocumentVersionStatus.Work || !string.Equals(source.Revision.Display, document.Revision.Display, StringComparison.OrdinalIgnoreCase)) continue;
                var revision = source.Revision.Release();
                var version = source with { Id = Guid.NewGuid(), Revision = revision, Status = DocumentVersionStatus.Released, CreatedBy = actor, CreatedAt = DateTimeOffset.UtcNow, ChangeNote = $"审批发布{revision.Display}", SourceVersionId = source.Id, SourceDescription = $"由{source.Revision.Display}审批发布", ApprovalTaskId = approvalTaskId, ReleasePackageId = releasePackageId };
                versions[version.Id] = version;
                documents[documentId] = document with { Revision = revision, State = DocumentLifecycleState.Released, CheckedOutBy = null, UpdatedAt = version.CreatedAt };
                released.Add(version);
            }
            return Task.FromResult<IReadOnlyList<DocumentVersion>>(released);
        }
    }

    private static IEnumerable<Guid> EnumerateDocumentIds(DocumentReferenceNode node)
    {
        if (node.DocumentId.HasValue) yield return node.DocumentId.Value;
        foreach (var child in node.Children)
            foreach (var id in EnumerateDocumentIds(child)) yield return id;
    }

    public Task<ReleasePackage> CreateReleasePackageAsync(ReleasePackage package, CancellationToken cancellationToken)
    {
        if (!packages.TryAdd(package.Id, package))
        {
            throw new PdmConflictException("发布包编号已经存在。 ");
        }

        return Task.FromResult(package);
    }

    public Task<ReleasePackage> SubmitReleasePackageAsync(Guid releasePackageId, string actor, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!packages.TryGetValue(releasePackageId, out var package)) throw new PdmNotFoundException("发布包不存在。");
            if (package.State is not (ReleasePackageState.Draft or ReleasePackageState.Rejected or ReleasePackageState.PublishFailed))
                throw new PdmConflictException("只有草稿、已驳回或发布失败的发布包可以提交。");
            var tasks = package.ApprovalTasks.Select(task => task with { DecisionBy = null, Decision = null, Comment = null, DecidedAt = null }).ToArray();
            var submitted = package with { State = ReleasePackageState.ProcessReview, ApprovalTasks = tasks, PublishedAt = null, PublishedPath = null, PublishError = null };
            packages[package.Id] = submitted;
            return Task.FromResult(submitted);
        }
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

    public Task<IReadOnlyList<AuditEntry>> ListAuditAsync(string actor, UserRole role, int take, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AuditEntry>>(audits.Where(entry => role == UserRole.Administrator || string.Equals(entry.Actor, actor, StringComparison.OrdinalIgnoreCase)).OrderByDescending(entry => entry.OccurredAt).Take(Math.Clamp(take, 1, 500)).ToArray());
}
