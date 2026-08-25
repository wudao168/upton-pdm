using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ProgramTemplateWorkflowTests
{
    [Fact]
    public async Task SubmitRequiresBothControlledFiles()
    {
        var clock = TimeProvider.System;
        var pdmRepository = new InMemoryPdmRepository(clock);
        var templateRepository = new InMemoryProgramTemplateRepository(clock);
        var service = new ProgramTemplateService(templateRepository, pdmRepository, new UnusedProgramTemplateStorage(), clock);
        await ConfigureElectricalApprovalChainAsync(pdmRepository);

        var created = await service.CreateAsync(new CreateProgramTemplateCommand(
            ProgramTemplateAssetType.PlcFunctionBlock,
            "缺少受控文件测试",
            "逻辑运算",
            "验证提交前的受控文件校验。",
            "Siemens",
            "TIA Portal",
            "V19",
            "S7-1200",
            [],
            "首版",
            [new(ProgramTemplateParameterDirection.Input, 0, "Enable", "BOOL", null, null, null)]),
            "uploader", UserRole.Engineer, default);
        var revision = created.Revisions.Single();

        var missingPackage = await Assert.ThrowsAsync<PdmRuleException>(() =>
            service.SubmitAsync(revision.Id, revision.RowVersion, "uploader", UserRole.Engineer, default));
        Assert.Equal("请先上传ZIP程序包。", missingPackage.Message);

        revision = await templateRepository.AttachFileAsync(revision.Id,
            new(revision.Id, ProgramTemplateAttachmentKind.Package, "template.zip", "package/template.zip", 128, new string('A', 64), clock.GetUtcNow()),
            revision.RowVersion, default);
        var missingEvidence = await Assert.ThrowsAsync<PdmRuleException>(() =>
            service.SubmitAsync(revision.Id, revision.RowVersion, "uploader", UserRole.Engineer, default));
        Assert.Equal("请先上传离线测试证据。", missingEvidence.Message);
    }

    [Fact]
    public async Task PlcFunctionBlock_IsListedOnlyAfterReviewAndApproval()
    {
        var clock = TimeProvider.System;
        var pdmRepository = new InMemoryPdmRepository(clock);
        var templateRepository = new InMemoryProgramTemplateRepository(clock);
        var service = new ProgramTemplateService(templateRepository, pdmRepository, new UnusedProgramTemplateStorage(), clock);
        await ConfigureElectricalApprovalChainAsync(pdmRepository);

        var created = await service.CreateAsync(new CreateProgramTemplateCommand(
            ProgramTemplateAssetType.PlcFunctionBlock,
            "电机正反转控制",
            "逻辑运算",
            "带互锁和切换延时的电机正反转控制。",
            "Siemens",
            "TIA Portal",
            "V19",
            "S7-1200 / S7-1500",
            ["电机", "互锁"],
            "首版标准化",
            [
                new(ProgramTemplateParameterDirection.Input, 0, "Fwd", "BOOL", null, null, "正转请求"),
                new(ProgramTemplateParameterDirection.Output, 1, "FwdOut", "BOOL", null, null, "正转输出")
            ]), "uploader", UserRole.Engineer, default);
        var revision = created.Revisions.Single();
        revision = await templateRepository.AttachFileAsync(revision.Id,
            new(revision.Id, ProgramTemplateAttachmentKind.Package, "motor.zip", "PT-FB-0001/1/package/motor.zip", 128, new string('A', 64), clock.GetUtcNow()),
            revision.RowVersion, default);
        revision = await templateRepository.AttachFileAsync(revision.Id,
            new(revision.Id, ProgramTemplateAttachmentKind.TestEvidence, "test.pdf", "PT-FB-0001/1/evidence/test.pdf", 64, new string('B', 64), clock.GetUtcNow()),
            revision.RowVersion, default);

        revision = await service.SubmitAsync(revision.Id, revision.RowVersion, "uploader", UserRole.Engineer, default);
        Assert.Equal(ProgramTemplateRevisionState.PendingReview, revision.State);
        Assert.Empty(await service.ListPublishedAsync("viewer", UserRole.ProductionViewer, default));

        var reviewTask = Assert.Single(await service.ListMyTasksAsync("reviewer", UserRole.BusinessUnitManager, default));
        var reviewed = await service.DecideAsync(reviewTask.Id,
            new(ProgramTemplateApprovalDecision.Approved, null, ProgramTemplateChecklist.For(created.AssetType), reviewTask.RowVersion),
            "reviewer", UserRole.BusinessUnitManager, default);
        Assert.Equal(ProgramTemplateRevisionState.PendingApproval, reviewed.Revision.State);

        var approvalTask = Assert.Single(await service.ListMyTasksAsync("approver", UserRole.Approver, default));
        var approved = await service.DecideAsync(approvalTask.Id,
            new(ProgramTemplateApprovalDecision.Approved, "批准发布", [], approvalTask.RowVersion),
            "approver", UserRole.Approver, default);

        Assert.Equal(ProgramTemplateRevisionState.Published, approved.Revision.State);
        var published = Assert.Single(await service.ListPublishedAsync("viewer", UserRole.ProductionViewer, default));
        Assert.Equal(approved.Revision.Id, published.CurrentPublishedRevisionId);
    }

    [Fact]
    public async Task ReviewCannotApproveWithoutCompletingChecklist()
    {
        var clock = TimeProvider.System;
        var pdmRepository = new InMemoryPdmRepository(clock);
        var templateRepository = new InMemoryProgramTemplateRepository(clock);
        var service = new ProgramTemplateService(templateRepository, pdmRepository, new UnusedProgramTemplateStorage(), clock);
        await ConfigureElectricalApprovalChainAsync(pdmRepository);
        var revisionId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var revision = new ProgramTemplateRevision(
            revisionId, templateId, 1, 0, 0, 1, ProgramTemplateRevisionState.Draft,
            "报警模板", "报警", "报警和复位逻辑", "Siemens", "TIA Portal", "V19", "S7-1500", [], "首版",
            "alarm.zip", "package/alarm.zip", 10, new string('A', 64), "test.pdf", "evidence/test.pdf", 10, new string('B', 64),
            "uploader", clock.GetUtcNow(), null, null, 1,
            [new(Guid.NewGuid(), revisionId, ProgramTemplateParameterDirection.Input, 0, "Alarm", "BOOL", null, null, null)]);
        await templateRepository.CreateAsync(new(templateId, "PT-FB-0001", ProgramTemplateAssetType.PlcFunctionBlock, null, null, null, false, "uploader", clock.GetUtcNow(), [revision]), default);
        revision = await service.SubmitAsync(revision.Id, revision.RowVersion, "uploader", UserRole.Engineer, default);
        var reviewTask = Assert.Single(await service.ListMyTasksAsync("reviewer", UserRole.BusinessUnitManager, default));

        await Assert.ThrowsAsync<PdmRuleException>(() => service.DecideAsync(reviewTask.Id,
            new(ProgramTemplateApprovalDecision.Approved, null, [ProgramTemplateChecklist.For(ProgramTemplateAssetType.PlcFunctionBlock)[0]], reviewTask.RowVersion),
            "reviewer", UserRole.BusinessUnitManager, default));

        var rejected = await service.DecideAsync(reviewTask.Id,
            new(ProgramTemplateApprovalDecision.Rejected, "补充边界条件测试", [], reviewTask.RowVersion),
            "reviewer", UserRole.BusinessUnitManager, default);
        Assert.Equal(ProgramTemplateRevisionState.Rejected, rejected.Revision.State);

        var retry = await service.CreateRevisionAsync(templateId, ProgramTemplateVersionBump.Patch, "uploader", UserRole.Engineer, default);
        Assert.Equal("v1.0.0", retry.VersionLabel);
        Assert.Equal(2, retry.AttemptNumber);
        Assert.Equal(ProgramTemplateRevisionState.Draft, retry.State);
        Assert.Null(retry.PackageStoragePath);
        Assert.Null(retry.EvidenceStoragePath);
    }

    private static async Task ConfigureElectricalApprovalChainAsync(InMemoryPdmRepository repository)
    {
        await repository.CreateUserAsync(new(Guid.NewGuid(), "uploader", "上传人", "unused", UserRole.Engineer, true, RoleCode: "ElectricalEngineer"), default);
        await repository.CreateUserAsync(new(Guid.NewGuid(), "reviewer", "电气负责人", "unused", UserRole.BusinessUnitManager, true), default);
        await repository.CreateUserAsync(new(Guid.NewGuid(), "approver", "标准化主管", "unused", UserRole.Approver, true), default);
        await repository.CreateUserAsync(new(Guid.NewGuid(), "viewer", "普通查看人", "unused", UserRole.ProductionViewer, true), default);
        var organization = (await repository.GetOrganizationDirectoryAsync(default)).Organizations.First();
        var unit = await repository.SaveOrganizationUnitAsync(new(null, organization.Id, null, "ELEC", "电气部", OrganizationUnitKind.Department, true, 1), default);
        await repository.SetOrganizationMembershipsAsync("uploader", [unit.Id], unit.Id, default);
        await repository.SetOrganizationUnitManagersAsync(unit.Id, "reviewer", [], default);
    }

    private sealed class UnusedProgramTemplateStorage : IProgramTemplateStorage
    {
        public Task<ProgramTemplateUploadSession> StartUploadAsync(Guid revisionId, string templateCode, ProgramTemplateAttachmentKind kind, string fileName, long totalLength, string expectedSha256, string owner, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ProgramTemplateUploadSession> WriteChunkAsync(Guid sessionId, int chunkIndex, Stream content, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StoredProgramTemplateFile> CompleteUploadAsync(Guid sessionId, string actor, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task VerifyAsync(string relativePath, long length, string sha256, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DiscardAsync(StoredProgramTemplateFile file, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
