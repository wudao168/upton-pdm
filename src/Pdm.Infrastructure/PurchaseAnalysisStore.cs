using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Dapper;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository : IPurchaseAnalysisStore
{
    // Only source rows are cached; every request still reads current authorization and applies resource scopes.
    private static readonly ConcurrentDictionary<(string Database, Guid Company), (DateTimeOffset? Version, IReadOnlyList<PurchaseAnalysisLine> Rows)> purchaseRows = new();
    private static readonly ConcurrentDictionary<(string Database, Guid Company), SemaphoreSlim> purchaseReadLocks = new();
    async Task<PurchaseAnalysisSettings> IPurchaseAnalysisStore.SettingsAsync(Guid company, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        var json = await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT payload_json FROM purchase_analysis_settings WHERE company_id=@company", new { company }, cancellationToken: ct));
        return json is null ? new(0, []) : JsonSerializer.Deserialize<PurchaseAnalysisSettings>(json)!;
    }
    async Task IPurchaseAnalysisStore.SaveSettingsAsync(Guid company, PurchaseAnalysisSettings settings, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        var sql = settings.Revision == 0
            ? "INSERT IGNORE INTO purchase_analysis_settings VALUES(@company,1,@json)"
            : "UPDATE purchase_analysis_settings SET revision=revision+1,payload_json=@json WHERE company_id=@company AND revision=@revision";
        if (await db.ExecuteAsync(new CommandDefinition(sql, new { company, revision = settings.Revision,
            json = JsonSerializer.Serialize(settings with { Revision = settings.Revision + 1 }) }, cancellationToken: ct)) != 1)
            throw new PdmConflictException("采集设置已变化，请重新加载后保存。");
    }
    async Task<PurchaseAnalysisSnapshot> IPurchaseAnalysisStore.ReadAsync(Guid company, CancellationToken ct)
    {
        var key = (connectionString, company);
        var readLock = purchaseReadLocks.GetOrAdd(key, _ => new(1, 1));
        await readLock.WaitAsync(ct);
        try {
        await using var db = await OpenAsync(ct);
        await using var transaction = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var json = await db.QuerySingleOrDefaultAsync<string>(new CommandDefinition("SELECT payload_json FROM purchase_analysis_status WHERE company_id=@company", new { company }, transaction, cancellationToken: ct));
        var status = json is null ? new() : JsonSerializer.Deserialize<PurchaseAnalysisStatus>(json)!;
        if (purchaseRows.TryGetValue(key, out var cached) && cached.Version == status.LastSuccess) {
            await transaction.CommitAsync(ct);
            return new(status, cached.Rows);
        }
        var rows = await db.QueryAsync<string>(new CommandDefinition("SELECT payload_json FROM purchase_analysis_line WHERE company_id=@company", new { company }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        var lines = rows.Select(row => JsonSerializer.Deserialize<PurchaseAnalysisLine>(row)!).ToArray();
        purchaseRows[key] = (status.LastSuccess, lines);
        return new(status, lines);
        } finally { readLock.Release(); }
    }
    async Task IPurchaseAnalysisStore.ReplaceAsync(Guid company, long revision, PurchaseAnalysisSnapshot snapshot, CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var current = await db.QuerySingleOrDefaultAsync<long>(new CommandDefinition("SELECT revision FROM purchase_analysis_settings WHERE company_id=@company FOR UPDATE", new { company }, tx, cancellationToken: ct));
        if (current != revision) throw new PdmConflictException("采集期间设置已变化，本次数据未替换，请重新同步。");
        await db.ExecuteAsync(new CommandDefinition("DELETE FROM purchase_analysis_line WHERE company_id=@company", new { company }, tx, cancellationToken: ct));
        foreach (var batch in snapshot.Lines.Chunk(200)) {
            var sql = new StringBuilder("INSERT INTO purchase_analysis_line VALUES ");
            var parameters = new DynamicParameters(); parameters.Add("company", company);
            for (var i = 0; i < batch.Length; i++) {
                if (i > 0) sql.Append(',');
                sql.Append($"(@company,@organization{i},@id{i},@json{i})");
                parameters.Add($"organization{i}", batch[i].Organization); parameters.Add($"id{i}", batch[i].Id);
                parameters.Add($"json{i}", JsonSerializer.Serialize(batch[i]));
            }
            await db.ExecuteAsync(new CommandDefinition(sql.ToString(), parameters, tx, cancellationToken: ct));
        }
        await db.ExecuteAsync(new CommandDefinition("INSERT INTO purchase_analysis_status VALUES(@company,@json) ON DUPLICATE KEY UPDATE payload_json=@json", new { company, json = JsonSerializer.Serialize(snapshot.Status) }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        purchaseRows.TryRemove((connectionString, company), out _);
    }
    async Task IPurchaseAnalysisStore.FailureAsync(Guid company, DateTimeOffset at, string message, CancellationToken ct)
    {
        var previous = await ((IPurchaseAnalysisStore)this).ReadAsync(company, ct);
        await using var db = await OpenAsync(ct);
        await db.ExecuteAsync(new CommandDefinition("INSERT INTO purchase_analysis_status VALUES(@company,@json) ON DUPLICATE KEY UPDATE payload_json=@json",
            new { company, json = JsonSerializer.Serialize(previous.Status with { LastAttempt = at, Error = message }) }, cancellationToken: ct));
    }
    async Task<IReadOnlyList<Guid>> IPurchaseAnalysisStore.CompaniesAsync(CancellationToken ct)
    {
        await using var db = await OpenAsync(ct);
        return (await db.QueryAsync<Guid>(new CommandDefinition("SELECT company_id FROM purchase_analysis_settings", cancellationToken: ct))).ToArray();
    }
}

public sealed partial class InMemoryPdmRepository : IPurchaseAnalysisStore
{
    private readonly Dictionary<Guid, PurchaseAnalysisSettings> purchaseAnalysisSettings = [];
    private readonly Dictionary<Guid, PurchaseAnalysisSnapshot> purchaseAnalysisSnapshots = [];
    Task<PurchaseAnalysisSettings> IPurchaseAnalysisStore.SettingsAsync(Guid company, CancellationToken ct)
    { lock (gate) return Task.FromResult(purchaseAnalysisSettings.GetValueOrDefault(company) ?? new(0, [])); }
    Task IPurchaseAnalysisStore.SaveSettingsAsync(Guid company, PurchaseAnalysisSettings settings, CancellationToken ct)
    {
        lock (gate) {
            if ((purchaseAnalysisSettings.GetValueOrDefault(company)?.Revision ?? 0) != settings.Revision) throw new PdmConflictException("采集设置已变化，请重新加载。");
            purchaseAnalysisSettings[company] = settings with { Revision = settings.Revision + 1 }; return Task.CompletedTask;
        }
    }
    Task<PurchaseAnalysisSnapshot> IPurchaseAnalysisStore.ReadAsync(Guid company, CancellationToken ct)
    { lock (gate) return Task.FromResult(purchaseAnalysisSnapshots.GetValueOrDefault(company) ?? new(new(), [])); }
    Task IPurchaseAnalysisStore.ReplaceAsync(Guid company, long revision, PurchaseAnalysisSnapshot snapshot, CancellationToken ct)
    {
        lock (gate) {
            if ((purchaseAnalysisSettings.GetValueOrDefault(company)?.Revision ?? 0) != revision) throw new PdmConflictException("采集设置已变化，请重新同步。");
            purchaseAnalysisSnapshots[company] = snapshot; return Task.CompletedTask;
        }
    }
    Task IPurchaseAnalysisStore.FailureAsync(Guid company, DateTimeOffset at, string message, CancellationToken ct)
    {
        lock (gate) { var old = purchaseAnalysisSnapshots.GetValueOrDefault(company) ?? new(new(), []);
            purchaseAnalysisSnapshots[company] = old with { Status = old.Status with { LastAttempt = at, Error = message } }; return Task.CompletedTask; }
    }
    Task<IReadOnlyList<Guid>> IPurchaseAnalysisStore.CompaniesAsync(CancellationToken ct)
    { lock (gate) return Task.FromResult<IReadOnlyList<Guid>>(purchaseAnalysisSettings.Keys.ToArray()); }
}
