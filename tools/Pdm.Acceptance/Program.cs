using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

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
if (string.Equals(Environment.GetEnvironmentVariable("PDM_ACCEPTANCE_SEED"), "1", StringComparison.Ordinal))
{
    await SeedAsync(connection);
}
var migrations = (await connection.QueryAsync<string>("SELECT version FROM pdm_schema_migration ORDER BY version")).ToArray();
var releaseColumns = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.columns WHERE table_schema=DATABASE() AND table_name='release_package' AND column_name IN ('mechanical_bom_snapshot_json','electrical_bom_snapshot_json','publish_error')");
var tableCountRows = await connection.QueryAsync<DatabaseTableCount>("SELECT 'project' AS TableName,COUNT(*) AS RowCount FROM project UNION ALL SELECT 'document',COUNT(*) FROM document UNION ALL SELECT 'document_version',COUNT(*) FROM document_version UNION ALL SELECT 'reference_snapshot',COUNT(*) FROM reference_snapshot UNION ALL SELECT 'bom_item',COUNT(*) FROM bom_item UNION ALL SELECT 'release_package',COUNT(*) FROM release_package UNION ALL SELECT 'audit_entry',COUNT(*) FROM audit_entry");
var tableCounts = tableCountRows.ToDictionary(row => row.TableName, row => row.RowCount, StringComparer.Ordinal);
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
    migrations,
    expectedMigrationApplied = migrations.Contains("004_phase1_bom_release_workflow", StringComparer.Ordinal),
    releaseColumns,
    tableCounts,
    projectReferenceRoots,
    referenceSnapshotCandidates,
    qaAdminExists = qaPasswordHash is not null,
    qaPasswordVerified = qaPasswordHash is not null && new Pbkdf2PasswordService().Verify(Environment.GetEnvironmentVariable("PDM_ACCEPTANCE_ADMIN_PASSWORD") ?? string.Empty, qaPasswordHash)
}, new JsonSerializerOptions { WriteIndented = true }));

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

    await connection.ExecuteAsync("INSERT INTO pdm_user(id,username,display_name,password_hash,role,is_active,row_version,created_at) VALUES(@Id,'qa_admin','一期验收管理员',@PasswordHash,'Administrator',1,1,@Now) ON DUPLICATE KEY UPDATE password_hash=@PasswordHash,is_active=1", new { Id = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"), PasswordHash = passwordHash, Now = now });
    await connection.ExecuteAsync("INSERT INTO pdm_user(id,username,display_name,password_hash,role,is_active,row_version,created_at) VALUES(@Id,'qa_engineer','一期验收工程师',@PasswordHash,'Engineer',1,1,@Now) ON DUPLICATE KEY UPDATE password_hash=@PasswordHash,is_active=1", new { Id = Guid.Parse("ffffffff-eeee-eeee-eeee-eeeeeeeeeeee"), PasswordHash = passwordHash, Now = now });
    await connection.ExecuteAsync("INSERT INTO project(id,code,name,owner,vault_location,release_location,is_active,row_version,created_at,updated_at) VALUES(@Id,'QA-PHASE1','一期自动验收项目','qa_admin',@Vault,@Release,1,1,@Now,@Now) ON DUPLICATE KEY UPDATE vault_location=@Vault,release_location=@Release,is_active=1,updated_at=@Now", new { Id = projectId, Vault = vault, Release = release, Now = now });
    await connection.ExecuteAsync("INSERT INTO document(id,project_id,drawing_number,name,file_name,kind,lifecycle_state,revision_label,checked_out_by,checked_out_at,row_version,created_at,updated_at) VALUES(@Id,@ProjectId,'QA-ROOT','一期自动验收装配','QA-ROOT.SLDASM','Assembly','Work','W1',NULL,NULL,1,@Now,@Now) ON DUPLICATE KEY UPDATE updated_at=@Now", new { Id = documentId, ProjectId = projectId, Now = now });
    await connection.ExecuteAsync("INSERT INTO reference_snapshot(id,project_id,root_document_id,captured_at,captured_by,sha256,root_json) VALUES(@Id,@ProjectId,@DocumentId,@Now,'qa_admin',REPEAT('0',64),@RootJson) ON DUPLICATE KEY UPDATE captured_at=@Now,root_json=@RootJson", new { Id = snapshotId, ProjectId = projectId, DocumentId = documentId, Now = now, RootJson = rootJson });
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
