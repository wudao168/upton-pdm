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
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            category = await AssignCategoryCodeAsync(connection, transaction, category, cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO standard_library_category(id,name,parent_id,sort_order,is_active,created_by,created_at,updated_by,updated_at,row_version,code)
                VALUES(@Id,@Name,@ParentId,@SortOrder,@IsActive,@CreatedBy,@CreatedAt,@UpdatedBy,@UpdatedAt,@RowVersion,@Code)
                """, category, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return category;
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            throw new PdmConflictException("标准分类名称或代码已存在，请刷新后核对。");
        }
    }

    public async Task<StandardLibraryCategory> UpdateCategoryAsync(StandardLibraryCategory category, long expectedRowVersion, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        int affected;
        try
        {
            category = await AssignCategoryCodeAsync(connection, transaction, category, cancellationToken);
            affected = await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE standard_library_category
                SET name=@Name,code=@Code,parent_id=@ParentId,sort_order=@SortOrder,is_active=@IsActive,
                    updated_by=@UpdatedBy,updated_at=@UpdatedAt,row_version=row_version+1
                WHERE id=@Id AND row_version=@ExpectedRowVersion
                """, new { category.Id, category.Name, category.Code, category.ParentId, category.SortOrder, category.IsActive, category.UpdatedBy, category.UpdatedAt, ExpectedRowVersion = expectedRowVersion }, transaction, cancellationToken: cancellationToken));
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            throw new PdmConflictException("标准分类名称或代码已存在，请刷新后核对。");
        }
        if (affected == 0) throw new PdmConflictException("标准分类已被其他用户修改，请刷新后重试。");
        await transaction.CommitAsync(cancellationToken);
        return (await FindCategoryAsync(category.Id, cancellationToken))!;
    }

    private static async Task<StandardLibraryCategory> AssignCategoryCodeAsync(MySqlConnection connection, MySqlTransaction transaction, StandardLibraryCategory category, CancellationToken cancellationToken)
    {
        if (category.ParentId is not null || category.Code is not null || StandardLibraryDefaults.IsUncategorized(category)) return category;
        var current = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT current_sequence FROM standard_library_category_counter WHERE counter_id=1 FOR UPDATE", transaction: transaction, cancellationToken: cancellationToken));
        var next = checked(current + 1);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE standard_library_category_counter SET current_sequence=@Next WHERE counter_id=1", new { Next = next }, transaction, cancellationToken: cancellationToken));
        return category with { Code = EngineeringKitNumbering.CategoryCode(next) };
    }

    public async Task DeleteCategoryAsync(Guid categoryId, long expectedRowVersion, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var childCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM standard_library_category WHERE parent_id=@CategoryId", new { CategoryId = categoryId }, transaction, cancellationToken: cancellationToken));
        var memberCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM standard_library_membership WHERE category_id=@CategoryId", new { CategoryId = categoryId }, transaction, cancellationToken: cancellationToken));
        var kitCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT (SELECT COUNT(*) FROM engineering_kit WHERE standard_library_category_id=@CategoryId)+(SELECT COUNT(*) FROM engineering_kit_revision WHERE standard_library_category_id=@CategoryId)", new { CategoryId = categoryId }, transaction, cancellationToken: cancellationToken));
        if (childCount > 0 || memberCount > 0 || kitCount > 0) throw new PdmRuleException("仅能删除无子分类、无成员且未被套件版本引用的标准分类。");
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
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var uncategorized = await connection.QuerySingleAsync<Guid>(new CommandDefinition(
            "SELECT id FROM standard_library_category WHERE parent_id IS NULL AND name='未分类'", transaction: transaction, cancellationToken: cancellationToken));
        foreach (var group in memberships.GroupBy(item => item.MaterialId))
        {
            // Serialize category assignment and import for the same material.
            await connection.ExecuteScalarAsync<Guid>(new CommandDefinition("SELECT id FROM material_master WHERE id=@Id FOR UPDATE", new { Id = group.Key }, transaction, cancellationToken: cancellationToken));
            var classified = group.Where(item => item.CategoryId != uncategorized).ToArray();
            if (classified.Length > 0)
                await connection.ExecuteAsync(new CommandDefinition("DELETE FROM standard_library_membership WHERE material_id=@Id AND category_id=@Uncategorized", new { Id = group.Key, Uncategorized = uncategorized }, transaction, cancellationToken: cancellationToken));
            else if (await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM standard_library_membership WHERE material_id=@Id AND category_id<>@Uncategorized", new { Id = group.Key, Uncategorized = uncategorized }, transaction, cancellationToken: cancellationToken)) > 0) continue;
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT IGNORE INTO standard_library_membership(category_id,material_id,added_by,added_at)
                VALUES(@CategoryId,@MaterialId,@AddedBy,@AddedAt)
                """, classified.Length > 0 ? classified : group.ToArray(), transaction, cancellationToken: cancellationToken));
        }
        await transaction.CommitAsync(cancellationToken);
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
        row.UpdatedBy, Utc(row.UpdatedAt), row.RowVersion, row.Code);

    private static StandardLibraryMembership MapMembership(MembershipRow row) => new(
        row.CategoryId, row.MaterialId, row.AddedBy, Utc(row.AddedAt));

    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private const string CategorySelect = """
        SELECT id Id,name Name,code Code,parent_id ParentId,sort_order SortOrder,is_active IsActive,
               created_by CreatedBy,created_at CreatedAt,updated_by UpdatedBy,updated_at UpdatedAt,row_version RowVersion
        FROM standard_library_category
        """;

    private sealed class CategoryRow
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string? Code { get; init; }
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
