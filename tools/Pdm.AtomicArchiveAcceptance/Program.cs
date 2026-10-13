using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

var root = Path.GetFullPath(args[0]);
using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".local/api/appsettings.json")));
using var secrets = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".local/secrets/pdm-secrets.json")).TrimStart('\uFEFF'));
var builder = new MySqlConnectionStringBuilder(settings.RootElement.GetProperty("ConnectionStrings").GetProperty("Pdm").GetString() ?? throw new Exception("数据库测试配置缺失"));
builder.UserID = "root"; builder.Password = secrets.RootElement.GetProperty("mysqlRootPassword").GetString();
var reuseIndex = Array.IndexOf(args, "--reuse");
var database = reuseIndex >= 0 ? args[reuseIndex + 1] : "pdm_atomic_qa_" + Guid.NewGuid().ToString("N"); builder.Database = "";
if (!database.StartsWith("pdm_atomic_qa_", StringComparison.Ordinal) || database.Length != 46) throw new Exception("Unexpected fixture database");
await using var admin = new MySqlConnection(builder.ConnectionString); await admin.OpenAsync();
if (args.Contains("--status"))
{
    foreach (var row in await admin.QueryAsync("SHOW PROCESSLIST"))
        if (((string?)row.db)?.StartsWith("pdm_atomic_qa_", StringComparison.Ordinal) == true || ((string?)row.Info)?.Contains("pdm_atomic_qa_", StringComparison.Ordinal) == true)
            Console.WriteLine(JsonSerializer.Serialize(new { row.Id, row.db, row.Time, row.State, row.Command }));
    foreach (var name in await admin.QueryAsync<string>("SHOW DATABASES LIKE 'pdm_atomic_qa_%'"))
    {
        Console.WriteLine("Fixture database: " + name);
        if (name.StartsWith("pdm_atomic_qa_", StringComparison.Ordinal) && name.Length == 46)
            Console.WriteLine("Latest fixture migration: " + await admin.QuerySingleOrDefaultAsync<string>($"SELECT version FROM `{name}`.pdm_schema_migration ORDER BY version DESC LIMIT 1"));
    }
    return;
}
if (reuseIndex < 0) await admin.ExecuteAsync($"CREATE DATABASE `{database}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci");
Console.WriteLine("Created isolated MySQL fixture " + database);
var passed = false;
try
{
    builder.Database = database;
    var options = Options.Create(new PdmDatabaseOptions { Provider = "MySql", ConnectionString = builder.ConnectionString, RunMigrations = true });
    await new MySqlMigrationRunner(options, NullLogger<MySqlMigrationRunner>.Instance, TimeProvider.System).RunAsync(default);
    Console.WriteLine("Fixture migrations completed");
    var repo = new MySqlPdmRepository(options, TimeProvider.System);
    var directory = await repo.GetOrganizationDirectoryAsync(default);
    if (!directory.Users.Any(user => user.Username == "qa"))
        await repo.CreateUserAsync(new UserAccount(Guid.NewGuid(), "qa", "事务隔离测试", "unused", UserRole.Administrator, true,
            CompanyId: directory.Organizations.First().Id), default);
    foreach (var failure in new[] { "permission", "missing-reference", "base-version", "success" })
    {
        Console.WriteLine("Running fixture " + failure);
        var project = await repo.CreateProjectAsync(new("QA-ATOMIC-" + Guid.NewGuid().ToString("N"), "整批事务测试", "qa", "vault", "release"), "qa", default);
        var session = Guid.NewGuid();
        var part = await Register(repo, project, "PART", DocumentKind.Part, session);
        var assembly = await Register(repo, project, "ROOT", DocumentKind.Assembly, failure == "permission" ? Guid.NewGuid() : session);
        var partCommit = Commit(project, part, Node(part), 'A');
        var child = Node(part) with { InstancePath = "ROOT/PART-1", Configuration = "CFG-A" };
        if (failure == "missing-reference") child = child with { DocumentId = null };
        var rootCommit = Commit(project, assembly, Node(assembly) with { Children = [child] }, 'B', true);
        if (failure == "base-version") rootCommit = rootCommit with { ExpectedVersionId = Guid.NewGuid() };
        var batch = Guid.NewGuid();
        ArchiveBatchFile[] files = [new(part.Id, partCommit), new(assembly.Id, rootCommit)];
        try
        {
            var receipt = await repo.CheckInBatchVersionsAsync(batch, "request", "qa", session, files, default);
            if (failure != "success") throw new Exception("Expected rollback " + failure);
            Require((await repo.ListDocumentVersionsAsync(part.Id, default)).Count == 1, "part not committed");
            Require((await repo.ListDocumentVersionsAsync(assembly.Id, default)).Count == 1, "root not committed");
            var replay = await repo.CheckInBatchVersionsAsync(batch, "request", "qa", session, files, default);
            Require(replay.Results[0].Version!.Id == receipt.Results[0].Version!.Id, "replay created another version");
            Require(await repo.FindArchiveBatchAsync(batch, default) != null, "committed receipt missing");
        }
        catch (Exception error) when (failure != "success" && error is PdmConflictException or PdmRuleException)
        {
            Require((await repo.ListDocumentVersionsAsync(part.Id, default)).Count == 0, "child version leaked");
            Require((await repo.ListDocumentVersionsAsync(assembly.Id, default)).Count == 0, "root version leaked");
            Require((await repo.FindDocumentAsync(part.Id, default))!.CheckoutSessionId == session, "edit lock was released");
            Require(await repo.GetLatestReferenceSnapshotAsync(project.Id, default) == null, "partial structure leaked");
            Require(await repo.FindArchiveBatchAsync(batch, default) == null, "failed receipt committed");
        }
        Console.WriteLine("PASS MySQL transaction " + failure);
    }
    passed = true;
}
catch (Exception failure) { Console.WriteLine("Fixture failure: " + failure.GetType().Name + ": " + failure.Message); throw; }
finally
{
    if (!database.StartsWith("pdm_atomic_qa_", StringComparison.Ordinal) || database.Length != 46) throw new Exception("Unexpected fixture database");
    if (passed)
    {
        builder.Database = "";
        await using var cleanup = new MySqlConnection(builder.ConnectionString);
        await cleanup.OpenAsync();
        await cleanup.ExecuteAsync($"DROP DATABASE `{database}`", commandTimeout: 180);
        Console.WriteLine("Removed isolated MySQL fixture");
    }
    else Console.WriteLine("Retained failed isolated fixture for diagnosis: " + database);
}
static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task<PdmDocument> Register(IPdmRepository repository, Project project, string name, DocumentKind kind, Guid session)
{
    var doc = await repository.RegisterDocumentAsync(new(project.Id, name, name, name + (kind == DocumentKind.Assembly ? ".SLDASM" : ".SLDPRT"), kind), "qa", default);
    return await repository.CheckoutAsync(doc.Id, "qa", session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
}
static DocumentReferenceNode Node(PdmDocument doc) => new(Guid.NewGuid(), doc.Id, doc.DrawingNumber, doc.FileName, doc.Name,
    doc.Kind, "Default", 1, ReferenceNodeStatus.Normal, doc.Revision, "qa", []);
static DocumentVersionCommit Commit(Project project, PdmDocument doc, DocumentReferenceNode tree, char hash, bool root = false) => new(
    new StoredFile(".versions/" + Guid.NewGuid().ToString("N"), 128, new string(hash, 64), DateTimeOffset.UtcNow), "整批测试",
    new Dictionary<string, string?>(), new CadReferenceSnapshot(Guid.NewGuid(), project.Id, doc.Id, DateTimeOffset.UtcNow, "qa", tree, new string('F', 64)), [], [], IsProjectRoot: root)
    { RequireBaseVersion = true };
