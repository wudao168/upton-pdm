using System.Text.Json;
using Dapper;
using Upton.Pdm.Domain;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<ReleasePackage> SaveReleasePdfTitleBlockAsync(Guid packageId, PdfTitleBlockSnapshot snapshot, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var affected = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE release_package SET pdf_title_block_snapshot_json=@Snapshot,row_version=row_version+1
            WHERE id=@Id AND (state IN ('Draft','Rejected') OR
                (state='Publishing' AND JSON_UNQUOTE(JSON_EXTRACT(pdf_title_block_snapshot_json,'$.quantitiesFrozen'))='false' AND @Frozen=true))
            """, new { Id = packageId, Snapshot = JsonSerializer.Serialize(snapshot, jsonOptions), Frozen = snapshot.QuantitiesFrozen }, cancellationToken: ct));
        if (affected != 1) throw new PdmConflictException("PDF填写快照已冻结或发布状态已变化，请刷新后重试。");
        return await FindReleasePackageAsync(packageId, ct) ?? throw new PdmNotFoundException("发布包不存在。");
    }
}
