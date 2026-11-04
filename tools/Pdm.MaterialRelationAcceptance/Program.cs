using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

var root = Path.GetFullPath(args[0]);
using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".local/api/appsettings.json")));
using var secrets = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, ".local/secrets/pdm-secrets.json")));
var settings = new MySqlConnectionStringBuilder(config.RootElement.GetProperty("ConnectionStrings").GetProperty("Pdm").GetString() ?? throw new Exception("数据库配置缺失。"))
{ UserID = "root", Password = secrets.RootElement.GetProperty("mysqlRootPassword").GetString() };
var production = settings.Database;
var database = "pdm_allocation_qa_" + Guid.NewGuid().ToString("N");
void Progress(string stage) { File.WriteAllText(args[1] + ".progress", database + "\n" + stage); Console.WriteLine(stage); }
if (database == production || !System.Text.RegularExpressions.Regex.IsMatch(database, "^pdm_allocation_qa_[a-f0-9]{32}$")) throw new Exception("隔离库名称无效。");
settings.Database = "";
await using var admin = new MySqlConnection(settings.ConnectionString);
Progress("正在连接隔离测试数据库");
await admin.OpenAsync();
foreach (var previous in args.Skip(2))
{
    if (previous == production || !System.Text.RegularExpressions.Regex.IsMatch(previous, "^pdm_allocation_qa_[a-f0-9]{32}$")) throw new Exception("待清理测试库名称无效。");
    Progress("正在清理已停止的隔离测试库");
    await admin.ExecuteAsync($"DROP DATABASE IF EXISTS `{previous}`", commandTimeout: 120);
}
await admin.ExecuteAsync($"CREATE DATABASE `{database}` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci");
try
{
    Progress("正在准备最小结构副本");
    foreach (var table in new[] { "project", "bom_item", "material_relation_selection", "bom_release_confirmation", "document_model_drawing_relation", "document", "document_version", "pdm_schema_migration" })
        await admin.ExecuteAsync($"CREATE TABLE `{database}`.`{table}` LIKE `{production}`.`{table}`");
    await admin.ExecuteAsync($"INSERT INTO `{database}`.pdm_schema_migration SELECT * FROM `{production}`.pdm_schema_migration");
    settings.Database = database;
    var options = Options.Create(new PdmDatabaseOptions { Provider = "MySql", ConnectionString = settings.ConnectionString });
    var migrations = new MySqlMigrationRunner(options, NullLogger<MySqlMigrationRunner>.Instance, TimeProvider.System);
    Progress("正在验证新增字段迁移与重复启动");
    await migrations.RunAsync(default);
    await migrations.RunAsync(default);
    IPdmRepository repository = new MySqlPdmRepository(options, TimeProvider.System);
    Progress("正在准备隔离测试数据");
    await using var sql = new MySqlConnection(settings.ConnectionString); await sql.OpenAsync();
    var projectId = Guid.NewGuid();
    await sql.ExecuteAsync("INSERT INTO project(id,code,name,owner,vault_location,release_location,created_at,updated_at) VALUES(@Id,'AL-QA','组合存储验收','qa','D:/qa','D:/release',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6))", new { Id = projectId });
    var revision = Guid.NewGuid();
    var main = new BomItem(Guid.NewGuid(), projectId, BomKind.Standard, 1, "MOTOR", "主物料", 2, "001", null, "MODEL", "W1", true)
    { RelationAllocations = [new(Guid.NewGuid(), "组合甲", 1, revision, 1, []), new(Guid.NewGuid(), "组合乙", 1, revision, 1, [])] };
    await repository.ReplaceBomAsync(projectId, BomKind.Standard, [main], default);
    Progress("正在核对两条写入路径与组合数据");
    var saved = (await repository.FindBomItemAsync(projectId, main.Id, default))!;
    if (saved.RelationAllocations.Count != 2 || saved.RelationAllocations[1].Name != "组合乙") throw new Exception("批量写入组合未保留。");
    await repository.ApplyBomBatchAsync(projectId, [saved], [], [], [], [], [], [], [], default);
    if ((await repository.FindBomItemAsync(projectId, main.Id, default))!.RelationAllocations.Count != 2) throw new Exception("选配保存未保留组合。");
    await repository.ReplaceBomAsync(projectId, BomKind.Standard, [saved with { RelationAllocations = [new(Guid.NewGuid(), "统一配置", 2, revision, 1, [])] }], default);
    repository = new MySqlPdmRepository(options, TimeProvider.System);
    if ((await repository.FindBomItemAsync(projectId, main.Id, default))!.RelationAllocations.Count != 1) throw new Exception("重新读取组合失败。");
    Progress("正在核对汇总数量标记与原始来源保留");
    var summary=(await repository.FindBomItemAsync(projectId,main.Id,default))! with {Quantity=4,IsSummaryQuantity=true,Source="Auto"};
    await repository.ReplaceBomAsync(projectId,BomKind.Standard,[summary],default);
    var reloaded=(await repository.GetBomAsync(projectId,BomKind.Standard,default)).Single();
    if(!reloaded.IsSummaryQuantity || reloaded.Quantity!=4 || reloaded.Source!="Auto")throw new Exception("汇总数量标记或来源未正确保存。");
    await repository.ApplyBomBatchAsync(projectId,[reloaded with {Quantity=6}],[],[],[],[],[],[],[],default);
    reloaded=(await repository.FindBomItemAsync(projectId,main.Id,default))!;
    if(!reloaded.IsSummaryQuantity || reloaded.Quantity!=6 || reloaded.Source!="Auto")throw new Exception("数量联动写入未保留汇总数量标记。");
    File.WriteAllText(args[1], JsonSerializer.Serialize(new { status = "passed", migrationsIdempotent = true, bulkWrite = true, applyBatchWrite = true, reloaded = true, summaryQuantityPersisted = true }));
    Console.WriteLine("隔离MySQL增量迁移、两条写入路径与重新读取核对通过。");
}
finally
{
    TenantContext.Clear();
    MySqlConnection.ClearAllPools();
    await admin.ExecuteAsync($"DROP DATABASE `{database}`");
}
