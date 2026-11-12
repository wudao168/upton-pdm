using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MySqlConnector;
using Upton.Pdm.Domain;

// Explicit project-scoped maintenance; dry run by default. Credentials are supplied
// by the operator's process environment and are never written to the report.
if (args.Length < 3 || args[0] != "--project") throw new ArgumentException("请指定项目编号及报告路径。");
var project = args[1]; var reportPath = Path.GetFullPath(args[2]);
var apply = args.Contains("--apply");
var expected = args.Contains("--expected-count") ? int.Parse(args[Array.IndexOf(args, "--expected-count") + 1]) : -1;
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
await using var connection = new MySqlConnection(Environment.GetEnvironmentVariable("UPLM_RELATION_REPAIR_CONNECTION")
    ?? throw new InvalidOperationException("尚未提供维护连接。"));
await connection.OpenAsync();
await using var tx = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead);
async Task<List<Dictionary<string, string?>>> Query(string sql)
{
    await using var cmd = new MySqlCommand(sql, connection, tx) { CommandTimeout = 30 };
    cmd.Parameters.AddWithValue("@Project", project);
    await using var reader = await cmd.ExecuteReaderAsync();
    var rows = new List<Dictionary<string, string?>>();
    while (await reader.ReadAsync())
    {
        var row = new Dictionary<string, string?>();
        for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
        rows.Add(row);
    }
    return rows;
}
var projects = await Query("SELECT HEX(id) Id FROM project WHERE code=@Project");
if (projects.Count != 1) throw new InvalidOperationException("项目编号不存在或不唯一，未修改数据。");
// Serialize with archive updates before taking the evidence snapshot.
await Query("SELECT HEX(d.id) Id FROM document d JOIN project p ON p.id=d.project_id WHERE p.code=@Project AND d.kind='Drawing' ORDER BY d.id FOR UPDATE");
const string versionSql = """
SELECT HEX(d.id) DocumentId,d.file_name FileName,d.revision_label CurrentRevision,
 HEX(v.id) VersionId,v.revision_label Revision,v.reference_snapshot_json Snapshot,v.sha256 FileSha256,v.file_length FileLength
FROM document d JOIN project p ON p.id=d.project_id JOIN document_version v ON v.document_id=d.id
WHERE p.code=@Project AND d.kind='Drawing' AND d.deleted_at IS NULL AND d.purged_at IS NULL
ORDER BY d.id,(v.revision_label=d.revision_label) DESC,v.created_at DESC,v.id DESC
""";
const string relationSql = """
SELECT HEX(r.drawing_document_id) DrawingId,HEX(r.model_document_id) ModelId,HEX(r.project_id) ProjectId,
 r.created_at CreatedAt,r.updated_at UpdatedAt,d.file_name DrawingFile,m.file_name ModelFile
FROM document_model_drawing_relation r JOIN document d ON d.id=r.drawing_document_id
JOIN document m ON m.id=r.model_document_id JOIN project p ON p.id=d.project_id
WHERE p.code=@Project ORDER BY r.drawing_document_id,r.model_document_id
""";
var versions = await Query(versionSql);
var before = await Query(relationSql);
var candidates = new List<Dictionary<string, string?>>();
foreach (var version in versions.GroupBy(row => row["DocumentId"]).Select(group => group.First()))
{
    if (string.IsNullOrWhiteSpace(version["Snapshot"])) continue;
    var snapshot = JsonSerializer.Deserialize<DocumentReferenceNode>(version["Snapshot"]!, json);
    if (snapshot?.DocumentId != Guid.ParseExact(version["DocumentId"]!, "N")
        || snapshot.Kind != DocumentKind.Drawing || !string.Equals(snapshot.FileName, version["FileName"], StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("图纸版本身份不一致，未修改数据：" + version["FileName"]);
    var invalid = DrawingModelRelations.DependencyOnlyModelIds(snapshot);
    candidates.AddRange(before.Where(row => row["DrawingId"] == version["DocumentId"] && invalid.Contains(Guid.ParseExact(row["ModelId"]!, "N"))));
}
var evidence = JsonSerializer.Serialize(versions, json);
var evidenceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence)));
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
// Exact row backup is persisted before any DELETE; historical files/versions are evidence only.
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { project, apply, applied = false,
    count = candidates.Count, evidenceHash, rowsBefore = before, remove = candidates }, json));
if (!apply) { await tx.RollbackAsync(); Console.WriteLine($"只读计划：{project}，拟纠正 {candidates.Count} 条关联；报告：{reportPath}"); return; }
if (expected < 0 || candidates.Count != expected) throw new InvalidOperationException("待纠正数量与审核计划不符，事务未提交。");
foreach (var row in candidates)
{
    await using var cmd = new MySqlCommand("DELETE FROM document_model_drawing_relation WHERE drawing_document_id=UNHEX(@Drawing) AND model_document_id=UNHEX(@Model) AND project_id=UNHEX(@Owner)", connection, tx);
    cmd.Parameters.AddWithValue("@Drawing", row["DrawingId"]); cmd.Parameters.AddWithValue("@Model", row["ModelId"]); cmd.Parameters.AddWithValue("@Owner", row["ProjectId"]);
    if (await cmd.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException("关联已变化，事务未提交。");
}
var after = await Query(relationSql);
var remaining = before.Except(candidates).ToArray();
if (JsonSerializer.Serialize(after, json) != JsonSerializer.Serialize(remaining, json)
    || JsonSerializer.Serialize(await Query(versionSql), json) != evidence)
    throw new InvalidOperationException("纠正后的关系或版本校验不符，事务未提交。");
await tx.CommitAsync();
await File.WriteAllTextAsync(reportPath + ".result.json", JsonSerializer.Serialize(new { project, applied = true,
    removed = candidates.Count, remaining = after.Count, evidenceHash, versionsUnchanged = true }, json));
Console.WriteLine($"已纠正 {candidates.Count} 条关联；其余 {after.Count} 条保留，图纸版本与引用快照校验一致。");
