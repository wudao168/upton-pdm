using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.ClientShared;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api;

public sealed class ClientLogOptions
{
    public string Root { get; set; } = Path.Combine(AppContext.BaseDirectory, "data", "client-logs");
    public int RuntimeDays { get; set; } = 30;
    public int ErrorDays { get; set; } = 90;
    public long MaxBytes { get; set; } = 10L * 1024 * 1024 * 1024;
}

public sealed record ClientLogEvent(DateTimeOffset Time, string Kind, string Operation, string Message,
    string OperationId, string RequestId, string SessionId, string Version, string UiVersion);
public sealed record ClientLogBatch(string BatchId, string DeviceId, string Machine, string Component, ClientLogEvent[] Entries);
public sealed class ClientLogRow
{
    public string Id { get; set; } = "";
    public string CompanyId { get; set; } = "";
    public string Username { get; set; } = "";
    public string BatchId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string Machine { get; set; } = "";
    public string Component { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Version { get; set; } = "";
    public string UiVersion { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }
    public DateTime ReceivedAt { get; set; }
    public int EntryCount { get; set; }
    public long Bytes { get; set; }
}
public sealed record ClientLogQuery(string? Device, string? User, string? Component, string? Kind, string? Version,
    DateTimeOffset? From, DateTimeOffset? To, int Page = 1);

public sealed class ClientLogStore
{
    private readonly PdmDatabaseOptions database;
    private readonly ClientLogOptions options;
    private readonly TimeProvider clock;
    private readonly ILogger<ClientLogStore> logger;
    private readonly ConcurrentDictionary<string, ClientLogRow> memory = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset nextCleanup;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string Select = "SELECT id Id,company_id CompanyId,username Username,batch_id BatchId,device_id DeviceId,machine Machine,component Component,kind Kind,version Version,ui_version UiVersion,started_at StartedAt,ended_at EndedAt,received_at ReceivedAt,entry_count EntryCount,bytes Bytes FROM client_diagnostic_logs";
    public ClientLogStore(IOptions<PdmDatabaseOptions> database, IOptions<ClientLogOptions> options, TimeProvider clock, ILogger<ClientLogStore> logger)
    {
        this.database = database.Value; this.options = options.Value; this.clock = clock; this.logger = logger;
        this.options.Root = Path.GetFullPath(this.options.Root);
        if (this.options.Root.Split(Path.DirectorySeparatorChar).Any(p => p.Equals("wwwroot", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("客户端日志目录不能位于公开网页目录中。");
    }

    public async Task SaveAsync(ClientLogBatch batch, Guid companyId, string username, CancellationToken ct)
    {
        if (!Guid.TryParse(batch.BatchId, out var batchId) || !Guid.TryParse(batch.DeviceId, out var deviceId)
            || batch.Component is not ("desktop" or "solidworks") || string.IsNullOrWhiteSpace(batch.Machine) || batch.Machine.Length > 160
            || batch.Entries is not { Length: > 0 and <= 2000 }) throw new PdmRuleException("日志格式不正确，请升级客户端后重试。");
        var now = clock.GetUtcNow();
        var kind = batch.Entries[0]?.Kind;
        if (kind is not ("runtime" or "error") || batch.Entries.Any(e => e == null || e.Kind != kind || e.Time > now.AddDays(1) || e.Time < now.AddDays(-8)))
            throw new PdmRuleException("日志分类或发生时间无效，请检查客户机时间。");
        var entries = batch.Entries.Select(e => e with
        {
            Operation = Clean(e.Operation, 160), Message = ClientLogRedaction.Clean(e.Message),
            OperationId = Clean(e.OperationId, 64), RequestId = Clean(e.RequestId, 64), SessionId = Clean(e.SessionId, 64),
            Version = Clean(e.Version, 160), UiVersion = Clean(e.UiVersion, 160)
        }).ToArray();
        var safe = batch with { BatchId = batchId.ToString("N"), DeviceId = deviceId.ToString("N"), Machine = Clean(batch.Machine, 160), Entries = entries };
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(companyId + "|" + username + "|" + safe.BatchId))).ToLowerInvariant();
        var row = new ClientLogRow
        {
            Id = id, CompanyId = companyId.ToString(), Username = username, BatchId = safe.BatchId, DeviceId = safe.DeviceId,
            Machine = safe.Machine, Component = safe.Component, Kind = kind!, Version = entries[0].Version, UiVersion = entries.Last().UiVersion,
            StartedAt = entries.Min(e => e.Time).UtcDateTime, EndedAt = entries.Max(e => e.Time).UtcDateTime,
            ReceivedAt = now.UtcDateTime, EntryCount = entries.Length
        };
        using var output = new MemoryStream();
        await using (var zip = new GZipStream(output, CompressionLevel.Fastest, true)) await JsonSerializer.SerializeAsync(zip, safe, Json, ct);
        row.Bytes = output.Length;
        await gate.WaitAsync(ct);
        try
        {
            if (await FindAsync(id, ct) is not null) return;
            if (clock.GetUtcNow() >= nextCleanup)
            {
                await CleanupCoreAsync(ct);
                nextCleanup = clock.GetUtcNow().AddMinutes(5);
            }
            var used = await TotalBytesAsync(ct);
            if (used + row.Bytes > options.MaxBytes) throw new PdmRuleException("服务器日志空间已满，请管理员调整容量或保留天数；客户端将稍后重试。");
            Directory.CreateDirectory(options.Root);
            var path = FilePath(id);
            var temp = path + ".tmp";
            try
            {
                await File.WriteAllBytesAsync(temp, output.ToArray(), ct);
                File.Move(temp, path, true);
                await InsertAsync(row, ct);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { gate.Release(); }
    }

    public async Task<(ClientLogRow[] Items, int Total)> ListAsync(Guid companyId, ClientLogQuery query, int limit, CancellationToken ct)
    {
        if (query.From > query.To || query.Page < 1 || query.Page > 10000) throw new PdmRuleException("请选择有效的时间范围和页码。");
        var args = new { Company = companyId.ToString(), Device = query.Device ?? "", User = query.User ?? "", Component = query.Component ?? "", Kind = query.Kind ?? "", Version = query.Version ?? "", From = query.From?.UtcDateTime, To = query.To?.UtcDateTime, Offset = (query.Page - 1) * limit, Limit = limit };
        if (!UseMySql)
        {
            var rows = memory.Values.Where(r => r.CompanyId == args.Company
                && (args.Device == "" || r.DeviceId == args.Device || r.Machine.Contains(args.Device, StringComparison.OrdinalIgnoreCase))
                && (args.User == "" || r.Username.Contains(args.User, StringComparison.OrdinalIgnoreCase))
                && (args.Component == "" || r.Component == args.Component) && (args.Kind == "" || r.Kind == args.Kind)
                && (args.Version == "" || r.Version.Contains(args.Version, StringComparison.OrdinalIgnoreCase) || r.UiVersion.Contains(args.Version, StringComparison.OrdinalIgnoreCase))
                && (args.From == null || r.EndedAt >= args.From) && (args.To == null || r.StartedAt <= args.To))
                .OrderByDescending(r => r.ReceivedAt).ThenBy(r => r.Id).ToArray();
            return (rows.Skip(args.Offset).Take(limit).ToArray(), rows.Length);
        }
        const string where = " WHERE company_id=@Company AND (@Device='' OR device_id=@Device OR LOCATE(@Device,machine)>0) AND (@User='' OR LOCATE(@User,username)>0) AND (@Component='' OR component=@Component) AND (@Kind='' OR kind=@Kind) AND (@Version='' OR LOCATE(@Version,version)>0 OR LOCATE(@Version,ui_version)>0) AND (@From IS NULL OR ended_at>=@From) AND (@To IS NULL OR started_at<=@To)";
        await using var connection = new MySqlConnection(database.ConnectionString);
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM client_diagnostic_logs" + where, args, cancellationToken: ct));
        var items = (await connection.QueryAsync<ClientLogRow>(new CommandDefinition(Select + where + " ORDER BY received_at DESC,id LIMIT @Limit OFFSET @Offset", args, cancellationToken: ct))).ToArray();
        // MySQL DATETIME has no Kind. Explicit UTC prevents browser time-zone ambiguity.
        foreach (var row in items) NormalizeTimes(row);
        return (items, total);
    }

    public async Task<(byte[] Content, int Count)> DownloadAsync(Guid companyId, ClientLogQuery query, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var (items, total) = await ListAsync(companyId, query with { Page = 1 }, 1000, ct);
            if (total == 0) throw new PdmNotFoundException("没有符合条件的日志，请调整筛选条件。");
            if (total > 1000 || items.Sum(r => r.Bytes) > 50L * 1024 * 1024) throw new PdmRuleException("日志包过大，请缩小时间范围或选择一台计算机后下载。");
            using var output = new MemoryStream();
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                using (var writer = new StreamWriter(archive.CreateEntry("诊断说明.txt").Open(), new UTF8Encoding(true)))
                    await writer.WriteAsync("UPLM 客户端诊断包\r\n运行日志记录操作过程，错误日志保留技术异常；通过 operationId、requestId 和 sessionId 关联。\r\n日志时间包含时区，索引时间使用 UTC。\r\n仅包含已成功上传且仍在保留期内的日志；请结合用户反馈的发生时间、图档和操作步骤分析。\r\n");
                await using (var manifest = archive.CreateEntry("日志索引.json").Open()) await JsonSerializer.SerializeAsync(manifest, items, Json, ct);
                long expanded = 0;
                foreach (var row in items)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!File.Exists(FilePath(row.Id))) throw new PdmRuleException("部分日志文件缺失，请联系管理员检查服务器日志存储。");
                    await using var file = File.OpenRead(FilePath(row.Id));
                    await using var gzip = new GZipStream(file, CompressionMode.Decompress);
                    var batch = await JsonSerializer.DeserializeAsync<ClientLogBatch>(gzip, Json, ct) ?? throw new IOException("日志文件无效");
                    var text = string.Join(Environment.NewLine, batch.Entries.Select(e => JsonSerializer.Serialize(e, Json))) + Environment.NewLine;
                    expanded += Encoding.UTF8.GetByteCount(text);
                    if (expanded > 100L * 1024 * 1024) throw new PdmRuleException("日志解压内容过大，请缩小时间范围后下载。");
                    using var writer = new StreamWriter(archive.CreateEntry($"{(row.Kind == "error" ? "错误日志" : "运行日志")}/{row.Component}-{row.Id}.jsonl").Open(), new UTF8Encoding(true));
                    await writer.WriteAsync(text.AsMemory(), ct);
                }
            }
            return (output.ToArray(), total);
        }
        finally { gate.Release(); }
    }

    public async Task CleanupAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { await CleanupCoreAsync(ct); }
        finally { gate.Release(); }
    }

    private async Task CleanupCoreAsync(CancellationToken ct)
    {
        var runtimeBefore = clock.GetUtcNow().AddDays(-Math.Max(1, options.RuntimeDays)).UtcDateTime;
        var errorBefore = clock.GetUtcNow().AddDays(-Math.Max(1, options.ErrorDays)).UtcDateTime;
        ClientLogRow[] expired;
        if (!UseMySql) expired = memory.Values.Where(r => r.EndedAt < (r.Kind == "error" ? errorBefore : runtimeBefore)).ToArray();
        else
        {
            await using var connection = new MySqlConnection(database.ConnectionString);
            expired = (await connection.QueryAsync<ClientLogRow>(new CommandDefinition(Select + " WHERE (kind='runtime' AND ended_at<@runtimeBefore) OR (kind='error' AND ended_at<@errorBefore) ORDER BY kind DESC,ended_at LIMIT 5000", new { runtimeBefore, errorBefore }, cancellationToken: ct))).ToArray();
        }
        foreach (var row in expired.OrderBy(r => r.Kind == "error").ThenBy(r => r.EndedAt))
        {
            File.Delete(FilePath(row.Id));
            if (!UseMySql) memory.TryRemove(row.Id, out _);
            else
            {
                await using var connection = new MySqlConnection(database.ConnectionString);
                await connection.ExecuteAsync(new CommandDefinition("DELETE FROM client_diagnostic_logs WHERE id=@Id", row, cancellationToken: ct));
            }
        }
        if (expired.Length > 0) logger.LogInformation("按保留策略清理客户端日志 {Count} 个分片", expired.Length);
        // Recover files left between durable write and index insertion, without touching recent uploads.
        if (Directory.Exists(options.Root))
            foreach (var file in new DirectoryInfo(options.Root).EnumerateFiles().Where(f => f.LastWriteTimeUtc < clock.GetUtcNow().AddDays(-1).UtcDateTime))
                if (file.Name.EndsWith(".tmp", StringComparison.Ordinal) || (file.Extension == ".gz" && await FindAsync(Path.GetFileNameWithoutExtension(file.Name), ct) is null)) file.Delete();
    }

    private bool UseMySql => database.Provider.Equals("MySql", StringComparison.OrdinalIgnoreCase);
    private string FilePath(string id) => Path.Combine(options.Root, id + ".gz");
    private static string Clean(string? text, int length)
    {
        var safe = ClientLogRedaction.Clean(text ?? "");
        return safe[..Math.Min(safe.Length, length)];
    }
    private static void NormalizeTimes(ClientLogRow r)
    {
        r.StartedAt = DateTime.SpecifyKind(r.StartedAt, DateTimeKind.Utc); r.EndedAt = DateTime.SpecifyKind(r.EndedAt, DateTimeKind.Utc); r.ReceivedAt = DateTime.SpecifyKind(r.ReceivedAt, DateTimeKind.Utc);
    }
    private async Task<ClientLogRow?> FindAsync(string id, CancellationToken ct)
    {
        if (!UseMySql) return memory.GetValueOrDefault(id);
        await using var connection = new MySqlConnection(database.ConnectionString);
        return await connection.QuerySingleOrDefaultAsync<ClientLogRow>(new CommandDefinition(Select + " WHERE id=@id", new { id }, cancellationToken: ct));
    }
    private async Task<long> TotalBytesAsync(CancellationToken ct)
    {
        if (!UseMySql) return memory.Values.Sum(r => r.Bytes);
        await using var connection = new MySqlConnection(database.ConnectionString);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT COALESCE(SUM(bytes),0) FROM client_diagnostic_logs", cancellationToken: ct));
    }
    private async Task InsertAsync(ClientLogRow row, CancellationToken ct)
    {
        if (!UseMySql) { memory[row.Id] = row; return; }
        await using var connection = new MySqlConnection(database.ConnectionString);
        await connection.ExecuteAsync(new CommandDefinition("INSERT INTO client_diagnostic_logs(id,company_id,username,batch_id,device_id,machine,component,kind,version,ui_version,started_at,ended_at,received_at,entry_count,bytes) VALUES(@Id,@CompanyId,@Username,@BatchId,@DeviceId,@Machine,@Component,@Kind,@Version,@UiVersion,@StartedAt,@EndedAt,@ReceivedAt,@EntryCount,@Bytes)", row, cancellationToken: ct));
    }
}

public sealed class ClientLogCleanupService(ClientLogStore store, ILogger<ClientLogCleanupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
            try { await store.CleanupAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { logger.LogWarning(ex, "客户端日志清理失败，将在下一周期重试"); }
    }
}
