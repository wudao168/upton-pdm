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
builder.GuidFormat = MySqlGuidFormat.Binary16;
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
    if (args.Contains("--receipt-migration-only"))
    {
        await VerifyReceiptMigration(builder.ConnectionString);
        passed = true;
        return;
    }
    var options = Options.Create(new PdmDatabaseOptions { Provider = "MySql", ConnectionString = builder.ConnectionString, RunMigrations = true });
    await new MySqlMigrationRunner(options, NullLogger<MySqlMigrationRunner>.Instance, TimeProvider.System).RunAsync(default);
    Console.WriteLine("Fixture migrations completed");
    var repo = new MySqlPdmRepository(options, TimeProvider.System);
    var directory = await repo.GetOrganizationDirectoryAsync(default);
    if (!directory.Users.Any(user => user.Username == "qa"))
        await repo.CreateUserAsync(new UserAccount(Guid.NewGuid(), "qa", "事务隔离测试", "unused", UserRole.Administrator, true,
            CompanyId: directory.Organizations.First().Id), default);
    foreach (var failure in new[] { "missing-reference", "base-version", "drawing-failure", "omitted-file", "foreign-session", "cancel", "success" })
    {
        var project = await repo.CreateProjectAsync(new("QA-PENDING-" + Guid.NewGuid().ToString("N"), "登记回滚测试", "qa", "vault", "release"), "qa", default);
        await repo.EnsureProjectFolderTreeAsync(project.Id, default);
        var folder = (await repo.ListProjectFoldersAsync(project.Id, "qa", UserRole.Administrator, default)).First(f => f.TemplateKey == "mechanical.project");
        var session = Guid.NewGuid(); var batch = Guid.NewGuid(); var context = new ArchiveRegistrationContext(batch, session, "QA");
        var rootCommand = new RegisterDocumentCommand(project.Id, "ROOT", "总装", "ROOT.SLDASM", DocumentKind.Assembly, folder.Id, SourceSha256: new string('A',64));
        var rootDocument = await repo.ReserveArchiveDocumentAsync(rootCommand, "qa", context, default);
        var drawingCommand = new RegisterDocumentCommand(project.Id, "DRAWING", "工程图", "DRAWING.SLDDRW", DocumentKind.Drawing, folder.Id, rootDocument.Id, new string('B',64));
        var drawing = await repo.ReserveArchiveDocumentAsync(drawingCommand, "qa", context, default);
        Require((await repo.ReserveArchiveDocumentAsync(rootCommand, "qa", context, default)).Id == rootDocument.Id, "reserve replay changed ID");
        Require((await repo.ListDocumentsAsync(project.Id, default)).Count == 0, "preflight leaked official registrations");
        Require((await repo.ListDocumentRelationsAsync(project.Id, default)).Count == 0, "preflight leaked drawing relations");
        Require(await repo.HasDocumentAccessAsync(drawing.Id,"qa",UserRole.Administrator,FolderAccess.View|FolderAccess.Edit,default), "pending preflight permissions failed");
        var rootTree = Node(rootDocument);
        if (failure == "missing-reference") rootTree = rootTree with { Children = [Node(drawing) with { Kind = DocumentKind.Part, DocumentId = null }] };
        var rootCommit = Commit(project, rootDocument, rootTree, 'C', true);
        if (failure == "base-version") rootCommit = rootCommit with { ExpectedVersionId = Guid.NewGuid() };
        ArchiveBatchFile[] files = [new(rootDocument.Id, rootCommit), new(drawing.Id, Commit(project,drawing,Node(drawing) with { Children = [Node(rootDocument)] }, 'D'))];
        if (failure == "drawing-failure") files[1] = files[1] with { Commit = files[1].Commit with { ExpectedVersionId = Guid.NewGuid() } };
        if (failure == "omitted-file") files = files.Take(1).ToArray();
        if (failure == "cancel") await repo.CancelArchiveRegistrationAsync(batch,"qa",session,default);
        try
        {
            var receipt = await repo.CheckInBatchVersionsAsync(batch, "pending-request", "qa", failure == "foreign-session" ? Guid.NewGuid() : session, files, default);
            Require(failure == "success", "expected registration transaction failure");
            Require((await repo.ListDocumentsAsync(project.Id, default)).Count == 2, "successful batch registrations missing");
            Require((await repo.ListDocumentRelationsAsync(project.Id, default)).Count == 1, "successful drawing relation missing");
            Require((await repo.ListDocumentVersionsAsync(drawing.Id,default)).Count == 1, "successful drawing version missing");
            var replay = await repo.CheckInBatchVersionsAsync(batch,"pending-request","qa",session,files,default);
            Require(replay.Results[0].Version!.Id == receipt.Results[0].Version!.Id,"pending replay duplicated versions");
            await repo.CancelArchiveRegistrationAsync(batch,"qa",session,default);
            Require((await repo.ListDocumentsAsync(project.Id, default)).Count == 2,"cancel after commit removed official documents");
        }
        catch (Exception error) when (failure != "success" && error is PdmConflictException or PdmRuleException or PdmNotFoundException)
        {
            Require((await repo.ListDocumentsAsync(project.Id,default)).Count == 0,"failed batch leaked official registration");
            Require((await repo.ListDocumentRelationsAsync(project.Id,default)).Count == 0,"failed batch leaked relations");
            Require((await repo.ListDocumentVersionsAsync(rootDocument.Id,default)).Count == 0,"failed batch leaked root version");
            Require(await repo.GetLatestReferenceSnapshotAsync(project.Id,default) is null,"failed batch leaked root structure");
            await repo.CancelArchiveRegistrationAsync(batch,"qa",session,default);
            Require(await repo.FindDocumentAsync(drawing.Id,default) is null,"cancel did not remove pending reservation");
        }
        Console.WriteLine("PASS MySQL pending registration " + failure);
    }
    foreach (var failure in new[] { "permission", "missing-reference", "base-version", "success" })
    {
        Console.WriteLine("Running fixture " + failure);
        var project = await repo.CreateProjectAsync(new("QA-ATOMIC-" + Guid.NewGuid().ToString("N"), "整批事务测试", "qa", "vault", "release"), "qa", default);
        var session = Guid.NewGuid();
        var part = await Register(repo, project, "PART", DocumentKind.Part, session);
        var assembly = await Register(repo, project, "ROOT", DocumentKind.Assembly, failure == "permission" ? Guid.NewGuid() : session);
        var partCommit = Commit(project, part, Node(part), 'A');
        var child = Node(part) with { InstancePath = "ROOT/PART-1", Configuration = "CFG-A" };
        if (failure == "success") child = child with { Role = ReferenceRole.ExternalFileDependency };
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
            Require((await repo.ListDocumentVersionsAsync(assembly.Id, default)).Single().ReferenceSnapshot.Children.Single().Role
                == ReferenceRole.ExternalFileDependency, "native dependency role was not preserved in MySQL");
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
    foreach (var failure in new[] { "none", "last-drawing", "cancelled" })
    {
        var project = await repo.CreateProjectAsync(new("QA-MULTI-" + Guid.NewGuid().ToString("N"), "多模型关联原子存档", "qa", "vault", "release"), "qa", default);
        await repo.EnsureProjectFolderTreeAsync(project.Id, default);
        var folder = (await repo.ListProjectFoldersAsync(project.Id, "qa", UserRole.Administrator, default)).First(f => f.TemplateKey == "mechanical.project");
        var session = Guid.NewGuid(); var batch = Guid.NewGuid(); var context = new ArchiveRegistrationContext(batch, session, "QA");
        async Task<PdmDocument> Reserve(string name, DocumentKind kind) => await repo.ReserveArchiveDocumentAsync(new(project.Id, name, name,
            name + (kind == DocumentKind.Part ? ".SLDPRT" : ".SLDDRW"), kind, folder.Id, SourceSha256: new string('A',64)), "qa", context, default);
        var first=await Reserve("FIRST",DocumentKind.Part);var second=await Reserve("SECOND",DocumentKind.Part);
        var drawing=await Reserve("MULTI",DocumentKind.Drawing);var last=await Reserve("LAST",DocumentKind.Drawing);
        DocumentReferenceNode MultiNode(PdmDocument d) => new(Guid.NewGuid(),d.Id,d.FileName,d.FileName,d.Name,d.Kind,"默认",1,ReferenceNodeStatus.Normal,d.Revision,null,[]);
        DocumentVersionCommit MultiCommit(PdmDocument d, DocumentReferenceNode root) => new(new("vault/"+Guid.NewGuid(),8,new string('C',64),DateTimeOffset.UtcNow),"关联测试",new Dictionary<string,string?>(),
            new(Guid.NewGuid(),project.Id,d.Id,DateTimeOffset.UtcNow,"qa",root,new string('D',64)),[],[]){RequireBaseVersion=true};
        var lastCommit=MultiCommit(last,MultiNode(last) with {Children=[MultiNode(first)]});
        if(failure=="last-drawing")lastCommit=lastCommit with {ExpectedVersionId=Guid.NewGuid()};
        ArchiveBatchFile[] files=[new(first.Id,MultiCommit(first,MultiNode(first))),new(second.Id,MultiCommit(second,MultiNode(second))),
            new(drawing.Id,MultiCommit(drawing,MultiNode(drawing) with {Children=[MultiNode(first),MultiNode(second) with {Configuration="D14"},MultiNode(second) with {Configuration="D15"}]})),new(last.Id,lastCommit)];
        using var cancelled=new CancellationTokenSource();if(failure=="cancelled")cancelled.Cancel();
        try
        {
            await repo.CheckInBatchVersionsAsync(batch,"multi","qa",session,files,cancelled.Token);
            Require(failure=="none","expected multi-model batch failure");
            Require((await repo.ListDocumentRelationsAsync(project.Id,default)).Count(r=>r.DrawingDocumentId==drawing.Id)==2,"lost drawing model relation");
            Require((await repo.ListDocumentVersionsAsync(drawing.Id,default)).Single().ReferenceSnapshot.Children.Count==3,"lost configuration reference");
        }
        catch(Exception error) when (failure!="none" && error is PdmConflictException or OperationCanceledException)
        {
            Require((await repo.ListDocumentsAsync(project.Id,default)).Count==0,"multi-model failure leaked documents");
            Require((await repo.ListDocumentRelationsAsync(project.Id,default)).Count==0,"multi-model failure leaked relations");
            Require(await repo.FindArchiveBatchAsync(batch,default)==null,"failed batch left successful receipt");
            await repo.CancelArchiveRegistrationAsync(batch,"qa",session,default);
        }
        Console.WriteLine("PASS MySQL multi-model drawing "+failure);
    }
    await ArchiveSafetyChecks.RunAsync(repo, builder.ConnectionString, root);
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
static async Task VerifyReceiptMigration(string connectionString)
{
    await using var connection = new MySqlConnection(connectionString);
    await connection.OpenAsync();
    var batch = Guid.Parse("f6451359-6c02-470c-8d11-8109c69fb77d");
    var project = Guid.NewGuid();
    const string receiptJson = "{\"message\":\"保留旧回执\"}";
    await connection.ExecuteAsync("""
        DROP TABLE IF EXISTS archive_batch_receipt;
        CREATE TABLE archive_batch_receipt (
            batch_id CHAR(36) NOT NULL PRIMARY KEY, project_id CHAR(36) NOT NULL,
            actor VARCHAR(120) NOT NULL, request_hash CHAR(64) NOT NULL,
            result_json LONGTEXT NULL, committed_at DATETIME(6) NULL
        ) CHARACTER SET utf8mb4;
        INSERT INTO archive_batch_receipt VALUES(@Batch,@Project,'qa','legacy',@Json,UTC_TIMESTAMP(6));
        """, new { Batch = batch.ToString(), Project = project.ToString(), Json = receiptJson });
    try
    {
        await using var transaction = await connection.BeginTransactionAsync();
        var newLegacyBatch = Guid.Parse("f7451359-6c02-470c-8d11-8109c69fb77d");
        await connection.ExecuteAsync("INSERT IGNORE INTO archive_batch_receipt(batch_id,project_id,actor,request_hash) VALUES(@Batch,@Project,'qa','new')", new { Batch = newLegacyBatch, Project = project }, transaction);
        await connection.QuerySingleAsync<string?>("SELECT result_json FROM archive_batch_receipt WHERE batch_id=@Batch FOR UPDATE", new { Batch = newLegacyBatch }, transaction);
        throw new Exception("Legacy schema should reproduce Binary16 conversion failure");
    }
    catch (MySqlException exception) when (exception.Number == 3854)
    {
        Console.WriteLine("PASS reproduced legacy receipt schema conversion failure");
    }
    var assembly = typeof(MySqlMigrationRunner).Assembly;
    var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith(".157_archive_batch_receipt_binary_ids.sql", StringComparison.Ordinal));
    using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
    var sql = await reader.ReadToEndAsync();
    await connection.ExecuteAsync(sql);
    Require(await connection.QuerySingleAsync<string>("SELECT result_json FROM archive_batch_receipt WHERE batch_id=@Batch AND project_id=@Project", new { Batch = batch, Project = project }) == receiptJson,
        "Migration changed legacy receipt or identifiers");
    Console.WriteLine("PASS legacy text GUID receipt preserved and readable through Binary16 parameters");
    var newBatch = Guid.NewGuid();
    await connection.ExecuteAsync("INSERT INTO archive_batch_receipt VALUES(@Batch,@Project,'qa','new',NULL,NULL)", new { Batch = newBatch, Project = project });
    await connection.ExecuteAsync(sql);
    Require(await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM archive_batch_receipt WHERE project_id=@Project", new { Project = project }) == 2,
        "Migration changed existing Binary16 identifiers");
    await using (var transaction = await connection.BeginTransactionAsync())
    {
        var rolledBack = Guid.NewGuid();
        await connection.ExecuteAsync("INSERT INTO archive_batch_receipt VALUES(@Batch,@Project,'qa','rollback',NULL,NULL)", new { Batch = rolledBack, Project = project }, transaction);
        await connection.QuerySingleOrDefaultAsync<string>("SELECT result_json FROM archive_batch_receipt WHERE batch_id=@Batch FOR UPDATE", new { Batch = rolledBack }, transaction);
        await transaction.RollbackAsync();
        Require(await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM archive_batch_receipt WHERE batch_id=@Batch", new { Batch = rolledBack }) == 0, "Failed batch receipt leaked");
    }
    Console.WriteLine("PASS binary schema replay safety and failed transaction rollback");
}
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
