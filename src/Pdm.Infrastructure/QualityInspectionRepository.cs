using System.Collections.Concurrent;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryQualityInspectionRepository : IQualityInspectionRepository
{
    private readonly ConcurrentDictionary<Guid, QualityInspectionRecord> records = new();
    public Task<IReadOnlyList<QualityInspectionRecord>> ListAsync(Guid projectId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<QualityInspectionRecord>>(records.Values.Where(x => x.ProjectId == projectId).OrderByDescending(x => x.UploadedAt).ToArray());
    public Task<QualityInspectionRecord?> FindAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(records.GetValueOrDefault(id));
    public Task AddAsync(QualityInspectionRecord record, CancellationToken cancellationToken) { records[record.Id] = record; return Task.CompletedTask; }
}

public sealed class MySqlQualityInspectionRepository(IOptions<PdmDatabaseOptions> options) : IQualityInspectionRepository
{
    private const string Select = "SELECT id,project_id ProjectId,kind,station,title,remark,file_name FileName,storage_relative_path StorageRelativePath,file_length FileLength,sha256,uploaded_by UploadedBy,uploaded_at UploadedAt FROM quality_inspection_record";
    public async Task<IReadOnlyList<QualityInspectionRecord>> ListAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        return (await connection.QueryAsync<RecordRow>(new CommandDefinition(Select + " WHERE project_id=@projectId ORDER BY uploaded_at DESC,id", new { projectId }, cancellationToken: cancellationToken))).Select(row => Map(row)!).ToArray();
    }
    public async Task<QualityInspectionRecord?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        return Map(await connection.QuerySingleOrDefaultAsync<RecordRow>(new CommandDefinition(Select + " WHERE id=@id", new { id }, cancellationToken: cancellationToken)));
    }
    private static QualityInspectionRecord? Map(RecordRow? row) => row is null ? null : new(row.Id, row.ProjectId, row.Kind, row.Station, row.Title, row.Remark, row.FileName, row.StorageRelativePath, row.FileLength, row.Sha256, row.UploadedBy, new DateTimeOffset(DateTime.SpecifyKind(row.UploadedAt, DateTimeKind.Utc)));
    private sealed class RecordRow {
        public Guid Id { get; set; } public Guid ProjectId { get; set; }
        public string Kind { get; set; } = ""; public string Station { get; set; } = ""; public string Title { get; set; } = "";
        public string? Remark { get; set; } public string FileName { get; set; } = ""; public string StorageRelativePath { get; set; } = "";
        public long FileLength { get; set; } public string Sha256 { get; set; } = ""; public string UploadedBy { get; set; } = ""; public DateTime UploadedAt { get; set; }
    }
    public async Task AddAsync(QualityInspectionRecord record, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        await connection.ExecuteAsync(new CommandDefinition("INSERT INTO quality_inspection_record(id,project_id,kind,station,title,remark,file_name,storage_relative_path,file_length,sha256,uploaded_by,uploaded_at) VALUES(@Id,@ProjectId,@Kind,@Station,@Title,@Remark,@FileName,@StorageRelativePath,@FileLength,@Sha256,@UploadedBy,@UploadedAt)",
            new { record.Id, record.ProjectId, record.Kind, record.Station, record.Title, record.Remark, record.FileName, record.StorageRelativePath, record.FileLength, record.Sha256, record.UploadedBy, UploadedAt = record.UploadedAt.UtcDateTime }, cancellationToken: cancellationToken));
    }
}
