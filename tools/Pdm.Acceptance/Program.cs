using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;
using Upton.Pdm.Application;

var baseConnection = Environment.GetEnvironmentVariable("PDM_ACCEPTANCE_CONNECTION")
    ?? throw new InvalidOperationException("PDM_ACCEPTANCE_CONNECTION未设置。");
var password = Environment.GetEnvironmentVariable("PDM_DB_PASSWORD")
    ?? throw new InvalidOperationException("PDM_DB_PASSWORD未设置。");
var builder = new MySqlConnectionStringBuilder(baseConnection) { Password = password };
var options = Options.Create(new PdmDatabaseOptions
{
    Provider = "MySql",
    RunMigrations = true,
    ConnectionString = builder.ConnectionString
});
var runner = new MySqlMigrationRunner(options, NullLogger<MySqlMigrationRunner>.Instance, TimeProvider.System);
await runner.RunAsync(CancellationToken.None);

await using var connection = new MySqlConnection(builder.ConnectionString);
await connection.OpenAsync();
long? financialBatchMilliseconds = null;
if (string.Equals(Environment.GetEnvironmentVariable("PDM_ACCEPTANCE_SEED"), "1", StringComparison.Ordinal))
{
    await SeedAsync(connection);
    financialBatchMilliseconds = await VerifyFinancialStorageAsync(connection, options);
}
var migrations = (await connection.QueryAsync<string>("SELECT version FROM pdm_schema_migration ORDER BY version")).ToArray();
var releaseColumns = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='release_package' AND column_name IN ('mechanical_bom_snapshot_json','electrical_bom_snapshot_json','publish_error')");
var projectFileTables = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE() AND table_name IN ('project_file','project_file_version')");
var projectFileTriggers = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.triggers WHERE trigger_schema=DATABASE() AND trigger_name IN ('trg_project_file_version_no_update','trg_project_file_version_no_delete')");
var tableCountRows = await connection.QueryAsync<DatabaseTableCount>("SELECT 'project' AS TableName,COUNT(*) AS RowCount FROM project UNION ALL SELECT 'document',COUNT(*) FROM document UNION ALL SELECT 'document_version',COUNT(*) FROM document_version UNION ALL SELECT 'reference_snapshot',COUNT(*) FROM reference_snapshot UNION ALL SELECT 'bom_item',COUNT(*) FROM bom_item UNION ALL SELECT 'release_package',COUNT(*) FROM release_package UNION ALL SELECT 'audit_entry',COUNT(*) FROM audit_entry");
var tableCounts = tableCountRows.ToDictionary(row => row.TableName, row => row.RowCount, StringComparer.Ordinal);
var projectCompanies = (await connection.QueryAsync("SELECT code,organization_id FROM project ORDER BY code")).ToArray();
var projectReferenceRoots = (await connection.QueryAsync<ProjectReferenceRootRow>("""
    SELECT p.code AS ProjectCode,
           d.file_name AS RootFileName,
           JSON_UNQUOTE(JSON_EXTRACT(rs.root_json, '$.fileName')) AS SnapshotRootFileName,
           rs.captured_at AS CapturedAt
    FROM project_reference_root prr
    INNER JOIN project p ON p.id=prr.project_id
    INNER JOIN reference_snapshot rs ON rs.id=prr.reference_snapshot_id
    INNER JOIN document d ON d.id=rs.root_document_id
    ORDER BY p.code
    """)).ToArray();
var referenceSnapshotCandidates = (await connection.QueryAsync<ReferenceSnapshotCandidateRow>("""
    SELECT p.code AS ProjectCode,
           d.file_name AS RootFileName,
           d.revision_label AS RootRevision,
           (SELECT dv.revision_label FROM document_version dv WHERE dv.document_id=d.id ORDER BY dv.created_at DESC, dv.id DESC LIMIT 1) AS LatestVersion,
           (SELECT dv.created_at FROM document_version dv WHERE dv.document_id=d.id ORDER BY dv.created_at DESC, dv.id DESC LIMIT 1) AS LatestVersionAt,
           (SELECT COUNT(*) FROM document_version dv WHERE dv.document_id=d.id) AS VersionCount,
           JSON_UNQUOTE(JSON_EXTRACT(rs.root_json, '$.instancePath')) AS InstancePath,
           JSON_LENGTH(JSON_EXTRACT(rs.root_json, '$.children')) AS DirectChildCount,
           rs.captured_at AS CapturedAt
    FROM reference_snapshot rs
    INNER JOIN project p ON p.id=rs.project_id
    INNER JOIN document d ON d.id=rs.root_document_id
    WHERE d.kind='Assembly'
    ORDER BY p.code, rs.captured_at DESC, rs.id DESC
    """)).ToArray();
var qaPasswordHash = await connection.QuerySingleOrDefaultAsync<string>("SELECT password_hash FROM pdm_user WHERE username='qa_admin'");
Console.WriteLine(JsonSerializer.Serialize(new
{
    database = builder.Database,
    financialBatchMilliseconds,
    migrations,
    expectedMigrationApplied = migrations.Contains("004_phase1_bom_release_workflow", StringComparer.Ordinal),
    projectFileMigrationApplied = migrations.Contains("075_project_files", StringComparer.Ordinal),
    projectFileRetentionMigrationApplied = migrations.Contains("076_project_file_retention", StringComparer.Ordinal),
    releaseColumns,
    projectFileTables,
    projectFileTriggers,
    tableCounts,
    projectCompanies,
    projectReferenceRoots,
    referenceSnapshotCandidates,
    qaAdminExists = qaPasswordHash is not null,
    qaPasswordVerified = qaPasswordHash is not null && new Pbkdf2PasswordService().Verify(Environment.GetEnvironmentVariable("PDM_ACCEPTANCE_ADMIN_PASSWORD") ?? string.Empty, qaPasswordHash)
}, new JsonSerializerOptions { WriteIndented = true }));

static async Task<long> VerifyFinancialStorageAsync(MySqlConnection connection, IOptions<PdmDatabaseOptions> options)
{
    // Only isolated QA databases may receive this financial fixture.
    if (!connection.Database.StartsWith("pdm_qa_full_", StringComparison.Ordinal))
        throw new InvalidOperationException("财务集成验收只能在独立QA数据库运行。");
    var first = Guid.Parse("11111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    var second = Guid.Parse("22222222-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    var overrideId = Guid.NewGuid();
    var now = DateTime.UtcNow;
    await connection.ExecuteAsync("""
        INSERT INTO material_master(id,material_code,name,material_kind,supply_mode,unit_code,source_bom_item_id,
          approval_status,u9_item_code,sync_status,created_by,created_at,updated_by,updated_at,row_version)
        VALUES(@Id,@Code,'财务映射验收','Standard','Purchase','001',@Source,'Approved',@U9,'NotQueued','qa_admin',@Now,'qa_admin',@Now,1)
        """, new[] {
            new { Id = Guid.NewGuid(), Code = "QA-LEGACY-FIRST", Source = (Guid?)first, U9 = (string?)null, Now = now },
            new { Id = overrideId, Code = "QA-LINKED", Source = (Guid?)null, U9 = (string?)"QA-U9-OVERRIDE", Now = now },
            new { Id = Guid.NewGuid(), Code = "QA-LEGACY-SECOND", Source = (Guid?)second, U9 = (string?)null, Now = now }
        });
    await connection.ExecuteAsync("INSERT INTO bom_material_link(bom_item_id,material_id,linked_by,linked_at) VALUES(@First,@Id,'qa_admin',@Now)", new { First = first, Id = overrideId, Now = now });
    var materials = new MySqlMaterialRepository(options);
    var requested = Enumerable.Range(0, 2000).Select(_ => Guid.NewGuid()).Prepend(first).Append(second).Append(first).ToArray();
    var timer = System.Diagnostics.Stopwatch.StartNew();
    var codes = await materials.FindMaterialCodesBySourceBomItemsAsync(requested, default);
    timer.Stop();
    if (codes.Count != 2 || codes[first] != "QA-U9-OVERRIDE" || codes[second] != "QA-LEGACY-SECOND")
        throw new InvalidOperationException("批量物料编码读取未保持显式关联优先及历史来源回退规则。");
    foreach (var id in new[] { first, second })
    {
        var single = (await materials.FindMaterialBySourceBomItemAsync(id, default))!;
        if (codes[id] != (single.U9ItemCode ?? single.MaterialCode)) throw new InvalidOperationException("批量与单条物料编码读取不一致。");
    }
    var store = new MySqlProjectBudgetRepository(options);
    var projectId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    var departmentId = Guid.NewGuid();
    var original = new ProjectBudget(projectId, [new("Standard", 125.25m, 42.50m, DepartmentId: departmentId)], UpdatedBy: "qa_admin", UpdatedAt: DateTimeOffset.UtcNow,
        AssessmentGroups: [new(Guid.NewGuid(), "部门评估", "Standard", null, [new(Guid.NewGuid(), "验收物料", Quantity: 2, UnitPrice: 5.25m)], departmentId)]);
    await store.SaveAsync(original, 0, default);
    var restored = (await store.FindAsync(projectId, default))!;
    if (restored.Lines.Single() != original.Lines.Single() || restored.AssessmentGroups!.Single().DepartmentId != departmentId)
        throw new InvalidOperationException("费用归属部门或财务金额保存后不一致。");
    try { await store.SaveAsync(original, 0, default); throw new InvalidOperationException("预算过期版本覆盖未被拒绝。"); }
    catch (PdmConflictException) { }
    var expenseStore = new MySqlExpenseRequestRepository(options);
    var expenseId = Guid.NewGuid();
    var pending = new ExpenseRequest(expenseId, projectId, "QA-PHASE1", "费用事务验收", "qa_admin", "SitePurchase", "Procurement",
        new DateOnly(2026, 12, 1), "验收", [new("Material", "QA", "配件", "", "", "个", 2m, 12.35m, "")], "Pending", null,
        [new(Guid.NewGuid(), "部门审批", new ApprovalGroup(["qa_engineer"], false, []))], 0, DateTimeOffset.UtcNow, departmentId);
    var notice = new UserNotification(Guid.NewGuid(), "qa_engineer", "ExpenseApprovalPending", "费用待审批", "请进入费用申请处理。", projectId, null,
        $"expense-request:{expenseId:N}:1", DateTimeOffset.UtcNow, null);
    var pendingSaved = await expenseStore.SaveAsync(pending, 0, [notice], default);
    var restoredExpense = (await expenseStore.FindAsync(expenseId, default))!;
    if (restoredExpense.DepartmentId != departmentId || restoredExpense.TotalAmount != 24.70m)
        throw new InvalidOperationException("费用部门或数量金额持久化不一致。");
    await expenseStore.SaveAsync(pendingSaved with { Status = "Withdrawn" }, pendingSaved.RowVersion, [], default);
    if ((await expenseStore.FindAsync(expenseId, default))!.CurrentStep is not null)
        throw new InvalidOperationException("撤回后仍存在待审批节点。");
    if (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM user_notification WHERE id=@Id AND resolved_at IS NULL", new { notice.Id }) != 0)
        throw new InvalidOperationException("费用撤回没有在同一事务关闭待办。");
    try { await expenseStore.SaveAsync(pendingSaved with { Status = "Approved" }, pendingSaved.RowVersion, [notice], default); throw new InvalidOperationException("费用过期审批覆盖未被拒绝。"); }
    catch (PdmConflictException) { }
    if (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM project_expense_request_version WHERE request_id=@Id", new { Id = expenseId }) != 2)
        throw new InvalidOperationException("费用撤回历史或并发事务回滚不一致。");
    return timer.ElapsedMilliseconds;
}

static async Task SeedAsync(MySqlConnection connection)
{
    var password = Environment.GetEnvironmentVariable("PDM_ACCEPTANCE_ADMIN_PASSWORD")
        ?? throw new InvalidOperationException("PDM_ACCEPTANCE_ADMIN_PASSWORD未设置。");
    var vault = Environment.GetEnvironmentVariable("PDM_ACCEPTANCE_VAULT")
        ?? throw new InvalidOperationException("PDM_ACCEPTANCE_VAULT未设置。");
    var release = Environment.GetEnvironmentVariable("PDM_ACCEPTANCE_RELEASE")
        ?? throw new InvalidOperationException("PDM_ACCEPTANCE_RELEASE未设置。");
    Directory.CreateDirectory(vault);
    Directory.CreateDirectory(release);
    var now = DateTime.UtcNow;
    var projectId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    var documentId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    var snapshotId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    var node = new DocumentReferenceNode(Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"), documentId, "QA-ROOT", "QA-ROOT.SLDASM", "一期自动验收装配", DocumentKind.Assembly, "Default", 1, ReferenceNodeStatus.Normal, RevisionLabel.InitialWork(), null, []);
    var rootJson = JsonSerializer.Serialize(node, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var passwordHash = new Pbkdf2PasswordService().Hash(password);
    var companyId = await connection.QuerySingleAsync<Guid>("SELECT id FROM project_organization WHERE is_active=1 ORDER BY name LIMIT 1");

    await connection.ExecuteAsync("INSERT INTO pdm_user(id,username,display_name,password_hash,role,company_id,is_active,row_version,created_at) VALUES(@Id,'qa_admin','一期验收管理员',@PasswordHash,'Administrator',@CompanyId,1,1,@Now) ON DUPLICATE KEY UPDATE password_hash=@PasswordHash,company_id=@CompanyId,is_active=1", new { Id = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"), PasswordHash = passwordHash, CompanyId = companyId, Now = now });
    await connection.ExecuteAsync("INSERT INTO pdm_user(id,username,display_name,password_hash,role,company_id,is_active,row_version,created_at) VALUES(@Id,'qa_engineer','一期验收工程师',@PasswordHash,'Engineer',@CompanyId,1,1,@Now) ON DUPLICATE KEY UPDATE password_hash=@PasswordHash,company_id=@CompanyId,is_active=1", new { Id = Guid.Parse("ffffffff-eeee-eeee-eeee-eeeeeeeeeeee"), PasswordHash = passwordHash, CompanyId = companyId, Now = now });
    await connection.ExecuteAsync("INSERT INTO project(id,code,name,owner,vault_location,release_location,organization_id,is_active,row_version,created_at,updated_at) VALUES(@Id,'QA-PHASE1','一期自动验收项目','qa_admin',@Vault,@Release,@CompanyId,1,1,@Now,@Now) ON DUPLICATE KEY UPDATE vault_location=@Vault,release_location=@Release,organization_id=@CompanyId,is_active=1,updated_at=@Now", new { Id = projectId, Vault = vault, Release = release, CompanyId = companyId, Now = now });
    await connection.ExecuteAsync("INSERT INTO document(id,project_id,drawing_number,name,file_name,kind,lifecycle_state,revision_label,checked_out_by,checked_out_at,row_version,created_at,updated_at) VALUES(@Id,@ProjectId,'QA-ROOT','一期自动验收装配','QA-ROOT.SLDASM','Assembly','Work','W1',NULL,NULL,1,@Now,@Now) ON DUPLICATE KEY UPDATE updated_at=@Now", new { Id = documentId, ProjectId = projectId, Now = now });
    await connection.ExecuteAsync("INSERT INTO reference_snapshot(id,project_id,root_document_id,captured_at,captured_by,sha256,root_json) VALUES(@Id,@ProjectId,@DocumentId,@Now,'qa_admin',REPEAT('0',64),@RootJson) ON DUPLICATE KEY UPDATE captured_at=@Now,root_json=@RootJson", new { Id = snapshotId, ProjectId = projectId, DocumentId = documentId, Now = now, RootJson = rootJson });
    await connection.ExecuteAsync("INSERT INTO project_reference_root(project_id,reference_snapshot_id,updated_at) VALUES(@ProjectId,@SnapshotId,@Now) ON DUPLICATE KEY UPDATE reference_snapshot_id=@SnapshotId,updated_at=@Now", new { ProjectId = projectId, SnapshotId = snapshotId, Now = now });
    await connection.ExecuteAsync("DELETE FROM bom_item WHERE project_id=@ProjectId", new { ProjectId = projectId });
    await connection.ExecuteAsync("INSERT INTO bom_item(id,project_id,bom_kind,sequence_no,drawing_number,name,quantity,unit,material,specification,revision_label,is_complete,row_version,updated_at) VALUES(@Id,@ProjectId,@Kind,1,@Drawing,@Name,@Quantity,'件',@Material,@Specification,'W1',1,1,@Now)", new[]
    {
        new { Id = Guid.Parse("11111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), ProjectId = projectId, Kind = "Mechanical", Drawing = "QA-M-001", Name = "验收机械件", Quantity = 2m, Material = (string?)"45#", Specification = (string?)null, Now = now },
        new { Id = Guid.Parse("22222222-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), ProjectId = projectId, Kind = "Electrical", Drawing = "QA-E-001", Name = "验收电气件", Quantity = 1m, Material = (string?)null, Specification = (string?)"24VDC", Now = now }
    });
    await connection.ExecuteAsync("INSERT INTO project_user_access(project_id,username,can_read,granted_at) VALUES(@ProjectId,'qa_admin',1,@Now) ON DUPLICATE KEY UPDATE can_read=1", new { ProjectId = projectId, Now = now });
    await connection.ExecuteAsync("INSERT INTO project_user_access(project_id,username,can_read,granted_at) VALUES(@ProjectId,'qa_engineer',1,@Now) ON DUPLICATE KEY UPDATE can_read=1", new { ProjectId = projectId, Now = now });
}

sealed class DatabaseTableCount
{
    public required string TableName { get; init; }
    public long RowCount { get; init; }
}

sealed class ProjectReferenceRootRow
{
    public required string ProjectCode { get; init; }
    public required string RootFileName { get; init; }
    public required string SnapshotRootFileName { get; init; }
    public DateTime CapturedAt { get; init; }
}

sealed class ReferenceSnapshotCandidateRow
{
    public required string ProjectCode { get; init; }
    public required string RootFileName { get; init; }
    public string? RootRevision { get; init; }
    public string? LatestVersion { get; init; }
    public DateTime? LatestVersionAt { get; init; }
    public int VersionCount { get; init; }
    public required string InstancePath { get; init; }
    public int DirectChildCount { get; init; }
    public DateTime CapturedAt { get; init; }
}
