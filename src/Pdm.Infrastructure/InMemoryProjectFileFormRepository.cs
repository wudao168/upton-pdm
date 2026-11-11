using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed class InMemoryProjectFileFormRepository(InMemoryProjectFileRepository files, TimeProvider clock) : IProjectFileFormRepository
{
    private readonly Dictionary<(Guid ProjectId, string Kind, Guid Id), ProjectFileForm> forms = new();

    public Task<IReadOnlyList<ProjectFileFormSummary>> ListAsync(Guid projectId, string kind, CancellationToken ct)
    {
        lock (forms) return Task.FromResult<IReadOnlyList<ProjectFileFormSummary>>(forms.Where(item => item.Key.ProjectId == projectId && item.Key.Kind == kind)
            .Select(item => item.Value.Summary).Reverse().ToArray());
    }

    public Task<ProjectFileForm?> FindAsync(Guid projectId, string kind, Guid id, CancellationToken ct)
    {
        lock (forms) return Task.FromResult(forms.GetValueOrDefault((projectId, kind, id)));
    }

    public Task<ProjectFileForm> SaveAsync(Project project, string kind, Guid id, long expectedRevision,
        ProjectFileFormData data, string actor, CancellationToken ct)
    {
        lock (forms)
        {
            var key = (project.Id, kind, id);
            var current = forms.GetValueOrDefault(key);
            ProjectFileFormSummary summary;
            if (current is null)
            {
                if (expectedRevision != 0 || forms.Keys.Any(item => item.Id == id)) throw new PdmConflictException("单据已变动，请重新打开后编辑。");
                var sequence = forms.Keys.Count(item => item.ProjectId == project.Id && item.Kind == kind) + 1;
                if (ProjectFileFormIdentity.IsSinglePerProject(kind) && sequence != 1)
                    throw new PdmConflictException("该项目已有同类型单据，请重新打开后编辑。");
                summary = new(id, ProjectFileFormIdentity.Number(project, kind, sequence), 1, 0, null, 0, null, clock.GetUtcNow());
            }
            else
            {
                var revision = current.Summary.RowVersion;
                if (revision >= expectedRevision && revision - expectedRevision <= 1 && JsonSerializer.Serialize(current.Data) == JsonSerializer.Serialize(data))
                    return Task.FromResult(current);
                if (revision != expectedRevision) throw new PdmConflictException("单据已被其他人修改，请重新打开后编辑。");
                summary = current.Summary with { RowVersion = revision + 1, UpdatedAt = clock.GetUtcNow() };
            }
            var saved = new ProjectFileForm(summary, data);
            forms[key] = saved;
            return Task.FromResult(saved);
        }
    }

    public Task<ProjectFileFormArchiveResult> ArchiveAsync(Project project, string kind, Guid id, long expectedRevision,
        int expectedFileVersion, int layoutVersion, StoredProjectFileUpload workbook, StoredProjectFileUpload? evidence, string actor, CancellationToken ct)
    {
        lock (forms)
        {
            var key = (project.Id, kind, id);
            var current = forms.GetValueOrDefault(key) ?? throw new PdmNotFoundException("当前项目中没有这张单据。");
            if (current.Summary.RowVersion != expectedRevision) throw new PdmConflictException("单据已更新，请重新打开后生成存档。");
            if (current.Summary.ArchivedRevision == expectedRevision && current.Summary.ArchivedLayoutVersion >= layoutVersion)
                return Task.FromResult(new ProjectFileFormArchiveResult(current.Summary, false));
            var result = files.AddFormVersions(project.RootProjectId ?? project.Id, workbook, evidence, current.Summary, expectedFileVersion, actor);
            var saved = current.Summary with { FileId = result.Workbook.Id, EvidenceFileId = result.Evidence?.Id,
                FileVersion = expectedFileVersion, ArchivedRevision = expectedRevision, ArchivedLayoutVersion = layoutVersion, UpdatedAt = clock.GetUtcNow() };
            forms[key] = current with { Summary = saved };
            return Task.FromResult(new ProjectFileFormArchiveResult(saved, true));
        }
    }
}
