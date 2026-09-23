using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class FullWorkflowAcceptanceTests
{
    [Fact]
    public async Task IsolatedQa_CompletesProjectDrawingBomPlanningAndValidationWorkflow()
    {
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 9, 13, 8, 0, 0, TimeSpan.Zero));
        var repository = new InMemoryPdmRepository(time);
        var storage = new RecordingFileStorage(time);
        var publisher = new RecordingPublisher();
        var projectPlans = new InMemoryProjectPlanningRepository();
        var planning = new ProjectPlanningService(projectPlans, repository, time);
        var workflow = new PdmWorkflowService(repository, storage, publisher, time, projectPlanningService: planning);
        var validationPlans = new InMemoryValidationPlanRepository();
        var validation = new ValidationPlanService(validationPlans, repository, storage, new StubRecognitionService(), new NoOpValidationPlanFileArchive(), time);

        var numbering = await repository.GetProjectNumberingOptionsAsync(default);
        var organizationId = numbering.Organizations.First(item => item.IsActive).Id;
        var customer = (await repository.ListCustomersAsync(true, default)).First(item => item.IsActive);
        var projectType = numbering.ProjectTypes.First(item => item.IsActive).Code;
        var equipmentType = numbering.EquipmentTypes.First(item => item.IsActive).Code;

        await CreateUserAsync(workflow, "qa_bu_manager", "QA事业部经理", "BusinessUnitManager", organizationId);
        await CreateUserAsync(workflow, "qa_pm", "QA项目经理", "ProjectManager", organizationId);
        await CreateUserAsync(workflow, "qa_lead", "QA主设", "MechanicalManager", organizationId);
        await CreateUserAsync(workflow, "qa_engineer", "QA工程师", "Engineer", organizationId);
        await CreateUserAsync(workflow, "qa_electrical", "QA电气工程师", "HardwareEngineer", organizationId);
        await CreateUserAsync(workflow, "qa_reviewer", "QA图纸审核人", "ProcessReviewer", organizationId);
        await CreateUserAsync(workflow, "qa_approver", "QA批准人", "Approver", organizationId);
        await CreateUserAsync(workflow, "qa_viewer", "QA只读人员", "ProductionViewer", organizationId);

        var division = await workflow.SaveOrganizationUnitAsync(
            new(null, organizationId, null, $"QA-DIV-{Guid.NewGuid():N}"[..24], "QA隔离测试事业部", OrganizationUnitKind.BusinessDivision, true, 9000, true),
            "admin", UserRole.Administrator, default);
        foreach (var username in new[] { "qa_bu_manager", "qa_pm", "qa_lead", "qa_engineer", "qa_electrical", "qa_reviewer", "qa_approver", "qa_viewer" })
            await workflow.SetOrganizationMembershipsAsync(username, [division.Id], division.Id, "admin", UserRole.Administrator, default);
        await workflow.SetOrganizationUnitManagersAsync(division.Id, "qa_bu_manager", [], "admin", UserRole.Administrator, default);
        // 事业部经理同时是本部门的机械主管，需要具备审图权限才能处理图纸审核的批准节点。
        await workflow.UpdateRolePermissionsAsync("BusinessUnitManager",
            [.. RolePermissionCatalog.InitialPermissions("BusinessUnitManager", UserRole.Approver), PermissionCodes.DrawingReviewAnnotate, PermissionCodes.DrawingReviewDecide],
            "admin", UserRole.Administrator, default);

        var root = await workflow.CreateNumberedProjectAsync(new(
            organizationId, projectType, equipmentType, customer.Id, "QA全流程主项目", "隔离验收",
            new DateOnly(2026, 9, 13), 1, "qa_pm", string.Empty, string.Empty),
            "qa_pm", UserRole.Engineer, default);
        root = await workflow.SetProjectExecutionUnitAsync(root.Id, division.Id, "admin", UserRole.Administrator, default);
        root = await workflow.SetMainProjectStaffingAsync(root.Id, new("qa_pm", [], ["qa_lead"]), "qa_bu_manager", UserRole.BusinessUnitManager, default);

        var child = await workflow.CreateSubprojectAsync(new(root.Id, "QA全流程子项目一", "单元一", 1), "qa_pm", UserRole.Engineer, default);
        var sibling = await workflow.CreateSubprojectAsync(new(root.Id, "QA全流程子项目二", "单元二", 1), "qa_pm", UserRole.Engineer, default);
        child = await workflow.SetChildProjectManagerAsync(child.Id, "qa_pm", "qa_lead", UserRole.Approver, default);
        child = await workflow.SetChildProjectDesignersAsync(child.Id, ["qa_engineer"], "qa_lead", UserRole.Approver, default);
        sibling = await workflow.SetChildProjectManagerAsync(sibling.Id, "qa_pm", "qa_pm", UserRole.Engineer, default);
        sibling = await workflow.SetChildProjectDesignersAsync(sibling.Id, ["qa_engineer"], "qa_pm", UserRole.Engineer, default);

        Assert.Equal(division.Id, root.ExecutionUnitId);
        Assert.Equal("qa_pm", child.PrimaryProjectManager);
        Assert.Contains("qa_engineer", child.Designers);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => workflow.SetChildProjectDesignersAsync(
            child.Id, ["qa_viewer"], "qa_viewer", UserRole.ProductionViewer, default));

        var template = (await planning.ListTemplatesAsync(false, "admin", UserRole.Administrator, default)).First();
        var plan = await planning.GenerateAsync(child.Id,
            new(template.Id, new DateOnly(2026, 9, 13), 90, false, null), "qa_pm", UserRole.Engineer, default);
        Assert.Contains(plan.Tasks, item => item.Assignee == "qa_engineer");
        plan = await planning.SubmitApprovalAsync(child.Id, plan.RowVersion, "qa_pm", UserRole.Engineer, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => planning.DecideApprovalAsync(
            child.Id, plan.RowVersion, true, "越权审批", "qa_reviewer", UserRole.ProcessReviewer, default));
        plan = await planning.DecideApprovalAsync(child.Id, plan.RowVersion, true, "事业部批准首版计划", "qa_bu_manager", UserRole.BusinessUnitManager, default);
        plan = await planning.SetBaselineAsync(child.Id, plan.RowVersion, "qa_pm", UserRole.Engineer, default);
        var engineerTask = plan.Tasks.First(item => item.Assignee == "qa_engineer" && !item.IsMilestone);
        plan = await planning.UpdateProgressAsync(child.Id, engineerTask.Id,
            new(50, new DateOnly(2026, 9, 13), null, plan.RowVersion), "qa_engineer", UserRole.Engineer, default);
        Assert.Equal(50, plan.Tasks.Single(item => item.Id == engineerTask.Id).CompletionPercent);

        var rootHash = new string('0', 64);
        var modelHash = new string('1', 64);
        var drawingHash = new string('2', 64);
        var preflight = await workflow.PreflightDocumentRegistrationAsync(child.Id,
            [new("root", "QA-ASM-ROOT.SLDASM", DocumentKind.Assembly, rootHash),
             new("model", "QA-PART-001.SLDPRT", DocumentKind.Part, modelHash),
             new("drawing", "QA-PART-001.SLDDRW", DocumentKind.Drawing, drawingHash)],
            "qa_engineer", UserRole.Engineer, default);
        Assert.All(preflight, item => Assert.Equal(DocumentRegistrationMatchKind.New, item.MatchKind));
        var rootAssembly = await workflow.RegisterDocumentAsync(
            new(child.Id, "QA-ASM-ROOT", "QA测试总装", "QA-ASM-ROOT.SLDASM", DocumentKind.Assembly, SourceSha256: rootHash),
            "qa_engineer", UserRole.Engineer, default);
        var model = await workflow.RegisterDocumentAsync(
            new(child.Id, "QA-PART-001", "QA测试零件", "QA-PART-001.SLDPRT", DocumentKind.Part, SourceSha256: modelHash),
            "qa_engineer", UserRole.Engineer, default);
        var drawing = await workflow.RegisterDocumentAsync(
            new(child.Id, "QA-PART-001", "QA测试零件工程图", "QA-PART-001.SLDDRW", DocumentKind.Drawing,
                RelatedModelDocumentId: model.Id, SourceSha256: drawingHash),
            "qa_engineer", UserRole.Engineer, default);
        await CheckInAsync(workflow, model, "qa_engineer", time.GetUtcNow(), 'A', false);
        await CheckInAsync(workflow, drawing, "qa_engineer", time.GetUtcNow(), 'B', false);
        var modelNode = new DocumentReferenceNode(Guid.NewGuid(), model.Id, "QA-ASM-ROOT/QA-PART-001",
            model.FileName, model.Name, model.Kind, "默认", 1, ReferenceNodeStatus.Normal, model.Revision, null, []);
        await CheckInAsync(workflow, rootAssembly, "qa_engineer", time.GetUtcNow(), 'D', true, children: [modelNode]);

        var sourceItem = Assert.Single(await repository.GetBomAsync(child.Id, BomKind.Unclassified, default));
        await workflow.ResolveBomItemAsync(child.Id, sourceItem.Id, new("classify", BomKind.NonStandard),
            "qa_engineer", UserRole.Engineer, default);
        await workflow.ReplaceBomAsync(child.Id, BomKind.NonStandard,
            [new(1, "QA-PART-001", "QA测试零件", 1, "件", "Q235B", null, "W1", true, model.Id, "默认",
                Id: sourceItem.Id, SourceInstancePath: "QA-ASM-ROOT/QA-PART-001")],
            "qa_engineer", UserRole.Engineer, default);
        var review = await workflow.CreateDrawingReviewPackageAsync(child.Id, "qa_engineer", UserRole.Engineer, default);
        var reviewItem = Assert.Single(review.Items);
        review = await workflow.AddDrawingReviewMarkupAsync(review.Id,
            new(reviewItem.Id, DrawingReviewTarget.Drawing2D, "A页", .5m, .5m, "尺寸链需复核", DrawingReviewMarkupSeverity.Blocking),
            "qa_reviewer", UserRole.ProcessReviewer, default);
        var markup = Assert.Single(review.Markups);
        await Assert.ThrowsAsync<PdmRuleException>(() => workflow.DecideDrawingReviewTargetAsync(
            review.Id, reviewItem.Id, new(DrawingReviewTarget.Drawing2D, DrawingReviewDecision.Approve, "同意"),
            "qa_reviewer", UserRole.ProcessReviewer, default));
        review = await workflow.ResolveDrawingReviewMarkupAsync(review.Id, markup.Id, "qa_reviewer", UserRole.ProcessReviewer, default);
        review = await workflow.DecideDrawingReviewTargetAsync(review.Id, reviewItem.Id,
            new(DrawingReviewTarget.Drawing2D, DrawingReviewDecision.Approve, "复核通过"),
            "qa_reviewer", UserRole.ProcessReviewer, default);
        Assert.Equal(DrawingReviewPackageState.PendingSupervisorApproval, review.State);
        review = await workflow.DecideDrawingReviewSupervisorAsync(review.Id,
            new(DrawingReviewDecision.Approve, "机械主管批准"), "qa_bu_manager", UserRole.BusinessUnitManager, default);
        Assert.Equal(DrawingReviewPackageState.WritingProperties, review.State);

        var writeback = Assert.Single(await repository.ListCadPropertyWritebacksAsync(child.Id, default),
            item => item.SourceDocumentId == drawing.Id && item.RequestedBy == "qa_bu_manager");
        await workflow.StartCadPropertyWritebackAsync(writeback.Id, "qa_engineer", UserRole.Engineer, default);
        var writebackResult = await CheckInAsync(workflow, drawing, "qa_engineer", time.GetUtcNow(), 'C', false, writeback.Id);
        await workflow.CompleteCadPropertyWritebackAsync(writeback.Id, Assert.IsType<DocumentVersion>(writebackResult.Version).Id,
            "qa_engineer", UserRole.Engineer, default);
        Assert.Equal(DrawingReviewPackageState.Approved,
            (await repository.FindDrawingReviewPackageAsync(review.Id, default))!.State);

        var standardBom = await workflow.ReplaceBomAsync(child.Id, BomKind.Standard, [], "qa_engineer", UserRole.Engineer, default);
        // 三类BOM编辑权限按专业拆分：电气BOM由电气/硬件工程师维护，机械工程师只维护标准件与非标件。
        var electricalBom = await workflow.ReplaceBomAsync(child.Id, BomKind.Electrical,
            [new(1, "QA-EL-001", "QA电气件", 1, "件", null, "24VDC", "W1", true)],
            "qa_electrical", UserRole.Engineer, default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => workflow.ReplaceBomAsync(
            child.Id, BomKind.Electrical, [], "qa_engineer", UserRole.Engineer, default));
        var nonStandardBom = await workflow.ReplaceBomAsync(child.Id, BomKind.NonStandard,
            [new(1, "QA-PART-001", "QA测试零件", 1, "件", "Q235B", null, "W1", true, model.Id, "默认",
                SourceInstancePath: "QA-ASM-ROOT/QA-PART-001")],
            "qa_engineer", UserRole.Engineer, default);
        Assert.All(electricalBom, item => Assert.True(item.IsComplete, $"电气BOM未完整：{item.DrawingNumber}"));
        Assert.All(nonStandardBom, item => Assert.True(item.IsComplete, $"非标BOM未完整：{item.DrawingNumber}"));
        Assert.All(electricalBom.Concat(nonStandardBom), item => Assert.False(
            item.IsPendingRemoval || item.IsPendingClassification || item.IsManualUnmatched,
            $"BOM状态异常：{item.Kind}/{item.DrawingNumber} / removal={item.IsPendingRemoval} / pending={item.IsPendingClassification} / unmatched={item.IsManualUnmatched}"));
        Assert.All(standardBom, item => Assert.True(item.IsComplete || item.IsManuallyExcluded,
            $"标准BOM状态异常：{item.DrawingNumber} / pending={item.IsPendingClassification} / unmatched={item.IsManualUnmatched}"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => workflow.ReplaceBomAsync(
            child.Id, BomKind.Electrical, [], "qa_viewer", UserRole.ProductionViewer, default));

        var release = await workflow.CreateReleasePackageAsync(child.Id, null, $"RP-QA-{Guid.NewGuid():N}",
            "qa_reviewer", "qa_approver", "qa_pm", UserRole.Engineer, default);
        release = await workflow.SubmitReleasePackageAsync(release.Id, "qa_pm", UserRole.Engineer, default);
        release = await workflow.DecideAsync(release.ApprovalTasks.Single(item => item.Stage == ApprovalStage.ProcessReview).Id,
            "qa_reviewer", UserRole.ProcessReviewer, ApprovalDecision.Approved, "工艺审核通过", default);
        release = await workflow.DecideAsync(release.ApprovalTasks.Single(item => item.Stage == ApprovalStage.Approval).Id,
            "qa_approver", UserRole.Approver, ApprovalDecision.Approved, "批准发布", default);
        Assert.Equal(ReleasePackageState.Published, release.State);
        Assert.Equal(1, publisher.PublishCalls);

        var category = await validation.SaveCategoryAsync(null,
            new("QA安全验证", 9000, true, "隔离验收"), "admin", UserRole.Administrator, default);
        var check = await validation.SaveItemAsync(null,
            new(category.Id, "急停回路验证", "内部评审", 9000, true, "隔离验收"), "admin", UserRole.Administrator, default);
        var validationPlan = await validation.SavePlanAsync(child.Id,
            new("qa_engineer", new DateOnly(2026, 9, 13),
                [new(check.Id, null, "内部评审", new DateOnly(2026, 9, 13), null, null, "qa_engineer", null, 1)]),
            "qa_engineer", UserRole.Engineer, default);
        validationPlan = await validation.SubmitAsync(child.Id, validationPlan.RowVersion, "qa_engineer", UserRole.Engineer, default);
        while (validationPlan.State == ProjectValidationPlanState.PendingApproval)
        {
            var task = validationPlan.ApprovalTasks.OrderBy(item => item.StepOrder).First(item => item.Decision is null);
            validationPlan = await validation.DecideAsync(task.Id, ApprovalDecision.Approved, "QA审批通过", "admin", UserRole.Administrator, default);
        }
        Assert.Equal(ProjectValidationPlanState.Effective, validationPlan.State);

        var upload = await validation.StartAttachmentUploadAsync(validationPlan.Id, ValidationPlanAttachmentKind.PlanDocument,
            "QA验证结果.png", 128, new string('D', 64), "qa_engineer", UserRole.Engineer, default);
        var attachment = await validation.CompleteAttachmentUploadAsync(validationPlan.Id, ValidationPlanAttachmentKind.PlanDocument,
            upload.Id, "qa_engineer", UserRole.Engineer, default);
        var recognition = await validation.RecognizeAttachmentAsync(attachment.Id, "qa_engineer", UserRole.Engineer, default);
        var recognized = Assert.Single(recognition.Candidates);
        var execution = await validation.ConfirmExecutionRecordAsync(validationPlan.Id,
            new(attachment.Id, recognition.OcrText,
                [new(recognized.PlanItemId, recognized.MatchConfidence, recognized.SourceText, recognized.RecognizedResult,
                    recognized.RecognizedValidationDate, recognized.RecognizedResponsiblePerson, recognized.RecognizedRemark,
                    "合格", new DateOnly(2026, 9, 13), "qa_engineer", "自动测试确认")]),
            "qa_engineer", UserRole.Engineer, default);
        Assert.Equal("qa_engineer", Assert.Single(execution.Items).ResponsiblePerson);

        var audit = await repository.ListAuditAsync("admin", UserRole.Administrator, 500, default);
        foreach (var action in new[]
        {
            "project.create", "project.child.create", "project.staffing.update", "document.checkin",
            "drawing-review.create", "drawing-review.decide", "release-package.publish",
            "project-plan.approval.submit", "project-plan.approval.approve",
            "project.validation-plan.submit", "project.validation-plan.execution.confirm"
        })
            Assert.Contains(audit, item => item.Action == action);
    }

    private static Task<UserAccount> CreateUserAsync(PdmWorkflowService workflow, string username, string displayName, string role, Guid companyId) =>
        workflow.CreateManagedUserAsync(new(username, displayName, "qa-password-hash", role, true, companyId),
            "admin", UserRole.Administrator, default);

    private static async Task<DocumentCheckInResult> CheckInAsync(
        PdmWorkflowService workflow,
        PdmDocument document,
        string actor,
        DateTimeOffset now,
        char hashCharacter,
        bool isProjectRoot,
        Guid? writebackId = null,
        IReadOnlyList<DocumentReferenceNode>? children = null)
    {
        var sessionId = Guid.NewGuid();
        var checkedOut = await workflow.CheckoutAsync(document.Id, actor, UserRole.Engineer, sessionId, "QA-CLIENT", default, writebackId);
        var root = new DocumentReferenceNode(Guid.NewGuid(), checkedOut.Id, checkedOut.DrawingNumber, checkedOut.FileName,
            checkedOut.Name, checkedOut.Kind, "默认", 1, ReferenceNodeStatus.Normal, checkedOut.Revision, actor, children ?? []);
        var snapshot = new CadReferenceSnapshot(Guid.NewGuid(), checkedOut.ProjectId, checkedOut.Id, now, actor, root, new string('F', 64));
        return await workflow.CheckInAsync(checkedOut.Id, actor, UserRole.Engineer, sessionId,
            new StoredFile($".versions/{checkedOut.Id:N}/{checkedOut.FileName}", 128, new string(hashCharacter, 64), now),
            "QA自动存档", new Dictionary<string, string?>(), snapshot, isProjectRoot, false, default,
            drawingReviewWritebackId: writebackId);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingPublisher : IReleasePackagePublisher
    {
        public int PublishCalls { get; private set; }
        public Task PrepareAsync(ReleasePackage package, Project project, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ValidateAsync(ReleasePackage package, Project project, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ReleasePublication> PublishAsync(ReleasePackage package, Project project, IReadOnlyList<ReleasePreviewSource> sources, CancellationToken cancellationToken)
        {
            PublishCalls++;
            var previews = sources.ToDictionary(source => source.DocumentId, source => new DocumentPreviewArtifact(
                source.Kind == DocumentKind.Drawing ? DocumentPreviewFormat.Pdf : DocumentPreviewFormat.Step,
                $".qa-release/{source.DocumentId:N}.{(source.Kind == DocumentKind.Drawing ? "pdf" : "step")}",
                128, new string('E', 64), source.Kind == DocumentKind.Drawing ? new string('F', 64) : source.SourceSha256));
            var formal = sources.Where(source => source.Kind == DocumentKind.Drawing).ToDictionary(
                source => source.DocumentId,
                source => new FormalDrawingSource(source.SourceVersionId,
                    $".release-formal/{package.Id:N}/{source.DocumentId:N}.slddrw",
                    source.FileLength, new string('F', 64), source.ExpectedFormalRevision,
                    $"UPLM-DRAWING|{source.DrawingNumber}|{source.ExpectedFormalRevision}|{source.DocumentId:N}"));
            return Task.FromResult(new ReleasePublication(@"C:\PDM\QA\release", previews, null, formal));
        }
    }

    private sealed class RecordingFileStorage(TimeProvider time) : IFileStorage
    {
        private readonly Dictionary<Guid, UploadSession> sessions = [];

        public Task<UploadSession> StartUploadAsync(Guid projectId, string fileName, long totalLength, string expectedSha256, CancellationToken cancellationToken)
        {
            var session = new UploadSession(Guid.NewGuid(), projectId, fileName, totalLength, 4 * 1024 * 1024,
                expectedSha256, totalLength, time.GetUtcNow().AddHours(1));
            sessions[session.Id] = session;
            return Task.FromResult(session);
        }

        public Task<UploadSession> GetUploadSessionAsync(Guid sessionId, CancellationToken cancellationToken) => Task.FromResult(sessions[sessionId]);
        public Task<UploadSession> WriteChunkAsync(Guid sessionId, int chunkIndex, Stream content, CancellationToken cancellationToken) => Task.FromResult(sessions[sessionId]);
        public Task<StoredFile> CompleteUploadAsync(Guid sessionId, string relativeTargetPath, CancellationToken cancellationToken)
        {
            var session = sessions[sessionId];
            return Task.FromResult(new StoredFile(relativeTargetPath, session.TotalLength, session.ExpectedSha256, time.GetUtcNow()));
        }
        public Task<Stream> OpenReadAsync(string absolutePath, CancellationToken cancellationToken) => Task.FromResult<Stream>(new MemoryStream());
        public Task<bool> IsAvailableAsync(string location, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task VerifyStoredFileAsync(Project project, StoredFile file, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<StoredFile> CopyVersionAsync(Project project, StoredFile source, string relativeTargetPath, CancellationToken cancellationToken) =>
            Task.FromResult(source with { RelativePath = relativeTargetPath });
    }

    private sealed class StubRecognitionService : IValidationPlanTextRecognitionService
    {
        public Task<string> RecognizeAsync(string absolutePath, CancellationToken cancellationToken) =>
            Task.FromResult("急停回路验证 结果：合格 验证日期：2026-09-13 责任人：qa_engineer");
    }

    private sealed class NoOpValidationPlanFileArchive : IValidationPlanFileArchive
    {
        public Task ArchiveWorkbookAsync(ValidationPlanExportData export, string actor, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ArchiveAttachmentAsync(ProjectValidationPlan plan, ValidationPlanAttachment attachment, string actor, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
