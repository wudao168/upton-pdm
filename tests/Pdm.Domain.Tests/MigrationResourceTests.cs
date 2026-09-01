using Upton.Pdm.Infrastructure;

namespace Pdm.Domain.Tests;

public sealed class MigrationResourceTests
{
    [Fact]
    public async Task ExecutionEngineerRoleMigration_GrantsCentralPermissionToDesignAndMechanicalRoles()
    {
        var assembly = typeof(MySqlMigrationRunner).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(".Migrations.078_execution_engineer_assignment_roles.sql", StringComparison.Ordinal));

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var sql = await reader.ReadToEndAsync();

        Assert.Contains("project.designer.assign", sql, StringComparison.Ordinal);
        Assert.Contains("'Engineer','MechanicalManager'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthenticationSchemaRepairMigration_IsEmbeddedAndRepairsTokenVersion()
    {
        var assembly = typeof(MySqlMigrationRunner).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(".Migrations.032_repair_user_authentication_schema.sql", StringComparison.Ordinal));

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var sql = await reader.ReadToEndAsync();

        Assert.Contains("column_name = 'token_version'", sql, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN token_version BIGINT NOT NULL DEFAULT 0", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS password_reset_request", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnclassifiedBomMigration_IsEmbeddedAndMovesPendingRowsOutOfMechanicalBom()
    {
        var assembly = typeof(MySqlMigrationRunner).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(".Migrations.033_separate_unclassified_bom_items.sql", StringComparison.Ordinal));

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var sql = await reader.ReadToEndAsync();

        Assert.Contains("WHERE is_pending_classification = 1", sql, StringComparison.Ordinal);
        Assert.Contains("item.bom_kind = 'Unclassified'", sql, StringComparison.Ordinal);
        Assert.Contains("ROW_NUMBER() OVER", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IndependentBomVersionMigration_IsEmbeddedAndPinsThreeVersionsPerBaseline()
    {
        var assembly = typeof(MySqlMigrationRunner).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(".Migrations.034_independent_bom_versions_and_baselines.sql", StringComparison.Ordinal));

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var sql = await reader.ReadToEndAsync();

        Assert.Contains("CREATE TABLE IF NOT EXISTS bom_version", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS manufacturing_bom_baseline", sql, StringComparison.Ordinal);
        Assert.Contains("standard_bom_version_id BINARY(16) NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("non_standard_bom_version_id BINARY(16) NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("electrical_bom_version_id BINARY(16) NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("effective_serial_from VARCHAR(80) NOT NULL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaterialCategoryTreeMigration_IsEmbeddedAndKeepsCreationPolicySeparateFromU9Identity()
    {
        var assembly = typeof(MySqlMigrationRunner).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(".Migrations.035_material_category_tree_and_lifecycle.sql", StringComparison.Ordinal));

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var sql = await reader.ReadToEndAsync();

        Assert.Contains("CREATE TABLE material_category", sql, StringComparison.Ordinal);
        Assert.Contains("u9_category_id VARCHAR(160)", sql, StringComparison.Ordinal);
        Assert.Contains("allow_create TINYINT(1)", sql, StringComparison.Ordinal);
        Assert.Contains("sequence_length TINYINT UNSIGNED", sql, StringComparison.Ordinal);
        Assert.Contains("('010401','劳保用品','0104'", sql, StringComparison.Ordinal);
        Assert.Contains("item_modify_path", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MaterialSyncProvenanceMigration_DoesNotTrustIdempotentCodeHits()
    {
        var assembly = typeof(MySqlMigrationRunner).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(".Migrations.036_material_sync_provenance.sql", StringComparison.Ordinal));

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var sql = await reader.ReadToEndAsync();

        Assert.Contains("u9_sync_confirmed", sql, StringComparison.Ordinal);
        Assert.Contains("$.created", sql, StringComparison.Ordinal);
        Assert.Contains("$.updated", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("$.alreadyExisted", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task U9UnitCodeMappingMigration_PersistsOrganizationSpecificMappings()
    {
        var assembly = typeof(MySqlMigrationRunner).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(".Migrations.038_u9_unit_code_mapping.sql", StringComparison.Ordinal));

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var sql = await reader.ReadToEndAsync();

        Assert.Contains("unit_code_mapping_json JSON", sql, StringComparison.Ordinal);
        Assert.Contains("JSON_OBJECT()", sql, StringComparison.Ordinal);
        Assert.Contains("NOT NULL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmedU9UnitMappingMigration_SeedsEaAs001WithoutOverwritingExistingMapping()
    {
        var assembly = typeof(MySqlMigrationRunner).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(".Migrations.039_seed_confirmed_u9_unit_mapping.sql", StringComparison.Ordinal));

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var sql = await reader.ReadToEndAsync();

        Assert.Contains("JSON_SET(unit_code_mapping_json, '$.EA', '001')", sql, StringComparison.Ordinal);
        Assert.Contains("JSON_EXTRACT(unit_code_mapping_json, '$.EA') IS NULL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingProjectReferenceRootMigration_RecoversTopLevelAssemblyWithoutReplacingExistingPointers()
    {
        var assembly = typeof(MySqlMigrationRunner).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(".Migrations.040_backfill_missing_project_reference_roots.sql", StringComparison.Ordinal));

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var sql = await reader.ReadToEndAsync();

        Assert.Contains("INSERT IGNORE INTO project_reference_root", sql, StringComparison.Ordinal);
        Assert.Contains("root_document.kind = 'Assembly'", sql, StringComparison.Ordinal);
        Assert.Contains("JSON_EXTRACT(container.root_json, '$.children')", sql, StringComparison.Ordinal);
        Assert.Contains("newer.captured_at > candidate.captured_at", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("UPDATE project_reference_root", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task U9CustomerDirectoryMigration_PreservesCustomerIdsWhileChangingTheSource()
    {
        var assembly = typeof(MySqlMigrationRunner).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(".Migrations.041_u9_customer_directory.sql", StringComparison.Ordinal));

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var sql = await reader.ReadToEndAsync();

        Assert.Contains("SET source_system = 'u9c'", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE source_system = 'crm'", sql, StringComparison.Ordinal);
        Assert.Contains("SET password_ciphertext = ''", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DELETE", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DirectU9UnitMigration_ConvertsLegacyValuesAndClearsMappings()
    {
        var assembly = typeof(MySqlMigrationRunner).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(".Migrations.043_use_u9_unit_codes_directly.sql", StringComparison.Ordinal));

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var sql = await reader.ReadToEndAsync();

        Assert.Contains("UPDATE material_master", sql, StringComparison.Ordinal);
        Assert.Contains("UPDATE bom_item", sql, StringComparison.Ordinal);
        Assert.Contains("WHEN '台' THEN '002'", sql, StringComparison.Ordinal);
        Assert.Contains("UPDATE u9_material_integration_setting", sql, StringComparison.Ordinal);
        Assert.Contains("SET unit_code_mapping_json = JSON_OBJECT()", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BomValidationRulesMigration_PersistsDefaultsAndVersionSnapshots()
    {
        var assembly = typeof(MySqlMigrationRunner).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(".Migrations.044_bom_validation_rules.sql", StringComparison.Ordinal));

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var sql = await reader.ReadToEndAsync();

        Assert.Contains("bom_standard_required_fields", sql, StringComparison.Ordinal);
        Assert.Contains("bom_nonstandard_required_fields", sql, StringComparison.Ordinal);
        Assert.Contains("bom_electrical_required_fields", sql, StringComparison.Ordinal);
        Assert.Contains("validation_rule_snapshot_json JSON NULL", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task U9InterfacePathMigration_PersistsCustomerMaterialAndBomContractsTogether()
    {
        var assembly = typeof(MySqlMigrationRunner).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(".Migrations.053_u9_interface_paths.sql", StringComparison.Ordinal));

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var sql = await reader.ReadToEndAsync();

        Assert.Contains("customer_query_path", sql, StringComparison.Ordinal);
        Assert.Contains("bom_create_path", sql, StringComparison.Ordinal);
        Assert.Contains("bom_query_path", sql, StringComparison.Ordinal);
        Assert.Contains("bom_batch_unapprove_path", sql, StringComparison.Ordinal);
        Assert.Contains("bom_bip_query_page_path", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BomOccurrenceMigration_PersistsSourcePathAndParentRelationship()
    {
        var assembly = typeof(MySqlMigrationRunner).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(".Migrations.055_bom_occurrence_relationship.sql", StringComparison.Ordinal));

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var sql = await reader.ReadToEndAsync();

        Assert.Contains("source_instance_path VARCHAR(1000)", sql, StringComparison.Ordinal);
        Assert.Contains("parent_drawing_number VARCHAR(160)", sql, StringComparison.Ordinal);
        Assert.Contains("ix_bom_item_source_occurrence", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IndependentBomHeaderMigration_PersistsDistinctCodesAndPinsVersions()
    {
        var assembly = typeof(MySqlMigrationRunner).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(".Migrations.057_independent_project_bom_headers.sql", StringComparison.Ordinal));

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var sql = await reader.ReadToEndAsync();

        Assert.Contains("CREATE TABLE IF NOT EXISTS project_bom_header", sql, StringComparison.Ordinal);
        Assert.Contains("UNIQUE KEY ux_project_bom_header_material (project_id,material_id)", sql, StringComparison.Ordinal);
        Assert.Contains("mother_material_id BINARY(16)", sql, StringComparison.Ordinal);
        Assert.Contains("information_schema.columns", sql, StringComparison.Ordinal);
        Assert.Contains("information_schema.statistics", sql, StringComparison.Ordinal);
        Assert.Contains("information_schema.referential_constraints", sql, StringComparison.Ordinal);
        Assert.Contains("BINARY constraint_schema = BINARY @pdm_schema_name", sql, StringComparison.Ordinal);
        Assert.Contains("pdm_kind='Product'", sql, StringComparison.Ordinal);
        Assert.Contains("category_code IN ('0301','0302')", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("WHERE code IN", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BomHeaderMaterialCategoryMigration_Enables0201ForVirtualProductCodes()
    {
        var assembly = typeof(MySqlMigrationRunner).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(".Migrations.058_enable_bom_header_material_category.sql", StringComparison.Ordinal));

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var sql = await reader.ReadToEndAsync();

        Assert.Contains("pdm_kind='Product'", sql, StringComparison.Ordinal);
        Assert.Contains("allow_create=1", sql, StringComparison.Ordinal);
        Assert.Contains("category_code='0201'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task U9MaterialFullSyncMigration_PersistsProgressAndCategoryResults()
    {
        var assembly = typeof(MySqlMigrationRunner).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(".Migrations.080_u9_material_full_sync_runs.sql", StringComparison.Ordinal));

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var sql = await reader.ReadToEndAsync();

        Assert.Contains("CREATE TABLE u9_material_full_sync_run", sql, StringComparison.Ordinal);
        Assert.Contains("category_results_json", sql, StringComparison.Ordinal);
        Assert.Contains("completed_category_count", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task U9MaterialSyncBatchMigration_PersistsOrderedDurableQueue()
    {
        var assembly = typeof(MySqlMigrationRunner).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(".Migrations.081_u9_material_sync_batches.sql", StringComparison.Ordinal));

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var sql = await reader.ReadToEndAsync();

        Assert.Contains("CREATE TABLE u9_material_sync_batch", sql, StringComparison.Ordinal);
        Assert.Contains("ordinal_no INT NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("lease_expires_at", sql, StringComparison.Ordinal);
    }
}
