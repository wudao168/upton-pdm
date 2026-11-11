using System.Text.Json;

namespace Upton.Pdm.Infrastructure;

public sealed record PreviewDispatchRecord(Guid Id, string Server, DateTimeOffset Started, DateTimeOffset? Finished,
    Guid[] Documents, string Message, IReadOnlyDictionary<Guid,string>? Errors)
{
    public IReadOnlyDictionary<Guid,string>? Progress { get; init; }
}

public sealed class PreviewDispatchHistory
{
    private readonly string path;
    private PreviewDispatchRecord record;
    private PreviewDispatchHistory(string path, PreviewDispatchRecord record) { this.path = path; this.record = record; }

    public static PreviewDispatchHistory Start(string root, Guid packageId, Guid attempt, string server, Guid[] documents)
    {
        var folder = Path.Combine(root, "dispatch-history", packageId.ToString("N"));
        var history = new PreviewDispatchHistory(Path.Combine(folder, $"{DateTime.UtcNow.Ticks}-{attempt:N}.json"),
            new(attempt, server, DateTimeOffset.UtcNow, null, documents, "已派发，等待代理返回结果", null));
        history.Save();
        return history;
    }

    public void Finish(IReadOnlyDictionary<Guid,string> errors)
    {
        record = record with { Finished = DateTimeOffset.UtcNow, Message = $"已返回，成功 {record.Documents.Length-errors.Count} 项，失败 {errors.Count} 项", Errors = errors };
        Save();
    }
    public void End(string reason) { record = record with { Finished = DateTimeOffset.UtcNow, Message = reason }; Save(); }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(record));
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { System.Diagnostics.Trace.TraceWarning("转图派发日志写入失败：{0}", error); }
    }
    public static IReadOnlyList<PreviewDispatchRecord> Read(string root, Guid packageId)
    {
        var folder = Path.Combine(root, "dispatch-history", packageId.ToString("N"));
        if (!Directory.Exists(folder)) return [];
        var result = new List<PreviewDispatchRecord>();
        foreach (var file in Directory.EnumerateFiles(folder, "*.json").OrderDescending().Take(100))
        {
            try { if (JsonSerializer.Deserialize<PreviewDispatchRecord>(File.ReadAllText(file)) is {} row) result.Add(row); }
            catch (Exception error) when (error is IOException or JsonException) { System.Diagnostics.Trace.TraceWarning("无法读取转图派发日志：{0}", error); }
        }
        return result;
    }
}
