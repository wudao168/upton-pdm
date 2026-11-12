using Dapper;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    async Task<AiWebUsage> IAiStore.WebUsageAsync(Guid company, DateTimeOffset at, CancellationToken ct)
    {
        var (day, month) = AiWebPeriods.For(at);
        await using var db = await OpenAsync(ct);
        var rows = (await db.QueryAsync<(string Period, int Calls)>(new CommandDefinition(
            "SELECT period_key Period,calls Calls FROM ai_web_usage WHERE company_id=@company AND period_key IN (@day,@month)",
            new { company, day, month }, cancellationToken: ct))).ToDictionary(r => r.Period, r => r.Calls);
        return new(rows.GetValueOrDefault(day), rows.GetValueOrDefault(month));
    }

    async Task<bool> IAiStore.ReserveWebCallAsync(Guid company, DateTimeOffset at, AiWebSettings limits, CancellationToken ct)
    {
        var (day, month) = AiWebPeriods.For(at);
        await using var db = await OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        // Always lock month before day, including requests crossing midnight. Failed attempts retain their reservation.
        foreach (var period in new[] { month, day })
        {
            // A duplicate INSERT IGNORE takes a shared lock; parallel readers upgrading to FOR UPDATE deadlock.
            // The no-op upsert obtains the exclusive row lock immediately and preserves the existing count.
            await db.ExecuteAsync(new CommandDefinition("INSERT INTO ai_web_usage(company_id,period_key,calls) VALUES(@company,@period,0) ON DUPLICATE KEY UPDATE calls=calls", new { company, period }, tx, cancellationToken: ct));
            var calls = await db.ExecuteScalarAsync<int>(new CommandDefinition("SELECT calls FROM ai_web_usage WHERE company_id=@company AND period_key=@period FOR UPDATE", new { company, period }, tx, cancellationToken: ct));
            if (calls >= (period == month ? limits.MonthlyLimit : limits.DailyLimit)) return false;
            await db.ExecuteAsync(new CommandDefinition("UPDATE ai_web_usage SET calls=calls+1 WHERE company_id=@company AND period_key=@period", new { company, period }, tx, cancellationToken: ct));
        }
        await tx.CommitAsync(ct); return true;
    }
}

public sealed partial class InMemoryPdmRepository
{
    private readonly Dictionary<(Guid Company, string Period), int> aiWebCalls = [];
    Task<AiWebUsage> IAiStore.WebUsageAsync(Guid company, DateTimeOffset at, CancellationToken ct)
    {
        var (day, month) = AiWebPeriods.For(at);
        lock (aiGate) return Task.FromResult(new AiWebUsage(aiWebCalls.GetValueOrDefault((company, day)), aiWebCalls.GetValueOrDefault((company, month))));
    }
    Task<bool> IAiStore.ReserveWebCallAsync(Guid company, DateTimeOffset at, AiWebSettings limits, CancellationToken ct)
    {
        var (day, month) = AiWebPeriods.For(at);
        lock (aiGate)
        {
            var today = aiWebCalls.GetValueOrDefault((company, day)); var monthly = aiWebCalls.GetValueOrDefault((company, month));
            if (today >= limits.DailyLimit || monthly >= limits.MonthlyLimit) return Task.FromResult(false);
            aiWebCalls[(company, day)] = today + 1; aiWebCalls[(company, month)] = monthly + 1; return Task.FromResult(true);
        }
    }
}

internal static class AiWebPeriods
{
    public static (string Day, string Month) For(DateTimeOffset at)
    {
        var beijing = at.ToOffset(TimeSpan.FromHours(8));
        return (beijing.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture), beijing.ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture));
    }
}
