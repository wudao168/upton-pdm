using System.Data.Common;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository : IPdmRepository
{
    private readonly string connectionString;
    private readonly TimeProvider timeProvider;
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    public MySqlPdmRepository(IOptions<PdmDatabaseOptions> options, TimeProvider timeProvider)
    {
        connectionString = options.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("PDM MySQL连接字符串未配置。 ");
        }

        this.timeProvider = timeProvider;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public async Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<ProjectRow>(new CommandDefinition(
            """
            SELECT p.id,p.code,p.name,p.project_alias,p.organization_id,o.name organization_name,p.project_type_code,
                   p.equipment_type_code,p.customer_code,p.customer_name,p.customer_project_sequence,p.device_model,
                   p.signed_date,p.quantity,p.parent_project_id,p.child_sequence,p.owner,p.vault_location,p.release_location,p.is_active
            FROM project p
            LEFT JOIN project_organization o ON o.id=p.organization_id
            LEFT JOIN project parent ON parent.id=p.parent_project_id
            ORDER BY COALESCE(parent.code,p.code), CASE WHEN p.parent_project_id IS NULL THEN 0 ELSE 1 END, p.child_sequence
            """,
            cancellationToken: cancellationToken));
        return await MapProjectsAsync(connection, null, rows, cancellationToken);
    }

    public async Task<IReadOnlyList<Project>> ListProjectsForUserAsync(string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (role == UserRole.Administrator)
        {
            return await ListProjectsAsync(cancellationToken);
        }

        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<ProjectRow>(new CommandDefinition(
            """
            SELECT p.id,p.code,p.name,p.project_alias,p.organization_id,o.name organization_name,p.project_type_code,
                   p.equipment_type_code,p.customer_code,p.customer_name,p.customer_project_sequence,p.device_model,
                   p.signed_date,p.quantity,p.parent_project_id,p.child_sequence,p.owner,p.vault_location,p.release_location,p.is_active
            FROM project p
            LEFT JOIN project_organization o ON o.id=p.organization_id
            LEFT JOIN project parent ON parent.id=p.parent_project_id
            WHERE p.owner=@Actor OR EXISTS (
                SELECT 1 FROM project_responsible responsible
                WHERE responsible.project_id=p.id AND responsible.username=@Actor)
            ORDER BY COALESCE(parent.code,p.code), CASE WHEN p.parent_project_id IS NULL THEN 0 ELSE 1 END, p.child_sequence
            """,
            new { Actor = actor },
            cancellationToken: cancellationToken));
        return await MapProjectsAsync(connection, null, rows, cancellationToken);
    }

    public async Task<Project?> FindProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await FindProjectAsync(connection, null, projectId, cancellationToken);
    }

    public async Task<bool> HasProjectReadAccessAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (role == UserRole.Administrator) return true;
        await using var connection = await OpenAsync(cancellationToken);
        var value = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            """
            SELECT CASE WHEN p.owner=@Actor OR EXISTS (
                SELECT 1 FROM project_responsible responsible
                WHERE responsible.project_id=p.id AND responsible.username=@Actor) THEN 1 ELSE 0 END
            FROM project p
            WHERE p.id=@ProjectId
            """,
            new { ProjectId = projectId, Actor = actor },
            cancellationToken: cancellationToken));
        return value == 1;
    }

    public async Task<Project> CreateProjectAsync(CreateProjectCommand command, string actor, CancellationToken cancellationToken)
    {
        var project = new Project(
            Guid.NewGuid(),
            command.Code,
            command.Name,
            command.Owner,
            command.VaultLocation,
            command.ReleaseLocation,
            true);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO project(id,code,name,owner,vault_location,release_location,is_active,row_version,created_at,updated_at)
                VALUES(@Id,@Code,@Name,@Owner,@VaultLocation,@ReleaseLocation,1,1,@Now,@Now)
                """,
                new { project.Id, project.Code, project.Name, project.Owner, project.VaultLocation, project.ReleaseLocation, Now = now },
                transaction,
                cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO project_responsible(project_id,username,assigned_at) VALUES(@ProjectId,@Username,@Now)",
                new { ProjectId = project.Id, Username = actor, Now = now }, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return project with { ResponsibleUsers = [actor] };
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new PdmConflictException("项目编码已经存在。");
        }
    }

    public async Task<ProjectNumberingOptions> GetProjectNumberingOptionsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var organizationRows = await connection.QueryAsync<ProjectOrganizationRow>(new CommandDefinition(
            """
            SELECT organization.id,organization.name,organization.project_company_code ProjectCompanyCode,
                   organization.model_company_code ModelCompanyCode,organization.crm_company_name CrmCompanyName,
                   organization.is_active IsActive,COALESCE(project_counter.current_value,0) CurrentProjectSequence,
                   COALESCE(serial_counter.current_value,0) CurrentSerialSequence
            FROM project_organization organization
            LEFT JOIN project_number_counter project_counter ON project_counter.organization_id=organization.id
            LEFT JOIN serial_number_counter serial_counter ON serial_counter.organization_id=organization.id
            WHERE organization.is_active=1 ORDER BY organization.project_company_code
            """,
            cancellationToken: cancellationToken));
        var organizations = organizationRows.Select(row => new ProjectOrganization(
            row.Id,
            row.Name,
            row.ProjectCompanyCode,
            row.ModelCompanyCode,
            row.CrmCompanyName,
            row.IsActive,
            checked((int)row.CurrentProjectSequence),
            checked((int)row.CurrentSerialSequence))).ToArray();
        var projectTypeRows = await connection.QueryAsync<ProjectTypeDefinitionRow>(new CommandDefinition(
            "SELECT code,name,is_active IsActive FROM project_type_definition WHERE is_active=1 ORDER BY code",
            cancellationToken: cancellationToken));
        var equipmentTypeRows = await connection.QueryAsync<EquipmentTypeDefinitionRow>(new CommandDefinition(
            "SELECT code,name,is_active IsActive FROM equipment_type_definition WHERE is_active=1 ORDER BY code",
            cancellationToken: cancellationToken));
        var projectTypes = projectTypeRows.Select(row => new ProjectTypeDefinition(row.Code, row.Name, row.IsActive)).ToArray();
        var equipmentTypes = equipmentTypeRows.Select(row => new EquipmentTypeDefinition(row.Code, row.Name, row.IsActive)).ToArray();
        return new ProjectNumberingOptions(organizations.ToArray(), projectTypes.ToArray(), equipmentTypes.ToArray());
    }

    public async Task<ProjectNumberingOptions> AdvanceOrganizationCountersAsync(Guid organizationId, int currentProjectSequence, int currentSerialSequence, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var exists = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM project_organization WHERE id=@OrganizationId FOR UPDATE",
            new { OrganizationId = organizationId }, transaction, cancellationToken: cancellationToken));
        if (exists == 0) throw new PdmNotFoundException("组织不存在。");
        await connection.ExecuteAsync(new CommandDefinition("INSERT IGNORE INTO project_number_counter(organization_id,current_value) VALUES(@OrganizationId,0)", new { OrganizationId = organizationId }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("INSERT IGNORE INTO serial_number_counter(organization_id,current_value) VALUES(@OrganizationId,0)", new { OrganizationId = organizationId }, transaction, cancellationToken: cancellationToken));
        var currentProject = await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT current_value FROM project_number_counter WHERE organization_id=@OrganizationId FOR UPDATE", new { OrganizationId = organizationId }, transaction, cancellationToken: cancellationToken));
        var currentSerial = await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT current_value FROM serial_number_counter WHERE organization_id=@OrganizationId FOR UPDATE", new { OrganizationId = organizationId }, transaction, cancellationToken: cancellationToken));
        if (currentProjectSequence < currentProject || currentSerialSequence < currentSerial)
            throw new PdmRuleException("流水基线只能向前调整，不能小于系统当前值。");
        await connection.ExecuteAsync(new CommandDefinition("UPDATE project_number_counter SET current_value=@Value WHERE organization_id=@OrganizationId", new { OrganizationId = organizationId, Value = currentProjectSequence }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("UPDATE serial_number_counter SET current_value=@Value WHERE organization_id=@OrganizationId", new { OrganizationId = organizationId, Value = currentSerialSequence }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return await GetProjectNumberingOptionsAsync(cancellationToken);
    }

    public async Task<Project> CreateNumberedProjectAsync(CreateNumberedProjectCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var organization = await connection.QuerySingleOrDefaultAsync<ProjectOrganizationRow>(new CommandDefinition(
                "SELECT id,name,project_company_code,model_company_code,crm_company_name,is_active FROM project_organization WHERE id=@OrganizationId FOR UPDATE",
                new { command.OrganizationId }, transaction, cancellationToken: cancellationToken))
                ?? throw new PdmRuleException("所选组织不存在。");
            if (!organization.IsActive) throw new PdmRuleException("所选组织已停用。");
            var customer = await connection.QuerySingleOrDefaultAsync<CustomerRow>(new CommandDefinition(
                "SELECT id,code,name,is_active IsActive FROM pdm_customer WHERE id=@CustomerId FOR UPDATE",
                new { command.CustomerId }, transaction, cancellationToken: cancellationToken))
                ?? throw new PdmRuleException("所选客户不存在。");
            if (!customer.IsActive) throw new PdmRuleException("所选客户已停用。");

            var projectSequence = await ReserveProjectNumberAsync(connection, transaction, command.OrganizationId, cancellationToken);
            var customerSequence = await ReserveCustomerProjectNumberAsync(connection, transaction, command.OrganizationId, customer.Code, cancellationToken);
            var serialStart = await ReserveSerialNumbersAsync(connection, transaction, command.OrganizationId, command.Quantity, cancellationToken);
            var code = $"{command.ProjectTypeCode}{organization.ProjectCompanyCode}{projectSequence:D5}";
            var deviceModel = $"{organization.ModelCompanyCode}-{command.EquipmentTypeCode}-{customer.Code}-{customerSequence:D3}-00";
            var project = BuildNumberedProject(
                Guid.NewGuid(), code, command.Name, command.ProjectAlias, command.OrganizationId, organization.Name,
                command.ProjectTypeCode, command.EquipmentTypeCode, customer.Code, customer.Name,
                customerSequence, deviceModel, command.SignedDate, command.Quantity, null, null, command.Owner,
                Path.Combine(command.VaultLocation, code), Path.Combine(command.ReleaseLocation, code), organization.ProjectCompanyCode, serialStart)
                with { ResponsibleUsers = [command.Owner] };
            await InsertNumberedProjectAsync(connection, transaction, project, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return project;
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new PdmConflictException("项目号、客户项目流水号或序列号发生冲突，请重试。");
        }
    }

    public async Task<Project> CreateSubprojectAsync(CreateSubprojectCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var parent = await connection.QuerySingleOrDefaultAsync<ProjectRow>(new CommandDefinition(
                """
                SELECT p.id,p.code,p.name,p.project_alias,p.organization_id,o.name organization_name,p.project_type_code,
                       p.equipment_type_code,p.customer_code,p.customer_name,p.customer_project_sequence,p.device_model,
                       p.signed_date,p.quantity,p.parent_project_id,p.child_sequence,p.owner,p.vault_location,p.release_location,p.is_active
                FROM project p LEFT JOIN project_organization o ON o.id=p.organization_id
                WHERE p.id=@ParentProjectId FOR UPDATE
                """,
                new { command.ParentProjectId }, transaction, cancellationToken: cancellationToken))
                ?? throw new PdmNotFoundException("主项目不存在。");
            if (parent.ParentProjectId is not null) throw new PdmRuleException("只能在主项目下创建子项目。");
            if (parent.OrganizationId is null || parent.EquipmentTypeCode is null || parent.CustomerProjectSequence is null
                || string.IsNullOrWhiteSpace(parent.ProjectTypeCode) || string.IsNullOrWhiteSpace(parent.CustomerCode)
                || string.IsNullOrWhiteSpace(parent.CustomerName) || parent.SignedDate is null)
                throw new PdmRuleException("旧项目缺少自动编号资料，不能直接创建子项目。");

            var childSequence = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COALESCE(MAX(child_sequence),0)+1 FROM project WHERE parent_project_id=@ParentProjectId",
                new { command.ParentProjectId }, transaction, cancellationToken: cancellationToken));
            var organization = await connection.QuerySingleAsync<ProjectOrganizationRow>(new CommandDefinition(
                "SELECT id,name,project_company_code,model_company_code,crm_company_name,is_active FROM project_organization WHERE id=@OrganizationId",
                new { OrganizationId = parent.OrganizationId.Value }, transaction, cancellationToken: cancellationToken));
            var serialStart = await ReserveSerialNumbersAsync(connection, transaction, parent.OrganizationId.Value, command.Quantity, cancellationToken);
            var code = $"{parent.Code}-{childSequence}";
            var deviceModel = $"{organization.ModelCompanyCode}-{parent.EquipmentTypeCode.Value}-{parent.CustomerCode}-{parent.CustomerProjectSequence.Value:D3}-{childSequence:D2}";
            var responsibleUsers = (await connection.QueryAsync<string>(new CommandDefinition(
                "SELECT username FROM project_responsible WHERE project_id=@ProjectId ORDER BY username",
                new { ProjectId = parent.Id }, transaction, cancellationToken: cancellationToken))).ToArray();
            if (responsibleUsers.Length == 0) responsibleUsers = [parent.Owner];
            var project = BuildNumberedProject(
                Guid.NewGuid(), code, command.Name, command.ProjectAlias, parent.OrganizationId.Value, organization.Name,
                parent.ProjectTypeCode, parent.EquipmentTypeCode.Value, parent.CustomerCode, parent.CustomerName,
                parent.CustomerProjectSequence.Value, deviceModel, DateOnly.FromDateTime(parent.SignedDate.Value), command.Quantity,
                parent.Id, childSequence, parent.Owner, Path.Combine(command.VaultRoot ?? Path.GetDirectoryName(parent.VaultLocation)!, code),
                Path.Combine(command.ReleaseRoot ?? Path.GetDirectoryName(parent.ReleaseLocation)!, code), organization.ProjectCompanyCode, serialStart)
                with { ResponsibleUsers = responsibleUsers };
            await InsertNumberedProjectAsync(connection, transaction, project, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return project;
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new PdmConflictException("子项目号或序列号发生冲突，请重试。");
        }
    }

    public async Task<IReadOnlyList<PdmDocument>> ListDocumentsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<DocumentRow>(new CommandDefinition(
            """
            SELECT id, project_id, drawing_number, name, file_name, kind, lifecycle_state, revision_label, checked_out_by, updated_at
            FROM document
            WHERE project_id = @ProjectId
            ORDER BY drawing_number, kind
            """,
            new { ProjectId = projectId },
            cancellationToken: cancellationToken));
        return rows.Select(MapDocument).ToArray();
    }

    public async Task<PdmDocument?> FindDocumentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await FindDocumentAsync(connection, null, documentId, cancellationToken);
    }

    public async Task<PdmDocument> RegisterDocumentAsync(RegisterDocumentCommand command, string actor, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var projectActive = await connection.ExecuteScalarAsync<bool?>(new CommandDefinition(
            "SELECT is_active FROM project WHERE id=@ProjectId FOR UPDATE",
            new { command.ProjectId },
            transaction,
            cancellationToken: cancellationToken));
        if (projectActive != true)
        {
            throw new PdmNotFoundException("项目不存在或已停用。");
        }

        var documentId = Guid.NewGuid();
        var now = timeProvider.GetUtcNow();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO document(id,project_id,drawing_number,name,file_name,kind,lifecycle_state,revision_label,checked_out_by,checked_out_at,row_version,created_at,updated_at)
            VALUES(@Id,@ProjectId,@DrawingNumber,@Name,@FileName,@Kind,'Work','W1',NULL,NULL,1,@Now,@Now)
            ON DUPLICATE KEY UPDATE id=id
            """,
            new
            {
                Id = documentId,
                command.ProjectId,
                command.DrawingNumber,
                command.Name,
                command.FileName,
                Kind = command.Kind.ToString(),
                Now = now.UtcDateTime
            },
            transaction,
            cancellationToken: cancellationToken));

        var row = await connection.QuerySingleAsync<DocumentRow>(new CommandDefinition(
            """
            SELECT id, project_id, drawing_number, name, file_name, kind, lifecycle_state, revision_label, checked_out_by, updated_at
            FROM document WHERE project_id=@ProjectId AND file_name=@FileName
            """,
            new { command.ProjectId, command.FileName },
            transaction,
            cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO document_user_access(document_id,username,can_read,granted_at)
            VALUES(@DocumentId,@Actor,1,@Now)
            ON DUPLICATE KEY UPDATE can_read=1
            """,
            new { DocumentId = row.Id, Actor = actor, Now = now.UtcDateTime },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return MapDocument(row);
    }

    public async Task<bool> HasDocumentReadAccessAsync(Guid documentId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (role == UserRole.Administrator) return true;
        await using var connection = await OpenAsync(cancellationToken);
        var value = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            """
            SELECT CASE WHEN p.owner = @Actor OR d.checked_out_by = @Actor OR EXISTS (
                SELECT 1 FROM project_responsible responsible
                WHERE responsible.project_id=p.id AND responsible.username=@Actor) THEN 1 ELSE 0 END
            FROM document d
            INNER JOIN project p ON p.id=d.project_id
            WHERE d.id=@DocumentId
            """,
            new { DocumentId = documentId, Actor = actor }, cancellationToken: cancellationToken));
        return value == 1;
    }

    public async Task<DocumentReferenceNode?> GetReferenceTreeAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await FindCurrentReferenceSnapshotAsync(connection, projectId, cancellationToken);
        return row is null ? null : JsonSerializer.Deserialize<DocumentReferenceNode>(row.RootJson, jsonOptions);
    }

    public async Task<CadReferenceSnapshot?> GetLatestReferenceSnapshotAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await FindCurrentReferenceSnapshotAsync(connection, projectId, cancellationToken);
        if (row is null) return null;
        var root = JsonSerializer.Deserialize<DocumentReferenceNode>(row.RootJson, jsonOptions)
            ?? throw new InvalidDataException("引用树快照损坏。");
        return new CadReferenceSnapshot(row.Id, row.ProjectId, row.RootDocumentId, DateTime.SpecifyKind(row.CapturedAt, DateTimeKind.Utc), row.CapturedBy, root, row.Sha256);
    }

    private async Task<ReferenceSnapshotRow?> FindCurrentReferenceSnapshotAsync(
        System.Data.Common.DbConnection connection,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var current = await connection.QuerySingleOrDefaultAsync<ReferenceSnapshotRow>(new CommandDefinition(
            """
            SELECT rs.id, rs.project_id, rs.root_document_id, rs.captured_at, rs.captured_by, rs.sha256, rs.root_json
            FROM project_reference_root current_root
            INNER JOIN reference_snapshot rs ON rs.id = current_root.reference_snapshot_id
            WHERE current_root.project_id = @ProjectId
            """,
            new { ProjectId = projectId },
            cancellationToken: cancellationToken));
        if (current is not null)
        {
            return current;
        }

        return await connection.QuerySingleOrDefaultAsync<ReferenceSnapshotRow>(new CommandDefinition(
            "SELECT id, project_id, root_document_id, captured_at, captured_by, sha256, root_json FROM reference_snapshot WHERE project_id=@ProjectId ORDER BY captured_at DESC LIMIT 1",
            new { ProjectId = projectId },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<BomItem>> GetBomAsync(Guid projectId, BomKind kind, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<BomRow>(new CommandDefinition(
            """
            SELECT id, project_id, bom_kind, sequence_no, drawing_number, name, quantity, unit, material, specification, revision_label, is_complete
            FROM bom_item
            WHERE project_id = @ProjectId AND bom_kind = @Kind
            ORDER BY sequence_no
            """,
            new { ProjectId = projectId, Kind = kind.ToString() },
            cancellationToken: cancellationToken));
        return rows.Select(MapBomItem).ToArray();
    }

    public async Task<IReadOnlyList<BomItem>> ReplaceBomAsync(Guid projectId, BomKind kind, IReadOnlyList<BomItem> items, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var exists = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM project WHERE id=@ProjectId AND is_active=1 FOR UPDATE",
            new { ProjectId = projectId }, transaction, cancellationToken: cancellationToken));
        if (exists != 1) throw new PdmNotFoundException("项目不存在或已停用。");
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM bom_item WHERE project_id=@ProjectId AND bom_kind=@Kind", new { ProjectId = projectId, Kind = kind.ToString() }, transaction, cancellationToken: cancellationToken));
        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var item in items)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO bom_item(id,project_id,bom_kind,sequence_no,drawing_number,name,quantity,unit,material,specification,revision_label,is_complete,row_version,updated_at) VALUES(@Id,@ProjectId,@Kind,@Sequence,@DrawingNumber,@Name,@Quantity,@Unit,@Material,@Specification,@Revision,@IsComplete,1,@Now)",
                new { item.Id, ProjectId = projectId, Kind = kind.ToString(), item.Sequence, item.DrawingNumber, item.Name, item.Quantity, item.Unit, item.Material, item.Specification, Revision = item.Revision, item.IsComplete, Now = now },
                transaction, cancellationToken: cancellationToken));
        }
        await transaction.CommitAsync(cancellationToken);
        return items.OrderBy(item => item.Sequence).ToArray();
    }

    public async Task<IReadOnlyList<ReleasePackage>> ListReleasePackagesAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<ReleasePackageRow>(new CommandDefinition(
            """
            SELECT id, project_id, package_number, state, reference_snapshot_id, mechanical_bom_revision, electrical_bom_revision, mechanical_bom_snapshot_json, electrical_bom_snapshot_json, published_at, published_path, publish_error, created_at
            FROM release_package
            WHERE project_id = @ProjectId
            ORDER BY created_at DESC
            """,
            new { ProjectId = projectId },
            cancellationToken: cancellationToken));
        var result = new List<ReleasePackage>();
        foreach (var row in rows)
        {
            result.Add(await MapReleasePackageAsync(connection, null, row, cancellationToken));
        }

        return result;
    }

    public async Task<ReleasePackage?> FindReleasePackageAsync(Guid releasePackageId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await FindReleasePackageAsync(connection, null, releasePackageId, cancellationToken);
    }

    public async Task<UserAccount?> FindUserAsync(string username, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            "SELECT id, username, display_name, password_hash, role, is_active FROM pdm_user WHERE username = @Username LIMIT 1",
            new { Username = username },
            cancellationToken: cancellationToken));
        return row is null ? null : new UserAccount(row.Id, row.Username, row.DisplayName, row.PasswordHash, Enum.Parse<UserRole>(row.Role), row.IsActive);
    }

    public async Task<int> CountUsersAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM pdm_user", cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<AuditEntry>> ListAuditAsync(string actor, UserRole role, int take, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var limit = Math.Clamp(take, 1, 500);
        var rows = await connection.QueryAsync<AuditRow>(new CommandDefinition(
            role == UserRole.Administrator
                ? "SELECT id,occurred_at,actor,action_name,entity_type,entity_id,detail_json FROM audit_entry ORDER BY occurred_at DESC LIMIT @Limit"
                : "SELECT id,occurred_at,actor,action_name,entity_type,entity_id,detail_json FROM audit_entry WHERE actor=@Actor ORDER BY occurred_at DESC LIMIT @Limit",
            new { Actor = actor, Limit = limit }, cancellationToken: cancellationToken));
        return rows.Select(row => new AuditEntry(row.Id, DateTime.SpecifyKind(row.OccurredAt, DateTimeKind.Utc), row.Actor, row.ActionName, row.EntityType, row.EntityId, ReadAuditDetail(row.DetailJson))).ToArray();
    }

    private static string ReadAuditDetail(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("detail", out var detail) ? detail.GetString() ?? string.Empty : json;
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<Project?> FindProjectAsync(DbConnection connection, DbTransaction? transaction, Guid projectId, CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<ProjectRow>(new CommandDefinition(
            """
            SELECT p.id,p.code,p.name,p.project_alias,p.organization_id,o.name organization_name,p.project_type_code,
                   p.equipment_type_code,p.customer_code,p.customer_name,p.customer_project_sequence,p.device_model,
                   p.signed_date,p.quantity,p.parent_project_id,p.child_sequence,p.owner,p.vault_location,p.release_location,p.is_active
            FROM project p LEFT JOIN project_organization o ON o.id=p.organization_id
            WHERE p.id=@ProjectId
            """,
            new { ProjectId = projectId },
            transaction,
            cancellationToken: cancellationToken));
        if (row is null) return null;
        var serials = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT serial_number FROM project_serial_number WHERE project_id=@ProjectId ORDER BY sequence_no",
            new { ProjectId = projectId }, transaction, cancellationToken: cancellationToken));
        var responsibleUsers = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT username FROM project_responsible WHERE project_id=@ProjectId ORDER BY username",
            new { ProjectId = projectId }, transaction, cancellationToken: cancellationToken));
        var responsibles = responsibleUsers.ToArray();
        if (responsibles.Length == 0 && !string.IsNullOrWhiteSpace(row.Owner)) responsibles = [row.Owner];
        return MapProject(row, serials.ToArray(), responsibles);
    }

    private static async Task<PdmDocument?> FindDocumentAsync(DbConnection connection, DbTransaction? transaction, Guid documentId, CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<DocumentRow>(new CommandDefinition(
            """
            SELECT id, project_id, drawing_number, name, file_name, kind, lifecycle_state, revision_label, checked_out_by, updated_at
            FROM document WHERE id = @DocumentId
            """,
            new { DocumentId = documentId },
            transaction,
            cancellationToken: cancellationToken));
        return row is null ? null : MapDocument(row);
    }

    private async Task<ReleasePackage?> FindReleasePackageAsync(DbConnection connection, DbTransaction? transaction, Guid packageId, CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<ReleasePackageRow>(new CommandDefinition(
            """
            SELECT id, project_id, package_number, state, reference_snapshot_id, mechanical_bom_revision, electrical_bom_revision, mechanical_bom_snapshot_json, electrical_bom_snapshot_json, published_at, published_path, publish_error, created_at
            FROM release_package WHERE id = @PackageId
            """,
            new { PackageId = packageId },
            transaction,
            cancellationToken: cancellationToken));
        return row is null ? null : await MapReleasePackageAsync(connection, transaction, row, cancellationToken);
    }

    private static Project MapProject(ProjectRow row, IReadOnlyList<string>? serialNumbers = null, IReadOnlyList<string>? responsibleUsers = null) =>
        new(row.Id, row.Code, row.Name, row.Owner, row.VaultLocation, row.ReleaseLocation, row.IsActive)
        {
            ProjectAlias = row.ProjectAlias,
            OrganizationId = row.OrganizationId,
            OrganizationName = row.OrganizationName,
            ProjectTypeCode = row.ProjectTypeCode,
            EquipmentTypeCode = row.EquipmentTypeCode,
            CustomerCode = row.CustomerCode,
            CustomerName = row.CustomerName,
            CustomerProjectSequence = row.CustomerProjectSequence,
            DeviceModel = row.DeviceModel,
            SignedDate = row.SignedDate is null ? null : DateOnly.FromDateTime(row.SignedDate.Value),
            Quantity = row.Quantity,
            ParentProjectId = row.ParentProjectId,
            ChildSequence = row.ChildSequence,
            SerialNumbers = serialNumbers ?? [],
            ResponsibleUsers = responsibleUsers ?? (string.IsNullOrWhiteSpace(row.Owner) ? [] : [row.Owner])
        };

    private static async Task<IReadOnlyList<Project>> MapProjectsAsync(DbConnection connection, DbTransaction? transaction, IEnumerable<ProjectRow> rows, CancellationToken cancellationToken)
    {
        var rowArray = rows.ToArray();
        if (rowArray.Length == 0) return [];
        var serialRows = await connection.QueryAsync<ProjectSerialRow>(new CommandDefinition(
            "SELECT project_id,sequence_no,serial_number FROM project_serial_number WHERE project_id IN @ProjectIds ORDER BY project_id,sequence_no",
            new { ProjectIds = rowArray.Select(row => row.Id).ToArray() }, transaction, cancellationToken: cancellationToken));
        var serials = serialRows.GroupBy(row => row.ProjectId).ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(row => row.SerialNumber).ToArray());
        var responsibleRows = await connection.QueryAsync<ProjectResponsibleRow>(new CommandDefinition(
            "SELECT project_id,username FROM project_responsible WHERE project_id IN @ProjectIds ORDER BY project_id,username",
            new { ProjectIds = rowArray.Select(row => row.Id).ToArray() }, transaction, cancellationToken: cancellationToken));
        var responsibles = responsibleRows.GroupBy(row => row.ProjectId).ToDictionary(group => group.Key, group => (IReadOnlyList<string>)group.Select(row => row.Username).ToArray());
        return rowArray.Select(row => MapProject(row, serials.GetValueOrDefault(row.Id, []), responsibles.GetValueOrDefault(row.Id))).ToArray();
    }

    private static Project BuildNumberedProject(
        Guid id, string code, string name, string? projectAlias, Guid organizationId, string organizationName,
        string projectTypeCode, int equipmentTypeCode, string customerCode, string customerName,
        int customerProjectSequence, string deviceModel, DateOnly signedDate, int quantity,
        Guid? parentProjectId, int? childSequence, string owner, string vaultLocation, string releaseLocation,
        string projectCompanyCode, int serialStart) =>
        new(id, code, name, owner, vaultLocation, releaseLocation, true)
        {
            ProjectAlias = projectAlias,
            OrganizationId = organizationId,
            OrganizationName = organizationName,
            ProjectTypeCode = projectTypeCode,
            EquipmentTypeCode = equipmentTypeCode,
            CustomerCode = customerCode,
            CustomerName = customerName,
            CustomerProjectSequence = customerProjectSequence,
            DeviceModel = deviceModel,
            SignedDate = signedDate,
            Quantity = quantity,
            ParentProjectId = parentProjectId,
            ChildSequence = childSequence,
            SerialNumbers = Enumerable.Range(serialStart, quantity).Select(value => $"{projectCompanyCode}{value:D7}").ToArray()
        };

    private static async Task InsertNumberedProjectAsync(MySqlConnection connection, DbTransaction transaction, Project project, DateTime now, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO project(id,code,name,project_alias,organization_id,project_type_code,equipment_type_code,customer_code,customer_name,
                customer_project_sequence,device_model,signed_date,quantity,parent_project_id,child_sequence,owner,vault_location,release_location,
                is_active,row_version,created_at,updated_at)
            VALUES(@Id,@Code,@Name,@ProjectAlias,@OrganizationId,@ProjectTypeCode,@EquipmentTypeCode,@CustomerCode,@CustomerName,
                @CustomerProjectSequence,@DeviceModel,@SignedDate,@Quantity,@ParentProjectId,@ChildSequence,@Owner,@VaultLocation,@ReleaseLocation,
                1,1,@Now,@Now)
            """,
            new
            {
                project.Id, project.Code, project.Name, project.ProjectAlias, project.OrganizationId, project.ProjectTypeCode,
                project.EquipmentTypeCode, project.CustomerCode, project.CustomerName, project.CustomerProjectSequence,
                project.DeviceModel, SignedDate = project.SignedDate!.Value.ToDateTime(TimeOnly.MinValue), project.Quantity,
                project.ParentProjectId, project.ChildSequence, project.Owner, project.VaultLocation, project.ReleaseLocation, Now = now
            }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO project_serial_number(project_id,sequence_no,serial_number) VALUES(@ProjectId,@Sequence,@SerialNumber)",
            project.SerialNumbers.Select((serial, index) => new { ProjectId = project.Id, Sequence = index + 1, SerialNumber = serial }),
            transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO project_responsible(project_id,username,assigned_at) VALUES(@ProjectId,@Username,@Now)",
            project.ResponsibleUsers.Select(username => new { ProjectId = project.Id, Username = username, Now = now }),
            transaction, cancellationToken: cancellationToken));
    }

    private static async Task<int> ReserveProjectNumberAsync(MySqlConnection connection, DbTransaction transaction, Guid organizationId, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition("INSERT IGNORE INTO project_number_counter(organization_id,current_value) VALUES(@OrganizationId,0)", new { OrganizationId = organizationId }, transaction, cancellationToken: cancellationToken));
        var last = await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT current_value FROM project_number_counter WHERE organization_id=@OrganizationId FOR UPDATE", new { OrganizationId = organizationId }, transaction, cancellationToken: cancellationToken));
        if (last >= 99999) throw new PdmRuleException("该组织的5位项目流水号已用尽。");
        var next = last + 1;
        await connection.ExecuteAsync(new CommandDefinition("UPDATE project_number_counter SET current_value=@Next WHERE organization_id=@OrganizationId", new { OrganizationId = organizationId, Next = next }, transaction, cancellationToken: cancellationToken));
        return next;
    }

    private static async Task<int> ReserveCustomerProjectNumberAsync(MySqlConnection connection, DbTransaction transaction, Guid organizationId, string customerCode, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition("INSERT IGNORE INTO customer_project_counter(organization_id,customer_code,current_value) VALUES(@OrganizationId,@CustomerCode,0)", new { OrganizationId = organizationId, CustomerCode = customerCode }, transaction, cancellationToken: cancellationToken));
        var last = await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT current_value FROM customer_project_counter WHERE organization_id=@OrganizationId AND customer_code=@CustomerCode FOR UPDATE", new { OrganizationId = organizationId, CustomerCode = customerCode }, transaction, cancellationToken: cancellationToken));
        if (last >= 999) throw new PdmRuleException("该客户的3位项目流水号已用尽。");
        var next = last + 1;
        await connection.ExecuteAsync(new CommandDefinition("UPDATE customer_project_counter SET current_value=@Next WHERE organization_id=@OrganizationId AND customer_code=@CustomerCode", new { OrganizationId = organizationId, CustomerCode = customerCode, Next = next }, transaction, cancellationToken: cancellationToken));
        return next;
    }

    private static async Task<int> ReserveSerialNumbersAsync(MySqlConnection connection, DbTransaction transaction, Guid organizationId, int quantity, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition("INSERT IGNORE INTO serial_number_counter(organization_id,current_value) VALUES(@OrganizationId,0)", new { OrganizationId = organizationId }, transaction, cancellationToken: cancellationToken));
        var last = await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT current_value FROM serial_number_counter WHERE organization_id=@OrganizationId FOR UPDATE", new { OrganizationId = organizationId }, transaction, cancellationToken: cancellationToken));
        if (last > 9999999 - quantity) throw new PdmRuleException("该组织的7位序列流水号余额不足。");
        var next = last + quantity;
        await connection.ExecuteAsync(new CommandDefinition("UPDATE serial_number_counter SET current_value=@Next WHERE organization_id=@OrganizationId", new { OrganizationId = organizationId, Next = next }, transaction, cancellationToken: cancellationToken));
        return last + 1;
    }

    private static PdmDocument MapDocument(DocumentRow row) =>
        new(row.Id, row.ProjectId, row.DrawingNumber, row.Name, row.FileName, Enum.Parse<DocumentKind>(row.Kind), Enum.Parse<DocumentLifecycleState>(row.LifecycleState), RevisionLabel.Parse(row.RevisionLabel), row.CheckedOutBy, DateTime.SpecifyKind(row.UpdatedAt, DateTimeKind.Utc));

    private static BomItem MapBomItem(BomRow row) =>
        new(row.Id, row.ProjectId, Enum.Parse<BomKind>(row.BomKind), row.SequenceNo, row.DrawingNumber, row.Name, row.Quantity, row.Unit, row.Material, row.Specification, row.RevisionLabel, row.IsComplete);

    private static async Task<ReleasePackage> MapReleasePackageAsync(DbConnection connection, DbTransaction? transaction, ReleasePackageRow row, CancellationToken cancellationToken)
    {
        var taskRows = await connection.QueryAsync<ApprovalTaskRow>(new CommandDefinition(
            """
            SELECT id, release_package_id, stage, assignee, decision_by, decision_value, decision_comment, decided_at
            FROM approval_task WHERE release_package_id = @PackageId ORDER BY stage
            """,
            new { PackageId = row.Id },
            transaction,
            cancellationToken: cancellationToken));
        var tasks = taskRows.Select(task => new ApprovalTask(
            task.Id,
            task.ReleasePackageId,
            Enum.Parse<ApprovalStage>(task.Stage),
            task.Assignee,
            task.DecisionBy,
            task.DecisionValue is null ? null : Enum.Parse<ApprovalDecision>(task.DecisionValue),
            task.DecisionComment,
            task.DecidedAt is null ? null : new DateTimeOffset(DateTime.SpecifyKind(task.DecidedAt.Value, DateTimeKind.Utc)))).ToArray();
        return new ReleasePackage(
            row.Id,
            row.ProjectId,
            row.PackageNumber,
            Enum.Parse<ReleasePackageState>(row.State),
            row.ReferenceSnapshotId,
            row.MechanicalBomRevision,
            row.ElectricalBomRevision,
            tasks,
            DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc),
            row.PublishedAt is null ? null : new DateTimeOffset(DateTime.SpecifyKind(row.PublishedAt.Value, DateTimeKind.Utc)),
            row.PublishedPath)
        {
            MechanicalBomSnapshot = JsonSerializer.Deserialize<List<BomItem>>(row.MechanicalBomSnapshotJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [],
            ElectricalBomSnapshot = JsonSerializer.Deserialize<List<BomItem>>(row.ElectricalBomSnapshotJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [],
            PublishError = row.PublishError
        };
    }

    private sealed class ProjectRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string? ProjectAlias { get; init; }
        public Guid? OrganizationId { get; init; }
        public string? OrganizationName { get; init; }
        public string? ProjectTypeCode { get; init; }
        public int? EquipmentTypeCode { get; init; }
        public string? CustomerCode { get; init; }
        public string? CustomerName { get; init; }
        public int? CustomerProjectSequence { get; init; }
        public string? DeviceModel { get; init; }
        public DateTime? SignedDate { get; init; }
        public int Quantity { get; init; } = 1;
        public Guid? ParentProjectId { get; init; }
        public int? ChildSequence { get; init; }
        public string Owner { get; init; } = string.Empty;
        public string VaultLocation { get; init; } = string.Empty;
        public string ReleaseLocation { get; init; } = string.Empty;
        public bool IsActive { get; init; }
    }

    private sealed class ProjectOrganizationRow
    {
        public Guid Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string ProjectCompanyCode { get; init; } = string.Empty;
        public string ModelCompanyCode { get; init; } = string.Empty;
        public string CrmCompanyName { get; init; } = string.Empty;
        public bool IsActive { get; init; }
        public long CurrentProjectSequence { get; init; }
        public long CurrentSerialSequence { get; init; }
    }

    private sealed class ProjectTypeDefinitionRow
    {
        public string Code { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public bool IsActive { get; init; }
    }

    private sealed class EquipmentTypeDefinitionRow
    {
        public int Code { get; init; }
        public string Name { get; init; } = string.Empty;
        public bool IsActive { get; init; }
    }

    private sealed class ProjectSerialRow
    {
        public Guid ProjectId { get; init; }
        public int SequenceNo { get; init; }
        public string SerialNumber { get; init; } = string.Empty;
    }

    private sealed class ProjectResponsibleRow
    {
        public Guid ProjectId { get; init; }
        public string Username { get; init; } = string.Empty;
    }

    private sealed class CustomerRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public bool IsActive { get; init; }
    }

    private sealed class DocumentRow
    {
        public Guid Id { get; init; }
        public Guid ProjectId { get; init; }
        public string DrawingNumber { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public string Kind { get; init; } = string.Empty;
        public string LifecycleState { get; init; } = string.Empty;
        public string RevisionLabel { get; init; } = string.Empty;
        public string? CheckedOutBy { get; init; }
        public DateTime UpdatedAt { get; init; }
    }

    private sealed class BomRow
    {
        public Guid Id { get; init; }
        public Guid ProjectId { get; init; }
        public string BomKind { get; init; } = string.Empty;
        public int SequenceNo { get; init; }
        public string DrawingNumber { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public decimal Quantity { get; init; }
        public string Unit { get; init; } = string.Empty;
        public string? Material { get; init; }
        public string? Specification { get; init; }
        public string RevisionLabel { get; init; } = string.Empty;
        public bool IsComplete { get; init; }
    }

    private sealed class ReleasePackageRow
    {
        public Guid Id { get; init; }
        public Guid ProjectId { get; init; }
        public string PackageNumber { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public Guid ReferenceSnapshotId { get; init; }
        public string MechanicalBomRevision { get; init; } = string.Empty;
        public string ElectricalBomRevision { get; init; } = string.Empty;
        public string MechanicalBomSnapshotJson { get; init; } = "[]";
        public string ElectricalBomSnapshotJson { get; init; } = "[]";
        public DateTime? PublishedAt { get; init; }
        public string? PublishedPath { get; init; }
        public string? PublishError { get; init; }
        public DateTime CreatedAt { get; init; }
    }

    private sealed class ReferenceSnapshotRow
    {
        public Guid Id { get; init; }
        public Guid ProjectId { get; init; }
        public Guid RootDocumentId { get; init; }
        public DateTime CapturedAt { get; init; }
        public string CapturedBy { get; init; } = string.Empty;
        public string Sha256 { get; init; } = string.Empty;
        public string RootJson { get; init; } = string.Empty;
    }

    private sealed class ApprovalTaskRow
    {
        public Guid Id { get; init; }
        public Guid ReleasePackageId { get; init; }
        public string Stage { get; init; } = string.Empty;
        public string Assignee { get; init; } = string.Empty;
        public string? DecisionBy { get; init; }
        public string? DecisionValue { get; init; }
        public string? DecisionComment { get; init; }
        public DateTime? DecidedAt { get; init; }
    }

    private sealed class UserRow
    {
        public Guid Id { get; init; }
        public string Username { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string PasswordHash { get; init; } = string.Empty;
        public string Role { get; init; } = string.Empty;
        public bool IsActive { get; init; }
    }

    private sealed class AuditRow
    {
        public Guid Id { get; init; }
        public DateTime OccurredAt { get; init; }
        public string Actor { get; init; } = string.Empty;
        public string ActionName { get; init; } = string.Empty;
        public string EntityType { get; init; } = string.Empty;
        public string EntityId { get; init; } = string.Empty;
        public string DetailJson { get; init; } = "{}";
    }
}
