using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryProjectOplRepository : IProjectOplRepository
{
    private readonly Dictionary<Guid, OplIssue> records = [];
    private readonly object gate = new();
    public Task<IReadOnlyList<OplIssue>> ListAsync(Guid projectId, CancellationToken ct)
    { lock (gate) return Task.FromResult<IReadOnlyList<OplIssue>>(records.Values.Where(x => x.ProjectId == projectId).ToArray()); }
    public Task<IReadOnlyDictionary<Guid, int>> CountOpenAsync(IReadOnlyCollection<Guid> projectIds, CancellationToken ct)
    {
        lock (gate) return Task.FromResult<IReadOnlyDictionary<Guid, int>>(records.Values
            .Where(issue => projectIds.Contains(issue.ProjectId) && issue.Status is not ("已完成" or "已作废"))
            .GroupBy(issue => issue.ProjectId).ToDictionary(group => group.Key, group => group.Count()));
    }
    public Task<OplIssue> SaveAsync(OplIssue issue, long expectedVersion, CancellationToken ct)
    {
        lock (gate)
        {
            var current = records.GetValueOrDefault(issue.Id);
            if ((current?.RowVersion ?? 0) != expectedVersion) throw new PdmConflictException("问题已被其他人修改，请关闭抽屉并刷新后重试。");
            var saved = issue with { Number = current?.Number ?? records.Values.Where(x => x.ProjectId == issue.ProjectId).Select(x => x.Number).DefaultIfEmpty().Max() + 1, RowVersion = expectedVersion + 1 };
            records[saved.Id] = saved;
            return Task.FromResult(saved);
        }
    }
}
public sealed class MySqlProjectOplRepository(IOptions<PdmDatabaseOptions> options) : IProjectOplRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public async Task<IReadOnlyList<OplIssue>> ListAsync(Guid projectId, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        var rows = await connection.QueryAsync<string>(new CommandDefinition("SELECT payload_json FROM project_opl_issue WHERE project_id=@projectId ORDER BY issue_number DESC", new { projectId }, cancellationToken: ct));
        return rows.Select(x => JsonSerializer.Deserialize<OplIssue>(x, JsonOptions)!).ToArray();
    }
    private sealed class OpenCountRow { public Guid ProjectId { get; set; } public int OpenCount { get; set; } }
    public async Task<IReadOnlyDictionary<Guid, int>> CountOpenAsync(IReadOnlyCollection<Guid> projectIds, CancellationToken ct)
    {
        if (projectIds.Count == 0) return new Dictionary<Guid, int>();
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        var rows = await connection.QueryAsync<OpenCountRow>(new CommandDefinition(
            "SELECT project_id AS ProjectId, COUNT(*) AS OpenCount FROM project_opl_issue WHERE project_id IN @ProjectIds AND JSON_UNQUOTE(JSON_EXTRACT(payload_json,'$.status')) NOT IN ('已完成','已作废') GROUP BY project_id",
            new { ProjectIds = projectIds.ToArray() }, cancellationToken: ct));
        return rows.ToDictionary(row => row.ProjectId, row => row.OpenCount);
    }
    public async Task<OplIssue> SaveAsync(OplIssue issue, long expectedVersion, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT row_version FROM project WHERE id=@ProjectId FOR UPDATE", new { issue.ProjectId }, transaction, cancellationToken: ct));
        var version = await connection.ExecuteScalarAsync<long?>(new CommandDefinition("SELECT row_version FROM project_opl_issue WHERE id=@Id AND project_id=@ProjectId FOR UPDATE", new { issue.Id, issue.ProjectId }, transaction, cancellationToken: ct));
        if ((version ?? 0) != expectedVersion) throw new PdmConflictException("问题已被其他人修改，请关闭抽屉并刷新后重试。");
        var number = version is null ? await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COALESCE(MAX(issue_number),0)+1 FROM project_opl_issue WHERE project_id=@ProjectId", new { issue.ProjectId }, transaction, cancellationToken: ct)) : issue.Number;
        var saved = issue with { Number = number, RowVersion = expectedVersion + 1 };
        await connection.ExecuteAsync(new CommandDefinition("INSERT INTO project_opl_issue(id,project_id,issue_number,row_version,payload_json) VALUES(@Id,@ProjectId,@Number,@RowVersion,@Payload) ON DUPLICATE KEY UPDATE row_version=@RowVersion,payload_json=@Payload", new { saved.Id, saved.ProjectId, saved.Number, saved.RowVersion, Payload = JsonSerializer.Serialize(saved, JsonOptions) }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return saved;
    }
}
public sealed class LocalProjectOplStorage : IProjectOplStorage
{
    private static string FilePath(string root, Guid projectId, Guid id) => Path.Combine(root, "project-opl", projectId.ToString("N"), id.ToString("N"));
    public async Task WriteAsync(string vaultRoot, Guid projectId, Guid attachmentId, byte[] content, CancellationToken ct)
    {
        var path = FilePath(vaultRoot, projectId, attachmentId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content, ct);
    }
    public Task<Stream> OpenAsync(string vaultRoot, Guid projectId, Guid attachmentId, CancellationToken ct)
    {
        var path = FilePath(vaultRoot, projectId, attachmentId);
        if (!File.Exists(path)) throw new PdmNotFoundException("附件文件缺失，请联系管理员检查文件库。");
        return Task.FromResult<Stream>(File.OpenRead(path));
    }
    public void Delete(string vaultRoot, Guid projectId, Guid attachmentId) => File.Delete(FilePath(vaultRoot, projectId, attachmentId));
}
