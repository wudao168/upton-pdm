using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class DrawingReviewWorkflowTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task DesignerCannotReviewOwnDrawingAndBlockingMarkupMustBeResolved()
    {
        var (repository, workflow, _, _) = await PrepareReviewAsync();
        var package = await workflow.CreateDrawingReviewPackageAsync(ProjectId, "submitter", UserRole.Administrator, default);
        Assert.StartsWith("DR-PRJ-2026-018-0-", package.Number);
        var item = Assert.Single(package.Items);

        var selfReview = await Assert.ThrowsAsync<PdmRuleException>(() => workflow.DecideDrawingReviewTargetAsync(
            package.Id, item.Id, new DecideDrawingReviewTargetCommand(DrawingReviewTarget.Drawing2D, DrawingReviewDecision.Approve, null),
            "designer", UserRole.Administrator, default));
        Assert.Contains("不能审核自己", selfReview.Message);

        package = await workflow.AddDrawingReviewMarkupAsync(package.Id,
            new AddDrawingReviewMarkupCommand(item.Id, DrawingReviewTarget.Drawing2D, "A页", null, null, "孔位尺寸需要确认", DrawingReviewMarkupSeverity.Blocking),
            "reviewer", UserRole.Administrator, default);
        var markup = Assert.Single(package.Markups);
        await Assert.ThrowsAsync<PdmRuleException>(() => workflow.DecideDrawingReviewTargetAsync(
            package.Id, item.Id, new DecideDrawingReviewTargetCommand(DrawingReviewTarget.Drawing2D, DrawingReviewDecision.Approve, "表达清晰"),
            "reviewer", UserRole.Administrator, default));

        await workflow.ResolveDrawingReviewMarkupAsync(package.Id, markup.Id, "reviewer", UserRole.Administrator, default);
        package = await workflow.DecideDrawingReviewTargetAsync(
            package.Id, item.Id, new DecideDrawingReviewTargetCommand(DrawingReviewTarget.Drawing2D, DrawingReviewDecision.Approve, "表达清晰"),
            "reviewer", UserRole.Administrator, default);
        Assert.Equal(DrawingReviewTargetState.Approved, Assert.Single(package.Items).DrawingState);
    }

    [Fact]
    public async Task ModelTargetCannotBeReviewedInThe2DOnlyWorkflow()
    {
        var (_, workflow, _, _) = await PrepareReviewAsync();
        var package = await workflow.CreateDrawingReviewPackageAsync(ProjectId, "submitter", UserRole.Administrator, default);
        var item = Assert.Single(package.Items);

        var blocked = await Assert.ThrowsAsync<PdmRuleException>(() => workflow.DecideDrawingReviewTargetAsync(
            package.Id, item.Id, new DecideDrawingReviewTargetCommand(DrawingReviewTarget.Model3D, DrawingReviewDecision.Approve, null),
            "reviewer", UserRole.Administrator, default));

        Assert.Contains("仅审核2D工程图", blocked.Message);
        Assert.Equal(DrawingReviewTargetState.NotRequired, item.ModelState);
    }

    [Fact]
    public async Task DeveloperCanReviewOwnDrawing()
    {
        var (_, workflow, _, _) = await PrepareReviewAsync();
        var package = await workflow.CreateDrawingReviewPackageAsync(ProjectId, "submitter", UserRole.Administrator, default);
        var item = Assert.Single(package.Items);
        TenantContext.Set(new CurrentTenant(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "designer",
            "developer",
            true,
            new HashSet<string>(StringComparer.Ordinal) { PermissionCodes.DrawingReviewDecide }));

        try
        {
            package = await workflow.DecideDrawingReviewTargetAsync(
                package.Id,
                item.Id,
                new DecideDrawingReviewTargetCommand(DrawingReviewTarget.Drawing2D, DrawingReviewDecision.Approve, "开发者自审验证"),
                "designer",
                UserRole.Administrator,
                default);
        }
        finally
        {
            TenantContext.Clear();
        }

        Assert.Equal(DrawingReviewTargetState.Approved, Assert.Single(package.Items).DrawingState);
    }

    [Fact]
    public async Task ActiveDrawingReviewLocksOnlyTheDrawing()
    {
        var (_, workflow, model, drawing) = await PrepareReviewAsync();
        await workflow.CreateDrawingReviewPackageAsync(ProjectId, "submitter", UserRole.Administrator, default);

        var editableModel = await workflow.CheckoutAsync(
            model.Id, "designer", UserRole.Administrator, Guid.NewGuid(), "TEST-WS", default);
        var drawingBlocked = await Assert.ThrowsAsync<PdmConflictException>(() => workflow.CheckoutAsync(
            drawing.Id, "designer", UserRole.Administrator, Guid.NewGuid(), "TEST-WS", default));

        Assert.Equal(model.Id, editableModel.Id);
        Assert.Contains("图纸审核", drawingBlocked.Message);
    }

    [Fact]
    public async Task WithdrawnDrawingReviewKeepsHistoryAndReleasesDocumentLocks()
    {
        var (_, workflow, model, drawing) = await PrepareReviewAsync();
        var package = await workflow.CreateDrawingReviewPackageAsync(ProjectId, "submitter", UserRole.Administrator, default);

        var withdrawn = await workflow.WithdrawDrawingReviewPackageAsync(package.Id, "审核范围选择有误", "submitter", UserRole.Administrator, default);

        Assert.Equal(DrawingReviewPackageState.Withdrawn, withdrawn.State);
        Assert.Equal("submitter", withdrawn.WithdrawnBy);
        Assert.Equal("审核范围选择有误", withdrawn.WithdrawalReason);
        Assert.NotNull(withdrawn.WithdrawnAt);
        await workflow.CheckoutAsync(model.Id, "designer", UserRole.Administrator, Guid.NewGuid(), "TEST-WS", default);
        await workflow.CheckoutAsync(drawing.Id, "designer", UserRole.Administrator, Guid.NewGuid(), "TEST-WS", default);
    }

    [Fact]
    public async Task DrawingReviewCanBeCreatedForSelectedCandidateOnly()
    {
        var (repository, workflow, model, _) = await PrepareReviewAsync();
        var standardModel = await repository.RegisterDocumentAsync(
            new RegisterDocumentCommand(ProjectId, "STD-SELECT", "待选择标准件", "STD-SELECT.SLDPRT", DocumentKind.Part),
            "designer",
            default);
        var standardDrawing = await repository.RegisterDocumentAsync(
            new RegisterDocumentCommand(ProjectId, "STD-SELECT", "待选择标准件工程图", "STD-SELECT.SLDDRW", DocumentKind.Drawing, RelatedModelDocumentId: standardModel.Id),
            "designer",
            default);
        await CheckInAsync(repository, standardModel.Id, "designer", new Dictionary<string, string?>(), 'C');
        await CheckInAsync(repository, standardDrawing.Id, "designer", new Dictionary<string, string?>(), 'D');
        await repository.ReplaceBomAsync(ProjectId, BomKind.Standard,
        [
            new BomItem(Guid.NewGuid(), ProjectId, BomKind.Standard, 1, "STD-SELECT", "待选择标准件", 1, "件", null, "STD-SELECT", "W1", true)
            {
                SourceDocumentId = standardModel.Id,
                SourceConfiguration = "默认",
                Source = "Auto"
            }
        ], default);

        var candidates = await workflow.ListDrawingReviewCandidatesAsync(ProjectId, "submitter", UserRole.Administrator, default);
        Assert.Contains(candidates, candidate => candidate.ModelDocumentId == model.Id && candidate.State == DrawingReviewCandidateState.Ready);
        Assert.DoesNotContain(candidates, candidate => candidate.ModelDocumentId == standardModel.Id);

        var package = await workflow.CreateDrawingReviewPackageAsync(ProjectId, [model.Id], "submitter", UserRole.Administrator, default);
        var item = Assert.Single(package.Items);
        Assert.Equal(model.Id, item.ModelDocumentId);
        Assert.Equal(DrawingReviewTargetState.NotRequired, item.ModelState);
    }

    [Fact]
    public async Task PropertyWritingRejectsOrdinaryCheckInWithoutControlledWriteback()
    {
        var (repository, workflow, model, drawing) = await PrepareReviewAsync();
        var review = await workflow.CreateDrawingReviewPackageAsync(ProjectId, "submitter", UserRole.Administrator, default);
        var item = Assert.Single(review.Items);
        review = await workflow.DecideDrawingReviewTargetAsync(review.Id, item.Id,
            new DecideDrawingReviewTargetCommand(DrawingReviewTarget.Drawing2D, DrawingReviewDecision.Approve, "2D通过"),
            "drawing-reviewer", UserRole.Administrator, default);
        Assert.Equal(DrawingReviewPackageState.WritingProperties, review.State);

        var lockedIds = await repository.ListActiveDrawingReviewDocumentIdsAsync(ProjectId, default);
        Assert.DoesNotContain(model.Id, lockedIds);
        Assert.Contains(drawing.Id, lockedIds);

        var writeback = (await repository.ListCadPropertyWritebacksAsync(ProjectId, default))
            .Single(request => request.SourceDocumentId == drawing.Id);
        await workflow.StartCadPropertyWritebackAsync(writeback.Id, "cad-client", UserRole.Administrator, default);
        var sessionId = Guid.NewGuid();
        var checkedOut = await repository.CheckoutAsync(drawing.Id, "cad-client", sessionId, "TEST-WS",
            DateTimeOffset.UtcNow.AddMinutes(15), writeback.Id, default);
        var root = new DocumentReferenceNode(Guid.NewGuid(), checkedOut.Id, checkedOut.DrawingNumber, checkedOut.FileName,
            checkedOut.Name, checkedOut.Kind, "默认", 1, ReferenceNodeStatus.Normal, checkedOut.Revision, "cad-client", []);
        var commit = new DocumentVersionCommit(
            new StoredFile($"versions/{checkedOut.FileName}", 128, new string('E', 64), DateTimeOffset.UtcNow),
            "非法普通存档",
            writeback.Properties,
            new CadReferenceSnapshot(Guid.NewGuid(), ProjectId, checkedOut.Id, DateTimeOffset.UtcNow, "cad-client", root, new string('F', 64)),
            [],
            []);

        var blocked = await Assert.ThrowsAsync<PdmConflictException>(() =>
            repository.CheckInVersionAsync(drawing.Id, "cad-client", sessionId, commit, default));
        Assert.Contains("不能提交存档", blocked.Message);
    }

    [Fact]
    public async Task FormalNonStandardReleaseWaitsForLatest2DReviewAndPropertyWriteback()
    {
        var (repository, workflow, model, drawing) = await PrepareReviewAsync();
        var review = await workflow.CreateDrawingReviewPackageAsync(ProjectId, "submitter", UserRole.Administrator, default);
        var item = Assert.Single(review.Items);
        review = await workflow.DecideDrawingReviewTargetAsync(
            review.Id, item.Id, new DecideDrawingReviewTargetCommand(DrawingReviewTarget.Drawing2D, DrawingReviewDecision.Approve, "2D通过"),
            "drawing-reviewer", UserRole.Administrator, default);
        Assert.Equal(DrawingReviewPackageState.WritingProperties, review.State);

        var release = await CreateLegacyReleasePackageAsync(repository);
        var blocked = await Assert.ThrowsAsync<PdmRuleException>(() => workflow.SubmitReleasePackageAsync(release.Id, "admin", UserRole.Administrator, default));
        Assert.Contains("尚无已完成", blocked.Message);

        var writebacks = await repository.ListCadPropertyWritebacksAsync(ProjectId, default);
        var request = Assert.Single(writebacks);
        Assert.Equal(drawing.Id, request.SourceDocumentId);
        Assert.Equal("2D", request.Properties["PLM_审图对象"]);
        Assert.Equal("drawing-reviewer", request.Properties["校对"]);
        await workflow.StartCadPropertyWritebackAsync(request.Id, "cad-client", UserRole.Administrator, default);
        var result = await CheckInAsync(repository, request.SourceDocumentId, "cad-client", request.Properties, 'D', drawingReviewWritebackId: request.Id);
        await workflow.CompleteCadPropertyWritebackAsync(request.Id, Assert.IsType<DocumentVersion>(result.Version).Id, "cad-client", UserRole.Administrator, default);

        review = await repository.FindDrawingReviewPackageAsync(review.Id, default) ?? throw new InvalidOperationException();
        Assert.Equal(DrawingReviewPackageState.Approved, review.State);
        Assert.All(review.Items, current =>
        {
            Assert.Equal(DrawingReviewTargetState.NotRequired, current.ModelState);
            Assert.Equal(DrawingReviewTargetState.Marked, current.DrawingState);
            Assert.Null(current.ModelResultVersionId);
            Assert.NotNull(current.DrawingResultVersionId);
        });

        await CheckInAsync(repository, model.Id, "designer", new Dictionary<string, string?>(), 'E');

        var submitted = await workflow.SubmitReleasePackageAsync(release.Id, "admin", UserRole.Administrator, default);
        Assert.Equal(ReleasePackageState.ProcessReview, submitted.State);
    }

    [Fact]
    public async Task NewDrawingVersionAfterReviewBlocksFormalRelease()
    {
        var (repository, workflow, _, drawing) = await PrepareReviewAsync();
        var review = await workflow.CreateDrawingReviewPackageAsync(ProjectId, "submitter", UserRole.Administrator, default);
        var item = Assert.Single(review.Items);
        await workflow.DecideDrawingReviewTargetAsync(review.Id, item.Id,
            new DecideDrawingReviewTargetCommand(DrawingReviewTarget.Drawing2D, DrawingReviewDecision.Approve, "2D通过"),
            "drawing-reviewer", UserRole.Administrator, default);
        var writeback = Assert.Single(await repository.ListCadPropertyWritebacksAsync(ProjectId, default));
        await workflow.StartCadPropertyWritebackAsync(writeback.Id, "cad-client", UserRole.Administrator, default);
        var marked = await CheckInAsync(repository, drawing.Id, "cad-client", writeback.Properties, 'D', drawingReviewWritebackId: writeback.Id);
        await workflow.CompleteCadPropertyWritebackAsync(writeback.Id, Assert.IsType<DocumentVersion>(marked.Version).Id, "cad-client", UserRole.Administrator, default);
        await CheckInAsync(repository, drawing.Id, "designer", new Dictionary<string, string?>(), 'E');
        var release = await CreateLegacyReleasePackageAsync(repository);

        var blocked = await Assert.ThrowsAsync<PdmRuleException>(() => workflow.SubmitReleasePackageAsync(release.Id, "admin", UserRole.Administrator, default));

        Assert.Contains("2D版本已变化", blocked.Message);
    }

    [Fact]
    public async Task FormalReleaseRejectsNonStandardRowWithoutSourceDocuments()
    {
        var (repository, workflow, _, _) = await PrepareReviewAsync();
        var items = (await repository.GetBomAsync(ProjectId, BomKind.NonStandard, default)).ToList();
        items.Add(new BomItem(Guid.NewGuid(), ProjectId, BomKind.NonStandard, 2, "02040000005", "夹紧胶2", 1, "件", "PU(90°)", null, "W1", true)
        {
            Source = "Manual",
            IsManuallyRetained = true
        });
        await repository.ReplaceBomAsync(ProjectId, BomKind.NonStandard, items, default);
        var release = await CreateLegacyReleasePackageAsync(repository);

        var blocked = await Assert.ThrowsAsync<PdmRuleException>(() =>
            workflow.SubmitReleasePackageAsync(release.Id, "admin", UserRole.Administrator, default));

        Assert.Contains("没有来源模型", blocked.Message);
        Assert.Contains("02040000005", blocked.Message);
    }

    [Fact]
    public async Task FormalReleaseRejectsMultipleDrawingsForOneNonStandardModel()
    {
        var (repository, workflow, model, drawing) = await PrepareReviewAsync();
        var alternateDrawing = await repository.RegisterDocumentAsync(
            new RegisterDocumentCommand(ProjectId, "A01-100-ALT", "机架备用工程图", "A01-100-ALT.SLDDRW", DocumentKind.Drawing,
                RelatedModelDocumentId: model.Id),
            "designer",
            default);
        await CheckInAsync(repository, alternateDrawing.Id, "designer", new Dictionary<string, string?>(), 'C');
        var modelVersion = (await repository.ListDocumentVersionsAsync(model.Id, default)).First();
        var drawingVersion = (await repository.ListDocumentVersionsAsync(drawing.Id, default)).First();
        var packageId = Guid.NewGuid();
        await repository.CreateDrawingReviewPackageAsync(new DrawingReviewPackage
        {
            Id = packageId,
            ProjectId = ProjectId,
            Number = "DR-MULTIPLE-DRAWINGS",
            State = DrawingReviewPackageState.Approved,
            CreatedBy = "reviewer",
            CreatedAt = DateTimeOffset.UtcNow,
            ApprovedAt = DateTimeOffset.UtcNow,
            Items =
            [
                new DrawingReviewItem
                {
                    Id = Guid.NewGuid(), PackageId = packageId, BomItemId = Guid.NewGuid(), DrawingNumber = "A01-100", Name = "机架组件",
                    ModelDocumentId = model.Id, ModelVersionId = modelVersion.Id, ModelRevision = modelVersion.Revision.Display,
                    ModelSha256 = modelVersion.Sha256, ModelCreatedBy = modelVersion.CreatedBy, ModelState = DrawingReviewTargetState.Marked,
                    DrawingDocumentId = drawing.Id, DrawingVersionId = drawingVersion.Id, DrawingRevision = drawingVersion.Revision.Display,
                    DrawingSha256 = drawingVersion.Sha256, DrawingCreatedBy = drawingVersion.CreatedBy, DrawingState = DrawingReviewTargetState.Marked
                }
            ]
        }, default);
        var release = await CreateLegacyReleasePackageAsync(repository);

        var blocked = await Assert.ThrowsAsync<PdmRuleException>(() =>
            workflow.SubmitReleasePackageAsync(release.Id, "admin", UserRole.Administrator, default));

        Assert.Contains("关联2张2D工程图", blocked.Message);
    }

    [Fact]
    public async Task FormalReleaseRejectsNonStandardQuantityMismatch()
    {
        var (repository, workflow, _, _) = await PrepareReviewAsync();
        var items = (await repository.GetBomAsync(ProjectId, BomKind.NonStandard, default))
            .Select(item => item with { Quantity = 2 })
            .ToArray();
        await repository.ReplaceBomAsync(ProjectId, BomKind.NonStandard, items, default);
        var release = await CreateLegacyReleasePackageAsync(repository);

        var blocked = await Assert.ThrowsAsync<PdmRuleException>(() =>
            workflow.SubmitReleasePackageAsync(release.Id, "admin", UserRole.Administrator, default));

        Assert.Contains("BOM数量与最新设计树数量不一致", blocked.Message);
        Assert.Contains("A01-100（BOM 2 / 源 1）", blocked.Message);
    }

    [Fact]
    public async Task ActiveReviewOnlyExcludesItsOwnDocumentsFromTheNextPackage()
    {
        var (repository, workflow, model, drawing) = await PrepareReviewAsync();
        var unrelatedModel = await repository.RegisterDocumentAsync(
            new RegisterDocumentCommand(ProjectId, "OTHER-001", "其他审核模型", "OTHER-001.SLDPRT", DocumentKind.Part),
            "other-designer",
            default);
        var unrelatedDrawing = await repository.RegisterDocumentAsync(
            new RegisterDocumentCommand(ProjectId, "OTHER-001", "其他审核工程图", "OTHER-001.SLDDRW", DocumentKind.Drawing,
                RelatedModelDocumentId: unrelatedModel.Id),
            "other-designer",
            default);
        await CheckInAsync(repository, unrelatedModel.Id, "other-designer", new Dictionary<string, string?>(), 'E');
        await CheckInAsync(repository, unrelatedDrawing.Id, "other-designer", new Dictionary<string, string?>(), 'F');
        var unrelatedPackageId = Guid.NewGuid();
        await repository.CreateDrawingReviewPackageAsync(new DrawingReviewPackage
        {
            Id = unrelatedPackageId,
            ProjectId = ProjectId,
            Number = "DR-UNRELATED",
            State = DrawingReviewPackageState.InReview,
            CreatedBy = "another-submitter",
            CreatedAt = DateTimeOffset.UtcNow,
            Items = [CreateReviewItem(unrelatedPackageId, unrelatedModel.Id, unrelatedDrawing.Id)]
        }, default);

        var package = await workflow.CreateDrawingReviewPackageAsync(ProjectId, "submitter", UserRole.Administrator, default);
        var item = Assert.Single(package.Items);
        Assert.Equal(model.Id, item.ModelDocumentId);
        Assert.Equal(drawing.Id, item.DrawingDocumentId);

        var duplicate = await Assert.ThrowsAsync<PdmConflictException>(() =>
            workflow.CreateDrawingReviewPackageAsync(ProjectId, "submitter", UserRole.Administrator, default));
        Assert.Contains("均已在审核中", duplicate.Message);
    }

    [Fact]
    public async Task ManualBomRowsWithoutSourceDocumentsAreVisibleAsBlockersWithoutHidingLinkedDrawings()
    {
        var (repository, workflow, model, drawing) = await PrepareReviewAsync();
        var bomItems = (await repository.GetBomAsync(ProjectId, BomKind.NonStandard, default)).ToList();
        bomItems.Add(new BomItem(Guid.NewGuid(), ProjectId, BomKind.NonStandard, 2, "02040000005", "夹紧胶2", 1, "件", "PU(90°)", null, "W1", true)
        {
            Source = "Manual"
        });
        bomItems.Add(new BomItem(Guid.NewGuid(), ProjectId, BomKind.NonStandard, 3, "02040000010", "抓手定位圆柱销", 1, "件", "SUS304", null, "W1", true)
        {
            Source = "Manual"
        });
        await repository.ReplaceBomAsync(ProjectId, BomKind.NonStandard, bomItems, default);

        var package = await workflow.CreateDrawingReviewPackageAsync(ProjectId, "submitter", UserRole.Administrator, default);

        var item = Assert.Single(package.Items);
        Assert.Equal(model.Id, item.ModelDocumentId);
        Assert.Equal(drawing.Id, item.DrawingDocumentId);
        var blockers = (await workflow.ListDrawingReviewCandidatesAsync(ProjectId, "submitter", UserRole.Administrator, default))
            .Where(candidate => candidate.ModelDocumentId is null)
            .ToArray();
        Assert.Equal(2, blockers.Length);
        Assert.All(blockers, blocker =>
        {
            Assert.Equal(DrawingReviewCandidateState.Unavailable, blocker.State);
            Assert.Equal(blocker.CandidateId, blocker.BomItemId);
            Assert.Contains("没有来源模型", blocker.Reason);
        });
    }

    [Fact]
    public async Task NonStandardAssemblyWithoutDrawingIsUnavailableForReview()
    {
        var (repository, workflow, _, _) = await PrepareReviewAsync();
        var assembly = await repository.RegisterDocumentAsync(
            new RegisterDocumentCommand(ProjectId, "A01-200", "非标装配体", "A01-200.SLDASM", DocumentKind.Assembly),
            "designer",
            default);
        await CheckInAsync(repository, assembly.Id, "designer", new Dictionary<string, string?>(), 'E', true);
        await repository.ReplaceBomAsync(ProjectId, BomKind.NonStandard,
        [
            new BomItem(Guid.NewGuid(), ProjectId, BomKind.NonStandard, 1, "A01-200", "非标装配体", 1, "件", "Q235B", null, "W1", true)
            {
                SourceDocumentId = assembly.Id,
                SourceConfiguration = "默认",
                Source = "Auto"
            }
        ], default);

        var candidate = Assert.Single(await workflow.ListDrawingReviewCandidatesAsync(ProjectId, "submitter", UserRole.Administrator, default));
        Assert.Equal(assembly.Id, candidate.ModelDocumentId);
        Assert.Equal(DrawingReviewCandidateState.Unavailable, candidate.State);
        Assert.Contains("缺少关联2D工程图", candidate.Reason);

        var blocked = await Assert.ThrowsAsync<PdmRuleException>(() => workflow.CreateDrawingReviewPackageAsync(
            ProjectId, [assembly.Id], "submitter", UserRole.Administrator, default));
        Assert.Contains("不能进入图纸审核", blocked.Message);
    }

    [Fact]
    public async Task DrawingReviewExcludesLinkedStandardBomDocuments()
    {
        var (repository, workflow, model, drawing) = await PrepareReviewAsync();
        var standardModel = await repository.RegisterDocumentAsync(
            new RegisterDocumentCommand(ProjectId, "STD-200", "标准件模型", "STD-200.SLDPRT", DocumentKind.Part),
            "designer",
            default);
        var standardDrawing = await repository.RegisterDocumentAsync(
            new RegisterDocumentCommand(ProjectId, "STD-200", "标准件工程图", "STD-200.SLDDRW", DocumentKind.Drawing, RelatedModelDocumentId: standardModel.Id),
            "designer",
            default);
        await CheckInAsync(repository, standardModel.Id, "designer", new Dictionary<string, string?>(), 'C');
        await CheckInAsync(repository, standardDrawing.Id, "designer", new Dictionary<string, string?>(), 'D');
        await repository.ReplaceBomAsync(ProjectId, BomKind.Standard,
        [
            new BomItem(Guid.NewGuid(), ProjectId, BomKind.Standard, 1, "STD-200", "标准件模型", 1, "件", null, "STD-200", "W1", true)
            {
                SourceDocumentId = standardModel.Id,
                SourceConfiguration = "默认",
                Source = "Auto"
            }
        ], default);

        var package = await workflow.CreateDrawingReviewPackageAsync(ProjectId, "submitter", UserRole.Administrator, default);

        var item = Assert.Single(package.Items);
        Assert.Equal(model.Id, item.ModelDocumentId);
        Assert.Equal(drawing.Id, item.DrawingDocumentId);
        Assert.DoesNotContain(package.Items, candidate => candidate.ModelDocumentId == standardModel.Id || candidate.DrawingDocumentId == standardDrawing.Id);
    }

    [Fact]
    public async Task DrawingReviewExcludesDesignTreeAssemblyWithoutNonStandardBomRow()
    {
        var (repository, workflow, model, drawing) = await PrepareReviewAsync();
        await repository.ReplaceBomAsync(ProjectId, BomKind.NonStandard, [], default);

        Assert.Empty(await workflow.ListDrawingReviewCandidatesAsync(ProjectId, "submitter", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => workflow.CreateDrawingReviewPackageAsync(ProjectId, "submitter", UserRole.Administrator, default));
    }

    [Fact]
    public async Task DrawingReviewDoesNotUseAssembliesOutsideNonStandardBom()
    {
        var (repository, workflow, _, _) = await PrepareReviewAsync();
        var assembly = await repository.RegisterDocumentAsync(
            new RegisterDocumentCommand(ProjectId, "A01-200", "无工程图装配体", "A01-200.SLDASM", DocumentKind.Assembly),
            "designer",
            default);
        await CheckInAsync(repository, assembly.Id, "designer", new Dictionary<string, string?>(), 'E', true);
        await repository.ReplaceBomAsync(ProjectId, BomKind.NonStandard, [], default);

        Assert.Empty(await workflow.ListDrawingReviewCandidatesAsync(ProjectId, "submitter", UserRole.Administrator, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => workflow.CreateDrawingReviewPackageAsync(ProjectId, "submitter", UserRole.Administrator, default));
    }

    private static async Task<(InMemoryPdmRepository Repository, PdmWorkflowService Workflow, PdmDocument Model, PdmDocument Drawing)> PrepareReviewAsync()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        foreach (var document in await repository.ListCheckedOutDocumentsAsync(default))
            await repository.ForceReleaseCheckoutAsync(document.Id, "admin", "测试准备", default);
        var documents = await repository.ListDocumentsAsync(ProjectId, default);
        var model = documents.Single(document => document.DrawingNumber == "A01-100" && document.Kind == DocumentKind.Assembly);
        var drawing = documents.Single(document => document.DrawingNumber == "A01-100" && document.Kind == DocumentKind.Drawing);
        await CheckInAsync(repository, model.Id, "designer", new Dictionary<string, string?>(), 'A', true);
        await CheckInAsync(repository, drawing.Id, "designer", new Dictionary<string, string?>(), 'B');
        var referenceRoot = await repository.RegisterDocumentAsync(
            new RegisterDocumentCommand(ProjectId, "REVIEW-ROOT", "审核测试根节点", "REVIEW-ROOT.SLDPRT", DocumentKind.Part),
            "designer",
            default);
        var modelNode = new DocumentReferenceNode(
            Guid.NewGuid(), model.Id, model.DrawingNumber, model.FileName, model.Name, model.Kind, "默认", 1,
            ReferenceNodeStatus.Normal, model.Revision, "designer", []);
        await CheckInAsync(repository, referenceRoot.Id, "designer", new Dictionary<string, string?>(), 'C', true, children: [modelNode]);
        await repository.ReplaceBomAsync(ProjectId, BomKind.Standard, [], default);
        await repository.ReplaceBomAsync(ProjectId, BomKind.Electrical, [], default);
        await repository.ReplaceBomAsync(ProjectId, BomKind.Unclassified, [], default);
        await repository.ReplaceBomAsync(ProjectId, BomKind.NonStandard,
        [
            new BomItem(Guid.NewGuid(), ProjectId, BomKind.NonStandard, 1, "A01-100", "机架组件", 1, "件", "Q235B", null, "W1", true)
            {
                SourceDocumentId = model.Id,
                SourceConfiguration = "默认",
                Source = "Auto"
            }
        ], default);
        return (repository, new PdmWorkflowService(repository, new UnusedFileStorage(), new RecordingPublisher(), TimeProvider.System), model, drawing);
    }

    private static async Task<DocumentCheckInResult> CheckInAsync(
        InMemoryPdmRepository repository,
        Guid documentId,
        string actor,
        IReadOnlyDictionary<string, string?> properties,
        char hashCharacter,
        bool isProjectRoot = false,
        Guid? drawingReviewWritebackId = null,
        IReadOnlyList<DocumentReferenceNode>? children = null)
    {
        var sessionId = Guid.NewGuid();
        var document = await repository.CheckoutAsync(
            documentId,
            actor,
            sessionId,
            "TEST-WS",
            DateTimeOffset.UtcNow.AddMinutes(15),
            drawingReviewWritebackId,
            default);
        var root = new DocumentReferenceNode(Guid.NewGuid(), document.Id, document.DrawingNumber, document.FileName, document.Name, document.Kind, "默认", 1, ReferenceNodeStatus.Normal, document.Revision, actor, children ?? []);
        return await repository.CheckInVersionAsync(documentId, actor, sessionId, new DocumentVersionCommit(
            new StoredFile($"versions/{document.FileName}", 128, new string(hashCharacter, 64), DateTimeOffset.UtcNow),
            "测试存档",
            properties,
            new CadReferenceSnapshot(Guid.NewGuid(), ProjectId, document.Id, DateTimeOffset.UtcNow, actor, root, new string('F', 64)),
            [],
            [],
            IsProjectRoot: isProjectRoot), drawingReviewWritebackId, default);
    }

    private static DrawingReviewItem CreateReviewItem(Guid packageId, Guid modelDocumentId, Guid drawingDocumentId) => new()
    {
        Id = Guid.NewGuid(),
        PackageId = packageId,
        BomItemId = Guid.NewGuid(),
        DrawingNumber = "OTHER-001",
        Name = "其他审核中图档",
        ModelDocumentId = modelDocumentId,
        ModelVersionId = Guid.NewGuid(),
        ModelRevision = "W1",
        ModelSha256 = new string('A', 64),
        ModelCreatedBy = "designer-a",
        DrawingDocumentId = drawingDocumentId,
        DrawingVersionId = Guid.NewGuid(),
        DrawingRevision = "W1",
        DrawingSha256 = new string('B', 64),
        DrawingCreatedBy = "designer-b"
    };

    private static async Task<ReleasePackage> CreateLegacyReleasePackageAsync(InMemoryPdmRepository repository)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var package = new ReleasePackage(
            id,
            ProjectId,
            $"RP-DRAWING-{id:N}",
            ReleasePackageState.Draft,
            Guid.NewGuid(),
            string.Empty,
            string.Empty,
            [
                new ApprovalTask(Guid.NewGuid(), id, ApprovalStage.ProcessReview, "reviewer", null, null, null, null),
                new ApprovalTask(Guid.NewGuid(), id, ApprovalStage.Approval, "approver", null, null, null, null)
            ],
            now,
            null,
            null)
        {
            Scope = ReleaseScope.LegacyCombined,
            ChangeNumber = $"ECN-{now:yyyyMMddHHmmss}",
            ChangeReason = "图纸审核门禁测试",
            EffectiveSerialFrom = "未指定"
        };
        return await repository.CreateReleasePackageAsync(package, default);
    }

    private sealed class RecordingPublisher : IReleasePackagePublisher
    {
        public Task PrepareAsync(ReleasePackage package, Project project, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ValidateAsync(ReleasePackage package, Project project, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ReleasePublication> PublishAsync(ReleasePackage package, Project project, IReadOnlyList<ReleasePreviewSource> sources, CancellationToken cancellationToken) =>
            Task.FromResult(new ReleasePublication("C:\\PDM\\Release\\drawing-review-test", PreviewArtifacts(sources)));

        private static IReadOnlyDictionary<Guid, DocumentPreviewArtifact> PreviewArtifacts(IReadOnlyList<ReleasePreviewSource> sources) =>
            sources.ToDictionary(
                source => source.DocumentId,
                source => new DocumentPreviewArtifact(
                    source.Kind == DocumentKind.Drawing ? DocumentPreviewFormat.Pdf : DocumentPreviewFormat.Step,
                    $".release-previews/{source.DocumentId:N}.{(source.Kind == DocumentKind.Drawing ? "pdf" : "step")}",
                    1,
                    new string('A', 64),
                    source.SourceSha256));
    }

    private sealed class UnusedFileStorage : IFileStorage
    {
        public Task<UploadSession> StartUploadAsync(Guid projectId, string fileName, long totalLength, string expectedSha256, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UploadSession> GetUploadSessionAsync(Guid sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UploadSession> WriteChunkAsync(Guid sessionId, int chunkIndex, Stream content, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoredFile> CompleteUploadAsync(Guid sessionId, string relativeTargetPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoredFile> CopyVersionAsync(Project project, StoredFile source, string relativeTargetPath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string absolutePath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> IsAvailableAsync(string location, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task VerifyStoredFileAsync(Project project, StoredFile file, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
