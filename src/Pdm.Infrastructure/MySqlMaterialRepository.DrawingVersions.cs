using Dapper;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlMaterialRepository
{
    public async Task<MaterialAttachment> EnableMaterialAttachmentAsync(Guid materialId, Guid attachmentId, Guid? expectedEnabledId,
        string actor, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var db = await OpenAsync(cancellationToken);
        await using var tx = await db.BeginTransactionAsync(cancellationToken);
        var active = await db.ExecuteScalarAsync<bool?>(new CommandDefinition(
            "SELECT NOT is_archived FROM material_master WHERE id=@materialId FOR UPDATE", new { materialId }, tx, cancellationToken: cancellationToken));
        if (active != true) throw new PdmRuleException("料品不存在或已停用，不能启用图纸版本。");
        var row = await db.QuerySingleOrDefaultAsync<MaterialAttachmentRow>(new CommandDefinition(
            MaterialAttachmentSelect + " WHERE id=@attachmentId AND material_id=@materialId AND is_removed=0", new { materialId, attachmentId }, tx, cancellationToken: cancellationToken));
        if (row is null || row.AttachmentKind is not ("Drawing2D" or "Model3D")) throw new PdmRuleException("只能启用当前料品的2D或3D版本。");
        var current = await db.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM material_attachment WHERE material_id=@materialId AND attachment_kind=@kind AND is_enabled=1",
            new { materialId, kind = row.AttachmentKind }, tx, cancellationToken: cancellationToken));
        if (current != expectedEnabledId) throw new PdmConflictException("启用版本已被其他人员修改，请刷新核对后重新选择。");
        await db.ExecuteAsync(new CommandDefinition("UPDATE material_attachment SET is_enabled=0 WHERE material_id=@materialId AND attachment_kind=@kind AND is_enabled=1",
            new { materialId, kind = row.AttachmentKind }, tx, cancellationToken: cancellationToken));
        await db.ExecuteAsync(new CommandDefinition("UPDATE material_attachment SET is_enabled=1,enabled_by=@actor,enabled_at=@at WHERE id=@attachmentId",
            new { attachmentId, actor, at = at.UtcDateTime }, tx, cancellationToken: cancellationToken));
        await tx.CommitAsync(cancellationToken);
        return MapMaterialAttachment(row) with { IsEnabled = true, EnabledBy = actor, EnabledAt = at };
    }
}
