using Microsoft.Extensions.Options;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Pdm.Domain.Tests;

public sealed class ProjectCopyServiceTests
{
    [Fact]
    public async Task ExecuteAsync_CopiesOnlyValidationCheckItems()
    {
        var clock = TimeProvider.System;
        var repository = new InMemoryPdmRepository(clock);
        var projectFiles = new InMemoryProjectFileRepository(clock);
        var validationPlans = new InMemoryValidationPlanRepository();
        var root = Path.Combine(Path.GetTempPath(), "pdm-project-copy-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Assert.Single(await repository.ListProjectsAsync(default));
            var target = await repository.CreateProjectAsync(new CreateProjectCommand(
                "COPY-001", "复制目标", "admin", Path.Combine(root, "target-vault"), Path.Combine(root, "target-release")), "admin", default);
            var now = clock.GetUtcNow();
            var sourcePlanId = Guid.NewGuid();
            var sourceItemId = Guid.NewGuid();
            await validationPlans.SavePlanAsync(new ProjectValidationPlan(
                sourcePlanId, source.Id, 3, ProjectValidationPlanState.Effective, "编制人", new DateOnly(2026, 9, 10),
                [new(sourceItemId, null, null, "安全", "检查急停按钮", "技术协议", new DateOnly(2026, 9, 11), "合格", "张三", "已确认", 1)],
                [new(Guid.NewGuid(), sourcePlanId, 1, ApprovalStage.Approval, "审核", "manager", ApprovalDecision.Approved, "manager", "同意", now, now)],
                [new(Guid.NewGuid(), sourcePlanId, ValidationPlanAttachmentKind.PlanDocument, "验证计划.pdf", 1, "plan.pdf", 10, new string('A', 64), "admin", now)],
                "admin", now, "admin", now, 1), null, default);

            var storageOptions = Options.Create(new PdmStorageOptions
            {
                UploadTempRoot = Path.Combine(root, "uploads")
            });
            var service = new ProjectCopyService(
                repository,
                new LocalFileStorage(storageOptions, repository, clock),
                projectFiles,
                new LocalProjectFileStorage(storageOptions, clock),
                validationPlans,
                clock);

            var result = await service.ExecuteAsync(source.Id, target.Id,
                new(false, false, false, true, []), "admin", UserRole.Administrator, default);

            Assert.Equal(1, result.ValidationItemCount);
            Assert.Equal(0, result.DocumentCount);
            Assert.Equal(0, result.BomItemCount);
            Assert.Equal(0, result.ProjectFileCount);
            var copied = Assert.IsType<ProjectValidationPlan>(await validationPlans.FindPlanAsync(target.Id, default));
            Assert.Equal(ProjectValidationPlanState.Draft, copied.State);
            Assert.Equal(1, copied.RevisionNumber);
            Assert.Null(copied.PreparedBy);
            Assert.Null(copied.ValidationDate);
            Assert.Empty(copied.ApprovalTasks);
            Assert.Empty(copied.Attachments);
            var item = Assert.Single(copied.Items);
            Assert.NotEqual(sourceItemId, item.Id);
            Assert.Equal("检查急停按钮", item.ValidationContent);
            Assert.Null(item.ValidationDate);
            Assert.Null(item.Result);
            Assert.Null(item.ResponsiblePerson);
            Assert.Null(item.Remark);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
