using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api;

public sealed class ServerStorageAdministration(
    ServerStorageSettingsStore store, IPdmRepository repository, IOptions<PdmStorageOptions> storage,
    IOptions<ClientLogOptions> logs, IOptions<PdmDatabaseOptions> database, TimeProvider clock)
{
    private static readonly object WorkerConfigurationGate = new();
    public static string SecretsRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "secrets"));
    public static string CadWorkerRootsFile => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "cad-archive-worker", "storage-roots.txt"));

    public async Task<ServerStorageSettings> ReadAsync(CancellationToken ct)
    {
        var saved = await store.ReadAsync(ct);
        if (saved is not null) return saved;
        var system = await repository.GetSystemSettingsAsync(ct);
        return new()
        {
            ProjectFileRoot = system.VaultRoot, OplAttachmentRoot = system.VaultRoot, LegacyOplAttachmentRoot = system.VaultRoot,
            ProgramTemplateRoot = storage.Value.ProgramTemplateRoot, UploadTempRoot = storage.Value.UploadTempRoot,
            BrowserCadArchiveRoot = Path.GetFullPath(Path.Combine(storage.Value.UploadTempRoot, "..", "browser-cad-archives")),
            ConversionWorkRoot = storage.Value.ConversionWorkRoot, ClientLogRoot = logs.Value.Root
        };
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        var settings = await ReadAsync(ct);
        if (File.Exists(CadWorkerRootsFile))
        {
            string[] configuredRoots;
            lock (WorkerConfigurationGate) configuredRoots = File.ReadAllLines(CadWorkerRootsFile);
            var previous = configuredRoots.Where(Path.IsPathFullyQualified)
                .Where(p => !p.Equals(settings.BrowserCadArchiveRoot, StringComparison.OrdinalIgnoreCase)).ToArray();
            var history = settings.HistoricalRoots.ToDictionary(p => p.Key, p => p.Value);
            var roots = (history.GetValueOrDefault("browserCad") ?? []).Concat(previous).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (!roots.SequenceEqual(history.GetValueOrDefault("browserCad") ?? [], StringComparer.OrdinalIgnoreCase))
            {
                history["browserCad"] = roots;
                settings = await store.SaveAsync(settings with { HistoricalRoots = history }, settings.Version, ct);
            }
        }
        if (settings.Version == 0)
        {
            try { settings = await store.SaveAsync(settings, 0, ct); }
            catch (PdmConflictException) { settings = (await store.ReadAsync(ct))!; }
        }
        Apply(settings);
    }

    public async Task<object> ViewAsync(CancellationToken ct)
    {
        var settings = await ReadAsync(ct);
        var actualDatabaseRoot = "";
        if (database.Value.Provider.Equals("MySql", StringComparison.OrdinalIgnoreCase))
        {
            await using var db = new MySqlConnection(database.Value.ConnectionString);
            actualDatabaseRoot = await db.ExecuteScalarAsync<string>(new CommandDefinition("SELECT @@datadir", cancellationToken: ct)) ?? "";
        }
        return new
        {
            Settings = settings, DatabaseDataRoot = actualDatabaseRoot, SecretsRoot,
            DatabaseBackupRoot = string.IsNullOrWhiteSpace(settings.BackupRoot) ? "" : Path.Combine(settings.BackupRoot, "<备份批次>", "database"),
            FilesBackupRoot = string.IsNullOrWhiteSpace(settings.BackupRoot) ? "" : Path.Combine(settings.BackupRoot, "<备份批次>", "data"),
            PreviewAgentUrl = (await repository.GetSystemSettingsAsync(ct)).PreviewConversion.NormalizedAgentUrl,
            HistoryPolicy = "历史存档及历史路径全部保留；保存设置只改变后续写入位置，不搬迁或删除已有文件。数据库、密钥和远程转图目录的目标配置需正式迁移后生效。"
        };
    }

    public async Task<ServerStorageSettings> SaveAsync(ServerStorageSettings input, string actor, CancellationToken ct)
    {
        var current = await ReadAsync(ct);
        if (input.Version != current.Version) throw new PdmConflictException("存储设置已变化，请重新加载后保存。");
        string Required(string path) => StorageLocationPolicy.Normalize(path);
        string Optional(string path) => string.IsNullOrWhiteSpace(path) ? "" : Required(path);
        var history = current.HistoricalRoots.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var next = input with
        {
            ProjectFileRoot = Required(input.ProjectFileRoot), OplAttachmentRoot = Required(input.OplAttachmentRoot),
            LegacyOplAttachmentRoot = current.LegacyOplAttachmentRoot,
            ProgramTemplateRoot = Required(input.ProgramTemplateRoot), UploadTempRoot = Required(input.UploadTempRoot),
            BrowserCadArchiveRoot = Required(input.BrowserCadArchiveRoot), ConversionWorkRoot = Required(input.ConversionWorkRoot),
            ClientLogRoot = Required(input.ClientLogRoot), BackupRoot = Optional(input.BackupRoot),
            DatabaseDataTarget = Optional(input.DatabaseDataTarget), SecretsTarget = Optional(input.SecretsTarget),
            PreviewAgentWorkTarget = Optional(input.PreviewAgentWorkTarget), HistoricalRoots = history
        };
        foreach (var (kind, oldPath, newPath) in new[]
        {
            ("projectFiles", current.ProjectFileRoot, next.ProjectFileRoot), ("opl", current.OplAttachmentRoot, next.OplAttachmentRoot),
            ("programTemplates", current.ProgramTemplateRoot, next.ProgramTemplateRoot), ("uploads", current.UploadTempRoot, next.UploadTempRoot),
            ("browserCad", current.BrowserCadArchiveRoot, next.BrowserCadArchiveRoot), ("clientLogs", current.ClientLogRoot, next.ClientLogRoot),
            ("conversion", current.ConversionWorkRoot, next.ConversionWorkRoot)
        })
        {
            if (newPath.Split(Path.DirectorySeparatorChar).Any(p => p.Equals("wwwroot", StringComparison.OrdinalIgnoreCase)))
                throw new PdmRuleException("存档、暂存和日志目录不能放在公开网页目录中。");
            if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                history[kind] = (history.GetValueOrDefault(kind) ?? []).Append(oldPath).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        if (next.BackupRoot.Length > 0)
        {
            var plan = await new StorageBackupPlanner(database, storage, repository).BuildAsync(next, ct);
            StorageBackupPlanner.ValidateDestination(next.BackupRoot, plan.Sources);
        }
        var saved = await store.SaveAsync(next, current.Version, ct);
        Apply(saved);
        await repository.AppendAuditAsync(new(Guid.NewGuid(), clock.GetUtcNow(), actor, "system.storage.locations.update", nameof(ServerStorageSettings),
            "server-storage", "更新集中存储与备份位置；历史文件和旧路径保留，未执行正式迁移。"), ct);
        return saved;
    }

    private void Apply(ServerStorageSettings value)
    {
        storage.Value.ProjectFileRoot = value.ProjectFileRoot;
        storage.Value.OplAttachmentRoot = value.OplAttachmentRoot;
        storage.Value.LegacyOplAttachmentRoot = value.LegacyOplAttachmentRoot;
        storage.Value.ProgramTemplateRoot = value.ProgramTemplateRoot;
        storage.Value.UploadTempRoot = value.UploadTempRoot;
        storage.Value.BrowserCadArchiveRoot = value.BrowserCadArchiveRoot;
        storage.Value.ConversionWorkRoot = value.ConversionWorkRoot;
        storage.Value.HistoricalRoots = value.HistoricalRoots;
        logs.Value.Root = value.ClientLogRoot;
        logs.Value.HistoricalRoots = value.HistoricalRoots.GetValueOrDefault("clientLogs") ?? [];
        Directory.CreateDirectory(Path.GetDirectoryName(CadWorkerRootsFile)!);
        var temporary = CadWorkerRootsFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllLines(temporary, storage.Value.Roots("browserCad", value.BrowserCadArchiveRoot));
            lock (WorkerConfigurationGate)
                for (var attempt = 0; ; attempt++)
                    try { File.Move(temporary, CadWorkerRootsFile, true); break; }
                    catch (IOException) when (attempt < 10) { Thread.Sleep(25); }
                    catch (UnauthorizedAccessException) when (attempt < 10) { Thread.Sleep(25); }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public static class ServerStorageEndpoints
{
    public static void MapServerStorageEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/system-settings/storage").RequireAuthorization();
        api.MapGet("", async (HttpContext context, IPdmRepository repository, ServerStorageAdministration storage, CancellationToken ct) =>
        {
            var (actor, role) = PdmEndpointExtensions.CurrentUser(context.User);
            if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, ct)) return Results.Forbid();
            return Results.Ok(await storage.ViewAsync(ct));
        });
        api.MapPut("", async (ServerStorageSettings input, HttpContext context, IPdmRepository repository, ServerStorageAdministration storage, CancellationToken ct) =>
        {
            var (actor, role) = PdmEndpointExtensions.CurrentUser(context.User);
            if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, ct)) return Results.Forbid();
            await storage.SaveAsync(input, actor, ct);
            return Results.Ok(await storage.ViewAsync(ct));
        });
        api.MapGet("/backup-plan", async (HttpContext context, IPdmRepository repository, ServerStorageAdministration storage, StorageBackupPlanner planner, CancellationToken ct) =>
        {
            var (actor, role) = PdmEndpointExtensions.CurrentUser(context.User);
            if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, ct)) return Results.Forbid();
            var plan = await planner.BuildAsync(await storage.ReadAsync(ct), ct, verifyReferences: true);
            return Results.Ok(new { plan.Format, plan.BackupRoot, plan.Sources, ReferenceCount = plan.References.Count, plan.Warnings, plan.Issues,
                Message = plan.BackupRoot.Length == 0 ? "备份目标待确认；当前仅核对覆盖范围，未执行备份。" : "已生成覆盖清单，未执行备份。" });
        });
    }
}
