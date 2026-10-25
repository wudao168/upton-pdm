using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed class MaterialAttachmentService(
    IMaterialRepository materials,
    IPdmRepository repository,
    IMaterialAttachmentStorage storage,
    TimeProvider timeProvider,
    IEngineeringKitRepository? kits = null)
{
    public async Task<IReadOnlyList<MaterialAttachment>> ListAsync(Guid materialId, MaterialAttachmentKind? kind, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireReadPermissionAsync(actor, role, cancellationToken);
        if (await materials.FindMaterialAsync(materialId, cancellationToken) is not null)
            return await materials.ListMaterialAttachmentsAsync(materialId, kind, cancellationToken);
        await RequireReleasedKitAsync(materialId, actor, role, false, cancellationToken);
        return await kits!.ListLibraryAttachmentsAsync(materialId, kind, cancellationToken);
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
        var material = await materials.FindMaterialAsync(materialId, cancellationToken);
        EngineeringKit? kit = null;
        if (material is null)
        {
            kit = await RequireReleasedKitAsync(materialId, actor, role, true, cancellationToken);
            if (kind is not (MaterialAttachmentKind.CoverImage or MaterialAttachmentKind.Model3D or MaterialAttachmentKind.Document)) throw new PdmRuleException("不支持该套件附件类型。");
        }
        else
        {
            await RequireAttachmentWritePermissionAsync(actor, role, kind, cancellationToken);
            if (material.IsArchived) throw new PdmRuleException("已停用料品不能上传附件。");
        }
        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.MaterialAttachmentRoot))
            throw new PdmRuleException("料品资料存档根目录尚未配置。");
        return await storage.StartUploadAsync(
            materialId, material?.MaterialCode ?? kit!.Code!, kind, fileName, totalLength, sha256,
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
        EngineeringKit? kit = null;
        try
        {
            if (material is null)
            {
                kit = await RequireReleasedKitAsync(stored.MaterialId, actor, role, true, cancellationToken);
                if (stored.Kind is not (MaterialAttachmentKind.CoverImage or MaterialAttachmentKind.Model3D or MaterialAttachmentKind.Document)) throw new PdmRuleException("不支持该套件附件类型。");
            }
            else
            {
                await RequireAttachmentWritePermissionAsync(actor, role, stored.Kind, cancellationToken);
                if (material.IsArchived) throw new PdmRuleException("料品已停用，附件未入库。");
            }
        }
        catch
        {
            await storage.DiscardAsync(stored, cancellationToken);
            throw;
        }

        var attachment = new MaterialAttachment(
            Guid.NewGuid(), stored.MaterialId, stored.Kind, stored.OriginalFileName, stored.StorageRoot,
            stored.RelativePath, stored.Length, stored.Sha256, actor, stored.StoredAt);
        try
        {
            attachment = material is null
                ? await kits!.CreateLibraryAttachmentAsync(attachment, cancellationToken)
                : await materials.CreateMaterialAttachmentAsync(attachment, cancellationToken);
        }
        catch
        {
            await storage.DiscardAsync(stored, cancellationToken);
            throw;
        }

        await repository.AppendAuditAsync(new AuditEntry(
            Guid.NewGuid(), timeProvider.GetUtcNow(), actor, "material.attachment.upload", nameof(MaterialAttachment),
            attachment.Id.ToString(), $"{material?.MaterialCode ?? kit!.Code} · {attachment.Kind} · {attachment.OriginalFileName} · {attachment.Sha256}"), cancellationToken);
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
        if (attachment is null && kits is not null)
        {
            attachment = await kits.FindLibraryAttachmentAsync(attachmentId, cancellationToken);
            if (attachment is not null && attachment.MaterialId == materialId)
                await RequireReleasedKitAsync(materialId, actor, role, false, cancellationToken);
        }
        if (attachment is null || attachment.MaterialId != materialId) throw new PdmNotFoundException("料品附件不存在。");
        await storage.VerifyAsync(attachment, cancellationToken);
        var content = await storage.OpenReadAsync(attachment, cancellationToken);
        await repository.AppendAuditAsync(new AuditEntry(
            Guid.NewGuid(), timeProvider.GetUtcNow(), actor, "material.attachment.download", nameof(MaterialAttachment),
            attachment.Id.ToString(), $"下载料品附件：{attachment.OriginalFileName}"), cancellationToken);
        return new(attachment, content);
    }

    public async Task DeleteAsync(Guid materialId, Guid attachmentId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireAnyAttachmentWritePermissionAsync(actor, role, cancellationToken);
        var material = await materials.FindMaterialAsync(materialId, cancellationToken);
        EngineeringKit? kit = null;
        MaterialAttachment? attachment;
        if (material is null)
        {
            kit = await RequireReleasedKitAsync(materialId, actor, role, true, cancellationToken);
            attachment = await kits!.FindLibraryAttachmentAsync(attachmentId, cancellationToken);
        }
        else
        {
            await RequirePermissionAsync(actor, role, PermissionCodes.MaterialManage, cancellationToken);
            if (material.IsArchived) throw new PdmRuleException("已停用料品不能删除附件，请先恢复启用。");
            attachment = await materials.FindMaterialAttachmentAsync(attachmentId, cancellationToken);
        }
        if (attachment is null || attachment.MaterialId != materialId) throw new PdmNotFoundException("料品附件不存在。");
        if (attachment.Kind is not (MaterialAttachmentKind.Model3D or MaterialAttachmentKind.Document))
            throw new PdmRuleException("此处只能删除3D或资料附件。");
        var deleted = material is null
            ? await kits!.DeleteLibraryAttachmentAsync(materialId, attachmentId, cancellationToken)
            : await materials.DeleteMaterialAttachmentAsync(materialId, attachmentId, cancellationToken);
        if (!deleted)
            throw new PdmConflictException("附件已被删除或料品状态已变化，请刷新后重试。");
        await repository.AppendAuditAsync(new AuditEntry(
            Guid.NewGuid(), timeProvider.GetUtcNow(), actor, material is null ? "engineering-kit.attachment.delete" : "material.attachment.delete", nameof(MaterialAttachment),
            attachment.Id.ToString(), $"删除附件记录：{material?.MaterialCode ?? kit!.Code} · {attachment.Kind} · {attachment.OriginalFileName} · {attachment.Sha256}；存档文件保留。"), cancellationToken);
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

    private async Task<EngineeringKit> RequireReleasedKitAsync(Guid id, string actor, UserRole role, bool write, CancellationToken cancellationToken)
    {
        var kit = kits is null ? null : await kits.FindAsync(id, cancellationToken);
        if (kit?.CurrentReleasedRevisionId is null) throw new PdmNotFoundException("料品或已发布套件不存在。");
        if (write)
        {
            await RequireAnyAttachmentWritePermissionAsync(actor, role, cancellationToken);
            if (!kit.IsActive) throw new PdmRuleException("已停用套件不能维护资料，请先恢复启用。");
        }
        else await RequirePermissionAsync(actor, role, PermissionCodes.StandardLibraryView, cancellationToken);
        return kit;
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
