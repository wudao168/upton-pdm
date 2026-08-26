using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class MySqlProjectFileRepository : IProjectFileRepository
{
    private readonly string connectionString;
    private readonly TimeProvider timeProvider;

    public MySqlProjectFileRepository(IOptions<PdmDatabaseOptions> options, TimeProvider timeProvider)
    {
        connectionString = options.Value.ConnectionString;
        this.timeProvider = timeProvider;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public async Task<IReadOnlyList<ProjectFile>> ListAsync(Guid rootProjectId, Guid? folderId, bool includeDeleted, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<ProjectFileRow>(new CommandDefinition(
            Select + " WHERE f.root_project_id=@RootProjectId AND (@FolderId IS NULL OR f.folder_id=@FolderId) AND (@IncludeDeleted OR f.deleted_at IS NULL) ORDER BY f.deleted_at IS NOT NULL,f.file_name",
            new { RootProjectId = rootProjectId, FolderId = folderId, IncludeDeleted = includeDeleted }, cancellationToken: cancellationToken));
        return rows.Select(row => Map(row)!).ToArray();
    }

    public async Task<ProjectFile?> FindAsync(Guid fileId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return Map(await connection.QuerySingleOrDefaultAsync<ProjectFileRow>(new CommandDefinition(Select + " WHERE f.id=@FileId", new { FileId = fileId }, cancellationToken: cancellationToken)));
    }

    public async Task<IReadOnlyList<ProjectFileVersion>> ListVersionsAsync(Guid fileId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<ProjectFileVersionRow>(new CommandDefinition(VersionSelect + " WHERE project_file_id=@FileId ORDER BY version_number DESC", new { FileId = fileId }, cancellationToken: cancellationToken));
        return rows.Select(row => MapVersion(row)!).ToArray();
    }

    public async Task<ProjectFileVersion?> FindVersionAsync(Guid fileId, Guid versionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return MapVersion(await connection.QuerySingleOrDefaultAsync<ProjectFileVersionRow>(new CommandDefinition(VersionSelect + " WHERE project_file_id=@FileId AND id=@VersionId", new { FileId = fileId, VersionId = versionId }, cancellationToken: cancellationToken)));
    }

    public async Task<ProjectFile> AddVersionAsync(StoredProjectFileUpload upload, string actor, string? comment, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var rootId = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT root_project_id FROM project_folder WHERE id=@FolderId AND root_project_id=COALESCE((SELECT root_project_id FROM project WHERE id=@ProjectId),(SELECT id FROM project WHERE id=@ProjectId))",
            new { upload.FolderId, upload.ProjectId }, transaction, cancellationToken: cancellationToken))
            ?? throw new PdmNotFoundException("项目文件夹不存在。");
        var fileId = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM project_file WHERE folder_id=@FolderId AND deleted_at IS NULL AND LOWER(file_name)=LOWER(@FileName) FOR UPDATE",
            new { upload.FolderId, upload.FileName }, transaction, cancellationToken: cancellationToken));
        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (fileId is null)
        {
            fileId = Guid.NewGuid();
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO project_file(id,root_project_id,folder_id,file_name,created_by,created_at,updated_by,updated_at) VALUES(@Id,@RootProjectId,@FolderId,@FileName,@Actor,@Now,@Actor,@Now)",
                new { Id = fileId, RootProjectId = rootId, upload.FolderId, upload.FileName, Actor = actor, Now = now }, transaction, cancellationToken: cancellationToken));
        }
        var versionNumber = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COALESCE(MAX(version_number),0)+1 FROM project_file_version WHERE project_file_id=@FileId",
            new { FileId = fileId }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO project_file_version(id,project_file_id,version_number,file_name,storage_root,storage_relative_path,file_length,sha256,uploaded_by,uploaded_at,comment) VALUES(@Id,@FileId,@VersionNumber,@FileName,@StorageRoot,@RelativePath,@Length,@Sha256,@Actor,@StoredAt,@Comment)",
            new { Id = upload.VersionId, FileId = fileId, VersionNumber = versionNumber, upload.FileName, upload.StorageRoot, upload.RelativePath, upload.Length, upload.Sha256, Actor = actor, StoredAt = upload.StoredAt.UtcDateTime, Comment = comment }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE project_file SET updated_by=@Actor,updated_at=@Now WHERE id=@FileId",
            new { Actor = actor, Now = now, FileId = fileId }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return await FindAsync(fileId.Value, cancellationToken) ?? throw new PdmNotFoundException("项目文件保存失败。");
    }

    public Task<ProjectFile> RenameAsync(Guid fileId, string fileName, string actor, CancellationToken cancellationToken) => UpdateAsync(fileId,
        "UPDATE project_file SET file_name=@Value,updated_by=@Actor,updated_at=@Now WHERE id=@FileId", fileName, actor, cancellationToken);

    public Task<ProjectFile> MoveAsync(Guid fileId, Guid folderId, string actor, CancellationToken cancellationToken) => UpdateAsync(fileId,
        "UPDATE project_file f INNER JOIN project_folder d ON d.id=@Value AND d.root_project_id=f.root_project_id SET f.folder_id=d.id,f.updated_by=@Actor,f.updated_at=@Now WHERE f.id=@FileId", folderId, actor, cancellationToken);

    public async Task<ProjectFile> SetDeletedAsync(Guid fileId, bool deleted, string actor, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        int affected;
        try
        {
            affected = await connection.ExecuteAsync(new CommandDefinition(
                deleted
                    ? "UPDATE project_file SET deleted_at=@Now,deleted_by=@Actor,updated_by=@Actor,updated_at=@Now WHERE id=@FileId AND deleted_at IS NULL"
                    : "UPDATE project_file SET deleted_at=NULL,deleted_by=NULL,updated_by=@Actor,updated_at=@Now WHERE id=@FileId AND deleted_at IS NOT NULL",
                new { FileId = fileId, Actor = actor, Now = timeProvider.GetUtcNow().UtcDateTime }, cancellationToken: cancellationToken));
        }
        catch (MySqlException exception) when (exception.Number == 1062) { throw new PdmConflictException("该目录已存在同名文件，无法恢复。"); }
        if (affected == 0) throw new PdmConflictException(deleted ? "文件已删除或不存在。" : "文件未删除，无法恢复。");
        return await FindAsync(fileId, cancellationToken) ?? throw new PdmNotFoundException("项目文件不存在。");
    }

    public async Task<bool> FolderHasFilesAsync(Guid folderId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM project_file WHERE folder_id=@FolderId)", new { FolderId = folderId }, cancellationToken: cancellationToken)) == 1;
    }

    public async Task<IReadOnlyList<ProjectFileVersion>> PurgeDeletedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<ProjectFileVersionRow>(new CommandDefinition(
            VersionSelect + " WHERE project_file_id IN (SELECT id FROM project_file WHERE deleted_at IS NOT NULL AND deleted_at<@Cutoff)",
            new { Cutoff = cutoff.UtcDateTime }, transaction, cancellationToken: cancellationToken))).ToArray();
        await connection.ExecuteAsync(new CommandDefinition("SET @pdm_allow_project_file_retention_purge=1", transaction: transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("DELETE version FROM project_file_version version INNER JOIN project_file file ON file.id=version.project_file_id WHERE file.deleted_at IS NOT NULL AND file.deleted_at<@Cutoff", new { Cutoff = cutoff.UtcDateTime }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM project_file WHERE deleted_at IS NOT NULL AND deleted_at<@Cutoff", new { Cutoff = cutoff.UtcDateTime }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("SET @pdm_allow_project_file_retention_purge=0", transaction: transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return rows.Select(row => MapVersion(row)!).ToArray();
    }

    private async Task<ProjectFile> UpdateAsync(Guid fileId, string sql, object value, string actor, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        try
        {
            var affected = await connection.ExecuteAsync(new CommandDefinition(sql, new { FileId = fileId, Value = value, Actor = actor, Now = timeProvider.GetUtcNow().UtcDateTime }, cancellationToken: cancellationToken));
            if (affected == 0) throw new PdmNotFoundException("项目文件不存在或目标目录无效。");
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            throw new PdmConflictException("目标目录已存在同名文件。");
        }
        return await FindAsync(fileId, cancellationToken) ?? throw new PdmNotFoundException("项目文件不存在。");
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static ProjectFile? Map(ProjectFileRow? row) => row is null ? null : new(row.Id, row.RootProjectId, row.FolderId, row.FileName, row.CreatedBy,
        new DateTimeOffset(row.CreatedAt, TimeSpan.Zero), row.UpdatedBy, new DateTimeOffset(row.UpdatedAt, TimeSpan.Zero),
        row.DeletedAt is null ? null : new DateTimeOffset(row.DeletedAt.Value, TimeSpan.Zero), row.DeletedBy)
    {
        CurrentVersion = row.VersionId is null ? null : new ProjectFileVersion(row.VersionId.Value, row.Id, row.VersionNumber!.Value, row.VersionFileName!, row.StorageRoot!, row.StorageRelativePath!, row.FileLength!.Value, row.Sha256!, row.UploadedBy!, new DateTimeOffset(row.UploadedAt!.Value, TimeSpan.Zero), row.Comment)
    };

    private static ProjectFileVersion? MapVersion(ProjectFileVersionRow? row) => row is null ? null : new(row.Id, row.ProjectFileId, row.VersionNumber, row.FileName, row.StorageRoot, row.StorageRelativePath, row.FileLength, row.Sha256, row.UploadedBy, new DateTimeOffset(row.UploadedAt, TimeSpan.Zero), row.Comment);

    private const string Select = """
        SELECT f.id,f.root_project_id,f.folder_id,f.file_name,f.created_by,f.created_at,f.updated_by,f.updated_at,f.deleted_at,f.deleted_by,
               v.id version_id,v.version_number,v.file_name version_file_name,v.storage_root,v.storage_relative_path,v.file_length,v.sha256,v.uploaded_by,v.uploaded_at,v.comment
          FROM project_file f
          LEFT JOIN project_file_version v ON v.project_file_id=f.id AND v.version_number=(SELECT MAX(v2.version_number) FROM project_file_version v2 WHERE v2.project_file_id=f.id)
        """;
    private const string VersionSelect = "SELECT id,project_file_id,version_number,file_name,storage_root,storage_relative_path,file_length,sha256,uploaded_by,uploaded_at,comment FROM project_file_version";
    private sealed class ProjectFileRow { public Guid Id { get; init; } public Guid RootProjectId { get; init; } public Guid FolderId { get; init; } public string FileName { get; init; } = string.Empty; public string CreatedBy { get; init; } = string.Empty; public DateTime CreatedAt { get; init; } public string UpdatedBy { get; init; } = string.Empty; public DateTime UpdatedAt { get; init; } public DateTime? DeletedAt { get; init; } public string? DeletedBy { get; init; } public Guid? VersionId { get; init; } public int? VersionNumber { get; init; } public string? VersionFileName { get; init; } public string? StorageRoot { get; init; } public string? StorageRelativePath { get; init; } public long? FileLength { get; init; } public string? Sha256 { get; init; } public string? UploadedBy { get; init; } public DateTime? UploadedAt { get; init; } public string? Comment { get; init; } }
    private sealed class ProjectFileVersionRow { public Guid Id { get; init; } public Guid ProjectFileId { get; init; } public int VersionNumber { get; init; } public string FileName { get; init; } = string.Empty; public string StorageRoot { get; init; } = string.Empty; public string StorageRelativePath { get; init; } = string.Empty; public long FileLength { get; init; } public string Sha256 { get; init; } = string.Empty; public string UploadedBy { get; init; } = string.Empty; public DateTime UploadedAt { get; init; } public string? Comment { get; init; } }
}
