using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed record StorageBackupSource(string Path, string[] Categories, bool IsDirectory, bool Required)
{
    public bool Exists => IsDirectory ? Directory.Exists(Path) : File.Exists(Path);
}
public sealed record StorageBackupIssue(string Category, string Path, string Message);
public sealed record StorageBackupReference(string Path, long Length, string? Sha256, string Category);
public sealed record StorageBackupPlan(string Format, string BackupRoot, string Database, IReadOnlyList<StorageBackupSource> Sources,
    IReadOnlyList<StorageBackupReference> References, IReadOnlyList<string> Warnings)
{
    public IReadOnlyDictionary<string, long> TableCounts { get; init; } = new Dictionary<string, long>();
    public IReadOnlyList<StorageBackupIssue> Issues { get; init; } = [];
}

// The API and the offline backup command use this same inventory. No paths come from a fixed .local/vault assumption.
public sealed class StorageBackupPlanner(IOptions<PdmDatabaseOptions> database, IOptions<PdmStorageOptions> options,
    IPdmRepository repository)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<StorageBackupPlan> BuildAsync(ServerStorageSettings settings, CancellationToken ct, bool includeTableCounts = false, bool verifyReferences = false)
    {
        var sources = new Dictionary<string, StorageBackupSource>(StringComparer.OrdinalIgnoreCase);
        var references = new Dictionary<string, StorageBackupReference>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var tableCounts = new Dictionary<string, long>();
        void Root(string category, string? path, bool required = false, bool directory = true)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            path = StorageLocationPolicy.Normalize(path);
            if (sources.TryGetValue(path, out var previous))
                sources[path] = previous with { Categories = previous.Categories.Append(category).Distinct().ToArray(), Required = previous.Required || required };
            else sources[path] = new(path, [category], directory, required);
        }
        void Reference(string category, string root, string relative, long length, string? hash)
        {
            var path = StorageLocationPolicy.ResolveUnder(root, relative);
            if (references.TryGetValue(path, out var previous)
                && (previous.Length != length || previous.Sha256 is not null && hash is not null && !previous.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase)))
                throw new PdmConflictException("同一存档路径的历史记录存在内容冲突，请先核对文件版本，备份未执行。");
            references[path] = new(path, length, hash, category);
            Root(category, root, true);
        }

        var system = await repository.GetSystemSettingsAsync(ct);
        Root("图档库", system.VaultRoot); Root("生产发包", system.ReleaseRoot); Root("料品与套件附件", system.MaterialAttachmentRoot);
        foreach (var project in await repository.ListProjectsAsync(ct))
        {
            Root("项目图档历史位置", project.VaultLocation); Root("项目发包历史位置", project.ReleaseLocation);
        }
        Root("普通项目文件", settings.ProjectFileRoot); Root("OPL附件", settings.OplAttachmentRoot);
        Root("旧版OPL附件", settings.LegacyOplAttachmentRoot);
        Root("程序模板", settings.ProgramTemplateRoot); Root("上传过程与回执", settings.UploadTempRoot);
        Root("网页CAD存档任务", settings.BrowserCadArchiveRoot); Root("本机转换过程", settings.ConversionWorkRoot);
        Root("客户端诊断日志", settings.ClientLogRoot); Root("标准结构默认位置", options.Value.StandardStructureRoot);
        foreach (var history in settings.HistoricalRoots)
            foreach (var root in history.Value) Root("历史位置：" + history.Key, root);
        var secrets = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "secrets"));
        Root("恢复凭据与加密密钥", secrets, database.Value.Provider.Equals("MySql", StringComparison.OrdinalIgnoreCase));
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "appsettings*.json")) Root("服务器配置", file, true, false);
        var workerRootsFile = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "cad-archive-worker", "storage-roots.txt"));
        if (File.Exists(workerRootsFile)) Root("网页存档后台目录配置", workerRootsFile, true, false);
        warnings.Add("密钥采用Windows本机保护；跨服务器恢复需先验证密钥解密，或重新配置受保护的集成凭据。");
        warnings.Add("远程转图电脑的工作目录和各客户端未提交草稿不在服务器文件系统中，需独立保护；正式转图成果已按服务器档案纳入。");

        var dbName = "";
        if (database.Value.Provider.Equals("MySql", StringComparison.OrdinalIgnoreCase))
        {
            dbName = new MySqlConnectionStringBuilder(database.Value.ConnectionString).Database;
            await using var db = new MySqlConnection(database.Value.ConnectionString);
            // Backups are server-wide, including roots outside the currently selected company and unindexed old files.
            foreach (var projectRoot in await db.QueryAsync<FileRow>(new CommandDefinition("""
                SELECT vault_location Root,'项目图档历史位置' Category FROM project
                UNION SELECT release_location,'项目发包历史位置' FROM project
                """, cancellationToken: ct))) Root(projectRoot.Category, projectRoot.Root);
            // Include all versions and removed records. A migration must never silently discard historical attachments.
            var rows = await db.QueryAsync<FileRow>(new CommandDefinition("""
                SELECT p.vault_location Root,v.storage_relative_path RelativePath,v.file_length Length,v.sha256 Sha256,'图档版本' Category
                FROM document_version v JOIN document d ON d.id=v.document_id JOIN project p ON p.id=d.project_id
                UNION ALL SELECT p.vault_location,v.preview_storage_relative_path,v.preview_file_length,v.preview_sha256,'图档预览'
                FROM document_version v JOIN document d ON d.id=v.document_id JOIN project p ON p.id=d.project_id WHERE v.preview_storage_relative_path IS NOT NULL
                UNION ALL SELECT storage_root,storage_relative_path,file_length,sha256,'料品附件' FROM material_attachment
                UNION ALL SELECT storage_root,storage_relative_path,file_length,sha256,'套件附件' FROM engineering_kit_attachment
                UNION ALL SELECT storage_root,storage_relative_path,file_length,sha256,'项目文件版本' FROM project_file_version
                UNION ALL SELECT p.vault_location,r.storage_relative_path,r.file_length,r.sha256,'检验资料' FROM quality_inspection_record r JOIN project p ON p.id=r.project_id
                UNION ALL SELECT p.vault_location,a.storage_relative_path,a.file_length,a.sha256,'验证计划附件' FROM validation_plan_attachment a JOIN project_validation_plan v ON v.id=a.plan_id JOIN project p ON p.id=v.project_id
                UNION ALL SELECT p.vault_location,a.storage_relative_path,a.file_length,a.sha256,'质量验收附件' FROM quality_validation_plan_attachment a JOIN quality_project_validation_plan v ON v.id=a.plan_id JOIN project p ON p.id=v.project_id
                """, cancellationToken: ct));
            foreach (var file in rows.Where(f => !string.IsNullOrWhiteSpace(f.RelativePath))) Reference(file.Category, file.Root, file.RelativePath, file.Length, file.Sha256);

            foreach (var json in await db.QueryAsync<string>(new CommandDefinition("SELECT payload_json FROM bom_file_catalog", cancellationToken: ct)))
            {
                using var payload = JsonDocument.Parse(json);
                if (!payload.RootElement.TryGetProperty("projectFiles", out var files)) continue;
                foreach (var entry in files.EnumerateArray())
                {
                    var file = entry.GetProperty("file");
                    Reference("BOM上传资料", file.GetProperty("storageRoot").GetString()!, file.GetProperty("storageRelativePath").GetString()!,
                        file.GetProperty("fileLength").GetInt64(), file.GetProperty("sha256").GetString());
                }
            }
            foreach (var json in await db.QueryAsync<string>(new CommandDefinition("SELECT payload_json FROM standard_structure_catalog", cancellationToken: ct)))
            {
                var catalog = JsonSerializer.Deserialize<StandardStructureCatalog>(json, Json)!;
                var root = string.IsNullOrWhiteSpace(catalog.StorageRoot) ? options.Value.StandardStructureRoot : catalog.StorageRoot;
                Root("标准结构库", root);
                foreach (var revision in catalog.Structures.SelectMany(s => s.Revisions))
                foreach (var file in revision.Files)
                    Reference("标准结构历史版本", revision.StorageRoot ?? root,
                        Path.Combine((revision.StorageCompanyId ?? catalog.CompanyId).ToString("N"), revision.Id.ToString("N"), file.Key + Path.GetExtension(file.FileName).ToLowerInvariant()), file.Length, file.Sha256);
            }
            var templateFiles = await db.QueryAsync<FileRow>(new CommandDefinition("""
                SELECT package_storage_path RelativePath,package_file_length Length,package_sha256 Sha256 FROM program_template_revision WHERE package_storage_path IS NOT NULL
                UNION ALL SELECT evidence_storage_path,evidence_file_length,evidence_sha256 FROM program_template_revision WHERE evidence_storage_path IS NOT NULL
                """, cancellationToken: ct));
            foreach (var file in templateFiles)
            {
                var root = new[] { settings.ProgramTemplateRoot }.Concat(settings.HistoricalRoots.GetValueOrDefault("programTemplates") ?? [])
                    .FirstOrDefault(r => File.Exists(StorageLocationPolicy.ResolveUnder(r, file.RelativePath))) ?? settings.ProgramTemplateRoot;
                Reference("程序模板历史文件", root, file.RelativePath, file.Length, file.Sha256);
            }
            foreach (var json in await db.QueryAsync<string>(new CommandDefinition("SELECT payload_json FROM project_opl_issue", cancellationToken: ct)))
            {
                var issue = JsonSerializer.Deserialize<OplIssue>(json, Json)!;
                foreach (var file in issue.Attachments)
                    Reference("OPL附件", file.StorageRoot ?? settings.LegacyOplAttachmentRoot,
                        Path.Combine("project-opl", (file.StorageProjectId ?? issue.AttachmentProjectId ?? issue.ProjectId).ToString("N"), file.Id.ToString("N")), file.Length, null);
            }
            foreach (var path in await db.QueryAsync<string>(new CommandDefinition("SELECT published_path FROM release_package WHERE published_path IS NOT NULL AND published_path<>''", cancellationToken: ct)))
                Root("历史发布包", path, true);
            if (includeTableCounts)
                foreach (var table in await db.QueryAsync<string>(new CommandDefinition("SELECT table_name FROM information_schema.tables WHERE table_schema=DATABASE() AND table_type='BASE TABLE'", cancellationToken: ct)))
                    tableCounts[table] = await db.ExecuteScalarAsync<long>(new CommandDefinition($"SELECT COUNT(*) FROM `{table.Replace("`", "``")}`", cancellationToken: ct));
        }
        else warnings.Add("当前为内存数据库，只能验证目录覆盖，不能生成正式数据库备份。");
        if (string.IsNullOrWhiteSpace(settings.BackupRoot)) warnings.Add("备份目标位置待确认，尚未配置；禁止自动选择其他目录开始备份。");
        var absentOptional = sources.Values.Count(s => !s.Required && !s.Exists);
        if (absentOptional > 0) warnings.Add($"有{absentOptional}个无文件引用的目录尚未创建或已停用，保留在来源台账中，不影响已存在文件的收集。");
        var issues = new List<StorageBackupIssue>();
        if (verifyReferences)
            foreach (var reference in references.Values)
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(reference.Path)) issues.Add(new(reference.Category, reference.Path, "数据库引用的文件不存在，请先查找历史副本；未删除版本记录。"));
                else if (new FileInfo(reference.Path).Length != reference.Length) issues.Add(new(reference.Category, reference.Path, "实体长度与历史版本记录不符，请核查原件；未覆盖文件或改写版本。"));
            }
        foreach (var source in sources.Values.Where(s => s.Required && !s.Exists))
            issues.Add(new(string.Join("、", source.Categories), source.Path, "数据库引用目录或恢复配置缺失，不能生成完整备份。"));
        if (issues.Count > 0) warnings.Add($"发现{issues.Count}项历史文件或目录完整性问题，详见问题清单；不能将此清单视为完整备份。完整备份时还会逐文件校验SHA256。");
        var result = new StorageBackupPlan("upton-pdm-backup-plan-v2", settings.BackupRoot, dbName,
            sources.Values.OrderBy(s => s.Path, StringComparer.OrdinalIgnoreCase).ToArray(), references.Values.ToArray(), warnings) { TableCounts = tableCounts, Issues = issues };
        if (!string.IsNullOrWhiteSpace(settings.BackupRoot)) ValidateDestination(settings.BackupRoot, result.Sources);
        return result;
    }

    public static void ValidateDestination(string destination, IEnumerable<StorageBackupSource> sources)
    {
        var root = StorageLocationPolicy.Normalize(destination);
        foreach (var source in sources)
        {
            var path = StorageLocationPolicy.Normalize(source.Path);
            if (root.Equals(path, StringComparison.OrdinalIgnoreCase) || root.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new PdmRuleException("备份目标必须独立于存档、暂存、日志和密钥目录，不能相同或互相包含。");
        }
    }

    private sealed class FileRow
    {
        public string Root { get; init; } = "";
        public string RelativePath { get; init; } = "";
        public long Length { get; init; }
        public string? Sha256 { get; init; }
        public string Category { get; init; } = "";
    }
}
