using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Infrastructure;

// Creates a new, isolated QA database; never changes existing project/CAD data.
var builder = new MySqlConnectionStringBuilder(
    Environment.GetEnvironmentVariable("PDM_FOLDER_QA_CONNECTION")
    ?? throw new InvalidOperationException("Set PDM_FOLDER_QA_CONNECTION to a local MySQL admin connection."));
if (builder.Server is not ("127.0.0.1" or "localhost") || builder.Database.Length != 0)
    throw new InvalidOperationException("Use a local connection without a database; a fresh QA database will be created.");
builder.GuidFormat = MySqlGuidFormat.Binary16;
var database = $"pdm_folder_{Guid.NewGuid():N}_qa";
await using (var admin = new MySqlConnection(builder.ConnectionString))
{
    await admin.OpenAsync();
    await admin.ExecuteAsync($"CREATE DATABASE `{database}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci");
}
builder.Database = database;
await using var connection = new MySqlConnection(builder.ConnectionString);
await connection.OpenAsync();
await connection.ExecuteAsync("""
    CREATE TABLE project (
        id BINARY(16) PRIMARY KEY, code VARCHAR(100) NOT NULL,
        parent_project_id BINARY(16) NULL, root_project_id BINARY(16) NULL, child_sequence INT NULL,
        KEY ix_project_root(root_project_id)
    ) ENGINE=InnoDB;
    """);
// Reuse the production folder table DDL (including indexes and foreign keys).
var assembly = typeof(MySqlPdmRepository).Assembly;
var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith("012_project_folder_tree.sql", StringComparison.Ordinal));
using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
var migration = await reader.ReadToEndAsync();
await connection.ExecuteAsync(migration[..migration.IndexOf("CREATE TABLE IF NOT EXISTS project_folder_permission", StringComparison.Ordinal)]);
await connection.ExecuteAsync("""
    CREATE TABLE document (
        id BINARY(16) PRIMARY KEY, project_id BINARY(16) NOT NULL, folder_id BINARY(16) NULL,
        KEY ix_document_project(project_id), KEY ix_document_folder(folder_id),
        FOREIGN KEY(project_id) REFERENCES project(id), FOREIGN KEY(folder_id) REFERENCES project_folder(id)
    ) ENGINE=InnoDB;
    INSERT INTO folder_template_node(folder_key,parent_key,name,purpose,sort_order,is_system,inherit_permissions) VALUES
        ('mechanical',NULL,'机械图纸','MechanicalRoot',10,1,1),
        ('electrical',NULL,'电气图纸','ElectricalRoot',20,1,1),
        ('mechanical.project','mechanical','项目','ProjectContainer',100,1,1),
        ('electrical.project','electrical','项目','ProjectContainer',110,1,1);
    """);
var repository = new MySqlPdmRepository(Options.Create(new PdmDatabaseOptions { ConnectionString = builder.ConnectionString }), TimeProvider.System);
var failures = new List<string>();
var rootA = await AddProject("QA-A");
var rootB = await AddProject("QA-B");
await repository.EnsureProjectFolderTreeAsync(rootA, CancellationToken.None);
await repository.EnsureProjectFolderTreeAsync(rootB, CancellationToken.None);

await Test("Unchanged folders keep IDs and timestamps", async () =>
{
    var ids = (await connection.QueryAsync<Guid>("SELECT id FROM project_folder")).ToHashSet();
    await connection.ExecuteAsync("UPDATE project_folder SET updated_at='2000-01-01'");
    await repository.EnsureProjectFolderTreeAsync(rootA, CancellationToken.None);
    Assert(ids.SetEquals(await connection.QueryAsync<Guid>("SELECT id FROM project_folder")), "Folder IDs changed.");
    Assert(await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM project_folder WHERE updated_at<>'2000-01-01'") == 0, "Unchanged folders were rewritten.");
});
await Test("Legacy backfill only touches the requested root", async () =>
{
    await connection.ExecuteAsync("INSERT INTO document(id,project_id) VALUES(@A,@RootA),(@B,@RootB)", new { A = Guid.NewGuid(), B = Guid.NewGuid(), RootA = rootA, RootB = rootB });
    await repository.EnsureProjectFolderTreeAsync(rootA, CancellationToken.None);
    Assert(await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document WHERE project_id=@RootA AND folder_id IS NOT NULL", new { RootA = rootA }) == 1, "Requested root was not backfilled.");
    Assert(await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document WHERE project_id=@RootB AND folder_id IS NULL", new { RootB = rootB }) == 1, "Another project was changed.");
});
await Test("New children and renamed projects update their existing folder tree", async () =>
{
    var child = await AddProject("QA-A-1", rootA);
    await connection.ExecuteAsync("UPDATE project SET code='QA-A-renamed' WHERE id=@Id", new { Id = rootA });
    await repository.EnsureProjectFolderTreeAsync(child, CancellationToken.None);
    Assert(await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM project_folder WHERE target_project_id=@Child", new { Child = child }) == 2, "Child containers missing.");
    Assert(await connection.ExecuteScalarAsync<string>("SELECT name FROM project_folder WHERE root_project_id=@RootA AND folder_key='root'", new { RootA = rootA }) == "QA-A-renamed", "Root name was not updated.");
});
await Test("Concurrent same-root and cross-root initialization", async () =>
{
    var roots = new List<Guid>();
    for (var i = 0; i < 8; i++) roots.Add(await AddProject($"QA-parallel-{i}"));
    var stopwatch = Stopwatch.StartNew();
    // 160 competing transactions, including simultaneous first initialization.
    var tasks = Enumerable.Range(0, 16).Select(async worker =>
    {
        for (var iteration = 0; iteration < 10; iteration++)
            await repository.EnsureProjectFolderTreeAsync(roots[(worker + iteration) % roots.Count], CancellationToken.None);
    });
    await Task.WhenAll(tasks);
    foreach (var root in roots)
        Assert(await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM project_folder WHERE root_project_id=@Root", new { Root = root }) == 5, "Missing or duplicate folders.");
    Console.WriteLine($"  160 transactions: {stopwatch.ElapsedMilliseconds} ms");
});

// Nontransactional counters deliberately survive rollback, so faults happen a bounded number of times.
await connection.ExecuteAsync("""
    CREATE TABLE fault_control(root_id BINARY(16) PRIMARY KEY,failures INT,attempts INT,error_number INT) ENGINE=MyISAM;
    CREATE TRIGGER qa_folder_fault BEFORE INSERT ON project_folder FOR EACH ROW
    BEGIN
        DECLARE fault INT DEFAULT 0;
        IF NEW.folder_key='mechanical' THEN
            UPDATE fault_control SET attempts=attempts+1 WHERE root_id=NEW.root_project_id;
            SELECT COALESCE(MAX(IF(attempts<=failures,error_number,0)),0) INTO fault
                FROM fault_control WHERE root_id=NEW.root_project_id;
            IF fault<>0 THEN
                SIGNAL SQLSTATE '45000' SET MYSQL_ERRNO=fault,MESSAGE_TEXT='QA injected folder error';
            END IF;
        END IF;
    END
    """);
foreach (var code in new[] { 1213, 1205 })
{
    await Test($"Retry and rollback transient MySQL {code}", async () =>
    {
        var root = await AddFault(code, 2);
        await repository.EnsureProjectFolderTreeAsync(root, CancellationToken.None);
        Assert(await Attempts(root) == 3, "Expected three attempts.");
        Assert(await FolderCount(root) == 5, "Partial attempt was not rolled back.");
    });
}
await Test("Persistent lock conflicts stop after three attempts", async () =>
{
    var root = await AddFault(1213, 99);
    await ExpectMySql(1213, () => repository.EnsureProjectFolderTreeAsync(root, CancellationToken.None));
    Assert(await Attempts(root) == 3 && await FolderCount(root) == 0, "Retry limit or rollback failed.");
});
await Test("Non-transient errors are not retried", async () =>
{
    var root = await AddFault(1644, 99);
    await ExpectMySql(1644, () => repository.EnsureProjectFolderTreeAsync(root, CancellationToken.None));
    Assert(await Attempts(root) == 1 && await FolderCount(root) == 0, "Unexpected retry or partial folder committed.");
});
await Test("Cancellation interrupts retry backoff", async () =>
{
    var root = await AddFault(1213, 99);
    using var cancellation = new CancellationTokenSource();
    var pending = repository.EnsureProjectFolderTreeAsync(root, cancellation.Token);
    while (!pending.IsCompleted && await Attempts(root) == 0) await Task.Delay(5);
    cancellation.Cancel();
    try { await pending; throw new InvalidOperationException("Cancellation ignored."); }
    catch (OperationCanceledException) { }
    Assert(await Attempts(root) == 1 && await FolderCount(root) == 0, "Cancellation retried or left partial data.");
});
Console.WriteLine($"QA database retained: {database}; failures: {failures.Count}");
return failures.Count == 0 ? 0 : 1;

async Task<Guid> AddProject(string code, Guid? root = null)
{
    var id = Guid.NewGuid();
    await connection.ExecuteAsync("INSERT INTO project(id,code,parent_project_id,root_project_id,child_sequence) VALUES(@Id,@Code,@Root,@Root,@Sequence)",
        new { Id = id, Code = code, Root = root, Sequence = root.HasValue ? (int?)1 : null });
    return id;
}
async Task<Guid> AddFault(int error, int count)
{
    var root = await AddProject($"QA-fault-{error}-{Guid.NewGuid():N}");
    await connection.ExecuteAsync("INSERT INTO fault_control VALUES(@Root,@Count,0,@Error)", new { Root = root, Count = count, Error = error });
    return root;
}
Task<int> Attempts(Guid root) => connection.ExecuteScalarAsync<int>("SELECT attempts FROM fault_control WHERE root_id=@Root", new { Root = root });
Task<int> FolderCount(Guid root) => connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM project_folder WHERE root_project_id=@Root", new { Root = root });
async Task Test(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception exception) { failures.Add(name); Console.WriteLine($"FAIL {name}: {exception.GetType().Name}: {exception.Message}"); }
}
static async Task ExpectMySql(int number, Func<Task> operation)
{
    try { await operation(); throw new InvalidOperationException("Expected MySQL error."); }
    catch (MySqlException exception) when (exception.Number == number) { }
}
static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
