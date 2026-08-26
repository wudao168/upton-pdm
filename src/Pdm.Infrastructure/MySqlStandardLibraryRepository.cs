using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class MySqlStandardLibraryRepository : IStandardLibraryRepository
{
    private readonly string connectionString;

    public MySqlStandardLibraryRepository(IOptions<PdmDatabaseOptions> options)
    {
        connectionString = options.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString)) throw new InvalidOperationException("PLM MySQL连接字符串未配置。 ");
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public async Task<IReadOnlyList<StandardLibraryCategory>> ListCategoriesAsync(bool includeInactive, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<CategoryRow>(new CommandDefinition(
            CategorySelect + " WHERE (@IncludeInactive=1 OR is_active=1) ORDER BY sort_order,name,id",
            new { IncludeInactive = includeInactive }, cancellationToken: cancellationToken));
        return rows.Select(MapCategory).ToArray();
    }

    public async Task<StandardLibraryCategory?> FindCategoryAsync(Guid categoryId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<CategoryRow>(new CommandDefinition(
            CategorySelect + " WHERE id=@CategoryId", new { CategoryId = categoryId }, cancellationToken: cancellationToken));
        return row is null ? null : MapCategory(row);
    }

    public async Task<StandardLibraryCategory> CreateCategoryAsync(StandardLibraryCategory category, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO standard_library_category(id,name,parent_id,sort_order,is_active,created_by,created_at,updated_by,updated_at,row_version)
                VALUES(@Id,@Name,@ParentId,@SortOrder,@IsActive,@CreatedBy,@CreatedAt,@UpdatedBy,@UpdatedAt,@RowVersion)
                """, category, cancellationToken: cancellationToken));
            return category;
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            throw new PdmConflictException("同一上级下已存在同名标准分类。");
        }
    }

    public async Task<StandardLibraryCategory> UpdateCategoryAsync(StandardLibraryCategory category, long expectedRowVersion, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        int affected;
        try
        {
            affected = await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE standard_library_category
                SET name=@Name,parent_id=@ParentId,sort_order=@SortOrder,is_active=@IsActive,
                    updated_by=@UpdatedBy,updated_at=@UpdatedAt,row_version=row_version+1
                WHERE id=@Id AND row_version=@ExpectedRowVersion
                """, new { category.Id, category.Name, category.ParentId, category.SortOrder, category.IsActive, category.UpdatedBy, category.UpdatedAt, ExpectedRowVersion = expectedRowVersion }, cancellationToken: cancellationToken));
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            throw new PdmConflictException("同一上级下已存在同名标准分类。");
        }
        if (affected == 0) throw new PdmConflictException("标准分类已被其他用户修改，请刷新后重试。");
        return (await FindCategoryAsync(category.Id, cancellationToken))!;
    }

    public async Task DeleteCategoryAsync(Guid categoryId, long expectedRowVersion, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var childCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM standard_library_category WHERE parent_id=@CategoryId", new { CategoryId = categoryId }, transaction, cancellationToken: cancellationToken));
        var memberCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM standard_library_membership WHERE category_id=@CategoryId", new { CategoryId = categoryId }, transaction, cancellationToken: cancellationToken));
        if (childCount > 0 || memberCount > 0) throw new PdmRuleException("仅能删除无子分类且无成员的标准分类。");
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM standard_library_category WHERE id=@CategoryId AND row_version=@ExpectedRowVersion",
            new { CategoryId = categoryId, ExpectedRowVersion = expectedRowVersion }, transaction, cancellationToken: cancellationToken));
        if (affected == 0) throw new PdmConflictException("标准分类不存在或已被其他用户修改。");
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StandardLibraryMembership>> ListMembershipsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<MembershipRow>(new CommandDefinition(
            "SELECT category_id CategoryId,material_id MaterialId,added_by AddedBy,added_at AddedAt FROM standard_library_membership",
            cancellationToken: cancellationToken));
        return rows.Select(MapMembership).ToArray();
    }

    public async Task AddMembershipsAsync(IReadOnlyList<StandardLibraryMembership> memberships, CancellationToken cancellationToken)
    {
        if (memberships.Count == 0) return;
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT IGNORE INTO standard_library_membership(category_id,material_id,added_by,added_at)
            VALUES(@CategoryId,@MaterialId,@AddedBy,@AddedAt)
            """, memberships, cancellationToken: cancellationToken));
    }

    public async Task RemoveMembershipAsync(Guid categoryId, Guid materialId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM standard_library_membership WHERE category_id=@CategoryId AND material_id=@MaterialId",
            new { CategoryId = categoryId, MaterialId = materialId }, cancellationToken: cancellationToken));
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static StandardLibraryCategory MapCategory(CategoryRow row) => new(
        row.Id, row.Name, row.ParentId, row.SortOrder, row.IsActive, row.CreatedBy, Utc(row.CreatedAt),
        row.UpdatedBy, Utc(row.UpdatedAt), row.RowVersion);

    private static StandardLibraryMembership MapMembership(MembershipRow row) => new(
        row.CategoryId, row.MaterialId, row.AddedBy, Utc(row.AddedAt));

    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private const string CategorySelect = """
        SELECT id Id,name Name,parent_id ParentId,sort_order SortOrder,is_active IsActive,
               created_by CreatedBy,created_at CreatedAt,updated_by UpdatedBy,updated_at UpdatedAt,row_version RowVersion
        FROM standard_library_category
        """;

    private sealed class CategoryRow
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public Guid? ParentId { get; init; }
        public int SortOrder { get; init; }
        public bool IsActive { get; init; }
        public string CreatedBy { get; init; } = string.Empty;
        public DateTime CreatedAt { get; init; }
        public string UpdatedBy { get; init; } = string.Empty;
        public DateTime UpdatedAt { get; init; }
        public long RowVersion { get; init; }
    }

    private sealed class MembershipRow
    {
        public Guid CategoryId { get; init; }
        public Guid MaterialId { get; init; }
        public string AddedBy { get; init; } = string.Empty;
        public DateTime AddedAt { get; init; }
    }
}
