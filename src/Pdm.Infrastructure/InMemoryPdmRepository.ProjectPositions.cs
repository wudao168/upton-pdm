using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class InMemoryPdmRepository
{
    public Task<IReadOnlyDictionary<string, int>> GetProjectPositionUsageAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<string, int>>(projects.Values.SelectMany(project => project.CustomPositionMembers.Where(item => item.Value.Count > 0).Select(item => item.Key)).GroupBy(key => key).ToDictionary(group => group.Key, group => group.Count()));

    public Task<Project> SetCustomProjectPositionsAsync(Guid projectId, IReadOnlyDictionary<string, IReadOnlyList<string>> members, string actor, CancellationToken cancellationToken)
    {
        var project = projects.GetValueOrDefault(projectId) ?? throw new PdmNotFoundException("项目不存在。");
        project = project with { CustomPositionMembers = members.ToDictionary(item => item.Key, item => (IReadOnlyList<string>)item.Value.ToArray()) };
        projects[projectId] = project;
        return Task.FromResult(project);
    }
}
