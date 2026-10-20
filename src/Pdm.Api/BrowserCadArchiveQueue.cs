using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api;

public sealed record BrowserCadBase(PdmDocument Document, DocumentVersion? Version);
public sealed record BrowserCadJob(Guid Id, Guid CompanyId, string Owner, BrowserCadArchiveTarget Target,
    string State, string Stage, IReadOnlyList<BrowserCadArchiveFile> Files, IReadOnlyList<BrowserCadBase> Baselines,
    string? RootPath = null, string? Error = null, int ArchivedFiles = 0, int NewVersions = 0,
    string? ResultCode = null, IReadOnlyList<string>? Warnings = null);

public sealed class BrowserCadArchiveQueue(IOptions<PdmStorageOptions> options)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> gates = new();
    public string Root { get; } = Path.GetFullPath(Path.Combine(options.Value.UploadTempRoot, "..", "browser-cad-archives"));
    public SemaphoreSlim Gate(Guid id) => gates.GetOrAdd(id, _ => new(1, 1));
    public string DirectoryFor(Guid id) => Path.Combine(Root, id.ToString("N"));
    public string Under(Guid id, string relative)
    {
        var directory = DirectoryFor(id);
        var path = Path.GetFullPath(Path.Combine(directory, relative));
        if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("文件路径越界，未写入任何图档。");
        return path;
    }
    public BrowserCadJob? Read(Guid id)
    {
        var path = Under(id, "job.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<BrowserCadJob>(File.ReadAllText(path), Json) : null;
    }
    public async Task SaveAsync(BrowserCadJob job, CancellationToken ct)
    {
        Directory.CreateDirectory(DirectoryFor(job.Id));
        var path = Under(job.Id, "job.json"); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(job, Json), ct);
        File.Move(temporary, path, true);
    }
    public IEnumerable<BrowserCadJob> Pending()
    {
        if (!Directory.Exists(Root)) yield break;
        foreach (var directory in Directory.GetDirectories(Root))
            if (Guid.TryParseExact(Path.GetFileName(directory), "N", out var id))
            {
                BrowserCadJob? job = null;
                try { job = Read(id); } catch (IOException) { } catch (JsonException) { }
                if (job?.State is "Queued" or "Processing" or "Committing") yield return job;
            }
    }
    public static BrowserCadArchiveView View(BrowserCadJob job) => new(job.Id, job.State, job.Stage, job.Files,
        job.RootPath, job.Error, job.ArchivedFiles, job.NewVersions, job.ResultCode, job.Warnings);
    public static void RequireOwner(BrowserCadJob job, string actor)
    {
        if (job.CompanyId != TenantContext.CompanyId || !string.Equals(job.Owner, actor, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("只能查看或继续本人在当前公司发起的存档。");
    }
    public static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
    }
}

public sealed class FileBrowserCadReader : IBrowserCadReader
{
    public async Task<BrowserCadNativeResult> ReadAsync(string directory, BrowserCadNativeRequest input, CancellationToken ct)
    {
        var result = Path.Combine(directory, "native-result.json");
        if (!File.Exists(result))
        {
            var request = Path.Combine(directory, "native-request.json");
            var temporary = request + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(input, BrowserCadArchiveQueue.Json), ct);
            File.Move(temporary, request, true);
            var until = DateTimeOffset.UtcNow.AddMinutes(31);
            while (!File.Exists(result))
            {
                ct.ThrowIfCancellationRequested();
                if (DateTimeOffset.UtcNow > until) throw new PdmRuleException("后台解析服务暂未完成任务，上传文件已保留，可稍后继续。");
                await Task.Delay(1000, ct);
            }
        }
        return JsonSerializer.Deserialize<BrowserCadNativeResult>(await File.ReadAllTextAsync(result, ct), BrowserCadArchiveQueue.Json)
            ?? throw new PdmRuleException("后台解析结果无法读取，上传文件已保留。");
    }
}
