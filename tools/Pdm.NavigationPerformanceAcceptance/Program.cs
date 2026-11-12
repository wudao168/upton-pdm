using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

// 全部写入仅发生在随机命名的隔离库；50个逻辑用户共用8个连接，以暴露连接占满后互相等待的问题。
var root = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".local/api/appsettings.json")));
using var secrets = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".local/secrets/pdm-secrets.json")));
var settings = new MySqlConnectionStringBuilder(config.RootElement.GetProperty("ConnectionStrings").GetProperty("Pdm").GetString() ?? throw new Exception("缺少数据库配置。"))
{ UserID = "root", Password = secrets.RootElement.GetProperty("mysqlRootPassword").GetString(), MaximumPoolSize = 8, MinimumPoolSize = 0 };
var production = settings.Database;
var serverArgument = Array.IndexOf(args, "--isolated-server");
if (serverArgument >= 0)
{
    settings.Server = "127.0.0.1"; settings.Port = uint.Parse(args[serverArgument + 1]); settings.Password = "";
    if (settings.Port == new MySqlConnectionStringBuilder(config.RootElement.GetProperty("ConnectionStrings").GetProperty("Pdm").GetString()!).Port) throw new Exception("独立实例端口不能与业务数据库相同。");
    settings.DefaultCommandTimeout = 180;
}
if (args.Contains("--read-existing", StringComparer.Ordinal) || args.Contains("--migrate-existing", StringComparer.Ordinal))
{
    settings.UserID = new MySqlConnectionStringBuilder(config.RootElement.GetProperty("ConnectionStrings").GetProperty("Pdm").GetString()!).UserID;
    settings.Password = secrets.RootElement.GetProperty("databasePassword").GetString();
    if (args.Contains("--migrate-existing", StringComparer.Ordinal))
        await new MySqlMigrationRunner(Options.Create(new PdmDatabaseOptions { Provider = "MySql", ConnectionString = settings.ConnectionString }), NullLogger<MySqlMigrationRunner>.Instance, TimeProvider.System).RunAsync(default);
    IPdmRepository readOnly = new MySqlPdmRepository(Options.Create(new PdmDatabaseOptions { Provider = "MySql", ConnectionString = settings.ConnectionString }), TimeProvider.System);
    var watch = Stopwatch.StartNew();
    var projects = await readOnly.ListProjectsAsync(default);
    var firstMs = watch.ElapsedMilliseconds;
    var selected = projects.Single(item => item.Code == "P700016-2");
    var all = await readOnly.ListProjectTreeDocumentsAsync(selected.Id, default);
    var current = await readOnly.ListDocumentsAsync(selected.Id, default);
    var reference = await readOnly.GetReferenceTreeAsync(selected.Id, default);
    var ids = current.Select(item => item.Id).ToHashSet();
    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var pending = new Stack<DocumentReferenceNode>();
    if (reference is not null) pending.Push(reference);
    while (pending.TryPop(out var item)) { if (item.DocumentId.HasValue) ids.Add(item.DocumentId.Value); names.Add(item.FileName.Trim().Replace('\\', '/').Split('/')[^1]); foreach (var child in item.Children) pending.Push(child); }
    watch.Restart();
    var index = await readOnly.ListWorkspaceDocumentsAsync(selected.Id, ids, names, default);
    var indexMs = watch.ElapsedMilliseconds;
    var expected = all.Where(item => ids.Contains(item.Id) || names.Contains(item.FileName.Trim().Replace('\\', '/').Split('/')[^1])).Select(item => item.Id).ToHashSet();
    if (!expected.SetEquals(index.Select(item => item.Id))) throw new Exception("精简索引与原目录按引用筛选结果不一致。");
    var counts = await readOnly.ListFolderDocumentCountsAsync(selected.Id, default);
    if (counts.Sum(item => item.Count) != all.Count(item => item.FolderId.HasValue)) throw new Exception("新目录计数与原读取不一致。");
    ids.UnionWith(index.GroupBy(item => item.FileName.Trim().Replace('\\', '/').Split('/')[^1], StringComparer.OrdinalIgnoreCase)
        .Where(group => group.Count() == 1 && names.Contains(group.Key)).Select(group => group.Single().Id));
    var relations = await readOnly.ListWorkspaceDocumentRelationsAsync(selected.Id, ids, default);
    watch.Restart(); await readOnly.ListProjectsAsync(default); var warmMs = watch.ElapsedMilliseconds;
    var result = new { status = "passed", readOnly = !args.Contains("--migrate-existing", StringComparer.Ordinal), project = selected.Code, familyDocuments = all.Count, workspaceDocuments = index.Count, relations = relations.Count, indexMs, firstProjectListMs = firstMs, warmProjectListMs = warmMs };
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(result));
    return;
}
var reuseArgument = Array.IndexOf(args, "--reuse-database");
if (reuseArgument >= 0 && serverArgument < 0) throw new Exception("仅独立测试实例允许复用隔离库。");
var database = reuseArgument >= 0 ? args[reuseArgument + 1] : "pdm_navigation_qa_" + Guid.NewGuid().ToString("N");
if (database == production || !System.Text.RegularExpressions.Regex.IsMatch(database, "^pdm_navigation_qa_[a-f0-9]{32}$")) throw new Exception("隔离库名称检查失败。");
settings.Database = "";
await using var admin = new MySqlConnection(settings.ConnectionString);
await admin.OpenAsync();
await admin.ExecuteAsync($"CREATE DATABASE IF NOT EXISTS `{database}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci");
var checks = new List<string>();
var samples = new List<object>();
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
try
{
    settings.Database = database;
    var snapshotArgument = Array.IndexOf(args, "--qa-snapshot");
    if (snapshotArgument >= 0 && reuseArgument < 0)
    {
        var source = Path.GetFullPath(args[snapshotArgument + 1]);
        if (!source.StartsWith(Path.Combine(root, ".local", "navigation-performance-qa"), StringComparison.OrdinalIgnoreCase)) throw new Exception("隔离样本路径无效。");
        if (!File.Exists(source)) throw new Exception("隔离样本文件不存在。");
        var command = new ProcessStartInfo(Path.Combine(root, ".runtime/mysql-8.4.11-winx64/bin/mysql.exe")) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        command.ArgumentList.Add(serverArgument >= 0 ? "--no-defaults" : "--defaults-extra-file=" + Path.Combine(root, ".local/secrets/mysql-root-client.ini"));
        command.ArgumentList.Add("--host=" + settings.Server);
        command.ArgumentList.Add("--port=" + settings.Port);
        if (serverArgument >= 0) { command.ArgumentList.Add("--user=root"); command.ArgumentList.Add("--password="); command.Environment.Remove("MYSQL_PWD"); }
        command.ArgumentList.Add("--database=" + database);
        command.ArgumentList.Add("--protocol=TCP");
        command.ArgumentList.Add("--execute=source " + source.Replace('\\', '/'));
        using var restore = Process.Start(command) ?? throw new Exception("不能启动隔离样本恢复。");
        var error = await restore.StandardError.ReadToEndAsync();
        await restore.WaitForExitAsync();
        if (restore.ExitCode != 0) throw new Exception("隔离样本恢复失败：" + error);
    }
    var options = Options.Create(new PdmDatabaseOptions { Provider = "MySql", ConnectionString = settings.ConnectionString });
    await new MySqlMigrationRunner(options, NullLogger<MySqlMigrationRunner>.Instance, TimeProvider.System).RunAsync(default);
    Console.WriteLine("隔离库结构准备完成。");
    IPdmRepository repository = new MySqlPdmRepository(options, TimeProvider.System);
    await using var sql = new MySqlConnection(settings.ConnectionString);
    await sql.OpenAsync();
    var company = (await repository.GetOrganizationDirectoryAsync(default)).Organizations.First().Id;
    for (var i = 0; i < 150; i++)
        if (await repository.FindUserAsync($"reader{i}", default) is null)
            await repository.CreateUserAsync(new(Guid.NewGuid(), $"reader{i}", $"隔离用户{i}", "not-a-login-password", UserRole.Administrator, true, CompanyId: company), default);
    Console.WriteLine("150个隔离账号准备完成。");
    var actor = (await repository.FindUserAsync("reader0", default))!;
    TenantContext.Set(new(actor.Id, company, company, actor.Username, actor.EffectiveRoleCode, true,
        await repository.GetUserPermissionsAsync(actor.Username, UserRole.Administrator, default), actor.EffectiveRoleCodes));
    var code = "NAV-QA-" + Guid.NewGuid().ToString("N")[..8];
    var project = await repository.CreateProjectAsync(new(code, "并发导航验收", "reader0", "D:/navigation-qa", "D:/navigation-qa-release"), "reader0", default);
    var sibling = await repository.CreateProjectAsync(new(code + "-1", "关联项目", "reader0", "D:/navigation-qa", "D:/navigation-qa-release"), "reader0", default);
    await sql.ExecuteAsync("UPDATE project SET organization_id=@Company WHERE id IN @Ids", new { Company = company, Ids = new[] { project.Id, sibling.Id } });
    await sql.ExecuteAsync("UPDATE project SET root_project_id=@Root,parent_project_id=@Root,child_sequence=1 WHERE id=@Id", new { Root = project.Id, sibling.Id });
    var model = await repository.RegisterDocumentAsync(new(project.Id, "ROOT", "总装", "ROOT.SLDASM", DocumentKind.Assembly), "reader0", default);
    var shared = await repository.RegisterDocumentAsync(new(sibling.Id, "SHARED", "共用零件", "SHARED.SLDPRT", DocumentKind.Part), "reader0", default);
    var collision = await repository.RegisterDocumentAsync(new(project.Id, "SHARED-2", "同名零件", "SHARED.SLDPRT", DocumentKind.Part), "reader0", default);
    var drawing = await repository.RegisterDocumentAsync(new(sibling.Id, "SHARED-D", "关联工程图", "SHARED.SLDDRW", DocumentKind.Drawing, RelatedModelDocumentId: shared.Id), "reader0", default);
    var folder = (await repository.ListDocumentsAsync(sibling.Id, default)).First().FolderId;
    // 这里仅准备读取样本；一次提交避免逐条落盘挤占后续导航性能测试的磁盘资源。
    await using (var fixtureTransaction = await sql.BeginTransactionAsync())
    {
        await sql.ExecuteAsync("""
            INSERT INTO document(id,project_id,drawing_number,name,file_name,kind,lifecycle_state,revision_label,folder_id,created_at,updated_at)
            VALUES(@Id,@ProjectId,@Name,@Name,@FileName,'Part','Work','W1',@FolderId,UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))
            """, Enumerable.Range(0, 1500).Select(i => new { Id = Guid.NewGuid(), ProjectId = sibling.Id, Name = $"UNRELATED-{i}", FileName = $"UNRELATED-{i}.SLDPRT", FolderId = folder }), fixtureTransaction);
        await fixtureTransaction.CommitAsync();
    }
    DocumentReferenceNode Node(PdmDocument document) => new(Guid.NewGuid(), document.Id, document.FileName, document.FileName, document.Name, document.Kind, "Default", 1, ReferenceNodeStatus.Normal, document.Revision, "reader0", []);
    var tree = Node(model) with { Children = [Node(shared), Node(shared) with { DocumentId = null, FileName = "VIRTUAL^ROOT.SLDPRT", Status = ReferenceNodeStatus.Virtual }] };
    var snapshot = Guid.NewGuid();
    async Task StoreTree(DocumentReferenceNode value)
    {
        var body = JsonSerializer.Serialize(value, json);
        await sql.ExecuteAsync("""
            INSERT INTO reference_snapshot(id,project_id,root_document_id,captured_at,captured_by,sha256,root_json)
            VALUES(@Id,@Project,@Document,UTC_TIMESTAMP(6),'reader0',@Sha,@Body)
            ON DUPLICATE KEY UPDATE root_json=VALUES(root_json),sha256=VALUES(sha256);
            INSERT INTO project_reference_root(project_id,reference_snapshot_id,updated_at)
            VALUES(@Project,@Id,UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE reference_snapshot_id=VALUES(reference_snapshot_id);
            """, new { Id = snapshot, Project = project.Id, Document = model.Id, Body = body, Sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))) });
    }
    await StoreTree(tree);
    await repository.EnsureProjectFolderTreeAsync(project.Id, default);
    await using (var blocker = new MySqlConnection(settings.ConnectionString))
    {
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await blocker.ExecuteScalarAsync<Guid>("SELECT id FROM project WHERE id=@Id FOR UPDATE", new { project.Id }, transaction);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var watch = Stopwatch.StartNew();
        await repository.EnsureProjectFolderTreeAsync(project.Id, timeout.Token);
        Check(watch.ElapsedMilliseconds < 4000, "目录已完整时读取不等待项目写锁");
        samples.Add(new { operation = "folder-read-with-project-write-lock", ms = watch.ElapsedMilliseconds });
        await transaction.RollbackAsync();
    }
    var templateKey = (await repository.ListFolderTemplateAsync(default)).First(node => node.Purpose != ProjectFolderPurpose.ProjectContainer).FolderKey;
    await sql.ExecuteAsync("UPDATE folder_template_node SET name=CONCAT(name,'测试') WHERE folder_key=@Key", new { Key = templateKey });
    await repository.EnsureProjectFolderTreeAsync(project.Id, default);
    var updatedFolders = await repository.ListProjectFoldersAsync(project.Id, "reader0", UserRole.Administrator, default);
    Check((await repository.ListFolderTemplateAsync(default)).All(node => node.Purpose == ProjectFolderPurpose.ProjectContainer || updatedFolders.Any(folderItem => folderItem.TemplateKey == node.FolderKey && folderItem.Name == node.Name)), "目录模板变更触发补建");
    var indexed = await repository.ListWorkspaceDocumentsAsync(project.Id, [model.Id], ["SHARED.SLDPRT"], default);
    Check(indexed.Count == 3 && indexed.Any(item => item.Id == shared.Id) && indexed.Any(item => item.Id == collision.Id), "索引保留跨项目引用和同名歧义，排除1500个无关图档");
    var relations = await repository.ListWorkspaceDocumentRelationsAsync(project.Id, [shared.Id], default);
    Check(relations.Any(item => item.ModelDocumentId == shared.Id && item.DrawingDocumentId == drawing.Id), "精简关系读取保留关联工程图");
    Check((await repository.ListFolderDocumentCountsAsync(project.Id, default)).Sum(item => item.Count) == 1504, "文件夹计数与实际图档一致");
    Check((await repository.ListFolderDocumentsAsync(project.Id, folder!.Value, default)).Count == 1502, "按文件夹读取不漏项");
    var oldCounts = (await repository.ListProjectsAsync(default)).Single(item => item.Id == project.Id).DocumentCount;
    var extra = (await repository.ListFolderDocumentsAsync(project.Id, folder!.Value, default)).First(item => item.FileName.StartsWith("UNRELATED-", StringComparison.Ordinal));
    tree = tree with { Children = tree.Children.Append(Node(extra)).ToArray() };
    await StoreTree(tree);
    var newCounts = (await repository.ListProjectsAsync(default)).Single(item => item.Id == project.Id).DocumentCount;
    Check(newCounts == oldCounts + 1, "引用快照更新后立即重新计算计数");
    await sql.ExecuteAsync("UPDATE document SET deleted_at=UTC_TIMESTAMP(6) WHERE id=@Id", new { collision.Id });
    Check((await repository.ListProjectsAsync(default)).Single(item => item.Id == project.Id).DocumentCount == oldCounts, "图档删除后立即失效旧计数缓存");
    // 用一个连接也能完成读取，确定性捕获持有外层连接后再嵌套申请连接的问题。
    var singleConnection = new MySqlConnectionStringBuilder(settings.ConnectionString) { MaximumPoolSize = 1, MinimumPoolSize = 0 };
    IPdmRepository singleConnectionRepository = new MySqlPdmRepository(Options.Create(new PdmDatabaseOptions { Provider = "MySql", ConnectionString = singleConnection.ConnectionString }), TimeProvider.System);
    Check((await singleConnectionRepository.GetOrganizationDirectoryAsync(default)).Users.Count == 150
        && (await singleConnectionRepository.GetRolePermissionDirectoryAsync(default)).Roles.Count > 0
        && (await singleConnectionRepository.ListProjectsForUserAsync("reader0", UserRole.Administrator, default)).Any(item => item.Id == project.Id)
        && (await singleConnectionRepository.FindProjectForUserAsync(project.Id, "reader0", UserRole.Administrator, default))?.Id == project.Id
        && (await singleConnectionRepository.ListProjectFoldersAsync(project.Id, "reader0", UserRole.Administrator, default)).Count > 0,
        "单连接池可完成组织与权限目录、项目列表、项目详情及文件夹读取，无嵌套连接等待");
    var allTimes = new List<double>();
    var workload = Stopwatch.StartNew();
    await Task.WhenAll(Enumerable.Range(0, 50).Select(async user =>
    {
        for (var round = 0; round < 3; round++)
        {
            var elapsed = Stopwatch.StartNew();
            var actor = $"reader{user}";
            Check(await repository.HasProjectReadAccessAsync(project.Id, actor, UserRole.Administrator, default), null);
            await repository.ListProjectsForUserAsync(actor, UserRole.Administrator, default);
            await repository.ListProjectFoldersAsync(project.Id, actor, UserRole.Administrator, default);
            await repository.ListWorkspaceDocumentsAsync(project.Id, [model.Id, shared.Id], [], default);
            await repository.ListWorkspaceDocumentRelationsAsync(project.Id, [model.Id, shared.Id], default);
            await repository.ListFolderDocumentCountsAsync(project.Id, default);
            lock (allTimes) allTimes.Add(elapsed.Elapsed.TotalMilliseconds);
        }
    })).WaitAsync(TimeSpan.FromMinutes(3));
    allTimes.Sort();
    samples.Add(new { operation = "50-users-three-navigation-rounds", completed = allTimes.Count, elapsedMs = workload.ElapsedMilliseconds, p50Ms = Math.Round(allTimes[74]), p95Ms = Math.Round(allTimes[142]), maxMs = Math.Round(allTimes[^1]), poolLimit = settings.MaximumPoolSize });
    Check(allTimes.Count == 150, "150个隔离账号、50个并发读取者完成三轮导航，无连接池死锁或读取错误");
    var adminTenant = TenantContext.Current!;
    var authorization = await repository.GetAuthorizationSettingsAsync(default);
    var emptyRole = new AuthorizationTemplate("navigation-empty", "导航无授权测试岗位", "隔离验收专用", [], RoleCode: "navigation-empty");
    var scopedTemplate = new AuthorizationTemplate("navigation-scoped", "导航项目范围测试", "隔离验收专用",
        [new(PermissionCodes.ProjectView, ["company"]), new(PermissionCodes.ProjectContentView, ["company"])], RoleCode: "navigation-scoped", ProjectAssignable: true);
    authorization = await repository.SaveAuthorizationSettingsAsync(authorization with { Templates = [.. authorization.Templates, emptyRole, scopedTemplate] }, authorization.Revision, default, updateRoles: true);
    var scopedActor = new UserAccount(Guid.NewGuid(), "navigation-scoped-reader", "导航范围测试人员", "not-a-login-password", UserRole.ProductionViewer, true, RoleCode: emptyRole.RoleCode, CompanyId: company);
    await repository.CreateUserAsync(scopedActor, default);
    async Task SelectScopedActor()
    {
        TenantContext.Set(new(scopedActor.Id, company, company, scopedActor.Username, scopedActor.EffectiveRoleCode, false,
            await repository.GetUserPermissionsAsync(scopedActor.Username, scopedActor.Role, default), scopedActor.EffectiveRoleCodes));
    }
    await SelectScopedActor();
    Check(!await repository.HasProjectReadAccessAsync(project.Id, scopedActor.Username, scopedActor.Role, default)
        && (await repository.ListProjectsForUserAsync(scopedActor.Username, scopedActor.Role, default)).Count == 0
        && await repository.FindProjectForUserAsync(project.Id, scopedActor.Username, scopedActor.Role, default) is null,
        "未授权账号不能读取项目入口、列表或详情");
    TenantContext.Set(adminTenant);
    var assignment = new AuthorizationAssignment(Guid.NewGuid(), scopedActor.Username, scopedTemplate.Key, company, "project", project.Id);
    authorization = await repository.SaveAuthorizationSettingsAsync(authorization with { Assignments = [.. authorization.Assignments, assignment] }, authorization.Revision, default);
    await SelectScopedActor();
    Check(await repository.HasProjectReadAccessAsync(project.Id, scopedActor.Username, scopedActor.Role, default)
        && (await repository.ListProjectsForUserAsync(scopedActor.Username, scopedActor.Role, default)).Select(item => item.Id).SequenceEqual([project.Id])
        && (await repository.FindProjectForUserAsync(project.Id, scopedActor.Username, scopedActor.Role, default))?.Id == project.Id,
        "明确项目授权后入口、列表和详情结果一致");
    Check(!await repository.HasProjectReadAccessAsync(sibling.Id, scopedActor.Username, scopedActor.Role, default)
        && await repository.FindProjectForUserAsync(sibling.Id, scopedActor.Username, scopedActor.Role, default) is null,
        "仅本项目授权不会扩展至其他项目");
    TenantContext.Set(adminTenant);
    var otherCompany = (await repository.GetOrganizationDirectoryAsync(default)).Organizations.First(item => item.Id != company).Id;
    var foreignProject = await repository.CreateProjectAsync(new(code + "-FOREIGN", "其他公司权限验收", "reader0", "D:/navigation-qa", "D:/navigation-qa-release"), "reader0", default);
    await sql.ExecuteAsync("UPDATE project SET organization_id=@Company WHERE id=@Id", new { Company = otherCompany, foreignProject.Id });
    await SelectScopedActor();
    Check(!await repository.HasProjectReadAccessAsync(foreignProject.Id, scopedActor.Username, scopedActor.Role, default)
        && await repository.FindProjectForUserAsync(foreignProject.Id, scopedActor.Username, scopedActor.Role, default) is null
        && !(await repository.ListProjectsForUserAsync(scopedActor.Username, scopedActor.Role, default)).Any(item => item.Id == foreignProject.Id),
        "项目授权不能跨公司读取入口、列表或详情");
    TenantContext.Set(adminTenant);
    authorization = await repository.SaveAuthorizationSettingsAsync(authorization with { Assignments = authorization.Assignments.Where(item => item.Id != assignment.Id).ToArray() }, authorization.Revision, default);
    await SelectScopedActor();
    Check(!await repository.HasProjectReadAccessAsync(project.Id, scopedActor.Username, scopedActor.Role, default)
        && (await repository.ListProjectsForUserAsync(scopedActor.Username, scopedActor.Role, default)).Count == 0
        && await repository.FindProjectForUserAsync(project.Id, scopedActor.Username, scopedActor.Role, default) is null,
        "撤销后下一次读取立即失去项目入口、列表和详情授权");
    TenantContext.Set(adminTenant);
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { status = "passed", database, productionDatabaseWritten = false, checks, samples }, json));
    Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", checks, samples }, json));
    void Check(bool condition, string? message) { if (!condition) throw new Exception(message ?? "并发权限读取失败。"); if (message is not null) checks.Add(message); }
}
catch (Exception error)
{
    await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { status = "failed", database, error = error.ToString(), checks, samples }, json));
    throw;
}
finally
{
    MySqlConnection.ClearAllPools();
    TenantContext.Clear();
    // 独立实例由测试结束时统一关闭，保留失败样本供诊断，不在每次失败后逐表删除。
    if (serverArgument < 0) await admin.ExecuteAsync($"DROP DATABASE `{database}`", commandTimeout: 300);
}
