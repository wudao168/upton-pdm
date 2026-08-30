using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class MaterialAttachmentService(
    IMaterialRepository materials,
    IPdmRepository repository,
    IMaterialAttachmentStorage storage,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<MaterialAttachment>> ListAsync(Guid materialId, MaterialAttachmentKind? kind, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireReadPermissionAsync(actor, role, cancellationToken);
        _ = await materials.FindMaterialAsync(materialId, cancellationToken)
            ?? throw new PdmNotFoundException("料品主档不存在。");
        return await materials.ListMaterialAttachmentsAsync(materialId, kind, cancellationToken);
    }

    public async Task<MaterialAttachmentUploadSession> StartUploadAsync(
        Guid materialId,
        MaterialAttachmentKind kind,
        string fileName,
        long totalLength,
        string sha256,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequireAttachmentWritePermissionAsync(actor, role, kind, cancellationToken);
        var material = await materials.FindMaterialAsync(materialId, cancellationToken)
            ?? throw new PdmNotFoundException("料品主档不存在。");
        if (material.IsArchived) throw new PdmRuleException("已停用料品不能上传附件。");
        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.MaterialAttachmentRoot))
            throw new PdmRuleException("料品资料存档根目录尚未配置。");
        return await storage.StartUploadAsync(
            material.Id, material.MaterialCode, kind, fileName, totalLength, sha256,
            settings.MaterialAttachmentRoot, actor, cancellationToken);
    }

    public async Task<MaterialAttachmentUploadSession> WriteChunkAsync(
        Guid sessionId,
        int chunkIndex,
        Stream content,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequireAnyAttachmentWritePermissionAsync(actor, role, cancellationToken);
        return await storage.WriteChunkAsync(sessionId, chunkIndex, content, actor, cancellationToken);
    }

    public async Task<MaterialAttachment> CompleteUploadAsync(
        Guid sessionId,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequireAnyAttachmentWritePermissionAsync(actor, role, cancellationToken);
        var stored = await storage.CompleteUploadAsync(sessionId, actor, cancellationToken);
        var material = await materials.FindMaterialAsync(stored.MaterialId, cancellationToken);
        if (material is null || material.IsArchived)
        {
            await storage.DiscardAsync(stored, cancellationToken);
            throw new PdmRuleException("料品不存在或已停用，附件未入库。");
        }

        var attachment = new MaterialAttachment(
            Guid.NewGuid(), stored.MaterialId, stored.Kind, stored.OriginalFileName, stored.StorageRoot,
            stored.RelativePath, stored.Length, stored.Sha256, actor, stored.StoredAt);
        try
        {
            attachment = await materials.CreateMaterialAttachmentAsync(attachment, cancellationToken);
        }
        catch
        {
            await storage.DiscardAsync(stored, cancellationToken);
            throw;
        }

        await repository.AppendAuditAsync(new AuditEntry(
            Guid.NewGuid(), timeProvider.GetUtcNow(), actor, "material.attachment.upload", nameof(MaterialAttachment),
            attachment.Id.ToString(), $"{material.MaterialCode} · {attachment.Kind} · {attachment.OriginalFileName} · {attachment.Sha256}"), cancellationToken);
        return attachment;
    }

    public async Task<MaterialAttachmentDownload> OpenDownloadAsync(
        Guid materialId,
        Guid attachmentId,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequireReadPermissionAsync(actor, role, cancellationToken);
        var attachment = await materials.FindMaterialAttachmentAsync(attachmentId, cancellationToken);
        if (attachment is null || attachment.MaterialId != materialId) throw new PdmNotFoundException("料品附件不存在。");
        await storage.VerifyAsync(attachment, cancellationToken);
        var content = await storage.OpenReadAsync(attachment, cancellationToken);
        await repository.AppendAuditAsync(new AuditEntry(
            Guid.NewGuid(), timeProvider.GetUtcNow(), actor, "material.attachment.download", nameof(MaterialAttachment),
            attachment.Id.ToString(), $"下载料品附件：{attachment.OriginalFileName}"), cancellationToken);
        return new(attachment, content);
    }

    public async Task<PdmMaterial> SetCoverAsync(
        Guid materialId,
        Guid? attachmentId,
        long expectedRowVersion,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequireAnyAttachmentWritePermissionAsync(actor, role, cancellationToken);
        var material = await materials.FindMaterialAsync(materialId, cancellationToken)
            ?? throw new PdmNotFoundException("料品主档不存在。");
        if (material.IsArchived) throw new PdmRuleException("已停用料品不能维护封面。");
        if (attachmentId is not null)
        {
            var attachment = await materials.FindMaterialAttachmentAsync(attachmentId.Value, cancellationToken);
            if (attachment is null || attachment.MaterialId != materialId || attachment.Kind != MaterialAttachmentKind.CoverImage)
                throw new PdmRuleException("只能将该料品的封面图片附件设为当前封面。");
        }
        var saved = await materials.UpdatePlmMetadataAsync(material with
        {
            CoverImageAttachmentId = attachmentId,
            UpdatedBy = actor,
            UpdatedAt = timeProvider.GetUtcNow()
        }, expectedRowVersion, cancellationToken);
        await repository.AppendAuditAsync(new AuditEntry(
            Guid.NewGuid(), timeProvider.GetUtcNow(), actor, attachmentId is null ? "material.cover.clear" : "material.cover.set",
            nameof(PdmMaterial), materialId.ToString(), attachmentId is null ? "清除当前封面指针；历史图片保留。" : $"切换当前封面：{attachmentId}"), cancellationToken);
        return saved;
    }

    private async Task RequirePermissionAsync(string actor, UserRole role, string permissionCode, CancellationToken cancellationToken)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, permissionCode, cancellationToken))
            throw new UnauthorizedAccessException("当前角色无权执行此操作。");
    }

    private async Task RequireReadPermissionAsync(string actor, UserRole role, CancellationToken cancellationToken)
    {
        foreach (var permissionCode in new[] { PermissionCodes.MaterialView, PermissionCodes.BomEdit, PermissionCodes.StandardLibraryView })
            if (await repository.HasUserPermissionAsync(actor, role, permissionCode, cancellationToken)) return;
        throw new UnauthorizedAccessException("当前角色无权查看料品附件。");
    }

    private Task RequireAttachmentWritePermissionAsync(string actor, UserRole role, MaterialAttachmentKind kind, CancellationToken cancellationToken) =>
        kind == MaterialAttachmentKind.CoverImage
            ? RequireAnyAttachmentWritePermissionAsync(actor, role, cancellationToken)
            : RequirePermissionAsync(actor, role, PermissionCodes.MaterialManage, cancellationToken);

    private async Task RequireAnyAttachmentWritePermissionAsync(string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.MaterialManage, cancellationToken)
            && !await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StandardLibraryManage, cancellationToken))
            throw new UnauthorizedAccessException("当前角色无权维护料品附件。");
    }
}
