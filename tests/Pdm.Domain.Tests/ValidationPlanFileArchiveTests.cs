using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Pdm.Domain.Tests;

public sealed class ValidationPlanFileArchiveTests
{
    [Fact]
    public async Task ApprovedWorkbookAndAttachmentsAppearInValidationPlanFolderWithoutDuplicates()
    {
        var time = TimeProvider.System;
        var repository = new InMemoryPdmRepository(time);
        var files = new InMemoryProjectFileRepository(time);
        var archive = new ValidationPlanFileArchive(repository, files);
        var vault = Path.Combine(Path.GetTempPath(), $"pdm-validation-archive-{Guid.NewGuid():N}");
        Directory.CreateDirectory(vault);
        try
        {
            var project = await repository.CreateNumberedProjectAsync(new(
                Guid.Parse("70000000-0000-0000-0000-000000000001"), "P", 2,
                Guid.Parse("c0046500-0000-0000-0000-000000000001"), "验证计划归档", null,
                new DateOnly(2026, 9, 15), 1, "engineer", vault, Path.Combine(vault, "release")), default);
            var effectiveAt = time.GetUtcNow();
            var plan = new ProjectValidationPlan(Guid.NewGuid(), project.Id, 2, ProjectValidationPlanState.Effective,
                "编制人", new DateOnly(2026, 9, 15),
                [new(Guid.NewGuid(), null, null, "人工项", "验证安全门", "内部评审", null, null, null, null, null, 1)],
                [], [], "admin", effectiveAt, "admin", effectiveAt, 1)
            { EffectiveBy = "admin", EffectiveAt = effectiveAt };
            var export = new ValidationPlanExportData(project, plan, effectiveAt, "审核人", "批准人");

            await archive.ArchiveWorkbookAsync(export, "admin", default);
            await archive.ArchiveWorkbookAsync(export, "admin", default);

            var folders = await repository.ListProjectFoldersAsync(project.Id, "admin", UserRole.Administrator, default);
            var folder = folders.Single(item => item.TemplateKey == "acceptance.validation-plan");
            var workbook = Assert.Single(await files.ListAsync(folder.RootProjectId, folder.Id, false, default));
            var version = Assert.Single(await files.ListVersionsAsync(workbook.Id, default));
            Assert.Contains("_R002_", workbook.FileName);
            Assert.True(File.Exists(Path.Combine(version.StorageRoot, version.StorageRelativePath)));
            Assert.True((File.GetAttributes(Path.Combine(version.StorageRoot, version.StorageRelativePath)) & FileAttributes.ReadOnly) != 0);
        }
        finally
        {
            if (Directory.Exists(vault))
            {
                foreach (var file in Directory.EnumerateFiles(vault, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(vault, true);
            }
        }
    }
}
