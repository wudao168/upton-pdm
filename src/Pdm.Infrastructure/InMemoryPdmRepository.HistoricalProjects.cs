using System.Globalization;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class InMemoryPdmRepository
{
    public Task<HistoricalProjectImportResult> ImportHistoricalProjectsAsync(IReadOnlyList<Project> batch,
        ProjectOrganization organization, bool preview, CancellationToken ct)
    {
        lock (gate)
        {
            ct.ThrowIfCancellationRequested();
            var organizationId = organization.Id;
            var roots = batch.Where(project => project.ParentProjectId is null).ToArray();
            var importedRoots = roots.Select(root => root.Id).ToHashSet();
            var codes = batch.Select(project => project.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var models = batch.Select(project => project.DeviceModel).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var serials = batch.SelectMany(project => project.SerialNumbers).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var customerNumbers = roots.Where(project => project.CustomerCode is not null && project.CustomerProjectSequence.HasValue)
                .Select(project => (project.CustomerCode, project.CustomerProjectSequence)).ToHashSet();
            var existing = projects.Values.Where(project => project.OrganizationId == organizationId).ToArray();
            HistoricalProjectConflict.ThrowIfAny(
                projects.Values.Where(project => codes.Contains(project.Code)).Select(project => project.Code).ToArray(),
                existing.Where(project => project.DeviceModel is not null && models.Contains(project.DeviceModel)).Select(project => project.DeviceModel!).ToArray(),
                projects.Values.SelectMany(project => project.SerialNumbers).Where(serials.Contains).ToArray(),
                existing.Where(project => project.ParentProjectId is null && customerNumbers.Contains((project.CustomerCode, project.CustomerProjectSequence)))
                    .Select(project => $"{project.CustomerCode}-{project.CustomerProjectSequence:D3}").ToArray());
            if (preview) return Task.FromResult(new HistoricalProjectImportResult(batch, false));

            var trees = batch.ToLookup(project => project.RootProjectId);
            try
            {
                foreach (var root in roots)
                {
                    ct.ThrowIfCancellationRequested();
                    EnsureProjectFolderTree(root.Id, trees[root.Id].ToArray());
                }
                ct.ThrowIfCancellationRequested();
            }
            catch
            {
                foreach (var id in projectFolders.Values.Where(folder => importedRoots.Contains(folder.RootProjectId)).Select(folder => folder.Id).ToArray())
                    projectFolders.TryRemove(id, out _);
                throw;
            }
            var now = timeProvider.GetUtcNow();
            var created = batch.Select(project => project with { CreatedAt = now }).ToArray();
            foreach (var project in created)
            {
                projects[project.Id] = project;
                projectResponsibles[project.Id] = project.ResponsibleUsers;
                audits.Enqueue(HistoricalProjectImport.Audit(project, now));
            }
            foreach (var root in roots)
            {
                if (root.ProjectTypeCode is { } type)
                    SetImportedNumber(projectCounters, releasedProjectNumbers, (organizationId, type),
                        int.Parse(root.Code.AsSpan(root.Code.Length - 5), CultureInfo.InvariantCulture));
                if (root.CustomerCode is { } customer && root.CustomerProjectSequence is { } customerSequence)
                    SetImportedNumber(customerCounters, releasedCustomerNumbers, (organizationId, customer.ToUpperInvariant()), customerSequence);
            }
            foreach (var serial in serials)
                if (HistoricalProjectImport.SerialSequence(organization, serial) is { } sequence)
                    SetImportedNumber(serialCounters, releasedSerialNumbers, organizationId, sequence);
            return Task.FromResult(new HistoricalProjectImportResult(created, true));
        }
    }

    private static void SetImportedNumber<TKey>(Dictionary<TKey, int> counters, Dictionary<TKey, SortedSet<int>> released,
        TKey key, int value) where TKey : notnull
    {
        counters[key] = Math.Max(counters.GetValueOrDefault(key), value);
        if (released.TryGetValue(key, out var numbers)) numbers.Remove(value);
    }
}
