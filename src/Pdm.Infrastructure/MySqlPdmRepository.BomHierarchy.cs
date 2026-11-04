using Dapper;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<IReadOnlyList<BomHierarchyMetric>> ReadBomHierarchyMetricsAsync(IReadOnlyList<Guid> projectIds, CancellationToken ct)
    {
        if (projectIds.Count == 0) return [];
        await using var connection = await OpenAsync(ct);
        var args = new { ProjectIds = projectIds };
        var items = (await connection.QueryAsync<BomRow>(new CommandDefinition("""
            SELECT id, project_id, bom_kind, sequence_no, drawing_number, name, quantity, unit, material, specification, remark, brand, surface_treatment, heat_treatment, weight, is_wear_part, impact_stage, inventory_replacement_material_code, revision_label, is_complete,
                   source_document_id, source_configuration, source_instance_path, parent_drawing_number,
                   engineering_kit_reference_id,engineering_kit_id,engineering_kit_revision_id,engineering_kit_code,engineering_kit_version_no,engineering_kit_component_id,engineering_kit_component_optional,relation_allocations_json,engineering_kit_relation_allocations_json,is_summary_quantity,
                   item_source, is_manually_overridden, is_pending_removal, is_pending_classification, is_manual_unmatched, is_manually_retained, is_manually_excluded, is_release_excluded, release_exclusion_reason,
                   reconciliation_status, reconciliation_note, reconciliation_updated_by, reconciliation_updated_at, deleted_at, deleted_by, delete_reason, property_writeback_status
            FROM bom_item WHERE project_id IN @ProjectIds AND bom_kind IN ('Standard','NonStandard','Electrical') ORDER BY sequence_no
            """, args, cancellationToken: ct))).Select(MapBomItem).ToArray();
        var rules = (await GetSystemSettingsAsync(ct)).ValidationRules;
        var versions = (await connection.QueryAsync<HierarchyVersionRow>(new CommandDefinition("""
            SELECT project_id,bom_kind,version_number,version_label,released_at FROM bom_version
            WHERE project_id IN @ProjectIds AND state='Released'
            """, args, cancellationToken: ct))).ToArray();
        var releases = (await connection.QueryAsync<HierarchyReleaseRow>(new CommandDefinition("""
            SELECT project_id,release_scope,MAX(published_at) published_at FROM release_package
            WHERE project_id IN @ProjectIds AND state='Published' AND release_scope IN ('StandardLongLead','NonStandardLongLead','ElectricalLongLead')
            GROUP BY project_id,release_scope
            """, args, cancellationToken: ct))).ToArray();
        return projectIds.SelectMany(id => new[] { BomKind.Standard, BomKind.NonStandard, BomKind.Electrical }.Select(kind =>
        {
            var name = kind.ToString();
            var count = BomHierarchyMetric.Count(id, kind, items.Where(item => item.ProjectId == id && item.Kind == kind).ToArray(), rules);
            var version = versions.Where(item => item.ProjectId == id && item.BomKind == name).OrderByDescending(item => item.VersionNumber).FirstOrDefault();
            var release = releases.FirstOrDefault(item => item.ProjectId == id && item.ReleaseScope == name + "LongLead");
            return count with { Version = version?.VersionLabel, ReleasedAt = version?.ReleasedAt is DateTime date ? AsUtc(date) : null,
                LongLeadPublishedAt = release?.PublishedAt is DateTime published ? AsUtc(published) : null, LongLeadPublished = release is not null };
        })).ToArray();
    }
    private sealed class HierarchyVersionRow { public Guid ProjectId { get; init; } public string BomKind { get; init; } = ""; public int VersionNumber { get; init; } public string VersionLabel { get; init; } = ""; public DateTime? ReleasedAt { get; init; } }
    private sealed class HierarchyReleaseRow { public Guid ProjectId { get; init; } public string ReleaseScope { get; init; } = ""; public DateTime? PublishedAt { get; init; } }
}
