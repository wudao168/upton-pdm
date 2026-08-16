using Dapper;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<OrganizationDirectory> GetOrganizationDirectoryAsync(CancellationToken cancellationToken)
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
            ORDER BY organization.is_active DESC,organization.project_company_code
            """, cancellationToken: cancellationToken));
        var unitRows = await connection.QueryAsync<OrganizationUnitRow>(new CommandDefinition(
            "SELECT id,organization_id,parent_unit_id,code,name,kind,is_active,sort_order FROM organization_unit ORDER BY organization_id,sort_order,name",
            cancellationToken: cancellationToken));
        var membershipRows = await connection.QueryAsync<OrganizationMembershipRow>(new CommandDefinition(
            "SELECT unit_id,username,is_primary FROM organization_membership ORDER BY username,is_primary DESC",
            cancellationToken: cancellationToken));
        var managerRows = await connection.QueryAsync<OrganizationManagerRow>(new CommandDefinition(
            "SELECT unit_id,username,is_primary FROM organization_unit_manager ORDER BY unit_id,is_primary DESC,username",
            cancellationToken: cancellationToken));
        var users = await ListUsersAsync(cancellationToken);
        return new OrganizationDirectory(
            organizationRows.Select(MapOrganization).ToArray(),
            unitRows.Select(MapOrganizationUnit).ToArray(),
            membershipRows.Select(row => new OrganizationMembership(row.UnitId, row.Username, row.IsPrimary)).ToArray(),
            managerRows.GroupBy(row => row.UnitId).Select(group => new OrganizationUnitManagers(
                group.Key,
                group.FirstOrDefault(item => item.IsPrimary)?.Username ?? string.Empty,
                group.Where(item => !item.IsPrimary).Select(item => item.Username).ToArray())).ToArray(),
            users.Select(user => new OrganizationDirectoryUser(user.Username, user.DisplayName, user.Role, user.IsActive)).ToArray());
    }

    public async Task<ProjectOrganization> SaveProjectOrganizationAsync(SaveProjectOrganizationCommand command, CancellationToken cancellationToken)
    {
        var id = command.Id ?? Guid.NewGuid();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (command.Id is null)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO project_organization(id,name,project_company_code,model_company_code,crm_company_name,is_active) VALUES(@Id,@Name,@ProjectCompanyCode,@ModelCompanyCode,@Name,@IsActive)",
                    new { Id = id, command.Name, command.ProjectCompanyCode, command.ModelCompanyCode, command.IsActive }, transaction, cancellationToken: cancellationToken));
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO project_number_counter(organization_id,current_value) VALUES(@Id,0)",
                    new { Id = id }, transaction, cancellationToken: cancellationToken));
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO serial_number_counter(organization_id,current_value) VALUES(@Id,0)",
                    new { Id = id }, transaction, cancellationToken: cancellationToken));
            }
            else
            {
                var affected = await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE project_organization SET name=@Name,project_company_code=@ProjectCompanyCode,model_company_code=@ModelCompanyCode,crm_company_name=@Name,is_active=@IsActive WHERE id=@Id",
                    new { Id = id, command.Name, command.ProjectCompanyCode, command.ModelCompanyCode, command.IsActive }, transaction, cancellationToken: cancellationToken));
                if (affected == 0) throw new PdmNotFoundException("公司不存在。");
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new PdmConflictException("公司名称、项目号公司代码或设备型号公司代码已经存在。");
        }
        return new ProjectOrganization(id, command.Name, command.ProjectCompanyCode, command.ModelCompanyCode, command.Name, command.IsActive);
    }

    public async Task<OrganizationUnit> SaveOrganizationUnitAsync(SaveOrganizationUnitCommand command, CancellationToken cancellationToken)
    {
        var id = command.Id ?? Guid.NewGuid();
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var connection = await OpenAsync(cancellationToken);
        try
        {
            if (command.Id is null)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO organization_unit(id,organization_id,parent_unit_id,code,name,kind,is_active,sort_order,created_at,updated_at)
                    VALUES(@Id,@OrganizationId,@ParentUnitId,@Code,@Name,@Kind,@IsActive,@SortOrder,@Now,@Now)
                    """,
                    new { Id = id, command.OrganizationId, command.ParentUnitId, command.Code, command.Name, Kind = command.Kind.ToString(), command.IsActive, command.SortOrder, Now = now }, cancellationToken: cancellationToken));
            }
            else
            {
                var affected = await connection.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE organization_unit SET organization_id=@OrganizationId,parent_unit_id=@ParentUnitId,code=@Code,name=@Name,
                        kind=@Kind,is_active=@IsActive,sort_order=@SortOrder,updated_at=@Now WHERE id=@Id
                    """,
                    new { Id = id, command.OrganizationId, command.ParentUnitId, command.Code, command.Name, Kind = command.Kind.ToString(), command.IsActive, command.SortOrder, Now = now }, cancellationToken: cancellationToken));
                if (affected == 0) throw new PdmNotFoundException("组织单元不存在。");
            }
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            throw new PdmConflictException("同一公司内的组织编码已经存在。");
        }
        return new OrganizationUnit(id, command.OrganizationId, command.ParentUnitId, command.Code, command.Name, command.Kind, command.IsActive, command.SortOrder);
    }

    public async Task<OrganizationDirectory> SetOrganizationMembershipsAsync(string username, IReadOnlyList<Guid> unitIds, Guid primaryUnitId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM organization_membership WHERE username=@Username", new { Username = username }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO organization_membership(unit_id,username,is_primary,assigned_at) VALUES(@UnitId,@Username,@IsPrimary,@Now)",
            unitIds.Select(unitId => new { UnitId = unitId, Username = username, IsPrimary = unitId == primaryUnitId, Now = now }), transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return await GetOrganizationDirectoryAsync(cancellationToken);
    }

    public async Task<OrganizationDirectory> SetOrganizationUnitManagersAsync(Guid unitId, string primaryManager, IReadOnlyList<string> collaborativeManagers, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM organization_unit_manager WHERE unit_id=@UnitId", new { UnitId = unitId }, transaction, cancellationToken: cancellationToken));
        var managers = new[] { new { Username = primaryManager, IsPrimary = true } }
            .Concat(collaborativeManagers.Select(username => new { Username = username, IsPrimary = false }));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO organization_unit_manager(unit_id,username,is_primary,assigned_at) VALUES(@UnitId,@Username,@IsPrimary,@Now)",
            managers.Select(item => new { UnitId = unitId, item.Username, item.IsPrimary, Now = now }), transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return await GetOrganizationDirectoryAsync(cancellationToken);
    }

    public async Task<Project> SetProjectExecutionUnitAsync(Guid projectId, Guid executionUnitId, string actor, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE project SET execution_unit_id=@ExecutionUnitId,row_version=row_version+1,updated_at=@Now WHERE id=@ProjectId AND parent_project_id IS NULL",
            new { ProjectId = projectId, ExecutionUnitId = executionUnitId, Now = now }, transaction, cancellationToken: cancellationToken));
        if (affected == 0) throw new PdmNotFoundException("主项目不存在。");
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE assignment FROM project_assignment assignment INNER JOIN project item ON item.id=assignment.project_id WHERE item.id=@ProjectId OR item.parent_project_id=@ProjectId",
            new { ProjectId = projectId }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return await FindProjectAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("主项目不存在。");
    }

    public async Task<Project> SetMainProjectStaffingAsync(Guid projectId, SetMainProjectStaffingCommand command, string actor, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var exists = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM project WHERE id=@ProjectId AND parent_project_id IS NULL FOR UPDATE", new { ProjectId = projectId }, transaction, cancellationToken: cancellationToken));
        if (exists == 0) throw new PdmNotFoundException("主项目不存在。");
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM project_assignment WHERE project_id=@ProjectId AND assignment_type IN ('PrimaryProjectManager','CollaborativeProjectManager','DesignLead')",
            new { ProjectId = projectId }, transaction, cancellationToken: cancellationToken));
        var assignments = new[]
        {
            new { Username = command.PrimaryProjectManager, Type = ProjectAssignmentType.PrimaryProjectManager.ToString() },
            new { Username = command.DesignLead, Type = ProjectAssignmentType.DesignLead.ToString() }
        }.Concat(command.CollaborativeProjectManagers.Select(username => new { Username = username, Type = ProjectAssignmentType.CollaborativeProjectManager.ToString() }));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO project_assignment(project_id,username,assignment_type,assigned_by,assigned_at) VALUES(@ProjectId,@Username,@Type,@Actor,@Now)",
            assignments.Select(item => new { ProjectId = projectId, item.Username, item.Type, Actor = actor, Now = now }), transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return await FindProjectAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("主项目不存在。");
    }

    public async Task<Project> SetChildProjectDesignersAsync(Guid projectId, IReadOnlyList<string> designers, string actor, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var exists = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM project WHERE id=@ProjectId AND parent_project_id IS NOT NULL FOR UPDATE", new { ProjectId = projectId }, transaction, cancellationToken: cancellationToken));
        if (exists == 0) throw new PdmNotFoundException("子项目不存在。");
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM project_assignment WHERE project_id=@ProjectId AND assignment_type='Designer'", new { ProjectId = projectId }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO project_assignment(project_id,username,assignment_type,assigned_by,assigned_at) VALUES(@ProjectId,@Username,'Designer',@Actor,@Now)",
            designers.Select(username => new { ProjectId = projectId, Username = username, Actor = actor, Now = now }), transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return await FindProjectAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("子项目不存在。");
    }

    private static ProjectOrganization MapOrganization(ProjectOrganizationRow row) => new(
        row.Id, row.Name, row.ProjectCompanyCode, row.ModelCompanyCode, row.CrmCompanyName, row.IsActive,
        checked((int)row.CurrentProjectSequence), checked((int)row.CurrentSerialSequence));

    private static OrganizationUnit MapOrganizationUnit(OrganizationUnitRow row) => new(
        row.Id, row.OrganizationId, row.ParentUnitId, row.Code, row.Name, Enum.Parse<OrganizationUnitKind>(row.Kind), row.IsActive, row.SortOrder);

    private sealed class OrganizationUnitRow
    {
        public Guid Id { get; init; }
        public Guid OrganizationId { get; init; }
        public Guid? ParentUnitId { get; init; }
        public string Code { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Kind { get; init; } = string.Empty;
        public bool IsActive { get; init; }
        public int SortOrder { get; init; }
    }

    private sealed class OrganizationMembershipRow
    {
        public Guid UnitId { get; init; }
        public string Username { get; init; } = string.Empty;
        public bool IsPrimary { get; init; }
    }

    private sealed class OrganizationManagerRow
    {
        public Guid UnitId { get; init; }
        public string Username { get; init; } = string.Empty;
        public bool IsPrimary { get; init; }
    }
}
