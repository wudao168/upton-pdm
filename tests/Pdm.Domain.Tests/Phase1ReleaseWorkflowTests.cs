using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class Phase1ReleaseWorkflowTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task NoPublish_KeepsBomItemVisibleAndCanRestorePublishing()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        var item = new BomItem(Guid.NewGuid(), ProjectId, BomKind.Standard, 1, "STD-001", "公共标准件", 2, "001", null, "M1", "W1", true);
        await repository.ReplaceBomAsync(ProjectId, BomKind.Standard, [item], default);

        var excluded = await workflow.SetBomReleaseExclusionAsync(
            ProjectId, new([item.Id], true, "其他项目已发布"), "admin", UserRole.Administrator, default);

        var excludedItem = Assert.Single(excluded, candidate => candidate.Id == item.Id);
        Assert.True(excludedItem.IsReleaseExcluded);
        Assert.Equal("其他项目已发布", excludedItem.ReleaseExclusionReason);
        Assert.Empty(Assert.Single(await repository.ListBomVersionsAsync(ProjectId, BomKind.Standard, default), version => version.State == BomVersionState.Draft).Items);

        var restored = await workflow.SetBomReleaseExclusionAsync(
            ProjectId, new([item.Id], false), "admin", UserRole.Administrator, default);

        var restoredItem = Assert.Single(restored, candidate => candidate.Id == item.Id);
        Assert.False(restoredItem.IsReleaseExcluded);
        Assert.Null(restoredItem.ReleaseExclusionReason);
        Assert.Contains(Assert.Single(await repository.ListBomVersionsAsync(ProjectId, BomKind.Standard, default), version => version.State == BomVersionState.Draft).Items, candidate => candidate.Id == item.Id);
    }

    [Fact]
    public async Task ApprovalChain_PublishesPreparedImmutablePackage()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var publisher = new RecordingPublisher();
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), publisher, TimeProvider.System);
        foreach (var document in await repository.ListCheckedOutDocumentsAsync(default))
            await repository.ForceReleaseCheckoutAsync(document.Id, "admin", "测试准备", default);
        await PrepareApprovedNonStandardDrawingReviewAsync(repository, workflow);
        var electrical = new[]
        {
            new BomItemInput(1, "EL-001", "光电传感器", 4, "件", null, "M18 PNP", "A", true)
        };
        await workflow.ReplaceBomAsync(ProjectId, BomKind.Electrical, electrical, "admin", UserRole.Administrator, default);

        var package = await workflow.CreateReleasePackageAsync(
            ProjectId, null, $"RP-TEST-{Guid.NewGuid():N}", "admin", "admin", "admin", UserRole.Administrator, default);

        Assert.Equal(ReleasePackageState.Draft, package.State);
        Assert.NotEmpty(package.MechanicalBomSnapshot);
        Assert.Single(package.ElectricalBomSnapshot);
        Assert.Equal(1, publisher.PrepareCalls);

        package = await workflow.SubmitReleasePackageAsync(package.Id, "admin", UserRole.Administrator, default);
        Assert.Equal(ReleasePackageState.ProcessReview, package.State);
        Assert.Equal(1, publisher.ValidateCalls);
        Assert.All(await repository.ListDocumentsAsync(ProjectId, default), document => Assert.Equal(DocumentLifecycleState.InReview, document.State));

        var processTask = package.ApprovalTasks.Single(task => task.Stage == ApprovalStage.ProcessReview);
        package = await workflow.DecideAsync(processTask.Id, "admin", UserRole.Administrator, ApprovalDecision.Approved, "工艺可行", default);
        Assert.Equal(ReleasePackageState.Approval, package.State);
        Assert.Equal(0, publisher.PublishCalls);

        var approvalTask = package.ApprovalTasks.Single(task => task.Stage == ApprovalStage.Approval);
        package = await workflow.DecideAsync(approvalTask.Id, "admin", UserRole.Administrator, ApprovalDecision.Approved, "批准发布", default);
        Assert.Equal(ReleasePackageState.Published, package.State);
        Assert.Equal(1, publisher.PublishCalls);
        Assert.NotEmpty(publisher.PreviewSources);
        Assert.Equal("C:\\PDM\\Release\\package", package.PublishedPath);
        Assert.All(await repository.ListDocumentsAsync(ProjectId, default), document => Assert.Equal(DocumentLifecycleState.Released, document.State));
        foreach (var source in publisher.PreviewSources)
        {
            var released = (await repository.ListDocumentVersionsAsync(source.DocumentId, default))
                .Single(version => version.Status == DocumentVersionStatus.Released);
            Assert.Equal(source.SourceSha256, released.Preview?.SourceSha256);
            Assert.Equal(source.Kind == DocumentKind.Drawing ? DocumentPreviewFormat.Pdf : DocumentPreviewFormat.Step, released.Preview?.Format);
        }
    }

    [Fact]
    public async Task CurrentApprovalAssignee_CanRejectWithoutApprovalRolePermission()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        var packageId = Guid.NewGuid();
        var task = new ApprovalTask(Guid.NewGuid(), packageId, ApprovalStage.MainDesigner, "designer", null, null, null, null)
        {
            StepOrder = 1,
            StepName = "主设审核"
        };
        await repository.CreateReleasePackageAsync(new ReleasePackage(
            packageId, ProjectId, "RP-ASSIGNEE", ReleasePackageState.ProcessReview, Guid.NewGuid(), "W1", "W1",
            [task], DateTimeOffset.UtcNow, null, null), default);

        await Assert.ThrowsAsync<PdmRuleException>(() =>
            workflow.DecideAsync(task.Id, "designer", UserRole.Engineer, ApprovalDecision.Rejected, null, default));
        var rejected = await workflow.DecideAsync(task.Id, "designer", UserRole.Engineer, ApprovalDecision.Rejected, "退回修改", default);

        Assert.Equal(ReleasePackageState.Rejected, rejected.State);
        Assert.Equal(ApprovalDecision.Rejected, Assert.Single(rejected.ApprovalTasks).Decision);
        var notification = Assert.Single(await repository.ListUserNotificationsAsync("designer", 20, default));
        Assert.Equal("BOM发布审批已退回", notification.Title);
        Assert.Contains("退回修改", notification.Content);
        Assert.Null(notification.ReadAt);
    }

    [Fact]
    public async Task CurrentApprovalAssignee_CanTransferToEligibleProjectApprover()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        await repository.CreateUserAsync(new UserAccount(
            Guid.NewGuid(), "transfer-approver", "转交审批人", "unused", UserRole.Approver, true), default);
        var packageId = Guid.NewGuid();
        var task = new ApprovalTask(Guid.NewGuid(), packageId, ApprovalStage.MainDesigner, "designer", null, null, null, null)
        {
            StepOrder = 1,
            StepName = "主设审核"
        };
        await repository.CreateReleasePackageAsync(new ReleasePackage(
            packageId, ProjectId, "RP-TRANSFER", ReleasePackageState.ProcessReview, Guid.NewGuid(), "W1", "W1",
            [task], DateTimeOffset.UtcNow, null, null), default);

        var candidates = await workflow.ListApprovalTransferCandidatesAsync(task.Id, "designer", default);
        var transferred = await workflow.TransferApprovalAsync(task.Id, "designer", "transfer-approver", "工作调整", default);

        Assert.Contains(candidates, candidate => candidate.Username == "transfer-approver" && candidate.DisplayName == "转交审批人");
        Assert.Equal("transfer-approver", Assert.Single(transferred.ApprovalTasks).Assignee);
        await Assert.ThrowsAsync<PdmConflictException>(() =>
            repository.TransferApprovalAsync(task.Id, "designer", "another-approver", default));
    }

    [Fact]
    public async Task ReleasePackage_RejectsUnclassifiedSourceItems()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        await repository.ReplaceBomAsync(ProjectId, BomKind.Unclassified,
        [
            new BomItem(Guid.NewGuid(), ProjectId, BomKind.Unclassified, 1, "PENDING-001", "待分类物料", 1, "个", null, null, "W1", false)
            {
                SourceDocumentId = Guid.NewGuid(),
                Source = "Auto",
                IsPendingClassification = true
            }
        ], default);

        var exception = await Assert.ThrowsAsync<PdmRuleException>(() => workflow.CreateReleasePackageAsync(
            ProjectId, null, $"RP-PENDING-{Guid.NewGuid():N}", "admin", "admin", "admin", UserRole.Administrator, default));

        Assert.Contains("待分类", exception.Message);
    }

    [Fact]
    public async Task ReleasePackage_AllowsDrawingSourceMismatchAndPendingWritebackAsReminder()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        foreach (var document in await repository.ListCheckedOutDocumentsAsync(default))
            await repository.ForceReleaseCheckoutAsync(document.Id, "admin", "测试准备", default);
        await PrepareApprovedNonStandardDrawingReviewAsync(repository, workflow);
        var nonStandard = await repository.GetBomAsync(ProjectId, BomKind.NonStandard, default);
        Assert.NotEmpty(nonStandard);
        await repository.ReplaceBomAsync(ProjectId, BomKind.NonStandard, nonStandard.Select(item => item with
        {
            ReconciliationStatus = "ManualOverrideMismatch",
            ReconciliationNote = "BOM维护值与最新图档源数据不一致：型号。",
            PropertyWritebackStatus = CadPropertyWritebackStatus.Pending
        }).ToArray(), default);
        await workflow.ReplaceBomAsync(ProjectId, BomKind.Electrical,
            [new BomItemInput(1, "EL-REMINDER", "提醒不阻断测试", 1, "件", null, "M18", "A", true)],
            "admin", UserRole.Administrator, default);

        var package = await workflow.CreateReleasePackageAsync(
            ProjectId, null, $"RP-REMINDER-{Guid.NewGuid():N}", "admin", "admin", "admin", UserRole.Administrator, default);

        Assert.Equal(ReleasePackageState.Draft, package.State);
        Assert.Contains(package.NonStandardBomSnapshot, item =>
            item.ReconciliationStatus == "ManualOverrideMismatch"
            && item.PropertyWritebackStatus == CadPropertyWritebackStatus.Pending);
    }

    [Fact]
    public async Task ApprovalLifecycle_RejectWithdrawWhereUsedAndObsolete_AreControlled()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        foreach (var document in await repository.ListCheckedOutDocumentsAsync(default))
            await repository.ForceReleaseCheckoutAsync(document.Id, "admin", "测试准备", default);
        await PrepareApprovedNonStandardDrawingReviewAsync(repository, workflow);
        await workflow.ReplaceBomAsync(ProjectId, BomKind.Electrical,
            [new BomItemInput(1, "EL-002", "接近开关", 2, "件", null, "M12", "A", true)],
            "admin", UserRole.Administrator, default);
        var package = await workflow.CreateReleasePackageAsync(
            ProjectId, null, $"RP-CONTROL-{Guid.NewGuid():N}", "admin", "admin", "admin", UserRole.Administrator, default);

        package = await workflow.SubmitReleasePackageAsync(package.Id, "admin", UserRole.Administrator, default);
        var review = package.ApprovalTasks.Single(task => task.Stage == ApprovalStage.ProcessReview);
        package = await workflow.DecideAsync(review.Id, "admin", UserRole.Administrator, ApprovalDecision.Rejected, "结构需修改", default);
        Assert.Equal(ReleasePackageState.Rejected, package.State);
        Assert.All(await repository.ListDocumentsAsync(ProjectId, default), document => Assert.Equal(DocumentLifecycleState.Work, document.State));

        package = await workflow.SubmitReleasePackageAsync(package.Id, "admin", UserRole.Administrator, default);
        package = await workflow.WithdrawReleasePackageAsync(package.Id, "admin", UserRole.Administrator, "补充材料", default);
        Assert.Equal(ReleasePackageState.Draft, package.State);
        Assert.All(await repository.ListDocumentsAsync(ProjectId, default), document => Assert.Equal(DocumentLifecycleState.Work, document.State));

        var root = await repository.GetReferenceTreeAsync(ProjectId, default);
        var childId = root!.Children.Select(child => child.DocumentId).First(id => id.HasValue)!.Value;
        Assert.NotEmpty(await workflow.ListWhereUsedAsync(childId, "admin", UserRole.Administrator, default));

        var obsolete = await workflow.ObsoleteDocumentAsync(childId, "admin", UserRole.Administrator, "零件停用", default);
        Assert.Equal(DocumentLifecycleState.Obsolete, obsolete.State);
        await Assert.ThrowsAsync<PdmConflictException>(() => workflow.CheckoutAsync(childId, "admin", UserRole.Administrator, default));
    }

    [Fact]
    public async Task EmptyBom_IsAutomaticallyTreatedAsNoSuchMaterials()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);

        await workflow.ReplaceBomAsync(ProjectId, BomKind.Electrical,
            [new BomItemInput(1, "EL-CLEAR", "待删除电气件", 1, "件", null, null, "W1", true)],
            "admin", UserRole.Administrator, default);
        var cleared = await workflow.ReplaceBomAsync(ProjectId, BomKind.Electrical, [], "admin", UserRole.Administrator, default);
        Assert.Empty(cleared);
        Assert.Empty(await repository.GetBomAsync(ProjectId, BomKind.Electrical, default));
        Assert.DoesNotContain(await repository.GetBomEmptyDeclarationsAsync(ProjectId, default), declaration => declaration.DeclaredEmpty);

        var package = await workflow.CreateReleasePackageAsync(
            ProjectId, null, $"RP-EMPTY-{Guid.NewGuid():N}", "admin", "admin", "admin", UserRole.Administrator, default);
        Assert.Empty(package.ElectricalBomSnapshot);
    }

    [Fact]
    public async Task BomDataStatus_IsDerivedByCategoryAndElectricalDoesNotRequireMaterial()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        var project = await repository.CreateProjectAsync(
            new CreateProjectCommand($"STATUS-{Guid.NewGuid():N}", "BOM资料状态", "admin", @"D:\PDM\Status", @"D:\Release\Status"),
            "admin",
            default);

        var standard = Assert.Single(await workflow.ReplaceBomAsync(project.Id, BomKind.Standard,
            [new BomItemInput(1, "STD-STATUS", "标准件", 1, "件", null, null, "W1", true)],
            "admin", UserRole.Administrator, default));
        var nonStandard = Assert.Single(await workflow.ReplaceBomAsync(project.Id, BomKind.NonStandard,
            [new BomItemInput(1, "NONSTD-STATUS", "非标件", 1, "件", null, "M10", "W1", true)],
            "admin", UserRole.Administrator, default));
        var electrical = Assert.Single(await workflow.ReplaceBomAsync(project.Id, BomKind.Electrical,
            [new BomItemInput(1, "ELEC-STATUS", "电气件", 1, "件", null, null, "W1", false)],
            "admin", UserRole.Administrator, default));

        Assert.False(standard.IsComplete);
        Assert.False(nonStandard.IsComplete);
        Assert.True(electrical.IsComplete);

        var updated = await workflow.BatchUpdateBomItemsAsync(project.Id,
            new BatchUpdateBomItemsCommand([nonStandard.Id], ["material"], Material: "6061"),
            "admin", UserRole.Administrator, default);
        Assert.True(Assert.Single(updated, item => item.Id == nonStandard.Id).IsComplete);
    }

    [Fact]
    public async Task BomDataStatus_UsesConfiguredRulesAndKeepsCoreFieldsRequired()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        var current = await repository.GetSystemSettingsAsync(default);
        var savedSettings = await workflow.UpdateSystemSettingsAsync(current with
        {
            ValidationRules = new(
                [BomValidationFieldCatalog.DrawingNumber, BomValidationFieldCatalog.Name, BomValidationFieldCatalog.Unit, BomValidationFieldCatalog.Brand, BomValidationFieldCatalog.Quantity, BomValidationFieldCatalog.Revision],
                BomValidationFieldCatalog.NonStandardDefaults,
                BomValidationFieldCatalog.ElectricalDefaults),
            ReleaseChangeReasonTypes = [" 设计变更 ", "客户需求", "设计变更"]
        }, "admin", UserRole.Administrator, default);
        Assert.Equal(["设计变更", "客户需求"], savedSettings.ReleaseChangeReasonTypes);

        var item = Assert.Single(await workflow.ReplaceBomAsync(ProjectId, BomKind.Standard,
            [new BomItemInput(1, "STD-RULE", "标准件", 1, "件", null, "M10", "W1", true)],
            "admin", UserRole.Administrator, default));
        Assert.False(item.IsComplete);

        var invalid = await Assert.ThrowsAsync<PdmRuleException>(() => workflow.UpdateSystemSettingsAsync(current with
        {
            ValidationRules = new(
                [BomValidationFieldCatalog.Name, BomValidationFieldCatalog.Unit, BomValidationFieldCatalog.Quantity, BomValidationFieldCatalog.Revision],
                BomValidationFieldCatalog.NonStandardDefaults,
                BomValidationFieldCatalog.ElectricalDefaults)
        }, "admin", UserRole.Administrator, default));
        Assert.Contains("物料编码", invalid.Message);
    }

    [Fact]
    public async Task BomDuplicateMaterialCode_PreservesSeparateRowsAndStableIds()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);

        var created = await workflow.ReplaceBomAsync(
            ProjectId,
            BomKind.Electrical,
            [
                new BomItemInput(1, "DUP-001", "物料一", 1, "件", null, null, "W1", true),
                new BomItemInput(2, "dup-001", "物料二", 1, "件", null, null, "W1", true)
            ],
            "admin",
            UserRole.Administrator,
            default);

        Assert.Equal(2, created.Count);
        Assert.All(created, item => Assert.Equal("DUP-001", item.DrawingNumber.ToUpperInvariant()));
        Assert.Equal(2, created.Select(item => item.Id).Distinct().Count());

        var savedAgain = await workflow.ReplaceBomAsync(
            ProjectId,
            BomKind.Electrical,
            created.Select(item => new BomItemInput(
                item.Sequence, item.DrawingNumber, item.Name, item.Quantity, item.Unit, item.Material,
                item.Specification, item.Revision, item.IsComplete, Id: item.Id,
                ParentDrawingNumber: item.Sequence == 1 ? "PARENT-A" : "PARENT-B")).ToArray(),
            "admin",
            UserRole.Administrator,
            default);

        Assert.Equal(created.Select(item => item.Id), savedAgain.Select(item => item.Id));
        Assert.Equal(new[] { "PARENT-A", "PARENT-B" }, savedAgain.Select(item => item.ParentDrawingNumber));
    }

    [Fact]
    public async Task MechanicalBom_SourceItemCannotBeDeletedByFullReplacement()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        var sourceItem = new BomItem(Guid.NewGuid(), ProjectId, BomKind.NonStandard, 1, "CAD-SOURCE", "图纸来源物料", 1, "件", null, "M1", "W1", true)
        {
            SourceDocumentId = Guid.NewGuid(),
            Source = "Auto"
        };
        await repository.ReplaceBomAsync(ProjectId, BomKind.NonStandard, [sourceItem], default);

        var exception = await Assert.ThrowsAsync<PdmRuleException>(() => workflow.ReplaceBomAsync(
            ProjectId, BomKind.NonStandard, [], "admin", UserRole.Administrator, default));

        Assert.Contains("图纸来源物料", exception.Message);
        Assert.Single(await repository.GetBomAsync(ProjectId, BomKind.NonStandard, default));
    }

    [Fact]
    public async Task BomReplace_ZeroQuantityMovesExistingItemToRecycleBinAndPreservesItsQuantity()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        var removed = new BomItem(Guid.NewGuid(), ProjectId, BomKind.NonStandard, 1, "ZERO-DELETE", "数量归零物料", 3, "件", "6061", "M3", "W1", true)
        {
            SourceDocumentId = Guid.NewGuid(),
            Source = "Auto"
        };
        var retained = new BomItem(Guid.NewGuid(), ProjectId, BomKind.NonStandard, 2, "ZERO-KEEP", "保留物料", 2, "件", "6061", "M2", "W1", true);
        await repository.ReplaceBomAsync(ProjectId, BomKind.NonStandard, [removed, retained], default);

        var saved = await workflow.ReplaceBomAsync(ProjectId, BomKind.NonStandard,
        [
            new BomItemInput(1, removed.DrawingNumber, removed.Name, 0, removed.Unit, removed.Material, removed.Specification, removed.Revision, true,
                Id: removed.Id, SourceDocumentId: removed.SourceDocumentId),
            new BomItemInput(2, retained.DrawingNumber, retained.Name, 5, retained.Unit, retained.Material, retained.Specification, retained.Revision, true,
                Id: retained.Id)
        ], "admin", UserRole.Administrator, default);

        var recycled = Assert.Single(saved, item => item.Id == removed.Id);
        Assert.True(recycled.IsManuallyExcluded);
        Assert.Equal(3, recycled.Quantity);
        Assert.Equal("admin", recycled.DeletedBy);
        Assert.Equal("数量修改为0，系统自动删除", recycled.DeleteReason);
        Assert.Equal(5, Assert.Single(saved, item => item.Id == retained.Id).Quantity);
        Assert.Contains(await repository.ListAuditAsync("admin", UserRole.Administrator, 100, default),
            entry => entry.Action == "bom.replace" && entry.Detail.Contains("零数量移入回收站:1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BomReplace_RejectsZeroQuantityForANewItem()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);

        var exception = await Assert.ThrowsAsync<PdmRuleException>(() => workflow.ReplaceBomAsync(
            ProjectId, BomKind.Electrical,
            [new BomItemInput(1, "ZERO-NEW", "新增零数量物料", 0, "件", null, "M1", "W1", true)],
            "admin", UserRole.Administrator, default));

        Assert.Contains("新增BOM物料的数量必须大于0", exception.Message);
    }

    [Fact]
    public async Task BomRecycleBin_PreservesManualAndSourceItemsAndRestoresManualSnapshot()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        var manual = new BomItem(Guid.NewGuid(), ProjectId, BomKind.Electrical, 1, "MANUAL-DELETE", "人工物料", 1, "件", null, null, "W1", true);
        var keep = new BomItem(Guid.NewGuid(), ProjectId, BomKind.Electrical, 2, "MANUAL-KEEP", "保留物料", 1, "件", null, null, "W1", true);
        await repository.ReplaceBomAsync(ProjectId, BomKind.Electrical, [manual, keep], default);

        var remaining = await workflow.BatchDeleteBomItemsAsync(ProjectId, new([manual.Id], "误删测试"), "admin", UserRole.Administrator, default);

        var deletedManual = Assert.Single(remaining, item => item.Id == manual.Id);
        Assert.True(deletedManual.IsManuallyExcluded);
        Assert.NotNull(deletedManual.DeletedAt);
        Assert.Equal("admin", deletedManual.DeletedBy);
        Assert.Equal("误删测试", deletedManual.DeleteReason);
        Assert.Contains(remaining, item => item.Id == keep.Id);

        var restoredManual = await workflow.BatchRestoreBomItemsAsync(ProjectId, new([manual.Id]), "admin", UserRole.Administrator, default);
        var restored = Assert.Single(restoredManual, item => item.Id == manual.Id);
        Assert.False(restored.IsManuallyExcluded);
        Assert.Null(restored.DeletedAt);
        Assert.Equal(manual.DrawingNumber, restored.DrawingNumber);
        Assert.Equal(manual.Name, restored.Name);

        var source = new BomItem(Guid.NewGuid(), ProjectId, BomKind.NonStandard, 1, "CAD-KEEP", "图纸来源物料", 1, "件", null, null, "W1", true)
        {
            SourceDocumentId = Guid.NewGuid(),
            Source = "Auto"
        };
        await repository.ReplaceBomAsync(ProjectId, BomKind.NonStandard, [source], default);

        var afterSourceDelete = await workflow.BatchDeleteBomItemsAsync(
            ProjectId, new([source.Id], "从当前BOM移除"), "admin", UserRole.Administrator, default);
        var excluded = Assert.Single(afterSourceDelete, item => item.Id == source.Id);
        Assert.True(excluded.IsManuallyExcluded);
        Assert.Equal("ManuallyExcluded", excluded.ReconciliationStatus);
        Assert.Equal("从当前BOM移除", excluded.DeleteReason);

        await workflow.GenerateMechanicalBomAsync(ProjectId, true, "admin", UserRole.Administrator, default);
        var afterRegeneration = Assert.Single(
            await repository.GetBomAsync(ProjectId, BomKind.NonStandard, default), item => item.Id == source.Id);
        Assert.True(afterRegeneration.IsManuallyExcluded);

        var pendingRemoval = afterRegeneration with
        {
            Id = Guid.NewGuid(),
            DrawingNumber = "CAD-MISSING",
            SourceDocumentId = Guid.NewGuid(),
            IsManuallyExcluded = false,
            IsPendingRemoval = true,
            ReconciliationStatus = "PendingRemoval",
            ReconciliationNote = "最新图档源数据中已不存在，等待确认删除或人工保留。"
        };
        await repository.ReplaceBomAsync(ProjectId, BomKind.NonStandard, [afterRegeneration, pendingRemoval], default);
        var afterConfirmedRemoval = await workflow.BatchDeleteBomItemsAsync(
            ProjectId, new([pendingRemoval.Id], "确认源数据已移除"), "admin", UserRole.Administrator, default);
        Assert.True(Assert.Single(afterConfirmedRemoval, item => item.Id == pendingRemoval.Id).IsManuallyExcluded);
        Assert.Contains(await repository.ListAuditAsync("admin", UserRole.Administrator, 100, default),
            entry => entry.Action == "bom.batch-delete" && entry.Detail.Contains("CAD-MISSING", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BomRecycleBin_AllowsEmptyReasonAndDuplicateMaterialCodeRestore()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        var active = new BomItem(Guid.NewGuid(), ProjectId, BomKind.Electrical, 1, "DUPLICATE", "当前物料", 1, "001", null, null, "W1", true);
        var recycled = new BomItem(Guid.NewGuid(), ProjectId, BomKind.Electrical, 2, "DUPLICATE", "回收站物料", 1, "001", null, null, "W1", true)
        {
            IsManuallyExcluded = true,
            DeletedAt = DateTimeOffset.UtcNow,
            DeletedBy = "admin",
            DeleteReason = "测试"
        };
        await repository.ReplaceBomAsync(ProjectId, BomKind.Electrical, [active, recycled], default);

        var afterDelete = await workflow.BatchDeleteBomItemsAsync(
            ProjectId, new([active.Id], " "), "admin", UserRole.Administrator, default);
        var deleted = Assert.Single(afterDelete, item => item.Id == active.Id);
        Assert.True(deleted.IsManuallyExcluded);
        Assert.Equal("未填写删除原因", deleted.DeleteReason);
        var restored = await workflow.BatchRestoreBomItemsAsync(
            ProjectId, new([recycled.Id]), "admin", UserRole.Administrator, default);

        var unchanged = await repository.GetBomAsync(ProjectId, BomKind.Electrical, default);
        Assert.True(Assert.Single(unchanged, item => item.Id == active.Id).IsManuallyExcluded);
        Assert.False(Assert.Single(restored, item => item.Id == recycled.Id).IsManuallyExcluded);
        Assert.False(Assert.Single(unchanged, item => item.Id == recycled.Id).IsManuallyExcluded);
    }

    [Fact]
    public async Task OrganizationHierarchy_RejectsMoreThanTenLevels()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        var organizationId = Guid.Parse("70000000-0000-0000-0000-000000000001");
        Guid? parentUnitId = null;

        for (var level = 1; level <= 10; level++)
        {
            var unit = await workflow.SaveOrganizationUnitAsync(
                new SaveOrganizationUnitCommand(null, organizationId, parentUnitId, $"LEVEL-{level}", $"第{level}级组织",
                    level == 1 ? OrganizationUnitKind.BusinessDivision : OrganizationUnitKind.Department, true, level),
                "admin", UserRole.Administrator, default);
            parentUnitId = unit.Id;
        }

        var exception = await Assert.ThrowsAsync<PdmRuleException>(() => workflow.SaveOrganizationUnitAsync(
            new SaveOrganizationUnitCommand(null, organizationId, parentUnitId, "LEVEL-11", "第11级组织", OrganizationUnitKind.Team, true, 11),
            "admin", UserRole.Administrator, default));

        Assert.Equal("公司下的组织层级不能超过10级。", exception.Message);
    }

    [Fact]
    public async Task ProjectExecutionUnit_RequiresManufacturingDepartment()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        var organizationId = Guid.Parse("70000000-0000-0000-0000-000000000001");
        var ordinaryDepartment = await workflow.SaveOrganizationUnitAsync(
            new SaveOrganizationUnitCommand(null, organizationId, null, "ORDINARY", "普通部门", OrganizationUnitKind.BusinessDivision, true, 0, false),
            "admin", UserRole.Administrator, default);

        var assignmentException = await Assert.ThrowsAsync<PdmRuleException>(() => workflow.SetProjectExecutionUnitAsync(
            ProjectId, ordinaryDepartment.Id, "admin", UserRole.Administrator, default));
        Assert.Equal("承接部门不存在、未启用或未设为制造部门。", assignmentException.Message);

        var manufacturingDepartment = await workflow.SaveOrganizationUnitAsync(
            new SaveOrganizationUnitCommand(null, organizationId, null, "MANUFACTURING", "T2事业部", OrganizationUnitKind.BusinessDivision, true, 0, true),
            "admin", UserRole.Administrator, default);
        Assert.True(manufacturingDepartment.CanManufacture);

        var nestedException = await Assert.ThrowsAsync<PdmRuleException>(() => workflow.SaveOrganizationUnitAsync(
            new SaveOrganizationUnitCommand(null, organizationId, manufacturingDepartment.Id, "NESTED", "下级制造部门", OrganizationUnitKind.Department, true, 0, true),
            "admin", UserRole.Administrator, default));
        Assert.Equal("只有公司直属部门可以设为制造部门。", nestedException.Message);
    }

    [Fact]
    public async Task OrganizationManagers_CanBeAssignedToChildUnitAndBecomeMembers()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        await repository.CreateUserAsync(
            new UserAccount(Guid.NewGuid(), "admin", "系统管理员", "unused", UserRole.Administrator, true),
            default);
        var organizationId = Guid.Parse("70000000-0000-0000-0000-000000000001");
        var division = await workflow.SaveOrganizationUnitAsync(
            new SaveOrganizationUnitCommand(null, organizationId, null, "MANAGER-DIV", "负责人测试事业部", OrganizationUnitKind.BusinessDivision, true, 0),
            "admin", UserRole.Administrator, default);
        var department = await workflow.SaveOrganizationUnitAsync(
            new SaveOrganizationUnitCommand(null, organizationId, division.Id, "MANAGER-DEPT", "负责人测试子部门", OrganizationUnitKind.Department, true, 0),
            "admin", UserRole.Administrator, default);

        var directory = await workflow.SetOrganizationUnitManagersAsync(
            department.Id, "admin", [], "admin", UserRole.Administrator, default);

        Assert.Equal("admin", Assert.Single(directory.Managers, item => item.UnitId == department.Id).PrimaryManager);
        var membership = Assert.Single(directory.Memberships, item => item.UnitId == department.Id && item.Username == "admin");
        Assert.True(membership.IsPrimary);
    }

    [Fact]
    public async Task MechanicalBomPreview_KeepsUnclassifiedAndManualUnmatchedItemsWithoutMutatingStoredBom()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        var storedBefore = (await repository.GetBomAsync(ProjectId, BomKind.NonStandard, default)).ToArray();

        var preview = await workflow.GenerateMechanicalBomAsync(ProjectId, false, "admin", UserRole.Administrator, default);

        Assert.False(preview.Applied);
        Assert.True(preview.UnclassifiedCount > 0);
        Assert.Contains(preview.UnclassifiedItems, item => item.IsPendingClassification && item.Kind == BomKind.Unclassified);
        Assert.DoesNotContain(preview.NonStandardItems, item => item.IsPendingClassification);
        Assert.True(preview.ManualUnmatchedCount > 0);
        var storedAfterPreview = await repository.GetBomAsync(ProjectId, BomKind.NonStandard, default);
        Assert.Equal(storedBefore.Length, storedAfterPreview.Count);
        Assert.DoesNotContain(storedAfterPreview, item => item.IsPendingClassification || item.IsManualUnmatched);

        var applied = await workflow.GenerateMechanicalBomAsync(ProjectId, true, "admin", UserRole.Administrator, default);
        Assert.True(applied.Applied);
        Assert.Equal(1, repository.BomBatchApplyCount);
        var appliedItems = applied.StandardItems.Concat(applied.NonStandardItems).Concat(applied.UnclassifiedItems).Concat(applied.ElectricalItems).ToArray();
        Assert.Equal(appliedItems.Length, appliedItems.Select(item => item.Id).Distinct().Count());
        Assert.Contains(await repository.GetBomAsync(ProjectId, BomKind.Unclassified, default), item => item.IsPendingClassification);
        Assert.DoesNotContain(await repository.GetBomAsync(ProjectId, BomKind.NonStandard, default), item => item.IsPendingClassification);
    }

    [Fact]
    public async Task MechanicalBomSource_PreservesEmptyCardFieldsWithoutLegacyNamesOrConfigurationFallback()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        var sourceDocument = await repository.FindDocumentAsync(
            Guid.Parse("22222222-2222-2222-2222-222222222223"), default) ?? throw new InvalidOperationException();
        var snapshot = await repository.GetLatestReferenceSnapshotAsync(ProjectId, default) ?? throw new InvalidOperationException();
        await repository.CheckoutAsync(sourceDocument.Id, "admin", default);
        await repository.CheckInVersionAsync(sourceDocument.Id, "admin", new DocumentVersionCommit(
            new StoredFile("versions/part-name.sldasm", 10, new string('A', 64), DateTimeOffset.UtcNow),
            "零件名称映射测试",
            new Dictionary<string, string?>
            {
                [CadPropertyCardSnapshot.SchemaKey] = "1",
                [CadPropertyCardSnapshot.ScopePrefix + "物料分类"] = "Global",
                [CadPropertyCardSnapshot.ScopePrefix + "物料编码"] = "Global",
                [CadPropertyCardSnapshot.ScopePrefix + "物料名称"] = "Global",
                [CadPropertyCardSnapshot.ScopePrefix + "材质"] = "Global",
                [CadPropertyCardSnapshot.ScopePrefix + "备注"] = "Global",
                ["全局/物料分类"] = "标准件",
                ["全局/物料编码"] = "",
                ["全局/物料名称"] = "",
                ["全局/材质"] = "",
                ["配置:Default/材质"] = "旧材料",
                ["全局/备注"] = "卡内备注",
                ["全局/数量"] = "0",
                ["全局/零件名称"] = "真空箱泵组安装架"
            },
            snapshot,
            [],
            [],
            ForceVersion: true,
            Name: "R70000050.02-1/CQ-WS-ISO63-PT2-20250520113101861-1"), default);

        var generated = await workflow.GenerateMechanicalBomAsync(ProjectId, false, "admin", UserRole.Administrator, default);
        var sourceItem = Assert.Single(generated.StandardItems, item => item.SourceDocumentId == sourceDocument.Id);

        Assert.Equal("", sourceItem.Name);
        Assert.Equal("", sourceItem.DrawingNumber);
        Assert.Null(sourceItem.Material);
        Assert.Equal("卡内备注", sourceItem.Remark);
        Assert.True(sourceItem.Quantity > 0);
        Assert.False(sourceItem.IsComplete);
    }

    [Fact]
    public async Task MechanicalBomReconcile_PreservesManuallyClassifiedDrawingItems()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        var generated = await workflow.GenerateMechanicalBomAsync(ProjectId, true, "admin", UserRole.Administrator, default);
        var pending = generated.UnclassifiedItems.First(item => item.IsPendingClassification);

        await workflow.BatchUpdateBomItemsAsync(
            ProjectId,
            new BatchUpdateBomItemsCommand([pending.Id], ["kind"], BomKind.NonStandard),
            "admin", UserRole.Administrator, default);

        var reconciled = await workflow.GenerateMechanicalBomAsync(ProjectId, true, "admin", UserRole.Administrator, default);
        var classified = Assert.Single(reconciled.NonStandardItems, item => item.Id == pending.Id);
        Assert.False(classified.IsPendingClassification);
        Assert.True(classified.IsManuallyOverridden);
        Assert.Equal("ManualOverrideMismatch", classified.ReconciliationStatus);
        Assert.Contains("物料分类", classified.ReconciliationNote);
        Assert.Equal(generated.UnclassifiedCount - 1, reconciled.UnclassifiedCount);
    }

    [Fact]
    public async Task MechanicalBom_VirtualClassificationRemainsOnlyInSourceData()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        var generated = await workflow.GenerateMechanicalBomAsync(ProjectId, true, "admin", UserRole.Administrator, default);
        var pending = generated.UnclassifiedItems.First(item => item.IsPendingClassification && item.SourceDocumentId.HasValue);

        var updated = Assert.Single(await workflow.BatchUpdateBomItemsAsync(
            ProjectId,
            new BatchUpdateBomItemsCommand([pending.Id], ["kind"], BomKind.Virtual),
            "admin", UserRole.Administrator, default));
        var sourceData = await workflow.GetBomSourceDataAsync(ProjectId, "admin", UserRole.Administrator, default);

        Assert.Equal(BomKind.Virtual, updated.Kind);
        Assert.Contains(await repository.GetBomAsync(ProjectId, BomKind.Virtual, default), item => item.Id == pending.Id);
        Assert.DoesNotContain(await repository.GetBomAsync(ProjectId, BomKind.Standard, default), item => item.Id == pending.Id);
        Assert.DoesNotContain(await repository.GetBomAsync(ProjectId, BomKind.NonStandard, default), item => item.Id == pending.Id);
        Assert.DoesNotContain(await repository.GetBomAsync(ProjectId, BomKind.Unclassified, default), item => item.Id == pending.Id);
        Assert.DoesNotContain(await repository.GetBomAsync(ProjectId, BomKind.Electrical, default), item => item.Id == pending.Id);
        Assert.Contains(sourceData, item => item.Id == pending.Id && item.Kind == BomKind.Virtual);
    }

    [Fact]
    public async Task DrawingSource_CanOnlyBeClassifiedAsStandardOrNonStandard()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        var generated = await workflow.GenerateMechanicalBomAsync(ProjectId, true, "admin", UserRole.Administrator, default);
        var pending = generated.UnclassifiedItems.First(item => item.IsPendingClassification && item.SourceDocumentId.HasValue);

        var exception = await Assert.ThrowsAsync<PdmRuleException>(() => workflow.BatchUpdateBomItemsAsync(
            ProjectId,
            new BatchUpdateBomItemsCommand([pending.Id], ["kind"], BomKind.Electrical),
            "admin", UserRole.Administrator, default));

        Assert.Contains("只能归入标准件或非标件BOM", exception.Message);
        Assert.Contains(await repository.GetBomAsync(ProjectId, BomKind.Unclassified, default), item => item.Id == pending.Id);
        Assert.DoesNotContain(await repository.GetBomAsync(ProjectId, BomKind.Electrical, default), item => item.Id == pending.Id);
    }

    [Fact]
    public async Task MechanicalReconcile_PreservesIndependentElectricalBomAndRawSourceExcludesIt()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        var electrical = new BomItem(Guid.NewGuid(), ProjectId, BomKind.Electrical, 1, "EL-INDEPENDENT", "独立电气件", 1, "件", null, "E1", "W1", true)
        {
            Source = "Manual"
        };
        await repository.ReplaceBomAsync(ProjectId, BomKind.Electrical, [electrical], default);

        var generated = await workflow.GenerateMechanicalBomAsync(ProjectId, true, "admin", UserRole.Administrator, default);
        var sourceData = await workflow.GetBomSourceDataAsync(ProjectId, "admin", UserRole.Administrator, default);

        Assert.Contains(generated.ElectricalItems, item => item.Id == electrical.Id);
        Assert.Contains(await repository.GetBomAsync(ProjectId, BomKind.Electrical, default), item => item.Id == electrical.Id);
        Assert.DoesNotContain(sourceData, item => item.Kind == BomKind.Electrical || item.Id == electrical.Id);
    }

    [Fact]
    public async Task NonStandardReconciliation_IgnoresNameAndReportsMaterialDifference()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        var generated = await workflow.GenerateMechanicalBomAsync(ProjectId, true, "admin", UserRole.Administrator, default);
        var pending = generated.UnclassifiedItems.First(item => item.IsPendingClassification && item.SourceDocumentId.HasValue);
        var sourceItem = Assert.Single(await workflow.BatchUpdateBomItemsAsync(
            ProjectId,
            new BatchUpdateBomItemsCommand([pending.Id], ["kind"], BomKind.NonStandard),
            "admin", UserRole.Administrator, default));
        var maintainedName = sourceItem.Name + "-维护值";

        var updated = Assert.Single(await workflow.BatchUpdateBomItemsAsync(
            ProjectId,
            new BatchUpdateBomItemsCommand([sourceItem.Id], ["name"], Name: maintainedName),
            "admin", UserRole.Administrator, default));
        var raw = Assert.Single(await workflow.GetBomSourceDataAsync(ProjectId, "admin", UserRole.Administrator, default),
            item => item.SourceDocumentId == sourceItem.SourceDocumentId && item.SourceConfiguration == sourceItem.SourceConfiguration);

        Assert.Equal(maintainedName, updated.Name);
        Assert.NotEqual(maintainedName, raw.Name);
        Assert.Equal("SourceMatched", updated.ReconciliationStatus);
        Assert.DoesNotContain("物料分类", updated.ReconciliationNote);
        Assert.DoesNotContain("物料名称", updated.ReconciliationNote);

        var materialUpdated = Assert.Single(await workflow.BatchUpdateBomItemsAsync(
            ProjectId,
            new BatchUpdateBomItemsCommand([sourceItem.Id], ["material"], Material: "6061"),
            "admin", UserRole.Administrator, default));

        Assert.Equal("SourceMatched", materialUpdated.ReconciliationStatus);
        Assert.DoesNotContain("材质", materialUpdated.ReconciliationNote);
    }

    [Fact]
    public async Task ApplyingMaterialCode_IgnoresSourceCodeAndBlankSourceModelAndBrand()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        var initial = await workflow.GenerateMechanicalBomAsync(ProjectId, true, "admin", UserRole.Administrator, default);
        var pending = initial.UnclassifiedItems.First(item => item.SourceDocumentId.HasValue);
        var sourceDocument = await repository.FindDocumentAsync(pending.SourceDocumentId!.Value, default) ?? throw new InvalidOperationException();
        if (!string.IsNullOrWhiteSpace(sourceDocument.CheckedOutBy))
            await repository.ForceReleaseCheckoutAsync(sourceDocument.Id, "admin", "测试准备", default);
        await repository.CheckoutAsync(sourceDocument.Id, "admin", default);
        var snapshot = await repository.GetLatestReferenceSnapshotAsync(ProjectId, default) ?? throw new InvalidOperationException();
        var properties = new Dictionary<string, string?>
        {
            [CadPropertyCardSnapshot.ScopePrefix + "物料分类"] = "Global",
            [CadPropertyCardSnapshot.ScopePrefix + "物料编码"] = "Global",
            ["全局/物料分类"] = "标准件",
            ["全局/物料编码"] = "01020014733"
        };
        await repository.CheckInVersionAsync(sourceDocument.Id, "admin", new DocumentVersionCommit(
            new StoredFile("versions/material-code.sldprt", 10, new string('B', 64), DateTimeOffset.UtcNow),
            "料号对账测试", properties, snapshot, [], [], ForceVersion: true), default);
        var generated = await workflow.GenerateMechanicalBomAsync(ProjectId, true, "admin", UserRole.Administrator, default);
        var sourceItem = generated.StandardItems.First(item => item.SourceDocumentId == sourceDocument.Id);
        var stale = sourceItem with
        {
            Specification = sourceItem.Specification + "-人工修改",
            ReconciliationStatus = "ManualOverrideMismatch",
            ReconciliationNote = "BOM维护值与最新图档源数据不一致：物料编码。"
        };
        await repository.ReplaceBomAsync(
            ProjectId,
            BomKind.Standard,
            generated.StandardItems.Select(item => item.Id == stale.Id ? stale : item).ToArray(),
            default);

        var updated = await workflow.ApplyMaterialCodeToBomAsync(
            ProjectId, stale.Id, "01020014734", "admin", default);

        Assert.Equal("SourceMatched", updated.ReconciliationStatus);
        Assert.DoesNotContain("物料编码", updated.ReconciliationNote);
        Assert.DoesNotContain("型号", updated.ReconciliationNote);
        Assert.DoesNotContain("品牌", updated.ReconciliationNote);
    }

    [Fact]
    public async Task ApplyingMaterialCode_ReportsOnlyNonBlankSourceModelAndBrandDifferences()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        var initial = await workflow.GenerateMechanicalBomAsync(ProjectId, true, "admin", UserRole.Administrator, default);
        var pending = initial.UnclassifiedItems.First(item => item.SourceDocumentId.HasValue);
        var sourceDocument = await repository.FindDocumentAsync(pending.SourceDocumentId!.Value, default) ?? throw new InvalidOperationException();
        if (!string.IsNullOrWhiteSpace(sourceDocument.CheckedOutBy))
            await repository.ForceReleaseCheckoutAsync(sourceDocument.Id, "admin", "测试准备", default);
        await repository.CheckoutAsync(sourceDocument.Id, "admin", default);
        var snapshot = await repository.GetLatestReferenceSnapshotAsync(ProjectId, default) ?? throw new InvalidOperationException();
        await repository.CheckInVersionAsync(sourceDocument.Id, "admin", new DocumentVersionCommit(
            new StoredFile("versions/source-model-brand.sldprt", 10, new string('E', 64), DateTimeOffset.UtcNow),
            "源数据型号品牌对账测试", StandardMaterialProperties("SOURCE-CODE", "SHF20", "美亚特"), snapshot, [], [], ForceVersion: true), default);
        var generated = await workflow.GenerateMechanicalBomAsync(ProjectId, true, "admin", UserRole.Administrator, default);
        var sourceItem = generated.StandardItems.First(item => item.SourceDocumentId == sourceDocument.Id);
        var maintained = sourceItem with
        {
            DrawingNumber = "BOM-CODE",
            Specification = "OTHER-MODEL",
            Brand = "其他品牌"
        };
        await repository.ReplaceBomAsync(
            ProjectId,
            BomKind.Standard,
            generated.StandardItems.Select(item => item.Id == maintained.Id ? maintained : item).ToArray(),
            default);

        var updated = await workflow.ApplyMaterialCodeToBomAsync(
            ProjectId, maintained.Id, "BOM-CODE", "admin", default);

        Assert.Equal("ManualOverrideMismatch", updated.ReconciliationStatus);
        Assert.Contains("型号", updated.ReconciliationNote);
        Assert.Contains("品牌", updated.ReconciliationNote);
        Assert.DoesNotContain("物料编码", updated.ReconciliationNote);
    }

    [Fact]
    public async Task ApplyingMaterialCode_ClearsStaleMismatch_WhenCodeModelAndBrandMatchApprovedMaster()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var materials = new InMemoryMaterialRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(
            repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System,
            materialRepository: materials);
        var initial = await workflow.GenerateMechanicalBomAsync(ProjectId, true, "admin", UserRole.Administrator, default);
        var pending = initial.UnclassifiedItems.First(item => item.SourceDocumentId.HasValue);
        var sourceDocument = await repository.FindDocumentAsync(pending.SourceDocumentId!.Value, default) ?? throw new InvalidOperationException();
        if (!string.IsNullOrWhiteSpace(sourceDocument.CheckedOutBy))
            await repository.ForceReleaseCheckoutAsync(sourceDocument.Id, "admin", "测试准备", default);
        await repository.CheckoutAsync(sourceDocument.Id, "admin", default);
        var snapshot = await repository.GetLatestReferenceSnapshotAsync(ProjectId, default) ?? throw new InvalidOperationException();
        var properties = StandardMaterialProperties("01020014733", "SHF20", "美亚特");
        await repository.CheckInVersionAsync(sourceDocument.Id, "admin", new DocumentVersionCommit(
            new StoredFile("versions/material-identity.sldprt", 10, new string('C', 64), DateTimeOffset.UtcNow),
            "料号一一对应测试", properties, snapshot, [], [], ForceVersion: true), default);
        await AddApprovedStandardMaterialAsync(materials, "01020014733", "SHF20", "美亚特");
        var generated = await workflow.GenerateMechanicalBomAsync(ProjectId, true, "admin", UserRole.Administrator, default);
        var sourceItem = generated.StandardItems.First(item => item.SourceDocumentId == sourceDocument.Id);
        var stale = sourceItem with
        {
            ReconciliationStatus = "ManualOverrideMismatch",
            ReconciliationNote = "BOM维护值与最新图档源数据不一致：物料编码。"
        };
        await repository.ReplaceBomAsync(
            ProjectId,
            BomKind.Standard,
            generated.StandardItems.Select(item => item.Id == stale.Id ? stale : item).ToArray(),
            default);

        var updated = await workflow.ApplyMaterialCodeToBomAsync(
            ProjectId, stale.Id, "01020014733", "admin", default);

        Assert.Equal("SourceMatched", updated.ReconciliationStatus);
        Assert.DoesNotContain("不一致", updated.ReconciliationNote);
    }

    [Fact]
    public async Task MechanicalBomReconcile_ReportsCanonicalBrandMismatch_WhenApprovedMasterDoesNotMatch()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var materials = new InMemoryMaterialRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(
            repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System,
            materialRepository: materials);
        var initial = await workflow.GenerateMechanicalBomAsync(ProjectId, true, "admin", UserRole.Administrator, default);
        var pending = initial.UnclassifiedItems.First(item => item.SourceDocumentId.HasValue);
        var sourceDocument = await repository.FindDocumentAsync(pending.SourceDocumentId!.Value, default) ?? throw new InvalidOperationException();
        if (!string.IsNullOrWhiteSpace(sourceDocument.CheckedOutBy))
            await repository.ForceReleaseCheckoutAsync(sourceDocument.Id, "admin", "测试准备", default);
        await repository.CheckoutAsync(sourceDocument.Id, "admin", default);
        var snapshot = await repository.GetLatestReferenceSnapshotAsync(ProjectId, default) ?? throw new InvalidOperationException();
        await repository.CheckInVersionAsync(sourceDocument.Id, "admin", new DocumentVersionCommit(
            new StoredFile("versions/material-brand-mismatch.sldprt", 10, new string('D', 64), DateTimeOffset.UtcNow),
            "品牌对账测试", StandardMaterialProperties("01020014733", "SHF20", "美亚特"), snapshot, [], [], ForceVersion: true), default);
        await AddApprovedStandardMaterialAsync(materials, "01020014733", "SHF20", "其他品牌");

        var generated = await workflow.GenerateMechanicalBomAsync(ProjectId, true, "admin", UserRole.Administrator, default);
        var item = generated.StandardItems.First(candidate => candidate.SourceDocumentId == sourceDocument.Id);

        Assert.Equal("ManualOverrideMismatch", item.ReconciliationStatus);
        Assert.Contains("品牌", item.ReconciliationNote);
        Assert.DoesNotContain("品牌与料品主档不一致", item.ReconciliationNote);
    }

    [Fact]
    public async Task CadPropertyWriteback_IsQueuedOnlyAfterBomIsExplicitlySaved()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        var generated = await workflow.GenerateMechanicalBomAsync(ProjectId, true, "admin", UserRole.Administrator, default);
        var pending = generated.UnclassifiedItems.First(item => item.IsPendingClassification && item.SourceDocumentId.HasValue);
        var sourceDocument = await repository.FindDocumentAsync(pending.SourceDocumentId!.Value, default) ?? throw new InvalidOperationException();
        if (!string.IsNullOrWhiteSpace(sourceDocument.CheckedOutBy))
            await repository.ForceReleaseCheckoutAsync(sourceDocument.Id, "admin", "测试准备", default);
        await repository.CheckoutAsync(sourceDocument.Id, "admin", default);
        var snapshot = await repository.GetLatestReferenceSnapshotAsync(ProjectId, default) ?? throw new InvalidOperationException();
        await repository.CheckInVersionAsync(sourceDocument.Id, "admin", new DocumentVersionCommit(
            new StoredFile("versions/source.sldprt", 10, new string('A', 64), DateTimeOffset.UtcNow),
            "测试版本", new Dictionary<string, string?>(), snapshot, [], [], ForceVersion: true), default);

        var maintained = Assert.Single(await workflow.BatchUpdateBomItemsAsync(
            ProjectId,
            new BatchUpdateBomItemsCommand([pending.Id], ["kind", "material"], BomKind.NonStandard, Material: "6061"),
            "admin", UserRole.Administrator, default));

        Assert.Equal(CadPropertyWritebackStatus.PendingSave, maintained.PropertyWritebackStatus);
        Assert.Empty(await repository.ListCadPropertyWritebacksAsync(ProjectId, default));

        var current = await repository.GetBomAsync(ProjectId, BomKind.NonStandard, default);
        var saved = await workflow.ReplaceBomAsync(ProjectId, BomKind.NonStandard, current.Select(item => new BomItemInput(
            item.Sequence, item.DrawingNumber, item.Name, item.Quantity, item.Unit, item.Material, item.Specification, item.Revision, item.IsComplete,
            item.SourceDocumentId, item.SourceConfiguration, item.Remark, item.Brand, item.SurfaceTreatment, item.Weight,
            item.IsPendingClassification, item.IsManualUnmatched, item.IsManuallyRetained)).ToArray(),
            "admin", UserRole.Administrator, default);

        Assert.Equal(CadPropertyWritebackStatus.Pending, saved.Single(item => item.Id == maintained.Id).PropertyWritebackStatus);
        var writeback = Assert.Single(await repository.ListCadPropertyWritebacksAsync(ProjectId, default));
        Assert.Equal(maintained.Id, writeback.BomItemId);
        Assert.Equal(CadPropertyWritebackStatus.Pending, writeback.Status);
    }

    [Fact]
    public async Task RestoreBomItemsFromSource_RestoresDrawingFieldsAndKeepsClassificationAndOrder()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        var generated = await workflow.GenerateMechanicalBomAsync(ProjectId, true, "admin", UserRole.Administrator, default);
        var pending = generated.UnclassifiedItems.First(item => item.IsPendingClassification && item.SourceDocumentId.HasValue);
        var classified = Assert.Single(await workflow.BatchUpdateBomItemsAsync(
            ProjectId,
            new BatchUpdateBomItemsCommand([pending.Id], ["kind"], BomKind.NonStandard),
            "admin", UserRole.Administrator, default));
        var source = Assert.Single(await workflow.GetBomSourceDataAsync(ProjectId, "admin", UserRole.Administrator, default),
            item => item.SourceDocumentId == classified.SourceDocumentId && item.SourceConfiguration == classified.SourceConfiguration);

        await workflow.BatchUpdateBomItemsAsync(
            ProjectId,
            new BatchUpdateBomItemsCommand([classified.Id], ["name", "material", "quantity", "revision"],
                Name: source.Name + "-人工修改", Material: "人工材质", Quantity: source.Quantity + 3, Revision: "W999"),
            "admin", UserRole.Administrator, default);
        var writebacksBeforeRestore = await repository.ListCadPropertyWritebacksAsync(ProjectId, default);

        var restored = Assert.Single(await workflow.RestoreBomItemsFromSourceAsync(
            ProjectId, new RestoreBomItemsFromSourceCommand([classified.Id]), "admin", UserRole.Administrator, default));
        var writebacksAfterRestore = await repository.ListCadPropertyWritebacksAsync(ProjectId, default);

        Assert.Equal(BomKind.NonStandard, restored.Kind);
        Assert.Equal(classified.Sequence, restored.Sequence);
        Assert.Equal(source.Name, restored.Name);
        Assert.Equal(source.Material, restored.Material);
        Assert.Equal(source.Quantity, restored.Quantity);
        Assert.Equal(source.Revision, restored.Revision);
        Assert.True(restored.IsManuallyOverridden);
        Assert.Equal("SourceMatched", restored.ReconciliationStatus);
        Assert.DoesNotContain("物料分类", restored.ReconciliationNote);
        Assert.Equal(writebacksBeforeRestore.Count, writebacksAfterRestore.Count);
        Assert.Contains("BOM分类与排序保持不变", restored.ReconciliationNote);
    }

    [Fact]
    public async Task RestoreBomItemsFromSource_RejectsManualItemsWithoutDrawingSource()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        var standard = (await repository.GetBomAsync(ProjectId, BomKind.Standard, default)).ToList();
        var manual = new BomItem(Guid.NewGuid(), ProjectId, BomKind.Standard, standard.Count + 1,
            "MANUAL-RESTORE", "人工新增物料", 1, "个", null, null, "W1", true) { Source = "Manual" };
        standard.Add(manual);
        await repository.ReplaceBomAsync(ProjectId, BomKind.Standard, standard, default);

        var exception = await Assert.ThrowsAsync<PdmRuleException>(() => workflow.RestoreBomItemsFromSourceAsync(
            ProjectId, new RestoreBomItemsFromSourceCommand([manual.Id]), "admin", UserRole.Administrator, default));

        Assert.Contains("没有图档源数据", exception.Message);
    }

    [Fact]
    public async Task ReclassifyBomItemsFromSource_PreviewsThenAtomicallyMovesAndRestoresSourceFields()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        var generated = await workflow.GenerateMechanicalBomAsync(ProjectId, true, "admin", UserRole.Administrator, default);
        var pending = generated.UnclassifiedItems.First(item => item.IsPendingClassification && item.SourceDocumentId.HasValue);
        var source = Assert.Single(await workflow.GetBomSourceDataAsync(ProjectId, "admin", UserRole.Administrator, default),
            item => item.SourceDocumentId == pending.SourceDocumentId && item.SourceConfiguration == pending.SourceConfiguration);
        await workflow.BatchUpdateBomItemsAsync(
            ProjectId,
            new BatchUpdateBomItemsCommand([pending.Id], ["name"], Name: source.Name + "-错误维护值"),
            "admin", UserRole.Administrator, default);

        var command = new ReclassifyBomItemsFromSourceCommand([pending.Id], BomKind.Standard);
        var preview = await workflow.PreviewBomSourceReclassificationAsync(ProjectId, command, "admin", UserRole.Administrator, default);
        var previewItem = Assert.Single(preview.Items);
        Assert.Equal(BomKind.Unclassified, previewItem.CurrentKind);
        Assert.Equal(BomKind.Standard, previewItem.TargetKind);
        Assert.DoesNotContain("物料名称", previewItem.ChangedFields);

        var updated = Assert.Single(await workflow.ReclassifyBomItemsFromSourceAsync(
            ProjectId, command, "admin", UserRole.Administrator, default));
        Assert.Equal(BomKind.Standard, updated.Kind);
        Assert.Equal(source.Name, updated.Name);
        Assert.Equal(source.DrawingNumber, updated.DrawingNumber);
        Assert.Equal(CadPropertyWritebackStatus.PendingSave, updated.PropertyWritebackStatus);
        Assert.DoesNotContain((await repository.GetBomAsync(ProjectId, BomKind.Unclassified, default)), item => item.Id == pending.Id);
        Assert.Contains((await repository.GetBomAsync(ProjectId, BomKind.Standard, default)), item => item.Id == pending.Id);
    }

    [Fact]
    public async Task CadPropertyWritebackQueue_SupersedesPendingRequestAndTracksCompletion()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var bomItem = (await repository.GetBomAsync(ProjectId, BomKind.Standard, default)).First();
        var document = (await repository.ListDocumentsAsync(ProjectId, default)).First();
        var first = new CadPropertyWriteback(Guid.NewGuid(), ProjectId, bomItem.Id, document.Id, "默认", Guid.NewGuid(), "W1",
            new Dictionary<string, string?> { ["物料分类"] = "标准件" }, CadPropertyWritebackStatus.Pending, "admin", DateTimeOffset.UtcNow);
        await repository.EnqueueCadPropertyWritebackAsync(first, default);
        var second = first with { Id = Guid.NewGuid(), RequestedAt = DateTimeOffset.UtcNow.AddSeconds(1) };

        await repository.EnqueueCadPropertyWritebackAsync(second, default);
        var all = await repository.ListCadPropertyWritebacksAsync(ProjectId, default);

        Assert.Equal(CadPropertyWritebackStatus.Superseded, all.Single(item => item.Id == first.Id).Status);
        Assert.Equal(CadPropertyWritebackStatus.Pending, all.Single(item => item.Id == second.Id).Status);
        var completed = await repository.UpdateCadPropertyWritebackAsync(second.Id, CadPropertyWritebackStatus.Succeeded, Guid.NewGuid(), null, default);
        Assert.Equal(CadPropertyWritebackStatus.Succeeded, completed.Status);
    }

    [Fact]
    public async Task IndependentBomVersions_OnlyChangedBomAdvancesAndBaselinePinsAllThree()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        foreach (var document in await repository.ListCheckedOutDocumentsAsync(default))
            await repository.ForceReleaseCheckoutAsync(document.Id, "admin", "测试准备", default);
        await PrepareApprovedNonStandardDrawingReviewAsync(repository, workflow);
        await workflow.ReplaceBomAsync(ProjectId, BomKind.Electrical,
            [new BomItemInput(1, "EL-VERSION", "版本测试电气件", 1, "个", null, "M18", "W1", true)],
            "admin", UserRole.Administrator, default);

        var first = await workflow.CreateReleasePackageAsync(
            ProjectId, null, $"RP-INDEPENDENT-{Guid.NewGuid():N}", "ECN-001", "首次建立三套独立BOM", "未指定", null,
            "admin", "admin", "admin", UserRole.Administrator, default);
        first = await PublishAsync(workflow, first);
        var firstBaseline = Assert.Single(await repository.ListManufacturingBomBaselinesAsync(ProjectId, default));
        Assert.Equal("BL-PRJ-2026-018-0-001", firstBaseline.Label);
        Assert.Equal(first.StandardBomVersionId, firstBaseline.StandardBomVersionId);
        Assert.Equal(first.NonStandardBomVersionId, firstBaseline.NonStandardBomVersionId);
        Assert.Equal(first.ElectricalBomVersionId, firstBaseline.ElectricalBomVersionId);

        var standard = await repository.GetBomAsync(ProjectId, BomKind.Standard, default);
        await workflow.ReplaceBomAsync(ProjectId, BomKind.Standard, standard.Select((item, index) => new BomItemInput(
            index + 1, item.DrawingNumber, item.Name, item.Quantity + 1, item.Unit, item.Material, item.Specification, item.Revision, true,
            SourceDocumentId: item.SourceDocumentId, SourceConfiguration: item.SourceConfiguration, Remark: item.Remark,
            Brand: item.Brand, SurfaceTreatment: item.SurfaceTreatment, Weight: item.Weight)).ToArray(),
            "admin", UserRole.Administrator, default);

        var second = await workflow.CreateReleasePackageAsync(
            ProjectId, null, $"RP-INDEPENDENT-{Guid.NewGuid():N}", "ECN-002", "标准件数量调整", "未指定", null,
            "admin", "admin", "admin", UserRole.Administrator, default);
        second = await PublishAsync(workflow, second);
        var baselines = await repository.ListManufacturingBomBaselinesAsync(ProjectId, default);
        Assert.Equal(2, baselines.Count);
        Assert.NotEqual(first.StandardBomVersionId, second.StandardBomVersionId);
        Assert.Equal(first.NonStandardBomVersionId, second.NonStandardBomVersionId);
        Assert.Equal(first.ElectricalBomVersionId, second.ElectricalBomVersionId);
        var versionHistory = await repository.ListBomVersionsAsync(ProjectId, null, default);
        Assert.All(versionHistory, version => Assert.Contains("-PRJ-2026-018-0-", version.Label));
        Assert.Equal("ECN-001", versionHistory.Single(version => version.Id == first.NonStandardBomVersionId).ChangeNumber);
        Assert.Equal("ECN-001", versionHistory.Single(version => version.Id == first.ElectricalBomVersionId).ChangeNumber);
        Assert.Equal(BomValidationFieldCatalog.ElectricalDefaults,
            versionHistory.Single(version => version.Id == first.ElectricalBomVersionId).ValidationRequiredFields);
        Assert.Contains(baselines, baseline => baseline.ReleasePackageId == second.Id
            && baseline.StandardBomVersionId == second.StandardBomVersionId
            && baseline.NonStandardBomVersionId == second.NonStandardBomVersionId
            && baseline.ElectricalBomVersionId == second.ElectricalBomVersionId);
    }

    [Fact]
    public async Task EcnReview_LocksAllThreeBomsUntilWithdrawn()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        foreach (var document in await repository.ListCheckedOutDocumentsAsync(default))
            await repository.ForceReleaseCheckoutAsync(document.Id, "admin", "测试准备", default);
        await PrepareApprovedNonStandardDrawingReviewAsync(repository, workflow);
        await workflow.ReplaceBomAsync(ProjectId, BomKind.Electrical,
            [new BomItemInput(1, "EL-LOCK", "锁定测试电气件", 1, "个", null, "M18", "W1", true)],
            "admin", UserRole.Administrator, default);
        var package = await workflow.CreateReleasePackageAsync(
            ProjectId, null, $"RP-LOCK-{Guid.NewGuid():N}", "ECN-LOCK", "审批期间锁定", "未指定", null,
            "admin", "admin", "admin", UserRole.Administrator, default);
        package = await workflow.SubmitReleasePackageAsync(package.Id, "admin", UserRole.Administrator, default);

        var exception = await Assert.ThrowsAsync<PdmConflictException>(() => workflow.ReplaceBomAsync(
            ProjectId, BomKind.Electrical,
            [new BomItemInput(1, "EL-LOCK", "锁定测试电气件", 2, "个", null, "M18", "W1", true)],
            "admin", UserRole.Administrator, default));
        Assert.Contains("三个BOM已锁定", exception.Message);

        await workflow.WithdrawReleasePackageAsync(package.Id, "admin", UserRole.Administrator, "继续修改", default);
        var changed = await workflow.ReplaceBomAsync(ProjectId, BomKind.Electrical,
            [new BomItemInput(1, "EL-LOCK", "锁定测试电气件", 2, "个", null, "M18", "W1", true)],
            "admin", UserRole.Administrator, default);
        Assert.Equal(2, Assert.Single(changed).Quantity);
    }

    [Fact]
    public async Task ScopedStandardReview_UsesVersionedMechanicalChainAndLocksOnlyStandardBom()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        await ConfigureApprovalWorkflowsAsync(repository);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        var package = await workflow.CreateScopedReleasePackageAsync(
            ProjectId, null, string.Empty, string.Empty, string.Empty, "SHOULD-NOT-APPLY", "SHOULD-NOT-APPLY",
            ReleaseScope.StandardFormal, [], "admin", UserRole.Administrator, default);

        Assert.StartsWith("RP-PRJ-2026-018-0-", package.Number);
        Assert.StartsWith("S-PRJ-2026-018-0-B", package.StandardBomRevision);
        Assert.Equal(package.Number, package.ChangeNumber);
        Assert.Equal("未指定", package.EffectiveSerialFrom);
        Assert.Null(package.EffectiveSerialTo);
        Assert.Equal(string.Empty, package.ChangeReason);
        Assert.Equal(ReleaseScope.StandardFormal, package.Scope);
        Assert.Equal("mechanical-release", package.WorkflowCode);
        Assert.Equal(3, package.ApprovalTasks.Count);
        Assert.Equal("admin", package.ApprovalTasks.Single(task => task.Stage == ApprovalStage.MechanicalEngineer).Assignee);
        Assert.Equal("admin", package.ApprovalTasks.Single(task => task.Stage == ApprovalStage.MainDesigner).Assignee);
        Assert.Equal("mechanical-supervisor", package.ApprovalTasks.Single(task => task.Stage == ApprovalStage.MechanicalSupervisor).Assignee);
        Assert.False(package.LocksDocuments);
        package = await workflow.SubmitReleasePackageAsync(package.Id, "admin", UserRole.Administrator, default);
        Assert.Equal(ApprovalDecision.Approved, package.ApprovalTasks.Single(task => task.Stage == ApprovalStage.MechanicalEngineer).Decision);

        await workflow.ReplaceBomAsync(ProjectId, BomKind.Electrical,
            [new BomItemInput(1, "EL-SCOPE", "独立电气件", 1, "个", null, "M18", "W1", true)],
            "admin", UserRole.Administrator, default);
        var standard = await repository.GetBomAsync(ProjectId, BomKind.Standard, default);
        var locked = await Assert.ThrowsAsync<PdmConflictException>(() => workflow.ReplaceBomAsync(
            ProjectId, BomKind.Standard, standard.Select(item => new BomItemInput(item.Sequence, item.DrawingNumber, item.Name, item.Quantity, item.Unit, item.Material, item.Specification, item.Revision, true)).ToArray(),
            "admin", UserRole.Administrator, default));
        Assert.Contains("标准件BOM已锁定", locked.Message);

        var mainDesigner = package.ApprovalTasks.Single(task => task.Stage == ApprovalStage.MainDesigner);
        package = await workflow.EmergencyDecideAsync(mainDesigner.Id, "admin", UserRole.Administrator, ApprovalDecision.Approved, "长交期采购窗口即将关闭", default);
        Assert.True(package.ApprovalTasks.Single(task => task.Id == mainDesigner.Id).IsEmergencySubstitute);
        var supervisor = package.ApprovalTasks.Single(task => task.Stage == ApprovalStage.MechanicalSupervisor);
        package = await workflow.DecideAsync(supervisor.Id, "admin", UserRole.Administrator, ApprovalDecision.Approved, "机械主管批准", default);
        Assert.Equal(ReleasePackageState.Published, package.State);
        Assert.Empty(await repository.ListManufacturingBomBaselinesAsync(ProjectId, default));

        var invalidNextFormal = await Assert.ThrowsAsync<PdmRuleException>(() => workflow.CreateScopedReleasePackageAsync(
            ProjectId, null, string.Empty, string.Empty, "重复正式发布", "未指定", null,
            ReleaseScope.StandardFormal, [], "admin", UserRole.Administrator, default));
        Assert.Contains("后续只能发起增补/变更", invalidNextFormal.Message);
    }

    [Fact]
    public async Task ApprovedCategoryBom_AutomaticallyCreatesCategoryAndMasterCodeApplications()
    {
        var time = TimeProvider.System;
        var repository = new InMemoryPdmRepository(time);
        var materials = new InMemoryMaterialRepository(time);
        await ConfigureApprovalWorkflowsAsync(repository);
        await ConfigureU9ReadOnlyAsync(materials, time);
        var materialService = new MaterialService(materials, repository, new TestU9SecretProtector(), new ReadOnlyU9OpenApiClient(), time);
        var headerService = new BomHeaderService(
            repository, materials, materialService,
            new MaterialSyncBatchService(materials, repository, time), time);
        var workflow = new PdmWorkflowService(
            repository, new UnusedFileStorage(), new RecordingPublisher(), time, null, headerService);
        var package = await workflow.CreateScopedReleasePackageAsync(
            ProjectId, null, string.Empty, string.Empty, string.Empty, "未指定", null,
            ReleaseScope.StandardFormal, [], "admin", UserRole.Administrator, default);

        package = await workflow.SubmitReleasePackageAsync(package.Id, "admin", UserRole.Administrator, default);
        foreach (var task in package.ApprovalTasks.Where(task => task.Decision is null).OrderBy(task => task.StepOrder))
            package = await workflow.DecideAsync(task.Id, "admin", UserRole.Administrator, ApprovalDecision.Approved, "同意", default);

        Assert.Equal(ReleasePackageState.Published, package.State);
        Assert.Empty(await materials.ListMaterialCodeApplicationsAsync(
            ProjectId, MaterialCodeApplicationStatus.Pending, default));
        var applications = await materials.ListMaterialCodeApplicationsAsync(
            ProjectId, MaterialCodeApplicationStatus.Approved, default);
        Assert.Equal(2, applications.Count);
        Assert.Equal(
            [ProjectBomHeaderKind.Master, ProjectBomHeaderKind.Standard],
            applications.Select(application => application.BomHeaderKind).OrderBy(kind => kind).ToArray());

        var headers = await headerService.ListAsync(ProjectId, "admin", UserRole.Administrator, default);
        Assert.Equal(MaterialApprovalStatus.Approved, headers.Single(header => header.Kind == ProjectBomHeaderKind.Master).ApprovalStatus);
        Assert.Equal(MaterialApprovalStatus.Approved, headers.Single(header => header.Kind == ProjectBomHeaderKind.Standard).ApprovalStatus);
        Assert.Null(headers.Single(header => header.Kind == ProjectBomHeaderKind.Electrical).MaterialId);
        Assert.Equal(2, Assert.Single(await materials.ListRecentSyncBatchesAsync("admin", 10, default)).TotalCount);
    }

    [Fact]
    public async Task ScopedSupplement_GeneratesReadOnlyChangeNumber()
    {
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-09-02T18:30:00Z"));
        var repository = new InMemoryPdmRepository(time);
        await ConfigureApprovalWorkflowsAsync(repository);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), time);

        var invalid = await Assert.ThrowsAsync<PdmRuleException>(() => workflow.CreateScopedReleasePackageAsync(
            ProjectId, null, string.Empty, string.Empty, "未配置原因", "SHOULD-NOT-APPLY", null,
            ReleaseScope.StandardSupplement, [], "admin", UserRole.Administrator, default));
        Assert.Contains("不在系统配置中", invalid.Message);

        var package = await workflow.CreateScopedReleasePackageAsync(
            ProjectId, null, string.Empty, "MANUAL-VALUE-IGNORED", "设计变更；客户需求", "SHOULD-NOT-APPLY", null,
            ReleaseScope.StandardSupplement, [], "admin", UserRole.Administrator, default);

        Assert.StartsWith("RP-PRJ-2026-018-0-20260903-", package.Number);
        Assert.StartsWith("ECN-PRJ-2026-018-0-20260903-", package.ChangeNumber);
        Assert.NotEqual("MANUAL-VALUE-IGNORED", package.ChangeNumber);
        Assert.Equal("设计变更；客户需求", package.ChangeReason);
    }

    [Fact]
    public async Task ElectricalReview_ResolvesDepartmentAndParentManagersWhenDraftIsCreated()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        await ConfigureApprovalWorkflowsAsync(repository);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        await workflow.ReplaceBomAsync(ProjectId, BomKind.Electrical,
            [new BomItemInput(1, "EL-ORG", "组织审批测试电气件", 1, "个", null, "M18", "W1", true)],
            "admin", UserRole.Administrator, default);

        var package = await workflow.CreateScopedReleasePackageAsync(
            ProjectId, null, $"RP-ELECTRICAL-{Guid.NewGuid():N}", "ELE-001", "电气正式发布", "未指定", null,
            ReleaseScope.ElectricalFormal, [], "admin", UserRole.Administrator, default);

        Assert.Equal("admin", package.ApprovalTasks.Single(task => task.Stage == ApprovalStage.HardwareEngineer).Assignee);
        Assert.Equal("mechanical-supervisor", package.ApprovalTasks.Single(task => task.Stage == ApprovalStage.HardwareSupervisor).Assignee);
        Assert.Equal("standardization-supervisor", package.ApprovalTasks.Single(task => task.Stage == ApprovalStage.StandardizationSupervisor).Assignee);
    }

    [Fact]
    public async Task LongLeadStandardRelease_PublishesSelectedControlledOutputWithoutManufacturingBaseline()
    {
        var time = TimeProvider.System;
        var repository = new InMemoryPdmRepository(time);
        var materials = new InMemoryMaterialRepository(time);
        await ConfigureApprovalWorkflowsAsync(repository);
        await ConfigureU9ReadOnlyAsync(materials, time);
        var materialService = new MaterialService(materials, repository, new TestU9SecretProtector(), new ReadOnlyU9OpenApiClient(), time);
        var headerService = new BomHeaderService(repository, materials, materialService,
            new MaterialSyncBatchService(materials, repository, time), time);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), time, null, headerService);
        var selected = (await repository.GetBomAsync(ProjectId, BomKind.Standard, default)).Take(1).Select(item => item.Id).ToArray();
        var package = await workflow.CreateScopedReleasePackageAsync(
            ProjectId, null, $"RP-LONGLEAD-{Guid.NewGuid():N}", "LL-001", "长交期件提前采购", "未指定", null,
            ReleaseScope.StandardLongLead, selected, "admin", UserRole.Administrator, default);

        Assert.Equal(selected, package.SelectedBomItemIds);
        Assert.Single(package.StandardBomSnapshot);
        Assert.Null(package.StandardBomVersionId);
        Assert.False(package.CreatesManufacturingBaseline);
        package = await workflow.SubmitReleasePackageAsync(package.Id, "admin", UserRole.Administrator, default);
        foreach (var task in package.ApprovalTasks.Where(task => task.Decision is null).OrderBy(task => task.StepOrder))
            package = await workflow.DecideAsync(task.Id, "admin", UserRole.Administrator, ApprovalDecision.Approved, "同意", default);
        Assert.Equal(ReleasePackageState.Published, package.State);
        Assert.Empty(await repository.ListManufacturingBomBaselinesAsync(ProjectId, default));
        Assert.Empty(await materials.ListMaterialCodeApplicationsAsync(ProjectId, MaterialCodeApplicationStatus.Pending, default));
        var applications = await materials.ListMaterialCodeApplicationsAsync(ProjectId, MaterialCodeApplicationStatus.Approved, default);
        Assert.Equal(
            [ProjectBomHeaderKind.Master, ProjectBomHeaderKind.Standard],
            applications.Select(application => application.BomHeaderKind).OrderBy(kind => kind).ToArray());
    }

    [Fact]
    public async Task LongLeadStandardRelease_RejectsOverlappingActiveItemsButAllowsDifferentItems()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        await ConfigureApprovalWorkflowsAsync(repository);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        await workflow.ReplaceBomAsync(ProjectId, BomKind.Standard,
        [
            new BomItemInput(1, "STD-LL-001", "长交期件A", 1, "个", null, "M1", "W1", true),
            new BomItemInput(2, "STD-LL-002", "长交期件B", 1, "个", null, "M2", "W1", true),
            new BomItemInput(3, "STD-LL-001", "长交期件A另一实例", 2, "个", null, "M1", "W1", true)
        ], "admin", UserRole.Administrator, default);
        var standard = await repository.GetBomAsync(ProjectId, BomKind.Standard, default);
        var firstItem = standard[0].Id;
        var secondItem = standard[1].Id;
        var sameMaterialInstance = standard[2].Id;

        var first = await workflow.CreateScopedReleasePackageAsync(
            ProjectId, null, $"RP-LONGLEAD-A-{Guid.NewGuid():N}", "LL-A", "第一批长交期件", "未指定", null,
            ReleaseScope.StandardLongLead, [firstItem], "admin", UserRole.Administrator, default);

        var conflict = await Assert.ThrowsAsync<PdmConflictException>(() => workflow.CreateScopedReleasePackageAsync(
            ProjectId, null, $"RP-LONGLEAD-B-{Guid.NewGuid():N}", "LL-B", "重复长交期件", "未指定", null,
            ReleaseScope.StandardLongLead, [firstItem], "admin", UserRole.Administrator, default));
        Assert.Contains(first.Number, conflict.Message);
        Assert.Contains("不能同时进入", conflict.Message);

        var sameMaterialConflict = await Assert.ThrowsAsync<PdmConflictException>(() => workflow.CreateScopedReleasePackageAsync(
            ProjectId, null, $"RP-LONGLEAD-SAME-{Guid.NewGuid():N}", "LL-SAME", "同料号另一实例", "未指定", null,
            ReleaseScope.StandardLongLead, [sameMaterialInstance], "admin", UserRole.Administrator, default));
        Assert.Contains(first.Number, sameMaterialConflict.Message);

        var disjoint = await workflow.CreateScopedReleasePackageAsync(
            ProjectId, null, $"RP-LONGLEAD-C-{Guid.NewGuid():N}", "LL-C", "另一批长交期件", "未指定", null,
            ReleaseScope.StandardLongLead, [secondItem], "admin", UserRole.Administrator, default);
        Assert.Equal(new[] { secondItem }, disjoint.SelectedBomItemIds);
    }

    [Fact]
    public async Task LongLeadStandardRelease_AllowsRemainingQuantityAfterPartialPublicationButRejectsOverRelease()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        await ConfigureApprovalWorkflowsAsync(repository);
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System);
        await workflow.ReplaceBomAsync(ProjectId, BomKind.Standard,
        [
            new BomItemInput(1, "STD-LL-PUBLISHED", "部分已发布长交期件", 4, "个", null, "M1", "W1", true),
            new BomItemInput(2, "STD-LL-NEW", "未发布长交期件", 1, "个", null, "M2", "W1", true)
        ], "admin", UserRole.Administrator, default);
        var standardItems = await repository.GetBomAsync(ProjectId, BomKind.Standard, default);
        var publishedItem = standardItems[0];
        var newItem = standardItems[1];
        await repository.CreateReleasePackageAsync(new ReleasePackage(
            Guid.NewGuid(), ProjectId, "RP-STANDARD-PUBLISHED", ReleasePackageState.Published, Guid.NewGuid(), "W1", "W1",
            [], DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(-1), "C:\\PDM\\Release\\standard")
        {
            Scope = ReleaseScope.StandardFormal
        }, default);

        await repository.CreateReleasePackageAsync(new ReleasePackage(
            Guid.NewGuid(), ProjectId, "RP-LONGLEAD-PUBLISHED", ReleasePackageState.Published, Guid.NewGuid(), "W1", "W1",
            [], DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(-1), "C:\\PDM\\Release\\long-lead")
        {
            Scope = ReleaseScope.StandardLongLead,
            SelectedBomItemIds = [publishedItem.Id],
            StandardBomSnapshot = [publishedItem with { Quantity = 1 }]
        }, default);
        await repository.CreateReleasePackageAsync(new ReleasePackage(
            Guid.NewGuid(), ProjectId, "RP-LONGLEAD-PUBLISHED-2", ReleasePackageState.Published, Guid.NewGuid(), "W1", "W1",
            [], DateTimeOffset.UtcNow.AddMinutes(-30), DateTimeOffset.UtcNow.AddMinutes(-30), "C:\\PDM\\Release\\long-lead-2")
        {
            Scope = ReleaseScope.StandardLongLead,
            SelectedBomItemIds = [publishedItem.Id],
            StandardBomSnapshot = [publishedItem with { Quantity = 1 }]
        }, default);

        var overRelease = await Assert.ThrowsAsync<PdmRuleException>(() => workflow.CreateScopedReleasePackageAsync(
            ProjectId, null, $"RP-LONGLEAD-REPEAT-{Guid.NewGuid():N}", "LL-REPEAT", "同一物料再次长交期发布", "未指定", null,
            ReleaseScope.StandardLongLead, [publishedItem.Id], "admin", UserRole.Administrator, default,
            new Dictionary<Guid, decimal> { [publishedItem.Id] = 3 }));
        Assert.Contains("已提前发布2", overRelease.Message);
        Assert.Contains("超过剩余可发布数量2", overRelease.Message);

        var remaining = await workflow.CreateScopedReleasePackageAsync(
            ProjectId, null, $"RP-LONGLEAD-REMAINING-{Guid.NewGuid():N}", "LL-REMAINING", "发布剩余长交期数量", "未指定", null,
            ReleaseScope.StandardLongLead, [publishedItem.Id], "admin", UserRole.Administrator, default,
            new Dictionary<Guid, decimal> { [publishedItem.Id] = 2 });
        Assert.Equal(2, Assert.Single(remaining.StandardBomSnapshot).Quantity);

        var second = await workflow.CreateScopedReleasePackageAsync(
            ProjectId, null, $"RP-LONGLEAD-NEW-{Guid.NewGuid():N}", "LL-NEW", "另一物料长交期发布", "未指定", null,
            ReleaseScope.StandardLongLead, [newItem.Id], "admin", UserRole.Administrator, default);
        Assert.Equal(new[] { newItem.Id }, second.SelectedBomItemIds);
    }

    [Fact]
    public async Task LongLeadStandardRelease_DraftCanUpdatePartialQuantityAndDeleteWithoutConflictingWithItself()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        await ConfigureApprovalWorkflowsAsync(repository);
        var publisher = new RecordingPublisher();
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), publisher, TimeProvider.System);
        await workflow.ReplaceBomAsync(ProjectId, BomKind.Standard,
        [
            new BomItemInput(1, "STD-LL-EDIT", "可编辑长交期件", 4, "个", null, "M1", "W1", true)
        ], "admin", UserRole.Administrator, default);
        var selectedItem = Assert.Single(await repository.GetBomAsync(ProjectId, BomKind.Standard, default));

        var package = await workflow.CreateScopedReleasePackageAsync(
            ProjectId, null, $"RP-LONGLEAD-EDIT-{Guid.NewGuid():N}", "LL-EDIT", "首次长交期数量", "未指定", null,
            ReleaseScope.StandardLongLead, [selectedItem.Id], "admin", UserRole.Administrator, default,
            new Dictionary<Guid, decimal> { [selectedItem.Id] = 3 });

        var updated = await workflow.UpdateReleasePackageDraftAsync(
            package.Id, "调整长交期数量", [selectedItem.Id],
            new Dictionary<Guid, decimal> { [selectedItem.Id] = 2 },
            "admin", UserRole.Administrator, default);

        Assert.Equal(package.Number, updated.Number);
        Assert.Equal(package.Scope, updated.Scope);
        Assert.Equal(package.WorkflowCode, updated.WorkflowCode);
        Assert.Equal(package.WorkflowVersion, updated.WorkflowVersion);
        Assert.Equal("调整长交期数量", updated.ChangeReason);
        Assert.Equal(2, Assert.Single(updated.StandardBomSnapshot).Quantity);
        Assert.Equal(2, publisher.PrepareCalls);

        await workflow.DeleteReleasePackageDraftAsync(package.Id, "admin", UserRole.Administrator, default);

        Assert.Null(await repository.FindReleasePackageAsync(package.Id, default));
        Assert.Equal(1, publisher.DiscardCalls);
    }

    [Fact]
    public async Task ReleasePackage_NonDraftCannotBeDeleted()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        await ConfigureApprovalWorkflowsAsync(repository);
        var publisher = new RecordingPublisher();
        var workflow = new PdmWorkflowService(repository, new UnusedFileStorage(), publisher, TimeProvider.System);
        var package = await repository.CreateReleasePackageAsync(new ReleasePackage(
            Guid.NewGuid(), ProjectId, $"RP-PUBLISHED-{Guid.NewGuid():N}", ReleasePackageState.Published, Guid.NewGuid(), "W1", "W1",
            [], DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, "C:\\PDM\\Release\\published"), default);

        await Assert.ThrowsAsync<PdmConflictException>(() =>
            workflow.DeleteReleasePackageDraftAsync(package.Id, "admin", UserRole.Administrator, default));

        Assert.NotNull(await repository.FindReleasePackageAsync(package.Id, default));
        Assert.Equal(0, publisher.DiscardCalls);
    }

    private static async Task ConfigureApprovalWorkflowsAsync(InMemoryPdmRepository repository)
    {
        if (await repository.FindUserAsync("admin", default) is null)
            await repository.CreateUserAsync(new UserAccount(Guid.NewGuid(), "admin", "系统管理员", "unused", UserRole.Administrator, true), default);
        if (await repository.FindUserAsync("mechanical-supervisor", default) is null)
            await repository.CreateUserAsync(new UserAccount(Guid.NewGuid(), "mechanical-supervisor", "机械主管", "unused", UserRole.Approver, true), default);
        if (await repository.FindUserAsync("standardization-supervisor", default) is null)
            await repository.CreateUserAsync(new UserAccount(Guid.NewGuid(), "standardization-supervisor", "标准化主管", "unused", UserRole.Approver, true), default);

        var organizationId = Guid.Parse("70000000-0000-0000-0000-000000000001");
        var division = await repository.SaveOrganizationUnitAsync(
            new SaveOrganizationUnitCommand(null, organizationId, null, "APPROVAL-DIV", "审批测试事业部", OrganizationUnitKind.BusinessDivision, true, 0), default);
        var department = await repository.SaveOrganizationUnitAsync(
            new SaveOrganizationUnitCommand(null, organizationId, division.Id, "APPROVAL-DEPT", "审批测试部门", OrganizationUnitKind.Department, true, 0), default);
        await repository.SetOrganizationMembershipsAsync("admin", [department.Id], department.Id, default);
        await repository.SetOrganizationUnitManagersAsync(department.Id, "mechanical-supervisor", [], default);
        await repository.SetOrganizationUnitManagersAsync(division.Id, "standardization-supervisor", [], default);
        await repository.SetMainProjectStaffingAsync(ProjectId, new SetMainProjectStaffingCommand("admin", [], ["admin"]), "admin", default);
    }

    private static Task ConfigureU9ReadOnlyAsync(InMemoryMaterialRepository materials, TimeProvider time) =>
        materials.SaveIntegrationConfigurationAsync(new(
            "http://u9.example.test/U9", "01", "7", "pdm", "PDM", "protected:test-secret",
            U9MaterialContract.CreatePath, U9MaterialContract.QueryPath, false, "admin", time.GetUtcNow(),
            UnitCodeMappings: new Dictionary<string, string>()), default);

    private static async Task PrepareApprovedNonStandardDrawingReviewAsync(InMemoryPdmRepository repository, PdmWorkflowService workflow)
    {
        var documents = await repository.ListDocumentsAsync(ProjectId, default);
        var model = documents.Single(document => document.DrawingNumber == "A01-100" && document.Kind == DocumentKind.Assembly);
        var drawing = documents.Single(document => document.DrawingNumber == "A01-100" && document.Kind == DocumentKind.Drawing);
        await CheckInAsync(repository, model.Id, "designer", new Dictionary<string, string?>(), 'A');
        await CheckInAsync(repository, drawing.Id, "designer", new Dictionary<string, string?>(), 'B');
        await repository.ReplaceBomAsync(ProjectId, BomKind.NonStandard,
        [
            new BomItem(Guid.NewGuid(), ProjectId, BomKind.NonStandard, 1, "A01-100", "机架组件", 1, "件", "Q235B", null, "W1", true)
            {
                SourceDocumentId = model.Id,
                SourceConfiguration = "默认",
                Source = "Auto"
            }
        ], default);

        var review = await workflow.CreateDrawingReviewPackageAsync(ProjectId, "submitter", UserRole.Administrator, default);
        var item = Assert.Single(review.Items);
        review = await workflow.DecideDrawingReviewTargetAsync(review.Id, item.Id,
            new DecideDrawingReviewTargetCommand(DrawingReviewTarget.Model3D, DrawingReviewDecision.Approve, "3D通过"),
            "model-reviewer", UserRole.Administrator, default);
        review = await workflow.DecideDrawingReviewTargetAsync(review.Id, item.Id,
            new DecideDrawingReviewTargetCommand(DrawingReviewTarget.Drawing2D, DrawingReviewDecision.Approve, "2D通过"),
            "drawing-reviewer", UserRole.Administrator, default);

        foreach (var request in (await repository.ListCadPropertyWritebacksAsync(ProjectId, default)).OrderBy(request => request.SourceDocumentId))
        {
            await workflow.StartCadPropertyWritebackAsync(request.Id, "cad-client", UserRole.Administrator, default);
            var result = await CheckInAsync(repository, request.SourceDocumentId, "cad-client", request.Properties,
                request.SourceDocumentId == model.Id ? 'C' : 'D', request.Id);
            await workflow.CompleteCadPropertyWritebackAsync(request.Id, Assert.IsType<DocumentVersion>(result.Version).Id,
                "cad-client", UserRole.Administrator, default);
        }

        Assert.Equal(DrawingReviewPackageState.Approved,
            (await repository.FindDrawingReviewPackageAsync(review.Id, default))?.State);
    }

    private static async Task<DocumentCheckInResult> CheckInAsync(
        InMemoryPdmRepository repository,
        Guid documentId,
        string actor,
        IReadOnlyDictionary<string, string?> properties,
        char hashCharacter,
        Guid? drawingReviewWritebackId = null)
    {
        var sessionId = Guid.NewGuid();
        var document = await repository.CheckoutAsync(documentId, actor, sessionId, "TEST-WS",
            DateTimeOffset.UtcNow.AddMinutes(15), drawingReviewWritebackId, default);
        var root = new DocumentReferenceNode(Guid.NewGuid(), document.Id, document.DrawingNumber, document.FileName, document.Name,
            document.Kind, "默认", 1, ReferenceNodeStatus.Normal, document.Revision, actor, []);
        return await repository.CheckInVersionAsync(documentId, actor, sessionId, new DocumentVersionCommit(
            new StoredFile($"versions/{document.FileName}", 128, new string(hashCharacter, 64), DateTimeOffset.UtcNow),
            "测试存档",
            properties,
            new CadReferenceSnapshot(Guid.NewGuid(), ProjectId, document.Id, DateTimeOffset.UtcNow, actor, root, new string('F', 64)),
            [],
            []), drawingReviewWritebackId, default);
    }

    private static Dictionary<string, string?> StandardMaterialProperties(string code, string model, string brand) => new()
    {
        [CadPropertyCardSnapshot.ScopePrefix + "物料分类"] = "Global",
        [CadPropertyCardSnapshot.ScopePrefix + "物料编码"] = "Global",
        [CadPropertyCardSnapshot.ScopePrefix + "型号"] = "Global",
        [CadPropertyCardSnapshot.ScopePrefix + "品牌"] = "Global",
        ["全局/物料分类"] = "标准件",
        ["全局/物料编码"] = code,
        ["全局/型号"] = model,
        ["全局/品牌"] = brand
    };

    private static async Task AddApprovedStandardMaterialAsync(
        InMemoryMaterialRepository repository,
        string code,
        string model,
        string brand)
    {
        var category = await repository.FindCategoryAsync("0102", default) ?? throw new InvalidOperationException();
        var now = DateTimeOffset.UtcNow;
        await repository.CreateMaterialAsync(new PdmMaterial(
            Guid.NewGuid(), code, $"料品{code}", MaterialKind.Standard, MaterialSupplyMode.Purchase, "001",
            model, null, null, brand, null, null, null, null,
            MaterialApprovalStatus.Approved, "admin", now, "0102", null, code, MaterialSyncStatus.Succeeded,
            "admin", now, "admin", now, 1, "0102", U9SyncConfirmed: true), category, default);
    }

    private static async Task<ReleasePackage> PublishAsync(PdmWorkflowService workflow, ReleasePackage package)
    {
        package = await workflow.SubmitReleasePackageAsync(package.Id, "admin", UserRole.Administrator, default);
        package = await workflow.DecideAsync(package.ApprovalTasks.Single(task => task.Stage == ApprovalStage.ProcessReview).Id,
            "admin", UserRole.Administrator, ApprovalDecision.Approved, "工艺审核通过", default);
        return await workflow.DecideAsync(package.ApprovalTasks.Single(task => task.Stage == ApprovalStage.Approval).Id,
            "admin", UserRole.Administrator, ApprovalDecision.Approved, "批准发布", default);
    }

    private sealed class RecordingPublisher : IReleasePackagePublisher
    {
        public int PrepareCalls { get; private set; }
        public int DiscardCalls { get; private set; }
        public int ValidateCalls { get; private set; }
        public int PublishCalls { get; private set; }
        public IReadOnlyList<ReleasePreviewSource> PreviewSources { get; private set; } = [];
        public Task PrepareAsync(ReleasePackage package, Project project, CancellationToken cancellationToken) { PrepareCalls++; return Task.CompletedTask; }
        public Task DiscardDraftAsync(ReleasePackage package, Project project, CancellationToken cancellationToken) { DiscardCalls++; return Task.CompletedTask; }
        public Task ValidateAsync(ReleasePackage package, Project project, CancellationToken cancellationToken) { ValidateCalls++; return Task.CompletedTask; }
        public Task<ReleasePublication> PublishAsync(ReleasePackage package, Project project, IReadOnlyList<ReleasePreviewSource> sources, CancellationToken cancellationToken)
        {
            PublishCalls++;
            PreviewSources = sources.ToArray();
            var previews = sources.ToDictionary(
                source => source.DocumentId,
                source => new DocumentPreviewArtifact(
                    source.Kind == DocumentKind.Drawing ? DocumentPreviewFormat.Pdf : DocumentPreviewFormat.Step,
                    $".release-previews/{source.DocumentId:N}.{(source.Kind == DocumentKind.Drawing ? "pdf" : "step")}",
                    1,
                    new string('A', 64),
                    source.SourceSha256));
            return Task.FromResult(new ReleasePublication("C:\\PDM\\Release\\package", previews));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class UnusedFileStorage : IFileStorage
    {
        public Task<StoredFile> CompleteUploadAsync(Guid sessionId, string relativeTargetPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoredFile> CopyVersionAsync(Project project, StoredFile source, string relativeTargetPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UploadSession> GetUploadSessionAsync(Guid sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> IsAvailableAsync(string location, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<Stream> OpenReadAsync(string absolutePath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UploadSession> StartUploadAsync(Guid projectId, string fileName, long totalLength, string expectedSha256, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task VerifyStoredFileAsync(Project project, StoredFile file, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UploadSession> WriteChunkAsync(Guid sessionId, int chunkIndex, Stream content, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TestU9SecretProtector : IU9SecretProtector
    {
        public string Protect(string secret) => $"protected:{secret}";
        public string Unprotect(string ciphertext) => ciphertext[10..];
    }

    private sealed class ReadOnlyU9OpenApiClient : IU9OpenApiClient
    {
        public Task<U9AuthenticationResult> AuthenticateAsync(U9AuthenticationRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new U9AuthenticationResult("token"));
        public Task<U9ItemQueryResult> QueryItemsAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("自动创建BOM料号申请时不应查询U9C料号。");
        public Task<U9UomQueryResult> QueryUomsAsync(string baseUrl, string token, string payloadJson, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("自动创建BOM料号申请时不应查询U9C单位。");
        public Task<U9BusinessBatchResult> PostBatchAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("自动创建BOM料号申请时不应写入U9C。");
        public Task<U9CustomerQueryResult> QueryCustomerReferencesAsync(string baseUrl, string path, string token, string payloadJson, CancellationToken cancellationToken) =>
            Task.FromResult(new U9CustomerQueryResult(0, null, [], 0));
    }
}
