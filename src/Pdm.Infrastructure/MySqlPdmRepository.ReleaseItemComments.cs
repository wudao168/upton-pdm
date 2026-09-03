using Dapper;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<IReadOnlyList<ReleaseItemComment>> ListReleaseItemCommentsAsync(Guid releasePackageId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<ReleaseItemCommentRow>(new CommandDefinition(
            """
            SELECT id,release_package_id,bom_item_id,material_key,material_code,material_name,
                   specification,source_instance_path,comment_text,created_by,created_at
            FROM release_item_comment
            WHERE release_package_id=@ReleasePackageId
            ORDER BY created_at,id
            """,
            new { ReleasePackageId = releasePackageId },
            cancellationToken: cancellationToken));
        return rows.Select(MapReleaseItemComment).ToArray();
    }

    public async Task<ReleaseItemComment> AddReleaseItemCommentAsync(ReleaseItemComment comment, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO release_item_comment(
                id,release_package_id,bom_item_id,material_key,material_code,material_name,
                specification,source_instance_path,comment_text,created_by,created_at)
            VALUES(
                @Id,@ReleasePackageId,@BomItemId,@MaterialKey,@MaterialCode,@MaterialName,
                @Specification,@SourceInstancePath,@Comment,@CreatedBy,@CreatedAt)
            """,
            new
            {
                comment.Id,
                comment.ReleasePackageId,
                comment.BomItemId,
                comment.MaterialKey,
                comment.MaterialCode,
                comment.MaterialName,
                comment.Specification,
                comment.SourceInstancePath,
                comment.Comment,
                comment.CreatedBy,
                CreatedAt = comment.CreatedAt.UtcDateTime
            },
            cancellationToken: cancellationToken));
        return comment;
    }

    private static ReleaseItemComment MapReleaseItemComment(ReleaseItemCommentRow row) => new(
        row.Id,
        row.ReleasePackageId,
        row.BomItemId,
        row.MaterialKey,
        row.MaterialCode,
        row.MaterialName,
        row.Specification,
        row.SourceInstancePath,
        row.CommentText,
        row.CreatedBy,
        new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)));

    private sealed class ReleaseItemCommentRow
    {
        public Guid Id { get; init; }
        public Guid ReleasePackageId { get; init; }
        public Guid BomItemId { get; init; }
        public string MaterialKey { get; init; } = string.Empty;
        public string MaterialCode { get; init; } = string.Empty;
        public string MaterialName { get; init; } = string.Empty;
        public string? Specification { get; init; }
        public string? SourceInstancePath { get; init; }
        public string CommentText { get; init; } = string.Empty;
        public string CreatedBy { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
    }
}
