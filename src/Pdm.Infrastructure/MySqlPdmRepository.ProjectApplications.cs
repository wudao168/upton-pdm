using System.Data.Common;
using System.Text.Json;
using Dapper;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository : IProjectApplicationRepository
{
    public async Task SetApplicationOrderDateAsync(Project project, ProjectOrderDateInput input, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var current = await FindProjectAsync(connection, transaction, project.Id, ct) ?? throw new PdmNotFoundException("项目不存在。");
        var signedDate = await connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition("SELECT signed_date FROM project WHERE id=@Id FOR UPDATE", new { project.Id }, transaction, cancellationToken: ct));
        if ((signedDate is null ? (DateOnly?)null : DateOnly.FromDateTime(signedDate.Value)) != input.ExpectedOrderDate)
            throw new PdmConflictException("订单日期已被修改，请刷新后重新填写。");
        await connection.ExecuteAsync(new CommandDefinition("UPDATE project SET signed_date=@Date,row_version=row_version+1,updated_at=@Now WHERE (id=@Id OR root_project_id=@Id) AND organization_id=@OrganizationId",
            new { project.Id, current.OrganizationId, Date = input.OrderDate?.ToDateTime(TimeOnly.MinValue), Now = timeProvider.GetUtcNow().UtcDateTime }, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
    }
    public async Task<IReadOnlyList<ProjectApplication>> ListApplicationsAsync(Guid companyId, string? applicant, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var rows = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT payload_json FROM project_application WHERE company_id=@companyId AND (@applicant IS NULL OR applicant=@applicant) ORDER BY number DESC",
            new { companyId, applicant }, cancellationToken: ct));
        return rows.Select(x => JsonSerializer.Deserialize<ProjectApplication>(x, jsonOptions)!).ToArray();
    }
    public async Task<ProjectApplication?> FindApplicationAsync(Guid id, Guid companyId, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        return await ReadApplication(connection, null, id, companyId, false, ct);
    }
    public async Task<IReadOnlyList<ProjectApplicationEvent>> ListApplicationHistoryAsync(Guid id, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        var rows = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT payload_json FROM project_application_history WHERE application_id=@id ORDER BY row_version", new { id }, cancellationToken: ct));
        return rows.Select(x => JsonSerializer.Deserialize<ProjectApplicationEvent>(x, jsonOptions)!).ToArray();
    }
    private async Task<ProjectApplication?> ReadApplication(MySqlConnection connection, DbTransaction? transaction, Guid id, Guid companyId, bool locked, CancellationToken ct)
    {
        var json = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT payload_json FROM project_application WHERE id=@id AND company_id=@companyId" + (locked ? " FOR UPDATE" : ""), new { id, companyId }, transaction, cancellationToken: ct));
        return json is null ? null : JsonSerializer.Deserialize<ProjectApplication>(json, jsonOptions);
    }
    public async Task<ProjectApplication> SaveApplicationAsync(ProjectApplication value, ProjectApplicationEvent entry, long expectedRowVersion, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var current = await ReadApplication(connection, transaction, value.Id, value.CompanyId, true, ct);
        if ((current?.RowVersion ?? 0) != expectedRowVersion) throw new PdmConflictException("立项申请已被修改或审批，请刷新后重试。");
        await WriteApplication(connection, transaction, value, entry, current is null, ct);
        await transaction.CommitAsync(ct);
        return value;
    }
    private async Task WriteApplication(MySqlConnection connection, DbTransaction transaction, ProjectApplication value, ProjectApplicationEvent entry, bool create, CancellationToken ct)
    {
        var sql = create
            ? "INSERT INTO project_application(id,company_id,number,applicant,status,row_version,payload_json) VALUES(@Id,@CompanyId,@Number,@Applicant,@Status,@RowVersion,@Payload)"
            : "UPDATE project_application SET status=@Status,row_version=@RowVersion,payload_json=@Payload WHERE id=@Id AND company_id=@CompanyId";
        await connection.ExecuteAsync(new CommandDefinition(sql,
            new { value.Id, value.CompanyId, value.Number, value.Applicant, value.Status, value.RowVersion, Payload = JsonSerializer.Serialize(value, jsonOptions) }, transaction, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO project_application_history(id,application_id,row_version,payload_json) VALUES(@Id,@ApplicationId,@RowVersion,@Payload)",
            new { entry.Id, ApplicationId = value.Id, value.RowVersion, Payload = JsonSerializer.Serialize(entry, jsonOptions) }, transaction, cancellationToken: ct));
    }
    private async Task<ProjectApplication?> LockApplicationApproval(MySqlConnection connection, DbTransaction transaction, CreateNumberedProjectCommand command, CancellationToken ct)
    {
        if (command.ApplicationApproval is not { } approval) return null;
        var value = await ReadApplication(connection, transaction, approval.ApplicationId, command.OrganizationId, true, ct)
            ?? throw new PdmNotFoundException("立项申请不存在或不属于当前公司。");
        if (value.Status == ProjectApplicationService.Approved) return value;
        if (value.Status != ProjectApplicationService.Pending || value.RowVersion != approval.ExpectedRowVersion)
            throw new PdmConflictException("申请已变化，请刷新后重新审批。");
        return value;
    }
    private async Task CompleteApplicationApproval(MySqlConnection connection, DbTransaction transaction, ProjectApplication application,
        ProjectApplicationApproval approval, Project project, CancellationToken ct)
    {
        var value = application with { Status = ProjectApplicationService.Approved, ProjectId = project.Id, ProjectCode = project.Code,
            RowVersion = application.RowVersion + 1, UpdatedAt = approval.Event.At };
        await WriteApplication(connection, transaction, value, approval.Event with { ProjectId = project.Id, ProjectCode = project.Code }, false, ct);
        var input = approval.Event.ApprovedData?.Input ?? application.Data.Input;
        var contacts = new ProjectContactInformation(project.Id, RowVersion: 1, UpdatedBy: approval.Event.Actor, UpdatedAt: approval.Event.At,
            SalesPeople: input.SalesUsername, PresalesPeople: string.Join('、', input.PresalesUsernames));
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO project_contact_information(project_id,row_version,payload_json) VALUES(@ProjectId,1,@Payload)",
            new { ProjectId = project.Id, Payload = JsonSerializer.Serialize(contacts, jsonOptions) }, transaction, cancellationToken: ct));
    }
}
