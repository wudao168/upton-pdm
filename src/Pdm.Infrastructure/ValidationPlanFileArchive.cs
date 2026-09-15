using System.Security.Cryptography;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class ValidationPlanFileArchive(
    IPdmRepository repository,
    IProjectFileRepository files) : IValidationPlanFileArchive
{
    private const string FolderTemplateKey = "acceptance.validation-plan";

    public async Task ArchiveWorkbookAsync(ValidationPlanExportData export, string actor, CancellationToken cancellationToken)
    {
        var folder = await FindFolderAsync(export.Project.Id, actor, cancellationToken);
        var effectiveAt = export.Plan.EffectiveAt ?? export.ExportedAt;
        var fileName = $"{SafeFileName(export.Project.Code)}_验证计划_R{export.Plan.RevisionNumber:D3}_{effectiveAt:yyyyMMdd_HHmmss}.xlsx";
        if (await ContainsAsync(folder, fileName, null, cancellationToken)) return;

        var content = ValidationPlanWorkbook.Write(export);
        var versionId = Guid.NewGuid();
        var relativePath = Path.Combine("验收资料", "验证计划", ".versions", $"R{export.Plan.RevisionNumber:D3}", "Generated", versionId.ToString("N"), fileName);
        var absolutePath = StorageLocationPolicy.ResolveUnder(export.Project.VaultLocation, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        var temporaryPath = absolutePath + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, content, cancellationToken);
            File.Move(temporaryPath, absolutePath);
            File.SetAttributes(absolutePath, File.GetAttributes(absolutePath) | FileAttributes.ReadOnly);
            var sha256 = Convert.ToHexString(SHA256.HashData(content));
            await files.AddVersionAsync(new(versionId, export.Project.Id, folder.Id, fileName, export.Project.VaultLocation,
                relativePath, content.LongLength, sha256, effectiveAt), actor,
                $"验证计划R{export.Plan.RevisionNumber}审批完成自动归档", cancellationToken);
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            if (File.Exists(absolutePath))
            {
                File.SetAttributes(absolutePath, FileAttributes.Normal);
                File.Delete(absolutePath);
            }
            throw;
        }
    }

    public async Task ArchiveAttachmentAsync(ProjectValidationPlan plan, ValidationPlanAttachment attachment, string actor, CancellationToken cancellationToken)
    {
        var project = await repository.FindProjectAsync(plan.ProjectId, cancellationToken) ?? throw new PdmNotFoundException("项目不存在。");
        var folder = await FindFolderAsync(project.Id, actor, cancellationToken);
        if (await ContainsAsync(folder, attachment.OriginalFileName, attachment.StorageRelativePath, cancellationToken)) return;
        await files.AddVersionAsync(new(attachment.Id, project.Id, folder.Id, attachment.OriginalFileName, project.VaultLocation,
            attachment.StorageRelativePath, attachment.FileLength, attachment.Sha256, attachment.UploadedAt), actor,
            $"验证计划R{plan.RevisionNumber}附件 · {attachment.Kind}", cancellationToken);
    }

    private async Task<ProjectFolder> FindFolderAsync(Guid projectId, string actor, CancellationToken cancellationToken)
    {
        var folders = await repository.ListProjectFoldersAsync(projectId, actor, UserRole.Administrator, cancellationToken);
        return folders.FirstOrDefault(item => string.Equals(item.TemplateKey, FolderTemplateKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new PdmNotFoundException("项目的验证计划归档文件夹不存在。");
    }

    private async Task<bool> ContainsAsync(ProjectFolder folder, string fileName, string? relativePath, CancellationToken cancellationToken)
    {
        var existing = (await files.ListAsync(folder.RootProjectId, folder.Id, false, cancellationToken))
            .FirstOrDefault(item => string.Equals(item.FileName, fileName, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return false;
        if (relativePath is null) return true;
        return (await files.ListVersionsAsync(existing.Id, cancellationToken))
            .Any(item => string.Equals(item.StorageRelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
    }

    private static string SafeFileName(string value) =>
        string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
}
