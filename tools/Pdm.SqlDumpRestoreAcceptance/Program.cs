using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using MySqlConnector;

if (args.Length != 3) throw new ArgumentException("请提供仓库、测试样本目录和MySQL客户端路径。");
var root = Path.GetFullPath(args[0]);
var sample = Path.GetFullPath(args[1]);
var mysql = Path.GetFullPath(args[2]);
var testRoot = Path.Combine(root, "tmp") + Path.DirectorySeparatorChar;
if (!sample.StartsWith(testRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(mysql))
    throw new InvalidOperationException("测试样本或数据库客户端路径不合法。");
var source = Path.Combine(sample, "source.sql");
var compatible = Path.Combine(sample, "compatible.sql");
var originalSha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source)));
var compatibleSha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(compatible)));
var input = await File.ReadAllTextAsync(compatible);
if (Regex.IsMatch(input, @"(?im)^\s*(USE\s|CREATE\s+DATABASE|DROP\s+DATABASE|GRANT\s|SOURCE\s|SYSTEM\s|\\!)"))
    throw new InvalidOperationException("隔离验收样本不得包含切换或修改其他数据库的命令。");
using var config = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, ".local/api/appsettings.json")));
using var secret = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, ".local/secrets/pdm-secrets.json")));
var settings = new MySqlConnectionStringBuilder(config.RootElement.GetProperty("ConnectionStrings").GetProperty("Pdm").GetString()
    ?? throw new InvalidOperationException("数据库连接配置缺失。"))
{
    UserID = "root", Password = secret.RootElement.GetProperty("mysqlRootPassword").GetString()
};
var production = settings.Database;
var database = "pdm_dump_" + Guid.NewGuid().ToString("N") + "_qa";
if (database == production || !Regex.IsMatch(database, "^pdm_dump_[a-f0-9]{32}_qa$"))
    throw new InvalidOperationException("测试数据库边界校验失败。");
settings.Database = "";
await using var admin = new MySqlConnection(settings.ConnectionString);
await admin.OpenAsync();
var result = new Dictionary<string, object?>
{
    ["status"] = "failed", ["database"] = database, ["productionDatabaseWritten"] = false,
    ["originalSourceSha256"] = originalSha, ["compatibleSqlSha256"] = compatibleSha
};
bool created = false;
try
{
    await admin.ExecuteAsync($"CREATE DATABASE `{database}` CHARACTER SET utf8mb4");
    created = true;
    var start = new ProcessStartInfo(mysql) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in new[] { "--defaults-extra-file=" + Path.Combine(root, ".local/secrets/mysql-root-client.ini"),
        "--host=" + settings.Server, "--port=" + settings.Port, "--protocol=TCP", "--default-character-set=utf8mb4", "--database=" + database,
        "--execute=source " + compatible.Replace('\\', '/') }) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动隔离导入进程。");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    try { await process.WaitForExitAsync(timeout.Token); }
    catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw new InvalidOperationException("隔离导入超时，已终止本次导入进程。"); }
    await File.WriteAllTextAsync(Path.Combine(sample, "import.stdout.log"), await stdout);
    await File.WriteAllTextAsync(Path.Combine(sample, "import.stderr.log"), await stderr);
    result["nativeImportExitCode"] = process.ExitCode;
    if (process.ExitCode != 0) throw new InvalidOperationException("原生客户端导入失败，请查看同目录诊断日志。");
    settings.Database = database;
    await using (var sql = new MySqlConnection(settings.ConnectionString))
    {
        await sql.OpenAsync();
        await sql.ExecuteAsync("INSERT INTO relation_template VALUES(9,1)");
        var sources = await sql.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sources");
        var observed = await sql.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM observed");
        var triggers = await sql.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA=@database", new { database });
        result["sources"] = sources; result["observed"] = observed; result["triggers"] = triggers;
        if (sources != 1 || observed != 1 || triggers != 3) throw new InvalidOperationException("恢复后的关联触发器、复合触发器或触发器数量不符合预期。");
        bool rejected = false;
        try { await sql.ExecuteAsync("UPDATE item SET id=2 WHERE id=1"); }
        catch (MySqlException error) when (error.Number == 1644 && error.SqlState == "45000" && error.Message == "immutable; preserve literal") { rejected = true; }
        var retained = await sql.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM item WHERE id=1") == 1
            && await sql.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM item WHERE id=2") == 0;
        result["immutableUpdateRejected"] = rejected; result["originalItemRetained"] = retained;
        if (!rejected || !retained) throw new InvalidOperationException("不可变触发器未保留原始提示或未阻止修改。");
    }
    var unchanged = originalSha == Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(source)));
    result["originalSourceUnchanged"] = unchanged;
    if (!unchanged) throw new InvalidOperationException("原始SQL样本发生变化。");
    result["status"] = "passed";
}
catch (Exception error) { result["failure"] = error.Message; }
finally
{
    MySqlConnection.ClearAllPools();
    if (created) await admin.ExecuteAsync($"DROP DATABASE `{database}`");
    result["isolatedDatabaseDeleted"] = await admin.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME=@database", new { database }) == 0;
    if (result["isolatedDatabaseDeleted"] is not true) result["status"] = "failed";
}
var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(Path.Combine(sample, "mysql-restore-validation.json"), json);
Console.WriteLine(json);
return result["status"] as string == "passed" ? 0 : 1;
