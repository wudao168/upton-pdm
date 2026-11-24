using System.Data;
using System.Globalization;
using Dapper;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

public sealed partial class MySqlPdmRepository
{
    public async Task<HistoricalProjectImportResult> ImportHistoricalProjectsAsync(IReadOnlyList<Project> projects,
        ProjectOrganization organization, bool preview, CancellationToken ct)
    {
        var organizationId = organization.Id;
        var roots = projects.Where(project => project.ParentProjectId is null).ToArray();
        var serials = projects.SelectMany(project => project.SerialNumbers).ToArray();
        var customerNumbers = roots.Where(project => project.CustomerCode is not null && project.CustomerProjectSequence.HasValue)
            .Select(project => (Code: project.CustomerCode!, Sequence: project.CustomerProjectSequence!.Value)).ToHashSet();
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        try
        {
            // Serialize explicit numbering with ordinary allocations for the same organization.
            if (!preview && !await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT is_active FROM project_organization WHERE id=@OrganizationId FOR UPDATE",
                new { OrganizationId = organizationId }, transaction, cancellationToken: ct)))
                throw new PdmRuleException("所属公司不存在或已停用。");

            using (var matches = await connection.QueryMultipleAsync(new CommandDefinition(
                """
                SELECT code FROM project WHERE code IN @Codes;
                SELECT device_model FROM project WHERE organization_id=@OrganizationId AND device_model IN @Models;
                SELECT serial_number FROM project_serial_number WHERE serial_number IN @Serials;
                SELECT customer_code Code,customer_project_sequence Sequence FROM project
                WHERE organization_id=@OrganizationId AND parent_project_id IS NULL
                  AND customer_code IN @Customers AND customer_project_sequence IN @CustomerSequences;
                """,
                new
                {
                    OrganizationId = organizationId, Codes = projects.Select(item => item.Code).ToArray(),
                    Models = projects.Select(item => item.DeviceModel).ToArray(), Serials = serials,
                    Customers = customerNumbers.Select(item => item.Code).Distinct().ToArray(),
                    CustomerSequences = customerNumbers.Select(item => item.Sequence).Distinct().ToArray()
                }, transaction, cancellationToken: ct)))
            {
                var usedCodes = (await matches.ReadAsync<string>()).ToArray();
                var usedModels = (await matches.ReadAsync<string>()).ToArray();
                var usedSerials = (await matches.ReadAsync<string>()).ToArray();
                var usedCustomerNumbers = (await matches.ReadAsync<(string Code, int Sequence)>())
                    .Where(customerNumbers.Contains).Select(item => $"{item.Code}-{item.Sequence:D3}").ToArray();
                HistoricalProjectConflict.ThrowIfAny(usedCodes, usedModels, usedSerials, usedCustomerNumbers);
            }

            if (preview) return new(projects, false);

            var now = timeProvider.GetUtcNow();
            foreach (var project in projects)
                await InsertNumberedProjectAsync(connection, transaction, project, now.UtcDateTime, ct);
            foreach (var root in roots)
                await EnsureProjectFolderTreeAsync(connection, transaction, root.Id, ct);
            foreach (var project in projects)
                await InsertAuditAsync(connection, transaction, AuditParameters(HistoricalProjectImport.Audit(project, now)), ct);

            var rootNumbers = roots.Where(project => project.ProjectTypeCode is not null).Select(project => new
            {
                Type = project.ProjectTypeCode!,
                Sequence = int.Parse(project.Code.AsSpan(project.Code.Length - 5), CultureInfo.InvariantCulture)
            }).ToArray();
            foreach (var group in rootNumbers.GroupBy(item => item.Type))
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO project_type_number_counter(organization_id,project_type_code,current_value)
                    VALUES(@OrganizationId,@Type,@Maximum) ON DUPLICATE KEY UPDATE current_value=GREATEST(current_value,@Maximum);
                    DELETE FROM released_project_type_number WHERE organization_id=@OrganizationId AND project_type_code=@Type AND sequence_value IN @Sequences;
                    """,
                    new { OrganizationId = organizationId, Type = group.Key, Maximum = group.Max(item => item.Sequence),
                        Sequences = group.Select(item => item.Sequence).ToArray() }, transaction, cancellationToken: ct));
            }
            foreach (var group in customerNumbers.GroupBy(item => item.Code))
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO customer_project_counter(organization_id,customer_code,current_value)
                    VALUES(@OrganizationId,@Customer,@Maximum) ON DUPLICATE KEY UPDATE current_value=GREATEST(current_value,@Maximum);
                    DELETE FROM released_customer_project_number WHERE organization_id=@OrganizationId AND customer_code=@Customer AND sequence_value IN @Sequences;
                    """,
                    new { OrganizationId = organizationId, Customer = group.Key, Maximum = group.Max(item => item.Sequence),
                        Sequences = group.Select(item => item.Sequence).ToArray() }, transaction, cancellationToken: ct));
            }
            var serialSequences = serials.Select(serial => HistoricalProjectImport.SerialSequence(organization, serial)).OfType<int>().ToArray();
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO project_number_counter(organization_id,current_value)
                VALUES(@OrganizationId,@MaxProject) ON DUPLICATE KEY UPDATE current_value=GREATEST(current_value,@MaxProject);
                INSERT INTO serial_number_counter(organization_id,current_value)
                VALUES(@OrganizationId,@MaxSerial) ON DUPLICATE KEY UPDATE current_value=GREATEST(current_value,@MaxSerial);
                DELETE FROM released_serial_number WHERE organization_id=@OrganizationId AND sequence_value IN @SerialSequences;
                """,
                new { OrganizationId = organizationId, MaxProject = rootNumbers.Select(item => item.Sequence).DefaultIfEmpty().Max(),
                    MaxSerial = serialSequences.DefaultIfEmpty().Max(), SerialSequences = serialSequences }, transaction, cancellationToken: ct));
            await transaction.CommitAsync(ct);
            return new(projects, true);
        }
        catch (MySqlException exception) when (exception.Number is 1062 or 1205 or 1213)
        {
            throw new PdmConflictException("编号已被其他操作占用，或编号正在更新。请重新预检整批清单。");
        }
    }
}

internal static class HistoricalProjectConflict
{
    public static void ThrowIfAny(IReadOnlyCollection<string> codes, IReadOnlyCollection<string> models,
        IReadOnlyCollection<string> serials, IReadOnlyCollection<string> customerNumbers)
    {
        var groups = new[] { (Name: "项目号", Values: codes), (Name: "设备型号", Values: models),
            (Name: "序列号", Values: serials), (Name: "客户项目流水", Values: customerNumbers) };
        var messages = groups.Where(group => group.Values.Count > 0).Select(group =>
            group.Name + " " + string.Join("、", group.Values.Order(StringComparer.Ordinal).Take(8)) +
            (group.Values.Count > 8 ? $" 等{group.Values.Count}项" : "")).ToArray();
        if (messages.Length > 0) throw new PdmConflictException($"已有数据占用以下编号：{string.Join("；", messages)}。");
    }
}
