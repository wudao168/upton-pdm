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
                   p.signed_date,p.quantity,p.parent_project_id,p.child_sequence,p.owner,p.vault_location,p.release_location,p.is_active,
                   COALESCE(p.execution_unit_id,parent.execution_unit_id) execution_unit_id,execution_unit.name execution_unit_name
             FROM project p
             LEFT JOIN project_organization o ON o.id=p.organization_id
             LEFT JOIN project parent ON parent.id=p.parent_project_id
             LEFT JOIN organization_unit execution_unit ON execution_unit.id=COALESCE(p.execution_unit_id,parent.execution_unit_id)
            ORDER BY COALESCE(parent.code,p.code), CASE WHEN p.parent_project_id IS NULL THEN 0 ELSE 1 END, p.child_sequence
            """,
            cancellationToken: cancellationToken));
        return await MapProjectsAsync(connection, null, rows, cancellationToken);
    }

    public async Task<IReadOnlyList<Project>> ListProjectsForUserAsync(string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await HasUserPermissionAsync(actor, role, PermissionCodes.ProjectView, cancellationToken)) return [];
        if (role == UserRole.Administrator)
        {
            var administratorProjects = await ListProjectsAsync(cancellationToken);
            return administratorProjects.Select(ApplyAdministratorCapabilities).ToArray();
        }

        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<ProjectRow>(new CommandDefinition(
            """
            SELECT p.id,p.code,p.name,p.project_alias,p.organization_id,o.name organization_name,p.project_type_code,
                   p.equipment_type_code,p.customer_code,p.customer_name,p.customer_project_sequence,p.device_model,
                   p.signed_date,p.quantity,p.parent_project_id,p.child_sequence,p.owner,p.vault_location,p.release_location,p.is_active,
                   COALESCE(p.execution_unit_id,parent.execution_unit_id) execution_unit_id,execution_unit.name execution_unit_name
             FROM project p
             LEFT JOIN project_organization o ON o.id=p.organization_id
             LEFT JOIN project parent ON parent.id=p.parent_project_id
             LEFT JOIN organization_unit execution_unit ON execution_unit.id=COALESCE(p.execution_unit_id,parent.execution_unit_id)
             WHERE EXISTS (
                 SELECT 1 FROM project_assignment assignment
                 WHERE assignment.project_id=COALESCE(p.parent_project_id,p.id) AND assignment.username=@Actor
                   AND assignment.assignment_type IN ('PrimaryProjectManager','CollaborativeProjectManager','DesignLead'))
                OR EXISTS (
                 SELECT 1 FROM project_assignment assignment
                 WHERE assignment.project_id=p.id AND assignment.username=@Actor AND assignment.assignment_type='Designer')
                OR (p.parent_project_id IS NULL AND EXISTS (
                 SELECT 1 FROM project child
                 INNER JOIN project_assignment assignment ON assignment.project_id=child.id
                 WHERE child.parent_project_id=p.id AND assignment.username=@Actor AND assignment.assignment_type='Designer'))
                OR EXISTS (
                 SELECT 1 FROM organization_unit_manager manager
                 WHERE manager.unit_id=COALESCE(p.execution_unit_id,parent.execution_unit_id) AND manager.username=@Actor)
                OR EXISTS (
                 SELECT 1 FROM release_package package
                 INNER JOIN approval_task task ON task.release_package_id=package.id
                 WHERE package.project_id=p.id AND task.assignee=@Actor)
                OR (@CanAssignExecutionUnit=1 AND EXISTS (
                 SELECT 1 FROM organization_membership membership
                 INNER JOIN organization_unit member_unit ON member_unit.id=membership.unit_id
                 WHERE membership.username=@Actor AND member_unit.organization_id=p.organization_id))
            ORDER BY COALESCE(parent.code,p.code), CASE WHEN p.parent_project_id IS NULL THEN 0 ELSE 1 END, p.child_sequence
            """,
            new { Actor = actor, CanAssignExecutionUnit = await HasUserPermissionAsync(actor, role, PermissionCodes.ProjectExecutionAssign, cancellationToken) },
            cancellationToken: cancellationToken));
        var projects = await MapProjectsAsync(connection, null, rows, cancellationToken);
        return await ApplyCapabilitiesAsync(connection, projects, actor, role, cancellationToken);
    }

    public async Task<Project?> FindProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await FindProjectAsync(connection, null, projectId, cancellationToken);
    }

    public async Task<bool> HasProjectReadAccessAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await HasUserPermissionAsync(actor, role, PermissionCodes.ProjectView, cancellationToken)) return false;
        if (role == UserRole.Administrator) return true;
        await using var connection = await OpenAsync(cancellationToken);
        var value = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            """
             SELECT CASE WHEN EXISTS (SELECT 1 FROM project_assignment assignment
                    WHERE assignment.project_id=COALESCE(p.parent_project_id,p.id) AND assignment.username=@Actor
                      AND assignment.assignment_type IN ('PrimaryProjectManager','CollaborativeProjectManager','DesignLead'))
                OR EXISTS (SELECT 1 FROM project_assignment assignment
                    WHERE assignment.project_id=p.id AND assignment.username=@Actor AND assignment.assignment_type='Designer')
                OR (p.parent_project_id IS NULL AND EXISTS (
                    SELECT 1 FROM project child INNER JOIN project_assignment assignment ON assignment.project_id=child.id
                    WHERE child.parent_project_id=p.id AND assignment.username=@Actor AND assignment.assignment_type='Designer'))
                OR EXISTS (SELECT 1 FROM organization_unit_manager manager
                    WHERE manager.unit_id=COALESCE(p.execution_unit_id,parent.execution_unit_id) AND manager.username=@Actor)
                OR EXISTS (SELECT 1 FROM release_package package
                    INNER JOIN approval_task task ON task.release_package_id=package.id
                    WHERE package.project_id=p.id AND task.assignee=@Actor)
                OR (@CanAssignExecutionUnit=1 AND EXISTS (
                    SELECT 1 FROM organization_membership membership INNER JOIN organization_unit member_unit ON member_unit.id=membership.unit_id
                    WHERE membership.username=@Actor AND member_unit.organization_id=p.organization_id)) THEN 1 ELSE 0 END
             FROM project p
             LEFT JOIN project parent ON parent.id=p.parent_project_id
             WHERE p.id=@ProjectId
            """,
            new { ProjectId = projectId, Actor = actor, CanAssignExecutionUnit = await HasUserPermissionAsync(actor, role, PermissionCodes.ProjectExecutionAssign, cancellationToken) },
            cancellationToken: cancellationToken));
        return value == 1;
    }

    public async Task<bool> HasProjectContentReadAccessAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await HasUserPermissionAsync(actor, role, PermissionCodes.ProjectContentView, cancellationToken)) return false;
        if (role == UserRole.Administrator) return true;
        await using var connection = await OpenAsync(cancellationToken);
        var value = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            """
            SELECT CASE WHEN EXISTS (SELECT 1 FROM project_assignment assignment
                    WHERE assignment.project_id=p.id AND assignment.username=@Actor AND assignment.assignment_type='Designer')
                OR EXISTS (SELECT 1 FROM project_assignment assignment
                    WHERE assignment.project_id=COALESCE(p.parent_project_id,p.id) AND assignment.username=@Actor AND assignment.assignment_type='DesignLead')
                OR EXISTS (SELECT 1 FROM release_package package
                    INNER JOIN approval_task task ON task.release_package_id=package.id
                    WHERE package.project_id=p.id AND task.assignee=@Actor)
                THEN 1 ELSE 0 END
            FROM project p WHERE p.id=@ProjectId
            """, new { ProjectId = projectId, Actor = actor }, cancellationToken: cancellationToken));
        return value == 1;
    }

    public async Task<bool> HasChildProjectsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM project WHERE parent_project_id=@ProjectId)",
            new { ProjectId = projectId },
            cancellationToken: cancellationToken)) == 1;
    }

    public async Task DeleteProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var deletedProject = await connection.QuerySingleOrDefaultAsync<DeletedProjectNumberRow>(new CommandDefinition(
                """
                SELECT project.id,project.code,project.organization_id,project.parent_project_id,project.project_type_code,
                       project.customer_code,project.customer_project_sequence,organization.project_company_code
                FROM project LEFT JOIN project_organization organization ON organization.id=project.organization_id
                WHERE project.id=@ProjectId FOR UPDATE
                """,
                new { ProjectId = projectId }, transaction, cancellationToken: cancellationToken));
            if (deletedProject is null) throw new PdmNotFoundException("项目不存在。");

            var dependencies = await connection.QuerySingleAsync<ProjectDependencyRow>(new CommandDefinition(
                """
                SELECT
                    (SELECT COUNT(*) FROM project WHERE parent_project_id=@ProjectId) ChildCount,
                    (SELECT COUNT(*) FROM document WHERE project_id=@ProjectId) DocumentCount,
                    (SELECT COUNT(*) FROM bom_item WHERE project_id=@ProjectId) BomCount,
                    (SELECT COUNT(*) FROM reference_snapshot WHERE project_id=@ProjectId) SnapshotCount,
                    (SELECT COUNT(*) FROM release_package WHERE project_id=@ProjectId) ReleasePackageCount
                """,
                new { ProjectId = projectId }, transaction, cancellationToken: cancellationToken));
            if (dependencies.ChildCount > 0) throw new PdmConflictException("该项目存在子项目，请先删除子项目。");
            if (dependencies.DocumentCount > 0) throw new PdmConflictException("该项目存在受控图档，不能删除。");
            if (dependencies.BomCount > 0) throw new PdmConflictException("该项目存在BOM数据，不能删除。");
            if (dependencies.SnapshotCount > 0) throw new PdmConflictException("该项目存在设计树快照，不能删除。");
            if (dependencies.ReleasePackageCount > 0) throw new PdmConflictException("该项目存在审批或发布包，不能删除。");

            var serialNumbers = (await connection.QueryAsync<string>(new CommandDefinition(
                "SELECT serial_number FROM project_serial_number WHERE project_id=@ProjectId ORDER BY sequence_no FOR UPDATE",
                new { ProjectId = projectId }, transaction, cancellationToken: cancellationToken))).ToArray();
            if (deletedProject.OrganizationId is not null && !string.IsNullOrWhiteSpace(deletedProject.ProjectCompanyCode))
            {
                if (deletedProject.ParentProjectId is null
                    && TryParseProjectSequence(deletedProject.Code, deletedProject.ProjectTypeCode, deletedProject.ProjectCompanyCode, out var projectSequence))
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        "INSERT IGNORE INTO released_project_number(organization_id,sequence_value,released_at) VALUES(@OrganizationId,@Sequence,@Now)",
                        new { OrganizationId = deletedProject.OrganizationId.Value, Sequence = projectSequence, Now = timeProvider.GetUtcNow().UtcDateTime }, transaction, cancellationToken: cancellationToken));
                    if (!string.IsNullOrWhiteSpace(deletedProject.CustomerCode) && deletedProject.CustomerProjectSequence is not null)
                        await connection.ExecuteAsync(new CommandDefinition(
                            "INSERT IGNORE INTO released_customer_project_number(organization_id,customer_code,sequence_value,released_at) VALUES(@OrganizationId,@CustomerCode,@Sequence,@Now)",
                            new { OrganizationId = deletedProject.OrganizationId.Value, CustomerCode = deletedProject.CustomerCode, Sequence = deletedProject.CustomerProjectSequence.Value, Now = timeProvider.GetUtcNow().UtcDateTime }, transaction, cancellationToken: cancellationToken));
                }

                var serialSequences = serialNumbers
                    .Select(serial => TryParseSerialSequence(serial, deletedProject.ProjectCompanyCode, out var value) ? value : (int?)null)
                    .Where(value => value is not null)
                    .Select(value => value!.Value)
                    .Distinct()
                    .ToArray();
                if (serialSequences.Length > 0)
                    await connection.ExecuteAsync(new CommandDefinition(
                        "INSERT IGNORE INTO released_serial_number(organization_id,sequence_value,released_at) VALUES(@OrganizationId,@Sequence,@Now)",
                        serialSequences.Select(value => new { OrganizationId = deletedProject.OrganizationId.Value, Sequence = value, Now = timeProvider.GetUtcNow().UtcDateTime }), transaction, cancellationToken: cancellationToken));
            }

            await connection.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM project_user_access WHERE project_id=@ProjectId;
                DELETE FROM project_assignment WHERE project_id=@ProjectId;
                DELETE FROM project_responsible WHERE project_id=@ProjectId;
                DELETE FROM project_serial_number WHERE project_id=@ProjectId;
                DELETE FROM project WHERE id=@ProjectId;
                """,
                new { ProjectId = projectId }, transaction, cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }
        catch (MySqlException exception) when (exception.Number == 1451)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new PdmConflictException("该项目仍有关联业务数据，不能删除。");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
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
                "SELECT id,code,name,is_active IsActive,source_system SourceSystem,last_synced_at LastSyncedAt FROM pdm_customer WHERE id=@CustomerId FOR UPDATE",
                new { command.CustomerId }, transaction, cancellationToken: cancellationToken))
                ?? throw new PdmRuleException("所选客户不存在。");
            if (!customer.IsActive) throw new PdmRuleException("所选客户已停用。");
            if (!string.Equals(customer.SourceSystem, "u9c", StringComparison.OrdinalIgnoreCase))
                throw new PdmRuleException("所选客户不是从U9C同步的数据，请重新选择客户。");

            var projectSequence = await ReserveProjectNumberAsync(connection, transaction, command.OrganizationId, cancellationToken);
            var customerSequence = await ReserveCustomerProjectNumberAsync(connection, transaction, command.OrganizationId, customer.Code, cancellationToken);
            var serialSequences = await ReserveSerialNumbersAsync(connection, transaction, command.OrganizationId, command.Quantity, cancellationToken);
            var code = $"{command.ProjectTypeCode}{organization.ProjectCompanyCode}{projectSequence:D5}";
            var deviceModel = $"{organization.ModelCompanyCode}-{command.EquipmentTypeCode}-{customer.Code}-{customerSequence:D3}-00";
            var project = BuildNumberedProject(
                Guid.NewGuid(), code, command.Name, command.ProjectAlias, command.OrganizationId, organization.Name,
                command.ProjectTypeCode, command.EquipmentTypeCode, customer.Code, customer.Name,
                customerSequence, deviceModel, command.SignedDate, command.Quantity, null, null, command.Owner,
                Path.Combine(command.VaultLocation, code), Path.Combine(command.ReleaseLocation, code), organization.ProjectCompanyCode, serialSequences)
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

            var usedChildSequences = (await connection.QueryAsync<int>(new CommandDefinition(
                "SELECT child_sequence FROM project WHERE parent_project_id=@ParentProjectId ORDER BY child_sequence FOR UPDATE",
                new { command.ParentProjectId }, transaction, cancellationToken: cancellationToken))).ToHashSet();
            var childSequence = Enumerable.Range(1, 99).FirstOrDefault(value => !usedChildSequences.Contains(value));
            if (childSequence == 0) throw new PdmRuleException("该主项目的两位子项目号已用尽。");
            var organization = await connection.QuerySingleAsync<ProjectOrganizationRow>(new CommandDefinition(
                "SELECT id,name,project_company_code,model_company_code,crm_company_name,is_active FROM project_organization WHERE id=@OrganizationId",
                new { OrganizationId = parent.OrganizationId.Value }, transaction, cancellationToken: cancellationToken));
            var serialSequences = await ReserveSerialNumbersAsync(connection, transaction, parent.OrganizationId.Value, command.Quantity, cancellationToken);
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
                Path.Combine(command.ReleaseRoot ?? Path.GetDirectoryName(parent.ReleaseLocation)!, code), organization.ProjectCompanyCode, serialSequences)
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
            SELECT d.id,d.project_id,d.folder_id,d.drawing_number,d.name,d.file_name,d.kind,d.lifecycle_state,d.revision_label,
                   d.checked_out_by,d.checked_out_at,d.checkout_session_id,d.checkout_machine,d.checkout_last_heartbeat_at,
                   d.checkout_lease_expires_at,d.checkout_release_requested_by,d.checkout_release_requested_at,
                   d.checkout_release_request_reason,d.updated_at,
                   (SELECT COUNT(*) FROM document_version v WHERE v.document_id=d.id) stored_version_count
            FROM document d
            WHERE d.project_id = @ProjectId
            ORDER BY d.drawing_number, d.kind
            """,
            new { ProjectId = projectId },
            cancellationToken: cancellationToken));
        return rows.Select(MapDocument).ToArray();
    }

    public async Task<IReadOnlyList<PdmDocument>> ListProjectTreeDocumentsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<DocumentRow>(new CommandDefinition(
            """
            SELECT d.id,d.project_id,d.folder_id,d.drawing_number,d.name,d.file_name,d.kind,d.lifecycle_state,d.revision_label,
                   d.checked_out_by,d.checked_out_at,d.checkout_session_id,d.checkout_machine,d.checkout_last_heartbeat_at,
                   d.checkout_lease_expires_at,d.checkout_release_requested_by,d.checkout_release_requested_at,
                   d.checkout_release_request_reason,d.updated_at,
                   (SELECT COUNT(*) FROM document_version v WHERE v.document_id=d.id) stored_version_count
            FROM document d
            INNER JOIN project requested ON requested.id=@ProjectId
            INNER JOIN project owner_project ON owner_project.id=d.project_id
            WHERE owner_project.id=COALESCE(requested.parent_project_id,requested.id)
               OR owner_project.parent_project_id=COALESCE(requested.parent_project_id,requested.id)
            ORDER BY owner_project.parent_project_id,owner_project.child_sequence,d.drawing_number,d.kind
            """,
            new { ProjectId = projectId }, cancellationToken: cancellationToken));
        return rows.Select(MapDocument).ToArray();
    }

    public async Task<IReadOnlyList<DocumentContentFingerprint>> ListDocumentContentFingerprintsAsync(
        IReadOnlyCollection<Guid> projectIds,
        CancellationToken cancellationToken)
    {
        var ids = (projectIds ?? Array.Empty<Guid>()).Where(id => id != Guid.Empty).Distinct().ToArray();
        if (ids.Length == 0) return Array.Empty<DocumentContentFingerprint>();

        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<DocumentFingerprintRow>(new CommandDefinition(
            """
            SELECT id,project_id,folder_id,drawing_number,name,file_name,kind,lifecycle_state,revision_label,
                   checked_out_by,checked_out_at,checkout_session_id,checkout_machine,checkout_last_heartbeat_at,
                   checkout_lease_expires_at,checkout_release_requested_by,checkout_release_requested_at,
                   checkout_release_request_reason,updated_at,source_fingerprint_sha256
            FROM document
            WHERE project_id IN @ProjectIds
            """,
            new { ProjectIds = ids },
            cancellationToken: cancellationToken));
        return rows.Select(row => new DocumentContentFingerprint(MapDocument(row), row.SourceFingerprintSha256 ?? string.Empty)).ToArray();
    }

    public async Task<IReadOnlyList<DocumentModelDrawingRelation>> ListDocumentRelationsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<DocumentModelDrawingRelation>(new CommandDefinition(
            """
            SELECT relation.model_document_id ModelDocumentId, relation.drawing_document_id DrawingDocumentId
            FROM document_model_drawing_relation relation
            INNER JOIN project requested ON requested.id=@ProjectId
            INNER JOIN project owner_project ON owner_project.id=relation.project_id
            WHERE owner_project.id=COALESCE(requested.parent_project_id,requested.id)
               OR owner_project.parent_project_id=COALESCE(requested.parent_project_id,requested.id)
            ORDER BY relation.model_document_id,relation.drawing_document_id
            """,
            new { ProjectId = projectId },
            cancellationToken: cancellationToken));
        return rows.ToArray();
    }

    public async Task<IReadOnlyList<DocumentWhereUsed>> ListWhereUsedAsync(Guid documentId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS(SELECT 1 FROM document WHERE id=@DocumentId)",
            new { DocumentId = documentId }, cancellationToken: cancellationToken)))
            throw new PdmNotFoundException("图档不存在。");

        var snapshotRows = await connection.QueryAsync<WhereUsedSnapshotRow>(new CommandDefinition(
            """
            SELECT project.id project_id,project.code project_code,project.name project_name,snapshot.root_json
            FROM project_reference_root current_root
            INNER JOIN reference_snapshot snapshot ON snapshot.id=current_root.reference_snapshot_id
            INNER JOIN project ON project.id=current_root.project_id
            ORDER BY project.code
            """,
            cancellationToken: cancellationToken));
        var documentRows = await connection.QueryAsync<DocumentRow>(new CommandDefinition(
            """
            SELECT id,project_id,folder_id,drawing_number,name,file_name,kind,lifecycle_state,revision_label,
                   checked_out_by,checked_out_at,checkout_session_id,checkout_machine,checkout_last_heartbeat_at,
                   checkout_lease_expires_at,checkout_release_requested_by,checkout_release_requested_at,
                   checkout_release_request_reason,updated_at
            FROM document
            """,
            cancellationToken: cancellationToken));
        var documentsById = documentRows.Select(MapDocument).ToDictionary(document => document.Id);
        var result = new List<DocumentWhereUsed>();
        foreach (var row in snapshotRows)
        {
            var root = JsonSerializer.Deserialize<DocumentReferenceNode>(row.RootJson, jsonOptions)
                ?? throw new InvalidDataException("引用树快照损坏。");
            CollectWhereUsed(root, documentId, row, documentsById, result);
        }

        return result
            .OrderBy(item => item.ProjectCode)
            .ThenBy(item => item.ParentDrawingNumber)
            .ThenBy(item => item.InstancePath)
            .ToArray();
    }

    private static void CollectWhereUsed(
        DocumentReferenceNode parent,
        Guid documentId,
        WhereUsedSnapshotRow project,
        IReadOnlyDictionary<Guid, PdmDocument> documentsById,
        ICollection<DocumentWhereUsed> result)
    {
        if (parent.DocumentId is Guid parentDocumentId && documentsById.TryGetValue(parentDocumentId, out var parentDocument))
        {
            foreach (var child in parent.Children.Where(child => child.DocumentId == documentId))
            {
                result.Add(new DocumentWhereUsed(
                    documentId,
                    parentDocumentId,
                    project.ProjectId,
                    project.ProjectCode,
                    project.ProjectName,
                    parentDocument.DrawingNumber,
                    parentDocument.Name,
                    parentDocument.FileName,
                    parentDocument.Kind,
                    parentDocument.State,
                    parentDocument.Revision,
                    child.InstancePath,
                    child.Configuration,
                    child.Quantity));
            }
        }

        foreach (var child in parent.Children) CollectWhereUsed(child, documentId, project, documentsById, result);
    }

    public async Task<PdmDocument?> FindDocumentAsync(Guid documentId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await FindDocumentAsync(connection, null, documentId, cancellationToken);
    }

    public async Task<PdmDocument> RegisterDocumentAsync(RegisterDocumentCommand command, string actor, CancellationToken cancellationToken)
    {
        await EnsureProjectFolderTreeAsync(command.ProjectId, cancellationToken);
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

        if (!string.IsNullOrWhiteSpace(command.SourceSha256))
        {
            var matches = (await connection.QueryAsync<RegistrationFingerprintRow>(new CommandDefinition(
                """
                SELECT id,file_name,source_fingerprint_sha256
                FROM document
                WHERE project_id=@ProjectId
                  AND (file_name=@FileName OR source_fingerprint_sha256=@SourceSha256)
                FOR UPDATE
                """,
                new { command.ProjectId, command.FileName, command.SourceSha256 },
                transaction,
                cancellationToken: cancellationToken))).ToArray();
            var sameName = matches.FirstOrDefault(item => string.Equals(item.FileName, command.FileName, StringComparison.OrdinalIgnoreCase));
            if (sameName is not null
                && !string.Equals(sameName.SourceFingerprintSha256, command.SourceSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new PdmConflictException($"项目中已存在同名但内容不同的图档{command.FileName}，不能覆盖或自动升版。");
            }

            var sameContent = matches.FirstOrDefault(item =>
                !string.Equals(item.FileName, command.FileName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(item.SourceFingerprintSha256, command.SourceSha256, StringComparison.OrdinalIgnoreCase));
            if (sameContent is not null && !command.AllowDuplicateContent)
            {
                throw new PdmConflictException($"项目中已有内容完全相同的图档{sameContent.FileName}。请选择引用已有图档，或确认独立登记并填写原因。");
            }
        }

        var folderId = await ResolveDocumentFolderAsync(connection, transaction, command.ProjectId, command.FolderId, cancellationToken);
        var documentId = Guid.NewGuid();
        var now = timeProvider.GetUtcNow();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO document(id,project_id,folder_id,drawing_number,name,file_name,source_fingerprint_sha256,kind,lifecycle_state,revision_label,checked_out_by,checked_out_at,row_version,created_at,updated_at)
            VALUES(@Id,@ProjectId,@FolderId,@DrawingNumber,@Name,@FileName,@SourceSha256,@Kind,'Work','W1',NULL,NULL,1,@Now,@Now)
            ON DUPLICATE KEY UPDATE folder_id=COALESCE(document.folder_id,VALUES(folder_id))
            """,
            new
            {
                Id = documentId,
                command.ProjectId,
                FolderId = folderId,
                command.DrawingNumber,
                command.Name,
                command.FileName,
                command.SourceSha256,
                Kind = command.Kind.ToString(),
                Now = now.UtcDateTime
            },
            transaction,
            cancellationToken: cancellationToken));

        var row = await connection.QuerySingleAsync<DocumentRow>(new CommandDefinition(
            """
            SELECT id,project_id,folder_id,drawing_number,name,file_name,kind,lifecycle_state,revision_label,
                   checked_out_by,checked_out_at,checkout_session_id,checkout_machine,checkout_last_heartbeat_at,
                   checkout_lease_expires_at,checkout_release_requested_by,checkout_release_requested_at,
                   checkout_release_request_reason,updated_at
            FROM document WHERE project_id=@ProjectId AND file_name=@FileName
            """,
            new { command.ProjectId, command.FileName },
            transaction,
            cancellationToken: cancellationToken));
        if (command.RelatedModelDocumentId is Guid relatedModelDocumentId)
        {
            var relatedModelExists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS(SELECT 1 FROM document WHERE id=@ModelDocumentId AND project_id=@ProjectId AND kind IN ('Assembly','Part'))",
                new { ModelDocumentId = relatedModelDocumentId, command.ProjectId },
                transaction,
                cancellationToken: cancellationToken));
            if (!relatedModelExists) throw new PdmRuleException("工程图只能关联同一项目中的装配体或零件。");
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO document_model_drawing_relation(drawing_document_id,model_document_id,project_id,created_at,updated_at)
                SELECT @DrawingDocumentId,model.id,@ProjectId,@Now,@Now
                FROM document model
                WHERE model.id=@ModelDocumentId AND model.project_id=@ProjectId AND model.kind IN ('Assembly','Part')
                ON DUPLICATE KEY UPDATE model_document_id=VALUES(model_document_id),project_id=VALUES(project_id),updated_at=VALUES(updated_at)
                """,
                new { DrawingDocumentId = row.Id, ModelDocumentId = relatedModelDocumentId, command.ProjectId, Now = now.UtcDateTime },
                transaction,
                cancellationToken: cancellationToken));
        }
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

    public Task<bool> HasDocumentReadAccessAsync(Guid documentId, string actor, UserRole role, CancellationToken cancellationToken) =>
        HasDocumentAccessAsync(documentId, actor, role, FolderAccess.View, cancellationToken);

    public async Task<bool> HasDocumentAccessAsync(Guid documentId, string actor, UserRole role, FolderAccess requiredAccess, CancellationToken cancellationToken)
    {
        var document = await FindDocumentAsync(documentId, cancellationToken);
        if (document is null || !await HasProjectContentReadAccessAsync(document.ProjectId, actor, role, cancellationToken)) return false;
        var folders = await ListProjectFoldersAsync(document.ProjectId, actor, role, cancellationToken);
        var folder = document.FolderId is null
            ? folders.FirstOrDefault(item => item.TargetProjectId == document.ProjectId && item.TemplateKey == "mechanical.project")
            : folders.FirstOrDefault(item => item.Id == document.FolderId.Value);
        return folder is not null && (folder.EffectiveAccess & requiredAccess) == requiredAccess;
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

        var recoveredRoot = await connection.QuerySingleOrDefaultAsync<ReferenceSnapshotRow>(new CommandDefinition(
            """
            SELECT candidate.id, candidate.project_id, candidate.root_document_id, candidate.captured_at,
                   candidate.captured_by, candidate.sha256, candidate.root_json
            FROM reference_snapshot candidate
            INNER JOIN document root_document
                ON root_document.id = candidate.root_document_id
               AND root_document.project_id = candidate.project_id
            WHERE candidate.project_id = @ProjectId
              AND root_document.kind = 'Assembly'
              AND NOT EXISTS (
                  SELECT 1
                  FROM reference_snapshot container
                  WHERE container.project_id = candidate.project_id
                    AND container.root_document_id <> candidate.root_document_id
                    AND JSON_SEARCH(
                        JSON_EXTRACT(container.root_json, '$.children'),
                        'one',
                        root_document.file_name
                    ) IS NOT NULL
              )
            ORDER BY candidate.captured_at DESC, candidate.id DESC
            LIMIT 1
            """,
            new { ProjectId = projectId },
            cancellationToken: cancellationToken));
        if (recoveredRoot is not null)
        {
            return recoveredRoot;
        }

        return await connection.QuerySingleOrDefaultAsync<ReferenceSnapshotRow>(new CommandDefinition(
            "SELECT id, project_id, root_document_id, captured_at, captured_by, sha256, root_json FROM reference_snapshot WHERE project_id=@ProjectId ORDER BY captured_at DESC, id DESC LIMIT 1",
            new { ProjectId = projectId },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<BomItem>> GetBomAsync(Guid projectId, BomKind kind, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<BomRow>(new CommandDefinition(
            """
            SELECT id, project_id, bom_kind, sequence_no, drawing_number, name, quantity, unit, material, specification, remark, brand, surface_treatment, weight, revision_label, is_complete,
                   source_document_id, source_configuration, item_source, is_manually_overridden, is_pending_removal,
                   is_pending_classification, is_manual_unmatched, is_manually_retained, is_manually_excluded,
                   reconciliation_status, reconciliation_note, reconciliation_updated_by, reconciliation_updated_at,
                   deleted_at, deleted_by, delete_reason,
                   property_writeback_status
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
                "INSERT INTO bom_item(id,project_id,bom_kind,sequence_no,drawing_number,name,quantity,unit,material,specification,remark,brand,surface_treatment,weight,revision_label,is_complete,source_document_id,source_configuration,item_source,is_manually_overridden,is_pending_removal,is_pending_classification,is_manual_unmatched,is_manually_retained,is_manually_excluded,reconciliation_status,reconciliation_note,reconciliation_updated_by,reconciliation_updated_at,deleted_at,deleted_by,delete_reason,property_writeback_status,row_version,updated_at) VALUES(@Id,@ProjectId,@Kind,@Sequence,@DrawingNumber,@Name,@Quantity,@Unit,@Material,@Specification,@Remark,@Brand,@SurfaceTreatment,@Weight,@Revision,@IsComplete,@SourceDocumentId,@SourceConfiguration,@Source,@IsManuallyOverridden,@IsPendingRemoval,@IsPendingClassification,@IsManualUnmatched,@IsManuallyRetained,@IsManuallyExcluded,@ReconciliationStatus,@ReconciliationNote,@ReconciliationUpdatedBy,@ReconciliationUpdatedAt,@DeletedAt,@DeletedBy,@DeleteReason,@PropertyWritebackStatus,1,@Now)",
                new { item.Id, ProjectId = projectId, Kind = kind.ToString(), item.Sequence, item.DrawingNumber, item.Name, item.Quantity, item.Unit, item.Material, item.Specification, item.Remark, item.Brand, item.SurfaceTreatment, item.Weight, Revision = item.Revision, item.IsComplete, item.SourceDocumentId, item.SourceConfiguration, item.Source, item.IsManuallyOverridden, item.IsPendingRemoval, item.IsPendingClassification, item.IsManualUnmatched, item.IsManuallyRetained, item.IsManuallyExcluded, item.ReconciliationStatus, item.ReconciliationNote, item.ReconciliationUpdatedBy, ReconciliationUpdatedAt = item.ReconciliationUpdatedAt?.UtcDateTime, DeletedAt = item.DeletedAt?.UtcDateTime, item.DeletedBy, item.DeleteReason, PropertyWritebackStatus = item.PropertyWritebackStatus?.ToString(), Now = now },
                transaction, cancellationToken: cancellationToken));
        }
        await transaction.CommitAsync(cancellationToken);
        return items.OrderBy(item => item.Sequence).ToArray();
    }

    public async Task ApplyBomBatchAsync(Guid projectId, IReadOnlyList<BomItem> standardItems, IReadOnlyList<BomItem> nonStandardItems, IReadOnlyList<BomItem> unclassifiedItems, IReadOnlyList<BomItem> electricalItems, IReadOnlyList<CadPropertyWriteback> writebacks, IReadOnlyList<AuditEntry> auditEntries, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var exists = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM project WHERE id=@ProjectId AND is_active=1 FOR UPDATE",
            new { ProjectId = projectId }, transaction, cancellationToken: cancellationToken));
        if (exists != 1) throw new PdmNotFoundException("项目不存在或已停用。");

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM bom_item WHERE project_id=@ProjectId AND bom_kind IN ('Standard','NonStandard','Unclassified','Electrical')",
            new { ProjectId = projectId }, transaction, cancellationToken: cancellationToken));
        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var item in standardItems.Concat(nonStandardItems).Concat(unclassifiedItems).Concat(electricalItems))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO bom_item(id,project_id,bom_kind,sequence_no,drawing_number,name,quantity,unit,material,specification,remark,brand,surface_treatment,weight,revision_label,is_complete,source_document_id,source_configuration,item_source,is_manually_overridden,is_pending_removal,is_pending_classification,is_manual_unmatched,is_manually_retained,is_manually_excluded,reconciliation_status,reconciliation_note,reconciliation_updated_by,reconciliation_updated_at,deleted_at,deleted_by,delete_reason,property_writeback_status,row_version,updated_at) VALUES(@Id,@ProjectId,@Kind,@Sequence,@DrawingNumber,@Name,@Quantity,@Unit,@Material,@Specification,@Remark,@Brand,@SurfaceTreatment,@Weight,@Revision,@IsComplete,@SourceDocumentId,@SourceConfiguration,@Source,@IsManuallyOverridden,@IsPendingRemoval,@IsPendingClassification,@IsManualUnmatched,@IsManuallyRetained,@IsManuallyExcluded,@ReconciliationStatus,@ReconciliationNote,@ReconciliationUpdatedBy,@ReconciliationUpdatedAt,@DeletedAt,@DeletedBy,@DeleteReason,@PropertyWritebackStatus,1,@Now)",
                new { item.Id, ProjectId = projectId, Kind = item.Kind.ToString(), item.Sequence, item.DrawingNumber, item.Name, item.Quantity, item.Unit, item.Material, item.Specification, item.Remark, item.Brand, item.SurfaceTreatment, item.Weight, Revision = item.Revision, item.IsComplete, item.SourceDocumentId, item.SourceConfiguration, item.Source, item.IsManuallyOverridden, item.IsPendingRemoval, item.IsPendingClassification, item.IsManualUnmatched, item.IsManuallyRetained, item.IsManuallyExcluded, item.ReconciliationStatus, item.ReconciliationNote, item.ReconciliationUpdatedBy, ReconciliationUpdatedAt = item.ReconciliationUpdatedAt?.UtcDateTime, DeletedAt = item.DeletedAt?.UtcDateTime, item.DeletedBy, item.DeleteReason, PropertyWritebackStatus = item.PropertyWritebackStatus?.ToString(), Now = now },
                transaction, cancellationToken: cancellationToken));
        }

        foreach (var request in writebacks)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE cad_property_writeback SET status='Superseded',completed_at=@Now WHERE bom_item_id=@BomItemId AND status IN ('Pending','InProgress')",
                new { request.BomItemId, Now = now }, transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO cad_property_writeback(id,project_id,bom_item_id,source_document_id,source_configuration,expected_version_id,expected_revision,property_payload,status,requested_by,requested_at) VALUES(@Id,@ProjectId,@BomItemId,@SourceDocumentId,@SourceConfiguration,@ExpectedVersionId,@ExpectedRevision,@PropertyPayload,@Status,@RequestedBy,@RequestedAt)",
                new { request.Id, request.ProjectId, request.BomItemId, request.SourceDocumentId, request.SourceConfiguration, request.ExpectedVersionId, request.ExpectedRevision, PropertyPayload = JsonSerializer.Serialize(request.Properties, jsonOptions), Status = request.Status.ToString(), request.RequestedBy, RequestedAt = request.RequestedAt.UtcDateTime },
                transaction, cancellationToken: cancellationToken));
        }

        foreach (var entry in auditEntries)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO audit_entry(id,occurred_at,actor,action_name,entity_type,entity_id,detail_json) VALUES(@Id,@OccurredAt,@Actor,@Action,@EntityType,@EntityId,@DetailJson)",
                new { entry.Id, OccurredAt = entry.OccurredAt.UtcDateTime, entry.Actor, entry.Action, entry.EntityType, entry.EntityId, DetailJson = JsonSerializer.Serialize(new { detail = entry.Detail }, jsonOptions) },
                transaction, cancellationToken: cancellationToken));
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<BomItem?> FindBomItemAsync(Guid projectId, Guid itemId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<BomRow>(new CommandDefinition(
            "SELECT id,project_id,bom_kind,sequence_no,drawing_number,name,quantity,unit,material,specification,remark,brand,surface_treatment,weight,revision_label,is_complete,source_document_id,source_configuration,item_source,is_manually_overridden,is_pending_removal,is_pending_classification,is_manual_unmatched,is_manually_retained,is_manually_excluded,reconciliation_status,reconciliation_note,reconciliation_updated_by,reconciliation_updated_at,deleted_at,deleted_by,delete_reason,property_writeback_status FROM bom_item WHERE project_id=@ProjectId AND id=@ItemId",
            new { ProjectId = projectId, ItemId = itemId }, cancellationToken: cancellationToken));
        return row is null ? null : MapBomItem(row);
    }

    public async Task<CadPropertyWriteback> EnqueueCadPropertyWritebackAsync(CadPropertyWriteback request, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE cad_property_writeback SET status='Superseded',completed_at=@Now WHERE bom_item_id=@BomItemId AND status IN ('Pending','InProgress')",
            new { request.BomItemId, Now = now }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO cad_property_writeback(id,project_id,bom_item_id,source_document_id,source_configuration,expected_version_id,expected_revision,property_payload,status,requested_by,requested_at) VALUES(@Id,@ProjectId,@BomItemId,@SourceDocumentId,@SourceConfiguration,@ExpectedVersionId,@ExpectedRevision,@PropertyPayload,@Status,@RequestedBy,@RequestedAt)",
            new { request.Id, request.ProjectId, request.BomItemId, request.SourceDocumentId, request.SourceConfiguration, request.ExpectedVersionId, request.ExpectedRevision, PropertyPayload = JsonSerializer.Serialize(request.Properties, jsonOptions), Status = request.Status.ToString(), request.RequestedBy, RequestedAt = request.RequestedAt.UtcDateTime }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE bom_item SET property_writeback_status='Pending',updated_at=@Now WHERE id=@BomItemId",
            new { request.BomItemId, Now = now }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return request;
    }

    public async Task<IReadOnlyList<CadPropertyWriteback>> ListCadPropertyWritebacksAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<CadPropertyWritebackRow>(new CommandDefinition(
            "SELECT id,project_id,bom_item_id,source_document_id,source_configuration,expected_version_id,expected_revision,property_payload,status,requested_by,requested_at,started_at,completed_at,result_version_id,last_error FROM cad_property_writeback WHERE project_id=@ProjectId ORDER BY requested_at DESC",
            new { ProjectId = projectId }, cancellationToken: cancellationToken));
        return rows.Select(MapCadPropertyWriteback).ToArray();
    }

    public async Task<CadPropertyWriteback?> FindCadPropertyWritebackAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<CadPropertyWritebackRow>(new CommandDefinition(
            "SELECT id,project_id,bom_item_id,source_document_id,source_configuration,expected_version_id,expected_revision,property_payload,status,requested_by,requested_at,started_at,completed_at,result_version_id,last_error FROM cad_property_writeback WHERE id=@Id",
            new { Id = id }, cancellationToken: cancellationToken));
        return row is null ? null : MapCadPropertyWriteback(row);
    }

    public async Task<CadPropertyWriteback> UpdateCadPropertyWritebackAsync(Guid id, CadPropertyWritebackStatus status, Guid? resultVersionId, string? error, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE cad_property_writeback SET status=@Status,started_at=CASE WHEN @Status='InProgress' THEN COALESCE(started_at,@Now) ELSE started_at END,completed_at=CASE WHEN @Status IN ('Succeeded','Conflict','Failed','Superseded') THEN @Now ELSE NULL END,result_version_id=@ResultVersionId,last_error=@Error WHERE id=@Id",
            new { Id = id, Status = status.ToString(), ResultVersionId = resultVersionId, Error = error, Now = now }, transaction, cancellationToken: cancellationToken));
        if (affected != 1) throw new PdmNotFoundException("属性写回任务不存在。");
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE bom_item b INNER JOIN cad_property_writeback w ON w.bom_item_id=b.id SET b.property_writeback_status=@Status,b.updated_at=@Now WHERE w.id=@Id",
            new { Id = id, Status = status.ToString(), Now = now }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return await FindCadPropertyWritebackAsync(id, cancellationToken) ?? throw new PdmNotFoundException("属性写回任务不存在。");
    }

    public async Task<IReadOnlyList<BomEmptyDeclaration>> GetBomEmptyDeclarationsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<BomEmptyDeclarationRow>(new CommandDefinition(
            "SELECT bom_kind,declared_empty,updated_by,updated_at FROM project_bom_empty_declaration WHERE project_id=@ProjectId",
            new { ProjectId = projectId }, cancellationToken: cancellationToken));
        return rows.Select(row => new BomEmptyDeclaration(Enum.Parse<BomKind>(row.BomKind), row.DeclaredEmpty, row.UpdatedBy, AsNullableUtc(row.UpdatedAt))).ToArray();
    }

    public async Task<BomEmptyDeclaration> SetBomEmptyDeclarationAsync(Guid projectId, BomKind kind, bool declaredEmpty, string actor, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO project_bom_empty_declaration(project_id,bom_kind,declared_empty,updated_by,updated_at)
            VALUES(@ProjectId,@Kind,@DeclaredEmpty,@Actor,@Now)
            ON DUPLICATE KEY UPDATE declared_empty=VALUES(declared_empty),updated_by=VALUES(updated_by),updated_at=VALUES(updated_at)
            """,
            new { ProjectId = projectId, Kind = kind.ToString(), DeclaredEmpty = declaredEmpty, Actor = actor, Now = now.UtcDateTime }, cancellationToken: cancellationToken));
        return new BomEmptyDeclaration(kind, declaredEmpty, actor, now);
    }

    public async Task<IReadOnlyList<ReleasePackage>> ListReleasePackagesAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<ReleasePackageRow>(new CommandDefinition(
            """
            SELECT id, project_id, package_number, state, reference_snapshot_id, mechanical_bom_revision, electrical_bom_revision, mechanical_bom_snapshot_json, electrical_bom_snapshot_json,
                   standard_bom_version_id, non_standard_bom_version_id, electrical_bom_version_id, standard_bom_revision, non_standard_bom_revision,
                   standard_bom_snapshot_json, non_standard_bom_snapshot_json, change_number, change_reason, effective_serial_from, effective_serial_to,
                   published_at, published_path, publish_error, created_at
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
            "SELECT id, username, display_name, password_hash, role, assigned_role_code RoleCode, is_active, token_version FROM pdm_user WHERE username = @Username LIMIT 1",
            new { Username = username },
            cancellationToken: cancellationToken));
        return row is null ? null : new UserAccount(row.Id, row.Username, row.DisplayName, row.PasswordHash, Enum.Parse<UserRole>(row.Role), row.IsActive, row.TokenVersion, row.RoleCode);
    }

    public async Task<UserProfile?> FindUserProfileAsync(string username, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<UserProfileRow>(new CommandDefinition(
            "SELECT username,display_name DisplayName,nickname,gender,landline,mobile_phone MobilePhone,email FROM pdm_user WHERE username=@Username LIMIT 1",
            new { Username = username }, cancellationToken: cancellationToken));
        return row is null ? null : MapUserProfile(row);
    }

    public async Task<UserProfile> UpdateUserProfileAsync(string username, string? nickname, string gender, string? landline, string? mobilePhone, string? email, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE pdm_user SET nickname=@Nickname,gender=@Gender,landline=@Landline,mobile_phone=@MobilePhone,email=@Email,row_version=row_version+1 WHERE username=@Username",
            new { Username = username, Nickname = nickname, Gender = gender, Landline = landline, MobilePhone = mobilePhone, Email = email }, cancellationToken: cancellationToken));
        if (affected != 1) throw new PdmNotFoundException("用户不存在。");
        return await FindUserProfileAsync(username, cancellationToken) ?? throw new PdmNotFoundException("用户不存在。");
    }

    public async Task<UserAccount> UpdateUserPasswordAsync(string username, string passwordHash, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE pdm_user SET password_hash=@PasswordHash,token_version=token_version+1,row_version=row_version+1 WHERE username=@Username",
            new { Username = username, PasswordHash = passwordHash }, cancellationToken: cancellationToken));
        if (affected != 1) throw new PdmNotFoundException("用户不存在。");
        return await FindUserAsync(username, cancellationToken) ?? throw new PdmNotFoundException("用户不存在。");
    }

    public async Task CreatePasswordResetRequestAsync(UserAccount user, DateTimeOffset requestedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO password_reset_request(id,user_id,requester_username,requester_display_name,active_username,requested_at)
            VALUES(@Id,@UserId,@Username,@DisplayName,@Username,@RequestedAt)
            ON DUPLICATE KEY UPDATE requester_display_name=VALUES(requester_display_name),requested_at=VALUES(requested_at)
            """,
            new { Id = Guid.NewGuid(), UserId = user.Id, user.Username, user.DisplayName, RequestedAt = requestedAt.UtcDateTime }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<PasswordResetTask>> ListPasswordResetTasksAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<PasswordResetTaskRow>(new CommandDefinition(
            "SELECT id,requester_username Username,requester_display_name DisplayName,requested_at RequestedAt FROM password_reset_request WHERE completed_at IS NULL ORDER BY requested_at",
            cancellationToken: cancellationToken));
        return rows.Select(row => new PasswordResetTask(row.Id, row.Username, row.DisplayName, new DateTimeOffset(DateTime.SpecifyKind(row.RequestedAt, DateTimeKind.Utc)))).ToArray();
    }

    public async Task CompletePasswordResetTaskAsync(Guid taskId, string passwordHash, string actor, DateTimeOffset completedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var username = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT requester_username FROM password_reset_request WHERE id=@TaskId AND completed_at IS NULL FOR UPDATE",
            new { TaskId = taskId }, transaction, cancellationToken: cancellationToken));
        if (string.IsNullOrWhiteSpace(username)) throw new PdmNotFoundException("密码重置申请不存在或已处理。");
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE pdm_user SET password_hash=@PasswordHash,token_version=token_version+1,row_version=row_version+1 WHERE username=@Username",
            new { Username = username, PasswordHash = passwordHash }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE password_reset_request SET active_username=NULL,completed_at=@CompletedAt,completed_by=@Actor WHERE id=@TaskId",
            new { TaskId = taskId, CompletedAt = completedAt.UtcDateTime, Actor = actor }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<int> CountUsersAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT COUNT(*) FROM pdm_user", cancellationToken: cancellationToken));
    }

    private static UserProfile MapUserProfile(UserProfileRow row) => new(row.Username, row.DisplayName, row.Nickname, row.Gender, row.Landline, row.MobilePhone, row.Email);

    private sealed class UserProfileRow
    {
        public string Username { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string? Nickname { get; init; }
        public string Gender { get; init; } = "unspecified";
        public string? Landline { get; init; }
        public string? MobilePhone { get; init; }
        public string? Email { get; init; }
    }

    private sealed class PasswordResetTaskRow
    {
        public Guid Id { get; init; }
        public string Username { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public DateTime RequestedAt { get; init; }
    }

    public async Task<IReadOnlyList<AuditEntry>> ListAuditAsync(string actor, UserRole role, int take, CancellationToken cancellationToken)
    {
        var canViewAll = await HasUserPermissionAsync(actor, role, PermissionCodes.AuditView, cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        var limit = Math.Clamp(take, 1, 500);
        var rows = await connection.QueryAsync<AuditRow>(new CommandDefinition(
            canViewAll
                ? "SELECT id,occurred_at,actor,action_name,entity_type,entity_id,detail_json FROM audit_entry ORDER BY occurred_at DESC LIMIT @Limit"
                : "SELECT id,occurred_at,actor,action_name,entity_type,entity_id,detail_json FROM audit_entry WHERE actor=@Actor ORDER BY occurred_at DESC LIMIT @Limit",
            new { Actor = actor, Limit = limit }, cancellationToken: cancellationToken));
        return rows.Select(row => new AuditEntry(row.Id, DateTime.SpecifyKind(row.OccurredAt, DateTimeKind.Utc), row.Actor, row.ActionName, row.EntityType, row.EntityId, ReadAuditDetail(row.DetailJson))).ToArray();
    }

    public async Task<IReadOnlyList<AuditEntry>> ListProjectAuditAsync(Guid projectId, int take, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var limit = Math.Clamp(take, 1, 500);
        var projectIdText = projectId.ToString();
        var rows = await connection.QueryAsync<AuditRow>(new CommandDefinition(
            """
            SELECT audit.id,audit.occurred_at,audit.actor,audit.action_name,audit.entity_type,audit.entity_id,audit.detail_json
            FROM audit_entry audit
            WHERE (audit.entity_type='Project' AND audit.entity_id=@ProjectIdText)
               OR (audit.entity_type='BomItem' AND audit.entity_id=@ProjectIdText)
               OR (audit.entity_type='PdmDocument' AND audit.entity_id IN (
                    SELECT BIN_TO_UUID(document.id) FROM document WHERE document.project_id=@ProjectId))
               OR (audit.entity_type='DocumentVersion' AND (
                    audit.entity_id IN (SELECT BIN_TO_UUID(document.id) FROM document WHERE document.project_id=@ProjectId)
                    OR audit.entity_id IN (
                        SELECT BIN_TO_UUID(version.id) FROM document_version version
                        JOIN document ON document.id=version.document_id
                        WHERE document.project_id=@ProjectId)))
               OR (audit.entity_type='ReleasePackage' AND audit.entity_id IN (
                    SELECT BIN_TO_UUID(package.id) FROM release_package package WHERE package.project_id=@ProjectId))
               OR (audit.entity_type='ApprovalTask' AND audit.entity_id IN (
                    SELECT BIN_TO_UUID(task.id) FROM approval_task task
                    JOIN release_package package ON package.id=task.release_package_id
                    WHERE package.project_id=@ProjectId))
            ORDER BY audit.occurred_at DESC
            LIMIT @Limit
            """,
            new { ProjectId = projectId, ProjectIdText = projectIdText, Limit = limit }, cancellationToken: cancellationToken));
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
                   p.signed_date,p.quantity,p.parent_project_id,p.child_sequence,p.owner,p.vault_location,p.release_location,p.is_active,
                   COALESCE(p.execution_unit_id,parent.execution_unit_id) execution_unit_id,execution_unit.name execution_unit_name
            FROM project p LEFT JOIN project_organization o ON o.id=p.organization_id
            LEFT JOIN project parent ON parent.id=p.parent_project_id
            LEFT JOIN organization_unit execution_unit ON execution_unit.id=COALESCE(p.execution_unit_id,parent.execution_unit_id)
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
        var assignments = (await connection.QueryAsync<ProjectAssignmentRow>(new CommandDefinition(
            "SELECT project_id,username,assignment_type FROM project_assignment WHERE project_id IN @ProjectIds ORDER BY username",
            new { ProjectIds = row.ParentProjectId is null ? new[] { row.Id } : new[] { row.ParentProjectId.Value, row.Id } }, transaction, cancellationToken: cancellationToken))).ToArray();
        return MapProject(row, serials.ToArray(), responsibles, assignments);
    }

    private static async Task<PdmDocument?> FindDocumentAsync(DbConnection connection, DbTransaction? transaction, Guid documentId, CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<DocumentRow>(new CommandDefinition(
            """
            SELECT id,project_id,folder_id,drawing_number,name,file_name,kind,lifecycle_state,revision_label,
                   checked_out_by,checked_out_at,checkout_session_id,checkout_machine,checkout_last_heartbeat_at,
                   checkout_lease_expires_at,checkout_release_requested_by,checkout_release_requested_at,
                   checkout_release_request_reason,updated_at
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
            SELECT id, project_id, package_number, state, reference_snapshot_id, mechanical_bom_revision, electrical_bom_revision, mechanical_bom_snapshot_json, electrical_bom_snapshot_json,
                   standard_bom_version_id, non_standard_bom_version_id, electrical_bom_version_id, standard_bom_revision, non_standard_bom_revision,
                   standard_bom_snapshot_json, non_standard_bom_snapshot_json, change_number, change_reason, effective_serial_from, effective_serial_to,
                   published_at, published_path, publish_error, created_at
            FROM release_package WHERE id = @PackageId
            """,
            new { PackageId = packageId },
            transaction,
            cancellationToken: cancellationToken));
        return row is null ? null : await MapReleasePackageAsync(connection, transaction, row, cancellationToken);
    }

    private static Project MapProject(ProjectRow row, IReadOnlyList<string>? serialNumbers = null, IReadOnlyList<string>? responsibleUsers = null,
        IReadOnlyList<ProjectAssignmentRow>? assignments = null, ProjectActivityRow? activity = null) =>
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
            ResponsibleUsers = responsibleUsers ?? (string.IsNullOrWhiteSpace(row.Owner) ? [] : [row.Owner]),
            ExecutionUnitId = row.ExecutionUnitId,
            ExecutionUnitName = row.ExecutionUnitName,
            PrimaryProjectManager = FindSingleAssignment(assignments, row.ParentProjectId ?? row.Id, ProjectAssignmentType.PrimaryProjectManager),
            CollaborativeProjectManagers = FindAssignments(assignments, row.ParentProjectId ?? row.Id, ProjectAssignmentType.CollaborativeProjectManager),
            DesignLead = FindSingleAssignment(assignments, row.ParentProjectId ?? row.Id, ProjectAssignmentType.DesignLead),
            Designers = FindAssignments(assignments, row.Id, ProjectAssignmentType.Designer),
            DocumentCount = activity?.DocumentCount,
            BusinessStatus = activity is null ? null : BuildBusinessStatus(activity),
            RootDocumentCheckedOutBy = activity?.RootDocumentCheckedOutBy
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
        var rootIds = rowArray.Select(row => row.ParentProjectId ?? row.Id).Concat(rowArray.Select(row => row.Id)).Distinct().ToArray();
        var assignments = (await connection.QueryAsync<ProjectAssignmentRow>(new CommandDefinition(
            "SELECT project_id,username,assignment_type FROM project_assignment WHERE project_id IN @ProjectIds ORDER BY username",
            new { ProjectIds = rootIds }, transaction, cancellationToken: cancellationToken))).ToArray();
        var activityRows = (await connection.QueryAsync<ProjectActivityRow>(new CommandDefinition(
            """
            SELECT p.id project_id,
                   COUNT(DISTINCT d.id) document_count,
                   MAX(current_root_document.checked_out_by) root_document_checked_out_by,
                   MAX(CASE WHEN package.state='Draft' THEN 1 ELSE 0 END) has_draft,
                   MAX(CASE WHEN package.state IN ('ProcessReview','Approval') THEN 1 ELSE 0 END) has_pending_approval,
                   MAX(CASE WHEN package.state='Rejected' THEN 1 ELSE 0 END) has_rejected_approval,
                   MAX(CASE WHEN package.state='Publishing' THEN 1 ELSE 0 END) is_publishing,
                   MAX(CASE WHEN package.state='PublishFailed' THEN 1 ELSE 0 END) has_publish_failure
              FROM project p
              LEFT JOIN document d ON d.project_id=p.id
              LEFT JOIN project_reference_root current_root ON current_root.project_id=p.id
              LEFT JOIN reference_snapshot current_snapshot ON current_snapshot.id=current_root.reference_snapshot_id AND current_snapshot.project_id=p.id
              LEFT JOIN document current_root_document ON current_root_document.id=current_snapshot.root_document_id AND current_root_document.project_id=p.id
              LEFT JOIN release_package package ON package.project_id=p.id
             WHERE p.id IN @ProjectIds
             GROUP BY p.id
            """,
            new { ProjectIds = rowArray.Select(row => row.Id).ToArray() }, transaction, cancellationToken: cancellationToken))).ToArray();
        var activity = activityRows.ToDictionary(item => item.ProjectId);
        return rowArray.Select(row => MapProject(row, serials.GetValueOrDefault(row.Id, []), responsibles.GetValueOrDefault(row.Id),
            assignments, activity.GetValueOrDefault(row.Id))).ToArray();
    }

    private static string BuildBusinessStatus(ProjectActivityRow activity)
    {
        var statuses = new List<string>();
        if (!string.IsNullOrWhiteSpace(activity.RootDocumentCheckedOutBy)) statuses.Add("编辑中");
        if (activity.HasDraft) statuses.Add("待提交");
        if (activity.HasPendingApproval) statuses.Add("待审批");
        if (activity.HasRejectedApproval) statuses.Add("审批退回");
        if (activity.IsPublishing) statuses.Add("发布中");
        if (activity.HasPublishFailure) statuses.Add("发布失败");
        return statuses.Count == 0 ? "正常" : string.Join("、", statuses);
    }

    private static string? FindSingleAssignment(IReadOnlyList<ProjectAssignmentRow>? assignments, Guid projectId, ProjectAssignmentType type) =>
        assignments?.FirstOrDefault(item => item.ProjectId == projectId && item.AssignmentType == type.ToString())?.Username;

    private static IReadOnlyList<string> FindAssignments(IReadOnlyList<ProjectAssignmentRow>? assignments, Guid projectId, ProjectAssignmentType type) =>
        assignments?.Where(item => item.ProjectId == projectId && item.AssignmentType == type.ToString()).Select(item => item.Username).ToArray() ?? [];

    private static Project ApplyAdministratorCapabilities(Project project) => project with
    {
        CanAssignExecutionUnit = project.ParentProjectId is null,
        CanManageMainStaffing = project.ParentProjectId is null && project.ExecutionUnitId is not null,
        CanAssignDesigners = project.ParentProjectId is not null,
        CanReadContent = true
    };

    private static async Task<IReadOnlyList<Project>> ApplyCapabilitiesAsync(DbConnection connection, IReadOnlyList<Project> projects, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var permissions = role == UserRole.Administrator
            ? RolePermissionCatalog.Defaults[role]
            : (await connection.QueryAsync<string>(new CommandDefinition(
                "SELECT permission_code FROM role_permission WHERE role_code=@RoleCode",
                new { RoleCode = role.ToString() }, cancellationToken: cancellationToken))).ToHashSet(StringComparer.Ordinal);
        var managedUnitIds = (await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT unit_id FROM organization_unit_manager WHERE username=@Actor", new { Actor = actor }, cancellationToken: cancellationToken))).ToHashSet();
        var organizationIds = (await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT DISTINCT unit.organization_id FROM organization_membership membership INNER JOIN organization_unit unit ON unit.id=membership.unit_id WHERE membership.username=@Actor",
            new { Actor = actor }, cancellationToken: cancellationToken))).ToHashSet();
        var approvalProjectIds = (await connection.QueryAsync<Guid>(new CommandDefinition(
            "SELECT DISTINCT package.project_id FROM release_package package INNER JOIN approval_task task ON task.release_package_id=package.id WHERE task.assignee=@Actor",
            new { Actor = actor }, cancellationToken: cancellationToken))).ToHashSet();
        return projects.Select(project =>
        {
            var canReadContent = permissions.Contains(PermissionCodes.ProjectContentView)
                && (string.Equals(project.DesignLead, actor, StringComparison.OrdinalIgnoreCase)
                    || project.Designers.Contains(actor, StringComparer.OrdinalIgnoreCase)
                    || approvalProjectIds.Contains(project.Id));
            return project with
            {
                CanAssignExecutionUnit = permissions.Contains(PermissionCodes.ProjectExecutionAssign) && project.ParentProjectId is null && project.OrganizationId is not null && organizationIds.Contains(project.OrganizationId.Value),
                CanManageMainStaffing = permissions.Contains(PermissionCodes.ProjectStaffingManage) && project.ParentProjectId is null && project.ExecutionUnitId is not null && managedUnitIds.Contains(project.ExecutionUnitId.Value),
                CanAssignDesigners = permissions.Contains(PermissionCodes.ProjectDesignerAssign) && project.ParentProjectId is not null && string.Equals(project.DesignLead, actor, StringComparison.OrdinalIgnoreCase),
                CanReadContent = canReadContent,
                DocumentCount = canReadContent ? project.DocumentCount : null,
                BusinessStatus = canReadContent ? project.BusinessStatus : null
            };
        }).ToArray();
    }

    private static Project BuildNumberedProject(
        Guid id, string code, string name, string? projectAlias, Guid organizationId, string organizationName,
        string projectTypeCode, int equipmentTypeCode, string customerCode, string customerName,
        int customerProjectSequence, string deviceModel, DateOnly signedDate, int quantity,
        Guid? parentProjectId, int? childSequence, string owner, string vaultLocation, string releaseLocation,
        string projectCompanyCode, IReadOnlyList<int> serialSequences) =>
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
            SerialNumbers = serialSequences.Select(value => $"{projectCompanyCode}{value:D7}").ToArray()
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
        var released = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT sequence_value FROM released_project_number WHERE organization_id=@OrganizationId ORDER BY sequence_value LIMIT 1 FOR UPDATE",
            new { OrganizationId = organizationId }, transaction, cancellationToken: cancellationToken));
        if (released is not null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM released_project_number WHERE organization_id=@OrganizationId AND sequence_value=@Sequence",
                new { OrganizationId = organizationId, Sequence = released.Value }, transaction, cancellationToken: cancellationToken));
            return released.Value;
        }
        await connection.ExecuteAsync(new CommandDefinition("INSERT IGNORE INTO project_number_counter(organization_id,current_value) VALUES(@OrganizationId,0)", new { OrganizationId = organizationId }, transaction, cancellationToken: cancellationToken));
        var last = await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT current_value FROM project_number_counter WHERE organization_id=@OrganizationId FOR UPDATE", new { OrganizationId = organizationId }, transaction, cancellationToken: cancellationToken));
        if (last >= 99999) throw new PdmRuleException("该组织的5位项目流水号已用尽。");
        var next = last + 1;
        await connection.ExecuteAsync(new CommandDefinition("UPDATE project_number_counter SET current_value=@Next WHERE organization_id=@OrganizationId", new { OrganizationId = organizationId, Next = next }, transaction, cancellationToken: cancellationToken));
        return next;
    }

    private static async Task<int> ReserveCustomerProjectNumberAsync(MySqlConnection connection, DbTransaction transaction, Guid organizationId, string customerCode, CancellationToken cancellationToken)
    {
        var released = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT sequence_value FROM released_customer_project_number WHERE organization_id=@OrganizationId AND customer_code=@CustomerCode ORDER BY sequence_value LIMIT 1 FOR UPDATE",
            new { OrganizationId = organizationId, CustomerCode = customerCode }, transaction, cancellationToken: cancellationToken));
        if (released is not null)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM released_customer_project_number WHERE organization_id=@OrganizationId AND customer_code=@CustomerCode AND sequence_value=@Sequence",
                new { OrganizationId = organizationId, CustomerCode = customerCode, Sequence = released.Value }, transaction, cancellationToken: cancellationToken));
            return released.Value;
        }
        await connection.ExecuteAsync(new CommandDefinition("INSERT IGNORE INTO customer_project_counter(organization_id,customer_code,current_value) VALUES(@OrganizationId,@CustomerCode,0)", new { OrganizationId = organizationId, CustomerCode = customerCode }, transaction, cancellationToken: cancellationToken));
        var last = await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT current_value FROM customer_project_counter WHERE organization_id=@OrganizationId AND customer_code=@CustomerCode FOR UPDATE", new { OrganizationId = organizationId, CustomerCode = customerCode }, transaction, cancellationToken: cancellationToken));
        if (last >= 999) throw new PdmRuleException("该客户的3位项目流水号已用尽。");
        var next = last + 1;
        await connection.ExecuteAsync(new CommandDefinition("UPDATE customer_project_counter SET current_value=@Next WHERE organization_id=@OrganizationId AND customer_code=@CustomerCode", new { OrganizationId = organizationId, CustomerCode = customerCode, Next = next }, transaction, cancellationToken: cancellationToken));
        return next;
    }

    private static async Task<IReadOnlyList<int>> ReserveSerialNumbersAsync(MySqlConnection connection, DbTransaction transaction, Guid organizationId, int quantity, CancellationToken cancellationToken)
    {
        var serials = (await connection.QueryAsync<int>(new CommandDefinition(
            "SELECT sequence_value FROM released_serial_number WHERE organization_id=@OrganizationId ORDER BY sequence_value LIMIT @Quantity FOR UPDATE",
            new { OrganizationId = organizationId, Quantity = quantity }, transaction, cancellationToken: cancellationToken))).ToList();
        if (serials.Count > 0)
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM released_serial_number WHERE organization_id=@OrganizationId AND sequence_value IN @Sequences",
                new { OrganizationId = organizationId, Sequences = serials }, transaction, cancellationToken: cancellationToken));
        var remaining = quantity - serials.Count;
        if (remaining == 0) return serials;
        await connection.ExecuteAsync(new CommandDefinition("INSERT IGNORE INTO serial_number_counter(organization_id,current_value) VALUES(@OrganizationId,0)", new { OrganizationId = organizationId }, transaction, cancellationToken: cancellationToken));
        var last = await connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT current_value FROM serial_number_counter WHERE organization_id=@OrganizationId FOR UPDATE", new { OrganizationId = organizationId }, transaction, cancellationToken: cancellationToken));
        if (last > 9999999 - remaining) throw new PdmRuleException("该组织的7位序列流水号余额不足。");
        var next = last + remaining;
        await connection.ExecuteAsync(new CommandDefinition("UPDATE serial_number_counter SET current_value=@Next WHERE organization_id=@OrganizationId", new { OrganizationId = organizationId, Next = next }, transaction, cancellationToken: cancellationToken));
        serials.AddRange(Enumerable.Range(last + 1, remaining));
        return serials;
    }

    private static bool TryParseProjectSequence(string code, string? projectTypeCode, string projectCompanyCode, out int sequence)
    {
        sequence = 0;
        var prefix = $"{projectTypeCode}{projectCompanyCode}";
        var number = code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? code[prefix.Length..] : string.Empty;
        return number.Length == 5 && int.TryParse(number, out sequence);
    }

    private static bool TryParseSerialSequence(string serialNumber, string projectCompanyCode, out int sequence)
    {
        sequence = 0;
        var number = serialNumber.StartsWith(projectCompanyCode, StringComparison.OrdinalIgnoreCase)
            ? serialNumber[projectCompanyCode.Length..]
            : string.Empty;
        return number.Length == 7 && int.TryParse(number, out sequence);
    }

    private static PdmDocument MapDocument(DocumentRow row) =>
        new(row.Id, row.ProjectId, row.DrawingNumber, row.Name, row.FileName, Enum.Parse<DocumentKind>(row.Kind), Enum.Parse<DocumentLifecycleState>(row.LifecycleState), RevisionLabel.Parse(row.RevisionLabel), row.CheckedOutBy, AsUtc(row.UpdatedAt))
        {
            FolderId = row.FolderId,
            StoredVersionCount = row.StoredVersionCount,
            CheckedOutAt = AsNullableUtc(row.CheckedOutAt),
            CheckoutSessionId = row.CheckoutSessionId,
            CheckoutMachine = row.CheckoutMachine,
            CheckoutLastHeartbeatAt = AsNullableUtc(row.CheckoutLastHeartbeatAt),
            CheckoutLeaseExpiresAt = AsNullableUtc(row.CheckoutLeaseExpiresAt),
            CheckoutReleaseRequestedBy = row.CheckoutReleaseRequestedBy,
            CheckoutReleaseRequestedAt = AsNullableUtc(row.CheckoutReleaseRequestedAt),
            CheckoutReleaseRequestReason = row.CheckoutReleaseRequestReason
        };

    private static DateTimeOffset AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTimeOffset? AsNullableUtc(DateTime? value) => value is null ? null : AsUtc(value.Value);

    private static BomItem MapBomItem(BomRow row) =>
        new(row.Id, row.ProjectId, Enum.Parse<BomKind>(row.BomKind), row.SequenceNo, row.DrawingNumber, row.Name, row.Quantity, row.Unit, row.Material, row.Specification, row.RevisionLabel, row.IsComplete)
        {
            Remark = row.Remark,
            Brand = row.Brand,
            SurfaceTreatment = row.SurfaceTreatment,
            Weight = row.Weight,
            SourceDocumentId = row.SourceDocumentId,
            SourceConfiguration = row.SourceConfiguration,
            Source = row.ItemSource,
            IsManuallyOverridden = row.IsManuallyOverridden,
            IsPendingRemoval = row.IsPendingRemoval,
            IsPendingClassification = row.IsPendingClassification,
            IsManualUnmatched = row.IsManualUnmatched,
            IsManuallyRetained = row.IsManuallyRetained,
            IsManuallyExcluded = row.IsManuallyExcluded,
            ReconciliationStatus = row.ReconciliationStatus,
            ReconciliationNote = row.ReconciliationNote,
            ReconciliationUpdatedBy = row.ReconciliationUpdatedBy,
            ReconciliationUpdatedAt = AsNullableUtc(row.ReconciliationUpdatedAt),
            DeletedAt = AsNullableUtc(row.DeletedAt),
            DeletedBy = row.DeletedBy,
            DeleteReason = row.DeleteReason,
            PropertyWritebackStatus = string.IsNullOrWhiteSpace(row.PropertyWritebackStatus) ? null : Enum.Parse<CadPropertyWritebackStatus>(row.PropertyWritebackStatus)
        };

    private CadPropertyWriteback MapCadPropertyWriteback(CadPropertyWritebackRow row) =>
        new(row.Id, row.ProjectId, row.BomItemId, row.SourceDocumentId, row.SourceConfiguration, row.ExpectedVersionId, row.ExpectedRevision,
            JsonSerializer.Deserialize<Dictionary<string, string?>>(row.PropertyPayload, jsonOptions) ?? new Dictionary<string, string?>(),
            Enum.Parse<CadPropertyWritebackStatus>(row.Status), row.RequestedBy, AsUtc(row.RequestedAt))
        {
            StartedAt = AsNullableUtc(row.StartedAt),
            CompletedAt = AsNullableUtc(row.CompletedAt),
            ResultVersionId = row.ResultVersionId,
            LastError = row.LastError
        };

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
            StandardBomVersionId = row.StandardBomVersionId,
            NonStandardBomVersionId = row.NonStandardBomVersionId,
            ElectricalBomVersionId = row.ElectricalBomVersionId,
            StandardBomRevision = row.StandardBomRevision,
            NonStandardBomRevision = row.NonStandardBomRevision,
            StandardBomSnapshot = JsonSerializer.Deserialize<List<BomItem>>(row.StandardBomSnapshotJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [],
            NonStandardBomSnapshot = JsonSerializer.Deserialize<List<BomItem>>(row.NonStandardBomSnapshotJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [],
            ChangeNumber = row.ChangeNumber,
            ChangeReason = row.ChangeReason,
            EffectiveSerialFrom = row.EffectiveSerialFrom,
            EffectiveSerialTo = row.EffectiveSerialTo,
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
        public Guid? ExecutionUnitId { get; init; }
        public string? ExecutionUnitName { get; init; }
    }

    private sealed class ProjectActivityRow
    {
        public Guid ProjectId { get; init; }
        public int DocumentCount { get; init; }
        public string? RootDocumentCheckedOutBy { get; init; }
        public bool HasDraft { get; init; }
        public bool HasPendingApproval { get; init; }
        public bool HasRejectedApproval { get; init; }
        public bool IsPublishing { get; init; }
        public bool HasPublishFailure { get; init; }
    }

    private sealed class WhereUsedSnapshotRow
    {
        public Guid ProjectId { get; init; }
        public string ProjectCode { get; init; } = string.Empty;
        public string ProjectName { get; init; } = string.Empty;
        public string RootJson { get; init; } = string.Empty;
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

    private sealed class ProjectAssignmentRow
    {
        public Guid ProjectId { get; init; }
        public string Username { get; init; } = string.Empty;
        public string AssignmentType { get; init; } = string.Empty;
    }

    private sealed class CustomerRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public bool IsActive { get; init; }
        public string SourceSystem { get; init; } = "legacy";
        public DateTime? LastSyncedAt { get; init; }
    }

    private class DocumentRow
    {
        public Guid Id { get; init; }
        public Guid ProjectId { get; init; }
        public Guid? FolderId { get; init; }
        public int? StoredVersionCount { get; init; }
        public string DrawingNumber { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string FileName { get; init; } = string.Empty;
        public string Kind { get; init; } = string.Empty;
        public string LifecycleState { get; init; } = string.Empty;
        public string RevisionLabel { get; init; } = string.Empty;
        public string? CheckedOutBy { get; init; }
        public DateTime? CheckedOutAt { get; init; }
        public Guid? CheckoutSessionId { get; init; }
        public string? CheckoutMachine { get; init; }
        public DateTime? CheckoutLastHeartbeatAt { get; init; }
        public DateTime? CheckoutLeaseExpiresAt { get; init; }
        public string? CheckoutReleaseRequestedBy { get; init; }
        public DateTime? CheckoutReleaseRequestedAt { get; init; }
        public string? CheckoutReleaseRequestReason { get; init; }
        public DateTime UpdatedAt { get; init; }
    }

    private sealed class DocumentFingerprintRow : DocumentRow
    {
        public string? SourceFingerprintSha256 { get; init; }
    }

    private sealed class RegistrationFingerprintRow
    {
        public Guid Id { get; init; }
        public string FileName { get; init; } = string.Empty;
        public string? SourceFingerprintSha256 { get; init; }
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
        public string? Remark { get; init; }
        public string? Brand { get; init; }
        public string? SurfaceTreatment { get; init; }
        public string? Weight { get; init; }
        public string RevisionLabel { get; init; } = string.Empty;
        public bool IsComplete { get; init; }
        public Guid? SourceDocumentId { get; init; }
        public string? SourceConfiguration { get; init; }
        public string ItemSource { get; init; } = "Manual";
        public bool IsManuallyOverridden { get; init; }
        public bool IsPendingRemoval { get; init; }
        public bool IsPendingClassification { get; init; }
        public bool IsManualUnmatched { get; init; }
        public bool IsManuallyRetained { get; init; }
        public bool IsManuallyExcluded { get; init; }
        public string? ReconciliationStatus { get; init; }
        public string? ReconciliationNote { get; init; }
        public string? ReconciliationUpdatedBy { get; init; }
        public DateTime? ReconciliationUpdatedAt { get; init; }
        public DateTime? DeletedAt { get; init; }
        public string? DeletedBy { get; init; }
        public string? DeleteReason { get; init; }
        public string? PropertyWritebackStatus { get; init; }
    }

    private sealed class CadPropertyWritebackRow
    {
        public Guid Id { get; init; }
        public Guid ProjectId { get; init; }
        public Guid BomItemId { get; init; }
        public Guid SourceDocumentId { get; init; }
        public string? SourceConfiguration { get; init; }
        public Guid ExpectedVersionId { get; init; }
        public string ExpectedRevision { get; init; } = string.Empty;
        public string PropertyPayload { get; init; } = "{}";
        public string Status { get; init; } = string.Empty;
        public string RequestedBy { get; init; } = string.Empty;
        public DateTime RequestedAt { get; init; }
        public DateTime? StartedAt { get; init; }
        public DateTime? CompletedAt { get; init; }
        public Guid? ResultVersionId { get; init; }
        public string? LastError { get; init; }
    }

    private sealed class BomEmptyDeclarationRow
    {
        public string BomKind { get; init; } = string.Empty;
        public bool DeclaredEmpty { get; init; }
        public string? UpdatedBy { get; init; }
        public DateTime? UpdatedAt { get; init; }
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
        public Guid? StandardBomVersionId { get; init; }
        public Guid? NonStandardBomVersionId { get; init; }
        public Guid? ElectricalBomVersionId { get; init; }
        public string? StandardBomRevision { get; init; }
        public string? NonStandardBomRevision { get; init; }
        public string StandardBomSnapshotJson { get; init; } = "[]";
        public string NonStandardBomSnapshotJson { get; init; } = "[]";
        public string? ChangeNumber { get; init; }
        public string? ChangeReason { get; init; }
        public string? EffectiveSerialFrom { get; init; }
        public string? EffectiveSerialTo { get; init; }
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
        public string? RoleCode { get; init; }
        public bool IsActive { get; init; }
        public long TokenVersion { get; init; }
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

    private sealed class ProjectDependencyRow
    {
        public int ChildCount { get; init; }
        public int DocumentCount { get; init; }
        public int BomCount { get; init; }
        public int SnapshotCount { get; init; }
        public int ReleasePackageCount { get; init; }
    }

    private sealed class DeletedProjectNumberRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = string.Empty;
        public Guid? OrganizationId { get; init; }
        public Guid? ParentProjectId { get; init; }
        public string? ProjectTypeCode { get; init; }
        public string? CustomerCode { get; init; }
        public int? CustomerProjectSequence { get; init; }
        public string ProjectCompanyCode { get; init; } = string.Empty;
    }
}
