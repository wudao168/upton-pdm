using System.Globalization;
using System.Text.RegularExpressions;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record HistoricalProjectRow(int RowNumber, string Code, string Name, string? Alias, string Model,
    IReadOnlyList<string> Serials, string Customer, DateOnly? SignedDate, DateOnly? DeliveryDate, string? BomCategory,
    DateOnly? HistoricalUpdatedDate, string? HistoricalRemarks, string? ParentCode = null);

public sealed record HistoricalProjectImportResult(IReadOnlyList<Project> Projects, bool Created);

public sealed partial class PdmWorkflowService
{
    public async Task RequireHistoricalProjectImportAsync(Guid organizationId, string actor, UserRole role, CancellationToken ct)
    {
        RequireActiveCompany(organizationId);
        await RequirePermissionAsync(actor, role, PermissionCodes.ProjectHistoricalImport, ct);
        await RequirePermissionAsync(actor, role, PermissionCodes.ProjectCreate, ct);
        var user = await repository.FindUserAsync(actor, ct);
        if (user is null || !user.IsActive || !await AuthorizationPolicy.CanAccessProjectCompanyAsync(repository, user, organizationId, ct))
            throw new UnauthorizedAccessException("当前账号没有该公司的项目导入权限。");
    }

    public async Task<HistoricalProjectImportResult> ImportHistoricalProjectsAsync(Guid organizationId,
        Func<IReadOnlyList<HistoricalProjectRow>> readRows, bool preview, string actor, UserRole role, CancellationToken ct)
    {
        await RequireHistoricalProjectImportAsync(organizationId, actor, role, ct);
        var rows = readRows();
        var options = await repository.GetProjectNumberingOptionsAsync(ct);
        var organization = options.Organizations.SingleOrDefault(item => item.Id == organizationId && item.IsActive)
            ?? throw new PdmRuleException("所属公司不存在或已停用。");
        var settings = await repository.GetSystemSettingsAsync(ct);
        var projects = PrepareHistoricalProjects(rows, organization, options,
            StorageLocationPolicy.Normalize(settings.VaultRoot), StorageLocationPolicy.Normalize(settings.ReleaseRoot), actor);
        if (projects.Any(item => item.ParentProjectId.HasValue))
        {
            var authorize = await RequireNumberedProjectCreationPermissionAsync(organizationId, 1, actor, role, ct);
            var rootsWithChildren = projects.Where(item => item.ParentProjectId.HasValue).Select(item => item.RootProjectId).ToHashSet();
            foreach (var root in projects.Where(item => rootsWithChildren.Contains(item.Id))) authorize!(root);
        }
        return await repository.ImportHistoricalProjectsAsync(projects, organization, preview, ct);
    }

    internal static IReadOnlyList<Project> PrepareHistoricalProjects(IReadOnlyList<HistoricalProjectRow> rows,
        ProjectOrganization organization, ProjectNumberingOptions options,
        string vaultRoot, string releaseRoot, string actor)
    {
        if (rows.Count is < 1 or > HistoricalProjectImport.MaximumRows)
            throw new PdmRuleException($"每批须包含1至{HistoricalProjectImport.MaximumRows}行项目。");
        if (rows.Sum(row => row.Serials.Count) > HistoricalProjectImport.MaximumSerials)
            throw new PdmRuleException($"每批最多导入{HistoricalProjectImport.MaximumSerials}个设备序列号。");
        if (string.Equals(vaultRoot, releaseRoot, StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("图档库与生产发包目录不能是同一位置。");

        var rootPattern = new Regex("^[A-Z]" + Regex.Escape(organization.ProjectCompanyCode) + "[0-9]{5}$");
        var modelPattern = new Regex("^" + Regex.Escape(organization.ModelCompanyCode) + "-([0-9]{1,2})-(.{1,30})-([0-9]{3})(?:-[0-9]{2})+$");
        var types = options.ProjectTypes.Select(item => item.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var equipment = options.EquipmentTypes.Where(item => item.IsActive).Select(item => item.Code).ToHashSet();
        var indexed = new Dictionary<string, HistoricalProjectRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (row.Code.Length is < 1 or > MaximumIdentityLength || row.Code.Any(char.IsControl))
                throw Invalid(row, "项目号不能为空、含控制字符或超过80个字符。");
            if (!indexed.TryAdd(row.Code, row)) throw Invalid(row, "清单中项目号重复。");
        }
        var parents = rows.ToDictionary(row => row.Code, row =>
        {
            if (row.ParentCode is not null) return row.ParentCode;
            var boundary = row.Code.LastIndexOf('-');
            return boundary > 0 && indexed.ContainsKey(row.Code[..boundary]) ? row.Code[..boundary] : null;
        }, StringComparer.OrdinalIgnoreCase);
        var parentCodes = parents.Values.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, Project>(StringComparer.OrdinalIgnoreCase);
        var rootsById = new Dictionary<Guid, string>();
        var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var serials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var childSequences = new Dictionary<Guid, HashSet<int>>();

        var remaining = rows.ToList();
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(row => parents[row.Code] is not { } parentCode || result.ContainsKey(parentCode)).ToArray();
            if (ready.Length == 0) throw Invalid(remaining[0], "上级项目缺失或存在循环关系。请一起导入完整项目树。");
            foreach (var row in ready)
            {
                PdmRuleException Error(string message) => Invalid(row, message);
                ValidateProjectDetails(row.Name, row.Alias, row.Serials.Count);
                if (row.HistoricalRemarks?.Length > HistoricalProjectImport.MaximumRemarkLength) throw Error("备注超过允许长度。");
                if (row.Customer.Length is < 1 or > 200) throw Error("客户名称不能为空或超过200个字符。");
                if (row.Model.Length is < 1 or > 160 || row.Model.Any(char.IsControl)) throw Error("设备型号不能为空、含控制字符或超过160个字符。");
                var parent = parents[row.Code] is { } parentCode ? result[parentCode] : null;
                var match = modelPattern.Match(row.Model);
                var equipmentCode = match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
                var customerSequence = match.Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
                var customerCode = match.Success ? match.Groups[2].Value : null;
                if (!models.Add(row.Model)) throw Error("清单中设备型号重复。");
                foreach (var serial in row.Serials)
                    if (serial.Length is < 1 or > MaximumIdentityLength || serial.Any(char.IsControl) || !serials.Add(serial))
                        throw Error($"序列号 {serial} 为空、过长、含控制字符或在清单内重复。");

                var category = row.BomCategory ?? (parent is null && parentCodes.Contains(row.Code) ? "0301" : "0302");
                if (category is not ("0301" or "0302") || parent is not null && category != "0302")
                    throw Error("BOM节点类型必须为0301或0302，子项目使用0302。");
                var id = Guid.NewGuid();
                if (parent is null) rootsById.Add(id, row.Code);
                var rootCode = parent is null ? row.Code : rootsById[parent.RootProjectId!.Value];
                var storedRoot = StorageSegment(rootCode);
                var relativeCode = parent is null ? $"{storedRoot}-0" : StorageSegment(row.Code);
                var signedDate = row.SignedDate ?? parent?.SignedDate;
                var deliveryDate = row.DeliveryDate ?? parent?.DeliveryDate;
                if (signedDate is { } order && deliveryDate is { } delivery && delivery < order)
                    throw Error("交货日期不能早于订单日期。");
                int? childSequence = null;
                if (parent is not null)
                {
                    if (!childSequences.TryGetValue(parent.Id, out var used)) childSequences[parent.Id] = used = [];
                    var suffix = row.Code.StartsWith(parent.Code + "-", StringComparison.OrdinalIgnoreCase)
                        ? row.Code[(parent.Code.Length + 1)..] : "";
                    childSequence = int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var position)
                        && position is >= 1 and <= 99 && !used.Contains(position)
                        ? position : ProjectChildCreation.AllocateSequences(used, 1)[0];
                    used.Add(childSequence.Value);
                }
                result.Add(row.Code, new Project(id, row.Code, row.Name, actor,
                    StorageLocationPolicy.ProjectStorageLocation(vaultRoot, storedRoot, relativeCode),
                    StorageLocationPolicy.ProjectStorageLocation(releaseRoot, storedRoot, relativeCode), true)
                {
                    ProjectAlias = row.Alias, OrganizationId = organization.Id, OrganizationName = organization.Name,
                    ProjectTypeCode = rootPattern.IsMatch(rootCode) && types.Contains(rootCode[..1]) ? rootCode[..1] : null,
                    EquipmentTypeCode = equipment.Contains(equipmentCode) ? equipmentCode : null,
                    CustomerCode = customerCode, CustomerName = row.Customer, CustomerProjectSequence = customerSequence > 0 ? customerSequence : null,
                    DeviceModel = row.Model, SignedDate = signedDate, DeliveryDate = deliveryDate,
                    Quantity = row.Serials.Count, SerialNumbers = row.Serials, ParentProjectId = parent?.Id, RootProjectId = parent?.RootProjectId ?? id,
                    ChildSequence = childSequence,
                    BomItemCategoryCode = category, ResponsibleUsers = [actor],
                    HistoricalUpdatedDate = row.HistoricalUpdatedDate, HistoricalRemarks = row.HistoricalRemarks
                });
            }
            remaining.RemoveAll(row => result.ContainsKey(row.Code));
        }
        return result.Values.ToArray();
    }

    private const int MaximumIdentityLength = 80;
    private static PdmRuleException Invalid(HistoricalProjectRow row, string message) =>
        new($"第{row.RowNumber}行（{row.Code}）：{message}");

    private static string StorageSegment(string code)
    {
        if (!code.StartsWith("history-", StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(code, @"\A[A-Za-z0-9_-]+\z") &&
            !Regex.IsMatch(code, @"\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z", RegexOptions.IgnoreCase))
            return code;
        return "history-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code)));
    }
}

public static class HistoricalProjectImport
{
    public const int MaximumRows = 200;
    public const int MaximumSerials = 2000;
    public const int MaximumFileBytes = 5 * 1024 * 1024;
    public const int MaximumRemarkLength = 4000;

    public static int? SerialSequence(ProjectOrganization organization, string serial) =>
        serial.Length == organization.ProjectCompanyCode.Length + 7 &&
        serial.StartsWith(organization.ProjectCompanyCode, StringComparison.Ordinal) &&
        int.TryParse(serial.AsSpan(organization.ProjectCompanyCode.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        && value > 0 ? value : null;

    public static AuditEntry Audit(Project project, DateTimeOffset now) =>
        new(Guid.NewGuid(), now, project.Owner, "project.history.import", nameof(Project), project.Id.ToString(),
            $"{project.Code} · {project.DeviceModel} · 数量{project.Quantity}");
}
