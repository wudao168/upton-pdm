using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public sealed record LongLeadU9RecoveryResult(
    Guid ReleasePackageId,
    string ReleasePackageNumber,
    BomHeaderGenerationResult HeaderApplications,
    ApprovalU9AutomationResult Automation);

public sealed class PdmWorkflowService(
    IPdmRepository repository,
    IFileStorage fileStorage,
    IReleasePackagePublisher publisher,
    TimeProvider timeProvider,
    ApprovalU9AutomationService? approvalU9Automation = null,
    BomHeaderService? bomHeaderService = null,
    IMaterialRepository? materialRepository = null)
{
    private const string ReconcileAutoAdded = "AutoAdded";
    private const string ReconcileClassificationChanged = "ClassificationChanged";
    private const string ReconcilePendingClassification = "PendingClassification";
    private const string ReconcilePendingRemoval = "PendingRemoval";
    private const string ReconcileManualUnmatched = "ManualUnmatched";
    private const string ReconcileManuallyClassified = "ManuallyClassified";
    private const string ReconcileManuallyRetained = "ManuallyRetained";
    private const string ReconcileManuallyExcluded = "ManuallyExcluded";
    private const string ReconcileManualAdded = "ManualAdded";
    private const string ReconcileRestored = "Restored";
    private const string ReconcileDeleted = "Deleted";

    public async Task<Project> CreateNumberedProjectAsync(CreateNumberedProjectCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProjectCreate, cancellationToken);
        RequireActiveCompany(command.OrganizationId);
        ValidateProjectDetails(command.Name, command.ProjectAlias, command.Quantity);
        var bomItemCategoryCode = NormalizeBomItemCategory(command.BomItemCategoryCode);

        var options = await repository.GetProjectNumberingOptionsAsync(cancellationToken);
        if (!options.Organizations.Any(item => item.Id == command.OrganizationId && item.IsActive))
            throw new PdmRuleException("所选组织不存在或已停用。");
        if (!options.ProjectTypes.Any(item => string.Equals(item.Code, command.ProjectTypeCode, StringComparison.OrdinalIgnoreCase) && item.IsActive))
            throw new PdmRuleException("所选项目类型不存在或已停用。");
        if (!options.EquipmentTypes.Any(item => item.Code == command.EquipmentTypeCode && item.IsActive))
            throw new PdmRuleException("所选设备类型不存在或已停用。");
        var customer = await repository.FindCustomerAsync(command.CustomerId, cancellationToken);
        if (customer is null || !customer.IsActive)
            throw new PdmRuleException("所选客户不存在或已停用。");
        if (!string.Equals(customer.SourceSystem, "u9c", StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("所选客户不是从U9C同步的数据，请重新选择客户。");

        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        var vaultRoot = StorageLocationPolicy.Normalize(settings.VaultRoot);
        var releaseRoot = StorageLocationPolicy.Normalize(settings.ReleaseRoot);
        if (string.Equals(vaultRoot, releaseRoot, StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("图档库与生产发包目录不能是同一位置。");

        var project = await repository.CreateNumberedProjectAsync(command with
        {
            ProjectTypeCode = command.ProjectTypeCode.Trim().ToUpperInvariant(),
            BomItemCategoryCode = bomItemCategoryCode,
            Name = command.Name.Trim(),
            ProjectAlias = NullIfWhiteSpace(command.ProjectAlias),
            Owner = actor,
            VaultLocation = vaultRoot,
            ReleaseLocation = releaseRoot
        }, cancellationToken);
        await repository.EnsureProjectFolderTreeAsync(project.Id, cancellationToken);
        await AuditAsync(actor, "project.create", nameof(Project), project.Id.ToString(), $"{project.Code} · {project.Name} · 数量{project.Quantity}", cancellationToken);
        return project;
    }

    public async Task<Project> CreateSubprojectAsync(CreateSubprojectCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProjectChildCreate, cancellationToken);
        ValidateProjectDetails(command.Name, command.ProjectAlias, command.Quantity);
        if (!await repository.HasProjectReadAccessAsync(command.ParentProjectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该上级项目的访问权限。");
        var options = await repository.GetProjectNumberingOptionsAsync(cancellationToken);
        if (command.EquipmentTypeCode is not null
            && !options.EquipmentTypes.Any(item => item.Code == command.EquipmentTypeCode && item.IsActive))
            throw new PdmRuleException("所选设备类型不存在或已停用。");
        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        var project = await repository.CreateSubprojectAsync(command with
        {
            Name = command.Name.Trim(),
            ProjectAlias = NullIfWhiteSpace(command.ProjectAlias),
            VaultRoot = StorageLocationPolicy.Normalize(settings.VaultRoot),
            ReleaseRoot = StorageLocationPolicy.Normalize(settings.ReleaseRoot)
        }, cancellationToken);
        await repository.EnsureProjectFolderTreeAsync(project.Id, cancellationToken);
        await AuditAsync(actor, "project.child.create", nameof(Project), project.Id.ToString(), $"{project.Code} · {project.Name} · 数量{project.Quantity}", cancellationToken);
        return project;
    }

    private static string NormalizeBomItemCategory(string? value)
    {
        var normalized = value?.Trim();
        return normalized is "0301" or "0302"
            ? normalized
            : throw new PdmRuleException("项目BOM类型只能选择0301产线或0302设备。");
    }

    public async Task<PdmCustomer> SaveCustomerAsync(Guid? customerId, string code, string name, bool isActive, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.CustomerSettingsManage, cancellationToken);
        code = code?.Trim().ToUpperInvariant() ?? string.Empty;
        name = name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
            throw new PdmRuleException("客户编码和客户名称不能为空。");
        if (code.Length > 30 || name.Length > 200)
            throw new PdmRuleException("客户编码或名称超过允许长度。");
        if (code.Any(character => !char.IsLetterOrDigit(character) && character is not ('-' or '_')))
            throw new PdmRuleException("客户编码只能包含字母、数字、短横线和下划线。");
        var customer = await repository.SaveCustomerAsync(customerId, code, name, isActive, cancellationToken);
        await AuditAsync(actor, customerId is null ? "customer.create" : "customer.update", nameof(PdmCustomer), customer.Id.ToString(), $"{customer.Code} · {customer.Name} · {(customer.IsActive ? "启用" : "停用")}", cancellationToken);
        return customer;
    }

    public async Task<EquipmentTypeDefinition> SaveEquipmentTypeAsync(int code, string name, bool isActive, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, cancellationToken);
        name = name?.Trim() ?? string.Empty;
        if (code is < 0 or > 99) throw new PdmRuleException("设备类型编码必须为0到99。");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) throw new PdmRuleException("设备类型名称不能为空且不能超过100个字符。");
        var equipmentType = await repository.SaveEquipmentTypeAsync(code, name, isActive, cancellationToken);
        await AuditAsync(actor, "equipment-type.update", nameof(EquipmentTypeDefinition), code.ToString("D2"), $"{code:D2} · {name} · {(isActive ? "启用" : "停用")}", cancellationToken);
        return equipmentType;
    }

    public async Task<PdmSystemSettings> UpdateSystemSettingsAsync(PdmSystemSettings input, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, cancellationToken);
        var current = await repository.GetSystemSettingsAsync(cancellationToken);
        var vaultRoot = StorageLocationPolicy.Normalize(input.VaultRoot);
        var releaseRoot = StorageLocationPolicy.Normalize(input.ReleaseRoot);
        var materialAttachmentRoot = StorageLocationPolicy.Normalize(input.MaterialAttachmentRoot);
        if (string.Equals(vaultRoot, releaseRoot, StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("图档库与生产发包目录不能是同一位置。");
        if (string.Equals(materialAttachmentRoot, vaultRoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(materialAttachmentRoot, releaseRoot, StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("料品资料目录必须与图档库、生产发包目录分开配置。");
        var settingsInput = BomPropertyMappingCatalog.Apply(input with
        {
            VaultRoot = vaultRoot,
            ReleaseRoot = releaseRoot,
            MaterialAttachmentRoot = materialAttachmentRoot,
            ValidationRules = NormalizeBomValidationRules(input.ValidationRules),
            ApprovalWorkflows = NormalizeApprovalWorkflows(input.ApprovalWorkflows, current.ApprovalWorkflows),
            MaterialCodeApproval = NormalizeMaterialCodeApproval(input.MaterialCodeApproval),
            ReleaseChangeReasonTypes = NormalizeReleaseChangeReasonTypes(input.ReleaseChangeReasonTypes)
        });
        ValidateCheckoutSettings(settingsInput);
        ValidateBomPropertyMappings(settingsInput);
        var settings = await repository.UpdateSystemSettingsAsync(settingsInput, cancellationToken);
        await AuditAsync(actor, "system.storage.update", nameof(PdmSystemSettings), "storage", $"图档根目录：{settings.VaultRoot}；发包根目录：{settings.ReleaseRoot}；料品资料根目录：{settings.MaterialAttachmentRoot}", cancellationToken);
        await AuditAsync(actor, "system.checkout-policy.update", nameof(PdmSystemSettings), "checkout-policy", $"心跳{settings.CheckoutHeartbeatSeconds}秒；离线宽限{settings.CheckoutOfflineGraceMinutes}分钟；超时{settings.CheckoutOverdueHours}小时；强制释放{settings.CheckoutForceReleaseHours}小时", cancellationToken);
        await AuditAsync(actor, "system.bom-property-mapping.update", nameof(PdmSystemSettings), "bom-property-mapping", $"PLM属性{settings.BomPropertyMappings.Count}项；SolidWorks映射{settings.BomPropertyMappings.Count(item => item.MappingEditable)}项", cancellationToken);
        await AuditAsync(actor, "system.bom-validation.update", nameof(PdmSystemSettings), "bom-validation", $"标准件{settings.ValidationRules.Standard.Count}项；非标件{settings.ValidationRules.NonStandard.Count}项；电气件{settings.ValidationRules.Electrical.Count}项", cancellationToken);
        await AuditAsync(actor, "system.approval-workflows.update", nameof(PdmSystemSettings), "approval-workflows", $"机械模板v{settings.ApprovalWorkflows.Mechanical.Version}；电气模板v{settings.ApprovalWorkflows.Electrical.Version}；紧急代批角色{settings.ApprovalWorkflows.EmergencySubstituteRoleCode}", cancellationToken);
        await AuditAsync(actor, "system.material-code-approval.update", nameof(PdmSystemSettings), "material-code-approval", string.Join('、', settings.MaterialCodeApproval.ApproverRoleCodes), cancellationToken);
        await AuditAsync(actor, "system.release-change-reasons.update", nameof(PdmSystemSettings), "release-change-reasons", string.Join('、', settings.ReleaseChangeReasonTypes), cancellationToken);
        return settings;
    }

    private static IReadOnlyList<string> NormalizeReleaseChangeReasonTypes(IReadOnlyList<string>? input)
    {
        var values = (input ?? PdmSystemSettings.DefaultReleaseChangeReasonTypes)
            .Select(value => value?.Trim() ?? string.Empty)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (values.Length == 0) throw new PdmRuleException("至少配置一种发布变更原因。");
        if (values.Any(value => value.Length > 50 || value.Contains('；')))
            throw new PdmRuleException("发布变更原因不能超过50个字符且不能包含分号。");
        return values;
    }

    private static MaterialCodeApprovalSettings NormalizeMaterialCodeApproval(MaterialCodeApprovalSettings? input)
    {
        var roles = (input?.ApproverRoleCodes ?? MaterialCodeApprovalSettings.Default.ApproverRoleCodes)
            .Select(value => value?.Trim() ?? string.Empty).Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roles.Length == 0) throw new PdmRuleException("料号审批至少需要一个标准化角色。");
        return new MaterialCodeApprovalSettings(Math.Max(1, input?.Version ?? 1), roles);
    }

    private static string NormalizeScopedReleaseDescription(string? input, ReleaseScope scope, IReadOnlyList<string> configuredReasonTypes)
    {
        var value = input?.Trim() ?? string.Empty;
        if (scope is ReleaseScope.StandardSupplement or ReleaseScope.ElectricalSupplement)
        {
            var selected = value.Split('；', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (selected.Length == 0) throw new PdmRuleException("请至少选择一种变更原因。");
            var invalid = selected.FirstOrDefault(reason => !configuredReasonTypes.Contains(reason, StringComparer.OrdinalIgnoreCase));
            if (invalid is not null) throw new PdmRuleException($"变更原因“{invalid}”不在系统配置中，请刷新后重试。");
            return string.Join('；', selected);
        }
        if (value.Length > 500) throw new PdmRuleException("备注不能超过500个字符。");
        return value;
    }

    private static ReleaseApprovalSettings NormalizeApprovalWorkflows(ReleaseApprovalSettings? input, ReleaseApprovalSettings current)
    {
        current = ReleaseApprovalSettings.UseOrganizationHierarchy(current);
        input = ReleaseApprovalSettings.UseOrganizationHierarchy(input ?? current);
        ApprovalWorkflowTemplate Normalize(
            ApprovalWorkflowTemplate template,
            ApprovalWorkflowTemplate currentTemplate,
            string code,
            string name,
            ApprovalStage[] stages,
            ApprovalAssigneeSource[] sources)
        {
            if (template.Steps.Count != stages.Length)
                throw new PdmRuleException($"{name}必须包含{stages.Length}个审批节点。");
            var steps = template.Steps.Select((step, index) =>
            {
                if (step.Stage != stages[index] || step.AssigneeSource != sources[index])
                    throw new PdmRuleException($"{name}第{index + 1}个节点的岗位来源不允许修改。");
                return step with { Name = RequiredComment(step.Name, "节点名称"), FixedAssignee = null };
            }).ToArray();
            var changed = steps.Where((step, index) => step != currentTemplate.Steps[index]).Any();
            return new ApprovalWorkflowTemplate(code, name, changed ? currentTemplate.Version + 1 : currentTemplate.Version, steps);
        }

        return new ReleaseApprovalSettings(
            Normalize(input.Mechanical, current.Mechanical, "mechanical-release", "机械发布审批",
                [ApprovalStage.MechanicalEngineer, ApprovalStage.MainDesigner, ApprovalStage.MechanicalSupervisor],
                [ApprovalAssigneeSource.Submitter, ApprovalAssigneeSource.ProjectDesignLead, ApprovalAssigneeSource.PrimaryUnitManager]),
            Normalize(input.Electrical, current.Electrical, "electrical-release", "电气发布审批",
                [ApprovalStage.HardwareEngineer, ApprovalStage.HardwareSupervisor, ApprovalStage.StandardizationSupervisor],
                [ApprovalAssigneeSource.Submitter, ApprovalAssigneeSource.PrimaryUnitManager, ApprovalAssigneeSource.ParentUnitManager]),
            UserRole.BusinessUnitManager.ToString());
    }

    private static BomValidationRules NormalizeBomValidationRules(BomValidationRules? input)
    {
        input ??= BomValidationRules.Default;
        IReadOnlyList<string> Normalize(IReadOnlyList<string> fields, string label, bool requireMaterialCode)
        {
            var normalized = BomValidationFieldCatalog.Normalize(fields);
            var unknown = normalized.FirstOrDefault(field => !BomValidationFieldCatalog.AllFields.Contains(field, StringComparer.OrdinalIgnoreCase));
            if (unknown is not null) throw new PdmRuleException($"{label}BOM包含未知校验字段：{unknown}。");
            var requiredCore = requireMaterialCode
                ? BomValidationFieldCatalog.CoreFields
                : BomValidationFieldCatalog.CoreFields.Where(field => !field.Equals(BomValidationFieldCatalog.DrawingNumber, StringComparison.OrdinalIgnoreCase));
            var missingCore = requiredCore.Where(field => !normalized.Contains(field, StringComparer.OrdinalIgnoreCase)).ToArray();
            if (missingCore.Length > 0)
                throw new PdmRuleException($"{label}BOM不能取消系统基础必填项：{string.Join('、', missingCore.Select(BomValidationFieldCatalog.Label))}。");
            return normalized;
        }

        return new(
            Normalize(input.Standard, "标准件", true),
            Normalize(input.NonStandard, "非标件", false),
            Normalize(input.Electrical, "电气件", true));
    }

    private static void ValidateBomPropertyMappings(PdmSystemSettings settings)
    {
        var mappings = settings.BomPropertyMappings.Where(mapping => mapping.MappingEditable).ToArray();
        if (mappings.Any(mapping =>
                string.IsNullOrWhiteSpace(mapping.PdmPropertyKey)
                || mapping.PdmPropertyKey.Trim().Length > 100
                || string.IsNullOrWhiteSpace(mapping.PdmPropertyName)
                || mapping.PdmPropertyName.Trim().Length > 100
                || string.IsNullOrWhiteSpace(mapping.SolidWorksProperty)
                || mapping.SolidWorksProperty.Trim().Length > 100))
            throw new PdmRuleException("SolidWorks属性名称不能为空且不能超过100个字符。");
    }

    public async Task<RolePermissionDirectory> UpdateRolePermissionsAsync(string targetRoleCode, IReadOnlyList<string> permissionCodes, string actor, UserRole actorRole, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, actorRole, PermissionCodes.RoleSettingsEdit, cancellationToken);
        var unknown = permissionCodes.Where(code => !RolePermissionCatalog.IsKnown(code)).Distinct(StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0) throw new PdmRuleException($"包含未登记的权限代码：{string.Join('、', unknown)}。");
        var directory = await repository.SetRolePermissionsAsync(targetRoleCode, permissionCodes, cancellationToken);
        var saved = directory.Roles.Single(item => string.Equals(item.Role, targetRoleCode, StringComparison.OrdinalIgnoreCase));
        await AuditAsync(actor, "role.permissions.update", nameof(RoleDefinition), targetRoleCode, $"{saved.Name} · {saved.Permissions.Count}项权限", cancellationToken);
        return directory;
    }

    public async Task<RolePermissionDirectory> CreateRoleAsync(string name, string description, string sourceRoleCode, string actor, UserRole actorRole, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, actorRole, PermissionCodes.RoleSettingsEdit, cancellationToken);
        name = name?.Trim() ?? string.Empty;
        description = description?.Trim() ?? string.Empty;
        sourceRoleCode = sourceRoleCode?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 80) throw new PdmRuleException("角色名称不能为空且不能超过80个字符。");
        if (description.Length > 300) throw new PdmRuleException("角色说明不能超过300个字符。");
        if (string.IsNullOrWhiteSpace(sourceRoleCode)) throw new PdmRuleException("请选择复制来源角色。");
        var directory = await repository.CreateRoleAsync(name, description, sourceRoleCode, cancellationToken);
        var saved = directory.Roles.Single(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase) && !item.IsSystem);
        await AuditAsync(actor, "role.create", nameof(RoleDefinition), saved.Role, $"{saved.Name} · 复制自{sourceRoleCode}", cancellationToken);
        return directory;
    }

    public async Task<RolePermissionDirectory> DeleteRoleAsync(string roleCode, string actor, UserRole actorRole, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, actorRole, PermissionCodes.RoleSettingsEdit, cancellationToken);
        var directory = await repository.GetRolePermissionDirectoryAsync(cancellationToken);
        var role = directory.Roles.SingleOrDefault(item => string.Equals(item.Role, roleCode, StringComparison.OrdinalIgnoreCase))
            ?? throw new PdmNotFoundException("角色不存在。");
        var saved = await repository.DeleteRoleAsync(role.Role, cancellationToken);
        await AuditAsync(actor, "role.delete", nameof(RoleDefinition), role.Role, role.Name, cancellationToken);
        return saved;
    }

    public async Task<UserAccount> CreateManagedUserAsync(CreateManagedUserCommand command, string actor, UserRole actorRole, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, actorRole, PermissionCodes.OrganizationSettingsManage, cancellationToken);
        await RequirePermissionAsync(actor, actorRole, PermissionCodes.RoleSettingsEdit, cancellationToken);
        var username = NormalizeUsername(command.Username);
        var displayName = NormalizeDisplayName(command.DisplayName);
        if (await repository.FindUserAsync(username, cancellationToken) is not null) throw new PdmConflictException("用户名已经存在。");
        var targetRoles = await FindRolesAsync(command.RoleCodes ?? [command.RoleCode], cancellationToken);
        var targetRole = targetRoles[0];
        if (targetRoles.Any(role => IsPlatformManagedRole(role.Role)) && TenantContext.Current?.IsPlatformAdministrator != true)
            throw new UnauthorizedAccessException("只有平台级账号可以创建平台管理员或开发者。");
        var directory = await repository.GetOrganizationDirectoryAsync(cancellationToken);
        var companyId = ResolveManagedCompany(command.CompanyId, directory);
        var accessibleCompanyIds = NormalizeAccessibleCompanies(command.AccessibleCompanyIds, companyId, directory);
        if ((command.CrossCompanyView || accessibleCompanyIds.Length > 0) && TenantContext.Current?.IsPlatformAdministrator != true)
            throw new UnauthorizedAccessException("只有平台管理员可以配置跨公司权限。");
        var crossCompanyView = command.CrossCompanyView && accessibleCompanyIds.Length > 0;
        var roleCodes = targetRoles.Select(role => role.Role).ToArray();
        var user = new UserAccount(Guid.NewGuid(), username, displayName, command.PasswordHash, targetRole.BaseRole, command.IsActive, RoleCode: targetRole.Role, CompanyId: companyId, CrossCompanyView: crossCompanyView, RoleCodes: roleCodes);
        await repository.CreateUserAsync(user, cancellationToken);
        await repository.SetUserCompanyScopeAsync(username, companyId, crossCompanyView, accessibleCompanyIds, actor, cancellationToken);
        await AuditAsync(actor, "user.create", nameof(UserAccount), username, $"{displayName} · {string.Join('、', targetRoles.Select(role => role.Name))} · {(command.IsActive ? "启用" : "停用")}", cancellationToken);
        return await repository.FindUserAsync(username, cancellationToken) ?? user;
    }

    public async Task<UserAccount> UpdateManagedUserAsync(UpdateManagedUserCommand command, string actor, UserRole actorRole, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, actorRole, PermissionCodes.OrganizationSettingsManage, cancellationToken);
        await RequirePermissionAsync(actor, actorRole, PermissionCodes.RoleSettingsEdit, cancellationToken);
        var username = NormalizeUsername(command.Username);
        var displayName = NormalizeDisplayName(command.DisplayName);
        var current = await repository.FindUserAsync(username, cancellationToken) ?? throw new PdmNotFoundException("用户不存在。");
        var targetRoles = await FindRolesAsync(command.RoleCodes ?? [command.RoleCode], cancellationToken);
        var targetRole = targetRoles[0];
        var roleCodes = targetRoles.Select(role => role.Role).ToArray();
        if (targetRoles.Any(role => IsPlatformManagedRole(role.Role)) && TenantContext.Current?.IsPlatformAdministrator != true)
            throw new UnauthorizedAccessException("只有平台级账号可以分配平台管理员或开发者角色。");
        if (string.Equals(actor, username, StringComparison.OrdinalIgnoreCase) && (!command.IsActive || !RoleSetsEqual(roleCodes, current.EffectiveRoleCodes)))
            throw new PdmRuleException("不能停用当前登录账号或修改其系统角色。");
        if (current.Role == UserRole.Administrator && current.IsActive && (targetRole.BaseRole != UserRole.Administrator || !command.IsActive))
        {
            var activeAdministrators = (await repository.ListUsersAsync(cancellationToken)).Count(user => user.Role == UserRole.Administrator && user.IsActive);
            if (activeAdministrators <= 1) throw new PdmRuleException("系统至少需要保留一个启用的管理员账号。");
        }
        var directory = await repository.GetOrganizationDirectoryAsync(cancellationToken);
        var companyId = ResolveManagedCompany(command.CompanyId == Guid.Empty ? current.CompanyId ?? Guid.Empty : command.CompanyId, directory);
        var accessibleCompanyIds = NormalizeAccessibleCompanies(command.AccessibleCompanyIds, companyId, directory);
        if ((command.CrossCompanyView || accessibleCompanyIds.Length > 0) && TenantContext.Current?.IsPlatformAdministrator != true)
            throw new UnauthorizedAccessException("只有平台管理员可以配置跨公司权限。");
        var crossCompanyView = command.CrossCompanyView && accessibleCompanyIds.Length > 0;
        var saved = await repository.UpdateUserAsync(username, displayName, targetRole.BaseRole, targetRole.Role, roleCodes, command.IsActive, cancellationToken);
        await repository.SetUserCompanyScopeAsync(username, companyId, crossCompanyView, accessibleCompanyIds, actor, cancellationToken);
        saved = await repository.FindUserAsync(username, cancellationToken) ?? saved;
        await AuditAsync(actor, "user.update", nameof(UserAccount), username, $"{displayName} · {string.Join('、', targetRoles.Select(role => role.Name))} · {(command.IsActive ? "启用" : "停用")}", cancellationToken);
        return saved;
    }

    public async Task<UserAccount> ResetManagedUserPasswordAsync(string username, string passwordHash, string actor, UserRole actorRole, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, actorRole, PermissionCodes.OrganizationSettingsManage, cancellationToken);
        username = NormalizeUsername(username);
        if (string.Equals(actor, username, StringComparison.OrdinalIgnoreCase)) throw new PdmRuleException("请在个人设置中修改当前账号密码。");
        var saved = await repository.UpdateUserPasswordAsync(username, passwordHash, cancellationToken);
        await AuditAsync(actor, "user.password.reset", nameof(UserAccount), username, "管理员重置为初始密码", cancellationToken);
        return saved;
    }

    public async Task<ProjectOrganization> SaveProjectOrganizationAsync(SaveProjectOrganizationCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.OrganizationSettingsManage, cancellationToken);
        if (command.Id is Guid organizationId && TenantContext.Current?.IsPlatformAdministrator != true) RequirePrimaryCompany(organizationId);
        if (command.Id is null && TenantContext.Current?.IsPlatformAdministrator != true)
            throw new UnauthorizedAccessException("只有平台管理员可以创建公司。");
        var name = command.Name?.Trim() ?? string.Empty;
        var projectCode = command.ProjectCompanyCode?.Trim().ToUpperInvariant() ?? string.Empty;
        var modelCode = command.ModelCompanyCode?.Trim().ToUpperInvariant() ?? string.Empty;
        if (name.Length is < 1 or > 200) throw new PdmRuleException("公司名称不能为空且不能超过200个字符。");
        if (projectCode.Length != 1 || !char.IsLetterOrDigit(projectCode[0])) throw new PdmRuleException("项目号公司代码必须是1位字母或数字。");
        if (modelCode.Length is < 1 or > 8 || modelCode.Any(character => !char.IsLetterOrDigit(character)))
            throw new PdmRuleException("设备型号公司代码必须是1到8位字母或数字。");
        var saved = await repository.SaveProjectOrganizationAsync(command with { Name = name, ProjectCompanyCode = projectCode, ModelCompanyCode = modelCode }, cancellationToken);
        await AuditAsync(actor, command.Id is null ? "organization.create" : "organization.update", nameof(ProjectOrganization), saved.Id.ToString(), $"{saved.Name} · {saved.ProjectCompanyCode}/{saved.ModelCompanyCode} · {(saved.IsActive ? "启用" : "停用")}", cancellationToken);
        return saved;
    }

    public async Task<OrganizationUnit> SaveOrganizationUnitAsync(SaveOrganizationUnitCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.OrganizationSettingsManage, cancellationToken);
        if (TenantContext.Current?.IsPlatformAdministrator != true) RequirePrimaryCompany(command.OrganizationId);
        var directory = await repository.GetOrganizationDirectoryAsync(cancellationToken);
        if (!directory.Organizations.Any(item => item.Id == command.OrganizationId)) throw new PdmRuleException("所属公司不存在。");
        var code = command.Code?.Trim().ToUpperInvariant() ?? string.Empty;
        var name = command.Name?.Trim() ?? string.Empty;
        if (code.Length is < 1 or > 40 || code.Any(character => !char.IsLetterOrDigit(character) && character is not ('-' or '_')))
            throw new PdmRuleException("组织编码只能包含字母、数字、短横线和下划线，且不能超过40位。");
        if (name.Length is < 1 or > 160) throw new PdmRuleException("组织名称不能为空且不能超过160个字符。");
        if (command.CanManufacture && command.Kind != OrganizationUnitKind.BusinessDivision)
            throw new PdmRuleException("只有公司直属部门可以设为制造部门。");
        if (command.ParentUnitId is not null)
        {
            var parent = directory.Units.SingleOrDefault(item => item.Id == command.ParentUnitId);
            if (parent is null || parent.OrganizationId != command.OrganizationId) throw new PdmRuleException("上级组织必须属于同一公司。");
            if (command.Kind == OrganizationUnitKind.BusinessDivision) throw new PdmRuleException("可承接项目的部门必须直接隶属于公司。");
            if (command.Id is not null && IsUnitWithin(directory.Units, parent.Id, command.Id.Value)) throw new PdmRuleException("上级组织不能选择当前组织自身或其下级。");
            var proposedDepth = GetOrganizationUnitDepth(directory.Units, parent.Id) + 1;
            var subtreeHeight = command.Id is null ? 1 : GetOrganizationSubtreeHeight(directory.Units, command.Id.Value);
            if (proposedDepth + subtreeHeight - 1 > 10) throw new PdmRuleException("公司下的组织层级不能超过10级。");
        }
        else if (command.Kind != OrganizationUnitKind.BusinessDivision)
        {
            throw new PdmRuleException("部门或团队必须选择上级组织。");
        }
        var saved = await repository.SaveOrganizationUnitAsync(command with { Code = code, Name = name }, cancellationToken);
        await AuditAsync(actor, command.Id is null ? "organization-unit.create" : "organization-unit.update", nameof(OrganizationUnit), saved.Id.ToString(), $"{saved.Code} · {saved.Name} · {saved.Kind}", cancellationToken);
        return saved;
    }

    public async Task<OrganizationDirectory> SetOrganizationMembershipsAsync(string username, IReadOnlyList<Guid> unitIds, Guid primaryUnitId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.OrganizationSettingsManage, cancellationToken);
        username = username?.Trim() ?? string.Empty;
        var distinctUnitIds = unitIds.Distinct().ToArray();
        if (distinctUnitIds.Length == 0 || !distinctUnitIds.Contains(primaryUnitId)) throw new PdmRuleException("人员至少需要一个所属组织，且主组织必须包含在所选组织中。");
        var directory = await repository.GetOrganizationDirectoryAsync(cancellationToken);
        if (!directory.Users.Any(user => user.IsActive && string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase)))
            throw new PdmRuleException("账号不存在或已停用。");
        var units = directory.Units.Where(unit => distinctUnitIds.Contains(unit.Id) && unit.IsActive).ToArray();
        if (units.Length != distinctUnitIds.Length || units.Select(unit => unit.OrganizationId).Distinct().Count() != 1)
            throw new PdmRuleException("人员所属组织必须启用并且属于同一公司。");
        var user = await repository.FindUserAsync(username, cancellationToken) ?? throw new PdmNotFoundException("用户不存在。");
        if (user.CompanyId is Guid companyId && companyId != units[0].OrganizationId) throw new PdmRuleException("组织关系必须属于用户的主公司。");
        if (TenantContext.Current?.IsPlatformAdministrator != true) RequirePrimaryCompany(units[0].OrganizationId);
        var saved = await repository.SetOrganizationMembershipsAsync(username, distinctUnitIds, primaryUnitId, cancellationToken);
        await AuditAsync(actor, "organization.memberships.update", nameof(OrganizationMembership), username, $"主组织：{primaryUnitId}；共{distinctUnitIds.Length}个组织", cancellationToken);
        return saved;
    }

    public async Task<OrganizationDirectory> SetOrganizationUnitManagersAsync(Guid unitId, string primaryManager, IReadOnlyList<string> collaborativeManagers, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.OrganizationSettingsManage, cancellationToken);
        primaryManager = primaryManager?.Trim() ?? string.Empty;
        var collaborators = NormalizeUsers(collaborativeManagers).Where(username => !string.Equals(username, primaryManager, StringComparison.OrdinalIgnoreCase)).ToArray();
        var directory = await repository.GetOrganizationDirectoryAsync(cancellationToken);
        var unit = directory.Units.SingleOrDefault(item => item.Id == unitId && item.IsActive)
            ?? throw new PdmRuleException("只能为启用的组织配置负责人。");
        if (TenantContext.Current?.IsPlatformAdministrator != true) RequirePrimaryCompany(unit.OrganizationId);
        if (string.IsNullOrWhiteSpace(primaryManager))
        {
            if (collaborators.Length > 0) throw new PdmRuleException("清除主负责人时不能保留协同负责人。");
            var cleared = await repository.SetOrganizationUnitManagersAsync(unitId, string.Empty, Array.Empty<string>(), cancellationToken);
            await AuditAsync(actor, "organization.managers.clear", nameof(OrganizationUnitManagers), unitId.ToString(), "已清除部门负责人", cancellationToken);
            return cleared;
        }
        var candidates = new[] { primaryManager }.Concat(collaborators).ToArray();
        if (candidates.Any(username => !IsActiveUserAvailableToOrganization(directory, username, unit.OrganizationId)))
            throw new PdmRuleException("部门负责人必须是主公司为当前公司的启用账号。");
        var saved = await repository.SetOrganizationUnitManagersAsync(unitId, primaryManager, collaborators, cancellationToken);
        await AuditAsync(actor, "organization.managers.update", nameof(OrganizationUnitManagers), unitId.ToString(), $"主负责人：{primaryManager}；协同：{string.Join('、', collaborators)}", cancellationToken);
        return saved;
    }

    public async Task<Project> SetProjectExecutionUnitAsync(Guid projectId, Guid executionUnitId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProjectExecutionAssign, cancellationToken);
        var project = await repository.FindProjectAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目不存在。");
        if (project.ParentProjectId is not null) throw new PdmRuleException("执行事业部只能在主项目上配置。");
        var directory = await repository.GetOrganizationDirectoryAsync(cancellationToken);
        var unit = directory.Units.SingleOrDefault(item => item.Id == executionUnitId && item.IsActive
            && item.Kind == OrganizationUnitKind.BusinessDivision && item.CanManufacture)
            ?? throw new PdmRuleException("承接部门不存在、未启用或未设为制造部门。");
        if (project.OrganizationId != unit.OrganizationId) throw new PdmRuleException("执行事业部必须属于项目公司。");
        if (role != UserRole.Administrator)
        {
            if (!directory.Users.Any(user => string.Equals(user.Username, actor, StringComparison.OrdinalIgnoreCase)
                    && user.CompanyId == unit.OrganizationId))
                throw new UnauthorizedAccessException("计划管理只能分配本人所属公司的项目。");
        }
        var saved = await repository.SetProjectExecutionUnitAsync(projectId, executionUnitId, actor, cancellationToken);
        await AuditAsync(actor, "project.execution-unit.update", nameof(Project), project.Id.ToString(), $"{project.Code} · {unit.Name}；原项目分工已清空", cancellationToken);
        return saved;
    }

    public async Task<Project> UpdateProjectDetailsAsync(Guid projectId, UpdateProjectDetailsCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProjectEdit, cancellationToken);
        if (!await repository.HasProjectReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前账号无权编辑该项目。");
        var project = await repository.FindProjectAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目不存在。");
        ValidateProjectDetails(command.Name, command.ProjectAlias, command.Quantity);
        if (command.SignedDate == default) throw new PdmRuleException("订单日期不能为空。");

        if (project.ParentProjectId is null)
        {
            if (command.OrganizationId is null || string.IsNullOrWhiteSpace(command.ProjectTypeCode)
                || command.EquipmentTypeCode is null)
                throw new PdmRuleException("所属公司、项目类型和设备类型不能为空。");

            var options = await repository.GetProjectNumberingOptionsAsync(cancellationToken);
            if (!options.Organizations.Any(item => item.Id == command.OrganizationId && item.IsActive))
                throw new PdmRuleException("所选组织不存在或已停用。");
            if (!options.ProjectTypes.Any(item => string.Equals(item.Code, command.ProjectTypeCode, StringComparison.OrdinalIgnoreCase) && item.IsActive))
                throw new PdmRuleException("所选项目类型不存在或已停用。");
            if (!options.EquipmentTypes.Any(item => item.Code == command.EquipmentTypeCode && item.IsActive))
                throw new PdmRuleException("所选设备类型不存在或已停用。");
            var customer = command.CustomerId is null ? null : await repository.FindCustomerAsync(command.CustomerId.Value, cancellationToken);
            if (command.CustomerId is not null && (customer is null || !customer.IsActive || !string.Equals(customer.SourceSystem, "u9c", StringComparison.OrdinalIgnoreCase)))
                throw new PdmRuleException("所选客户不存在、已停用或不是从U9C同步的数据。");

            var codeWillChange = project.OrganizationId != command.OrganizationId
                || !string.Equals(project.ProjectTypeCode, command.ProjectTypeCode, StringComparison.OrdinalIgnoreCase);
            var numberingWillChange = codeWillChange
                || project.EquipmentTypeCode != command.EquipmentTypeCode
                || customer is not null && !string.Equals(project.CustomerCode, customer.Code, StringComparison.OrdinalIgnoreCase)
                || project.Quantity != command.Quantity;
            if (numberingWillChange)
            {
                var tree = (await repository.ListProjectsAsync(cancellationToken))
                    .Where(item => item.Id == project.Id || item.RootProjectId == project.Id)
                    .ToArray();
                if (codeWillChange)
                {
                    foreach (var item in tree)
                        if ((await repository.ListDocumentsAsync(item.Id, cancellationToken)).Count > 0)
                            throw new PdmConflictException("项目或子项目已有受控图档，不能修改所属公司或项目类型。请在图档入库前完成编号调整。");
                }
                if (command.Quantity < project.Quantity && (await repository.ListDocumentsAsync(project.Id, cancellationToken)).Count > 0)
                    throw new PdmConflictException("项目已有受控图档，不能减少设备数量和序列号。");
                foreach (var item in tree)
                    if ((await repository.ListReleasePackagesAsync(item.Id, cancellationToken)).Count > 0)
                        throw new PdmConflictException("项目或子项目已有审批或发布包，不能修改编号资料。");
            }
        }
        else if (command.Quantity != project.Quantity && (await repository.ListReleasePackagesAsync(project.Id, cancellationToken)).Count > 0)
        {
            throw new PdmConflictException("子项目已有审批或发布包，不能修改数量和序列号。");
        }
        else if (command.Quantity < project.Quantity && (await repository.ListDocumentsAsync(project.Id, cancellationToken)).Count > 0)
        {
            throw new PdmConflictException("子项目已有受控图档，不能减少设备数量和序列号。");
        }

        var normalized = command with
        {
            ProjectTypeCode = NullIfWhiteSpace(command.ProjectTypeCode)?.ToUpperInvariant(),
            Name = command.Name.Trim(),
            ProjectAlias = NullIfWhiteSpace(command.ProjectAlias)
        };
        var saved = await repository.UpdateProjectDetailsAsync(projectId, normalized, cancellationToken);
        await AuditAsync(actor, "project.details.update", nameof(Project), project.Id.ToString(),
            $"{project.Code} → {saved.Code}；{project.DeviceModel ?? "—"} → {saved.DeviceModel ?? "—"}；数量 {project.Quantity} → {saved.Quantity}；{saved.Name}", cancellationToken);
        return saved;
    }

    public async Task<Project> SetMainProjectStaffingAsync(Guid projectId, SetMainProjectStaffingCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProjectStaffingManage, cancellationToken);
        var project = await repository.FindProjectAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目不存在。");
        if (project.ParentProjectId is not null || project.ExecutionUnitId is null) throw new PdmRuleException("请先为主项目分配执行事业部。");
        var directory = await repository.GetOrganizationDirectoryAsync(cancellationToken);
        if (role != UserRole.Administrator && !directory.Managers.Any(item => item.UnitId == project.ExecutionUnitId
                && (string.Equals(item.PrimaryManager, actor, StringComparison.OrdinalIgnoreCase) || item.CollaborativeManagers.Contains(actor, StringComparer.OrdinalIgnoreCase))))
            throw new UnauthorizedAccessException("只有执行事业部负责人可以配置项目经理和设计负责人。");
        var primary = command.PrimaryProjectManager?.Trim() ?? string.Empty;
        var designLeads = NormalizeUsers(command.DesignLeads);
        var collaborators = NormalizeUsers(command.CollaborativeProjectManagers).Where(username => !string.Equals(username, primary, StringComparison.OrdinalIgnoreCase)).ToArray();
        var candidates = new[] { primary }.Concat(designLeads).Concat(collaborators).ToArray();
        if (string.IsNullOrWhiteSpace(primary) || designLeads.Length == 0
            || candidates.Any(username => !IsActiveMemberOfDivision(directory, username, project.ExecutionUnitId.Value)))
            throw new PdmRuleException("项目经理、协同项目经理和主设必须是执行事业部内的启用账号。");
        var saved = await repository.SetMainProjectStaffingAsync(projectId, new(primary, collaborators, designLeads), actor, cancellationToken);
        await AuditAsync(actor, "project.staffing.update", nameof(Project), project.Id.ToString(), $"项目经理：{primary}；主设：{string.Join('、', designLeads)}；协同：{string.Join('、', collaborators)}", cancellationToken);
        return saved;
    }

    public async Task<Project> SetChildProjectDesignersAsync(Guid projectId, IReadOnlyList<string> designers, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProjectDesignerAssign, cancellationToken);
        var project = await repository.FindProjectAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("项目不存在。");
        var root = project.ParentProjectId is null
            ? project
            : await repository.FindProjectAsync(project.RootProjectId ?? project.ParentProjectId.Value, cancellationToken) ?? throw new PdmNotFoundException("主项目不存在。");
        if (root.ExecutionUnitId is null) throw new PdmRuleException("请先为主项目分配执行事业部。");
        var directory = await repository.GetOrganizationDirectoryAsync(cancellationToken);
        var managesExecutionUnit = directory.Managers.Any(item => item.UnitId == root.ExecutionUnitId
            && (string.Equals(item.PrimaryManager, actor, StringComparison.OrdinalIgnoreCase) || item.CollaborativeManagers.Contains(actor, StringComparer.OrdinalIgnoreCase)));
        var user = directory.Users.FirstOrDefault(item => string.Equals(item.Username, actor, StringComparison.OrdinalIgnoreCase));
        var isMechanicalSupervisor = user is not null
            && user.EffectiveRoleCodes.Contains("MechanicalManager", StringComparer.OrdinalIgnoreCase)
            && IsActiveMemberOfDivision(directory, actor, root.ExecutionUnitId.Value);
        var isCurrentProjectManager = string.Equals(project.PrimaryProjectManager, actor, StringComparison.OrdinalIgnoreCase);
        var isMainDesigner = project.DesignLeads.Contains(actor, StringComparer.OrdinalIgnoreCase)
            || string.Equals(project.DesignLead, actor, StringComparison.OrdinalIgnoreCase);
        if (role != UserRole.Administrator && !managesExecutionUnit && !isMechanicalSupervisor && !isCurrentProjectManager && !isMainDesigner)
            throw new UnauthorizedAccessException("只有执行事业部负责人、机械主管、当前项目经理或主设可以分配执行工程师。");
        var normalized = NormalizeUsers(designers);
        if (root.OrganizationId is null || normalized.Any(username => !IsActiveTechnicalMemberOfOrganization(directory, username, root.OrganizationId.Value)))
            throw new PdmRuleException("工程师必须是项目公司内启用的机械、电气、硬件、标准化等技术岗位人员。");
        var saved = await repository.SetChildProjectDesignersAsync(projectId, normalized, actor, cancellationToken);
        await AuditAsync(actor, "project.designers.update", nameof(Project), project.Id.ToString(), $"{project.Code} · {(normalized.Length == 0 ? "清空" : string.Join('、', normalized))}", cancellationToken);
        return saved;
    }

    public async Task<Project> SetChildProjectManagerAsync(Guid projectId, string projectManager, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProjectDesignerAssign, cancellationToken);
        var child = await repository.FindProjectAsync(projectId, cancellationToken) ?? throw new PdmNotFoundException("子项目不存在。");
        if (child.ParentProjectId is null) throw new PdmRuleException("负责人只能配置到子项目。");
        var root = await repository.FindProjectAsync(child.RootProjectId ?? child.ParentProjectId.Value, cancellationToken) ?? throw new PdmNotFoundException("主项目不存在。");
        var directory = await repository.GetOrganizationDirectoryAsync(cancellationToken);
        var managesExecutionUnit = root.ExecutionUnitId is Guid executionUnitId && directory.Managers.Any(item => item.UnitId == executionUnitId
            && (string.Equals(item.PrimaryManager, actor, StringComparison.OrdinalIgnoreCase) || item.CollaborativeManagers.Contains(actor, StringComparer.OrdinalIgnoreCase)));
        var belongsToProjectStaffing = string.Equals(root.PrimaryProjectManager, actor, StringComparison.OrdinalIgnoreCase)
            || root.CollaborativeProjectManagers.Contains(actor, StringComparer.OrdinalIgnoreCase)
            || root.DesignLeads.Contains(actor, StringComparer.OrdinalIgnoreCase)
            || string.Equals(root.DesignLead, actor, StringComparison.OrdinalIgnoreCase);
        if (role != UserRole.Administrator && !managesExecutionUnit && !belongsToProjectStaffing)
            throw new UnauthorizedAccessException("只有执行事业部负责人、项目经理或主设可以配置子项目负责人。");
        var normalized = projectManager?.Trim() ?? string.Empty;
        var candidates = new[] { root.PrimaryProjectManager }.Concat(root.CollaborativeProjectManagers).Where(item => !string.IsNullOrWhiteSpace(item));
        if (string.IsNullOrWhiteSpace(normalized) || !candidates.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            throw new PdmRuleException("子项目负责人只能选择主项目经理或协同项目经理。");
        var saved = await repository.SetChildProjectManagerAsync(projectId, normalized, actor, cancellationToken);
        await AuditAsync(actor, "project.manager.update", nameof(Project), child.Id.ToString(), $"{child.Code} · {normalized}", cancellationToken);
        return saved;
    }

    public async Task<Project> DeleteProjectAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProjectDelete, cancellationToken);
        var project = await repository.FindProjectAsync(projectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。");
        await repository.DeleteProjectAsync(projectId, cancellationToken);
        await AuditAsync(actor, "project.delete", nameof(Project), project.Id.ToString(), $"{project.Code} · {project.Name}", cancellationToken);
        return project;
    }

    public async Task<Project> CreateProjectAsync(CreateProjectCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProjectCreate, cancellationToken);
        var code = command.Code?.Trim() ?? string.Empty;
        var name = command.Name?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(code)
            || string.IsNullOrWhiteSpace(name)
            || string.IsNullOrWhiteSpace(command.VaultLocation)
            || string.IsNullOrWhiteSpace(command.ReleaseLocation))
        {
            throw new PdmRuleException("项目编码、项目名称和存储位置不能为空。");
        }

        if (code.Length > 80 || name.Length > 200)
        {
            throw new PdmRuleException("项目编码或名称超过允许长度。");
        }

        if (code.Any(character => !char.IsLetterOrDigit(character) && character is not ('-' or '_' or '.')))
        {
            throw new PdmRuleException("项目编码只能包含字母、数字、短横线、下划线和点。");
        }

        var vaultLocation = StorageLocationPolicy.Normalize(command.VaultLocation);
        var releaseLocation = StorageLocationPolicy.Normalize(command.ReleaseLocation);
        if (string.Equals(vaultLocation, releaseLocation, StringComparison.OrdinalIgnoreCase))
        {
            throw new PdmRuleException("图档库与生产发包目录不能是同一位置。");
        }

        var project = await repository.CreateProjectAsync(
            new CreateProjectCommand(code, name, actor, vaultLocation, releaseLocation),
            actor,
            cancellationToken);
        await AuditAsync(actor, "project.create", nameof(Project), project.Id.ToString(), $"{project.Code} · {project.Name}", cancellationToken);
        return project;
    }

    public async Task<IReadOnlyList<DocumentRegistrationMatch>> PreflightDocumentRegistrationAsync(
        Guid projectId,
        IReadOnlyList<DocumentRegistrationCandidate> candidates,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.DocumentEdit, cancellationToken);
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有目标项目的图档权限。");
        await RequireProjectSubmissionAccessAsync(projectId, actor, role, "登记并提交图档", cancellationToken);
        if (candidates is null || candidates.Count == 0) return Array.Empty<DocumentRegistrationMatch>();
        if (candidates.Count > 2000) throw new PdmRuleException("单次最多检查2000个待入库图档。");

        var normalized = candidates.Select(candidate => candidate with
        {
            CandidateKey = candidate.CandidateKey?.Trim() ?? string.Empty,
            FileName = Path.GetFileName(candidate.FileName?.Trim() ?? string.Empty),
            SourceSha256 = NormalizeSha256(candidate.SourceSha256)
        }).ToArray();
        if (normalized.Any(candidate => candidate.CandidateKey.Length == 0 || candidate.FileName.Length == 0))
            throw new PdmRuleException("待入库图档标识和文件名不能为空。");
        if (normalized.Select(candidate => candidate.CandidateKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() != normalized.Length)
            throw new PdmRuleException("待入库图档标识不能重复。");

        var targetProject = await repository.FindProjectAsync(projectId, cancellationToken)
            ?? throw new PdmNotFoundException("目标项目不存在。");
        var targetProjects = new Dictionary<Guid, Project> { [targetProject.Id] = targetProject };
        var targetFingerprints = await repository.ListDocumentContentFingerprintsAsync([projectId], cancellationToken);
        var candidateFileNames = normalized.Select(candidate => candidate.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var relevantFingerprints = targetFingerprints
            .Where(fingerprint => candidateFileNames.Contains(fingerprint.Document.FileName))
            .ToArray();
        var results = new List<DocumentRegistrationMatch>(normalized.Length);
        foreach (var candidate in normalized)
        {
            var sameName = relevantFingerprints.FirstOrDefault(item =>
                item.Document.ProjectId == projectId
                && string.Equals(item.Document.FileName, candidate.FileName, StringComparison.OrdinalIgnoreCase));
            if (sameName is not null)
            {
                results.Add(ToRegistrationMatch(
                    candidate.CandidateKey,
                    string.Equals(sameName.SourceSha256, candidate.SourceSha256, StringComparison.OrdinalIgnoreCase)
                        ? DocumentRegistrationMatchKind.SameNameSameContent
                        : DocumentRegistrationMatchKind.SameNameDifferentContent,
                    sameName,
                    targetProjects));
                continue;
            }

            results.Add(new DocumentRegistrationMatch(candidate.CandidateKey, DocumentRegistrationMatchKind.New, null, null, null, null, null, null, null));
        }

        return results;
    }

    public async Task<PdmDocument> RegisterDocumentAsync(RegisterDocumentCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.DocumentEdit, cancellationToken);
        await RequireProjectSubmissionAccessAsync(command.ProjectId, actor, role, "登记并提交图档", cancellationToken);
        if (string.IsNullOrWhiteSpace(command.DrawingNumber)
            || string.IsNullOrWhiteSpace(command.Name)
            || string.IsNullOrWhiteSpace(command.FileName))
        {
            throw new PdmRuleException("图号、名称和文件名不能为空。");
        }

        if (command.Kind is not (DocumentKind.Assembly or DocumentKind.Part or DocumentKind.Drawing))
        {
            throw new PdmRuleException("只有SolidWorks装配体、零件和工程图可以登记。");
        }

        if (string.IsNullOrWhiteSpace(command.SourceSha256))
            throw new PdmRuleException("登记图档前必须提供源文件SHA-256指纹并完成重复预检。");
        var sourceSha256 = NormalizeSha256(command.SourceSha256);
        var duplicateReason = command.DuplicateReason?.Trim();
        if (command.AllowDuplicateContent && string.IsNullOrWhiteSpace(duplicateReason))
            throw new PdmRuleException("确认独立登记完全相同的图档时，必须填写原因。");
        if (duplicateReason?.Length > 500)
            throw new PdmRuleException("独立登记原因不能超过500个字符。");

        if (command.RelatedModelDocumentId.HasValue)
        {
            if (command.Kind != DocumentKind.Drawing)
                throw new PdmRuleException("只有工程图可以关联三维模型。");
            var relatedModel = await repository.FindDocumentAsync(command.RelatedModelDocumentId.Value, cancellationToken)
                ?? throw new PdmNotFoundException("关联的三维模型不存在。");
            if (relatedModel.ProjectId != command.ProjectId
                || relatedModel.Kind is not (DocumentKind.Assembly or DocumentKind.Part))
                throw new PdmRuleException("工程图只能关联同一项目中的装配体或零件。");
        }

        var project = await repository.FindProjectAsync(command.ProjectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。");
        if (!project.IsActive)
        {
            throw new PdmConflictException("项目已停用，不能登记图档。");
        }
        await repository.EnsureProjectFolderTreeAsync(project.Id, cancellationToken);
        var folders = await repository.ListProjectFoldersAsync(project.Id, actor, role, cancellationToken);
        var targetFolder = command.FolderId is null
            ? folders.FirstOrDefault(item => item.TargetProjectId == project.Id && item.TemplateKey == "mechanical.project")
            : folders.FirstOrDefault(item => item.Id == command.FolderId.Value);
        if (targetFolder is null || targetFolder.TargetProjectId != project.Id || targetFolder.Purpose != ProjectFolderPurpose.ProjectContainer)
            throw new PdmRuleException("图档只能登记到机械图纸或电气图纸下当前项目对应的目录。");
        if ((targetFolder.EffectiveAccess & FolderAccess.Upload) == 0)
            throw new UnauthorizedAccessException("当前用户没有向该目录登记图档的权限。");
        var normalized = command with
        {
            DrawingNumber = command.DrawingNumber.Trim(),
            Name = command.Name.Trim(),
            FileName = Path.GetFileName(command.FileName.Trim()),
            FolderId = targetFolder.Id,
            SourceSha256 = sourceSha256,
            DuplicateReason = duplicateReason
        };
        var document = await repository.RegisterDocumentAsync(normalized, actor, cancellationToken);
        await AuditAsync(actor, "document.register", nameof(PdmDocument), document.Id.ToString(), document.FileName, cancellationToken);
        if (normalized.AllowDuplicateContent)
            await AuditAsync(actor, "document.register.duplicate-content", nameof(PdmDocument), document.Id.ToString(), duplicateReason!, cancellationToken);
        return document;
    }

    public Task<PdmDocument> CheckoutAsync(Guid documentId, string actor, UserRole role, CancellationToken cancellationToken) =>
        CheckoutAsync(documentId, actor, role, Guid.NewGuid(), "legacy-client", cancellationToken);

    public async Task<PdmDocument> CheckoutAsync(Guid documentId, string actor, UserRole role, Guid sessionId, string machineName, CancellationToken cancellationToken, Guid? drawingReviewWritebackId = null)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.DocumentEdit, cancellationToken);
        await RequireDocumentAccessAsync(documentId, actor, role, FolderAccess.View | FolderAccess.Edit, cancellationToken);
        if (sessionId == Guid.Empty) throw new PdmRuleException("编辑会话编号不能为空。");
        if (string.IsNullOrWhiteSpace(machineName)) throw new PdmRuleException("客户端电脑名称不能为空。");
        var document = await repository.FindDocumentAsync(documentId, cancellationToken)
            ?? throw new PdmNotFoundException("图档不存在。 ");
        await RequireProjectSubmissionAccessAsync(document.ProjectId, actor, role, "获取编辑权限", cancellationToken);

        if (document.State == DocumentLifecycleState.InReview)
        {
            throw new PdmConflictException("图档正在审批，不能获取编辑权限。 ");
        }
        if (document.State == DocumentLifecycleState.Obsolete)
        {
            throw new PdmConflictException("图档已作废，不能获取编辑权限。 ");
        }
        await EnsureDrawingReviewEditAllowedAsync(documentId, drawingReviewWritebackId, "获取编辑权限", cancellationToken);

        var normalizedMachineName = machineName.Trim();
        var now = timeProvider.GetUtcNow();
        var sameUserAndMachine = document.CheckedOutBy is not null
            && string.Equals(document.CheckedOutBy, actor, StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(document.CheckoutMachine)
                || string.Equals(document.CheckoutMachine, normalizedMachineName, StringComparison.OrdinalIgnoreCase));
        var reclaimingLocalSession = sameUserAndMachine && document.CheckoutSessionId != sessionId;
        if (document.CheckedOutBy is not null && !sameUserAndMachine)
        {
            throw new PdmConflictException($"图档正在由{document.CheckedOutBy}在{document.CheckoutMachine ?? "其他会话"}编辑。 ");
        }

        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        var updated = await repository.CheckoutAsync(documentId, actor, sessionId, normalizedMachineName, now.AddMinutes(settings.CheckoutLeaseMinutes), drawingReviewWritebackId, cancellationToken);
        var action = reclaimingLocalSession ? "document.checkout.reclaim-local-session" : "document.checkout";
        var detail = reclaimingLocalSession
            ? $"{updated.Revision.Display}；旧会话{document.CheckoutSessionId}；新会话{sessionId}；电脑{normalizedMachineName}"
            : $"{updated.Revision.Display}；会话{sessionId}；电脑{normalizedMachineName}";
        await AuditAsync(actor, action, nameof(PdmDocument), documentId.ToString(), detail, cancellationToken);
        return updated;
    }

    public async Task<EditSessionHeartbeat> HeartbeatEditSessionAsync(Guid sessionId, string actor, UserRole role, string machineName, IReadOnlyList<Guid> documentIds, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.DocumentEdit, cancellationToken);
        if (sessionId == Guid.Empty) throw new PdmRuleException("编辑会话编号不能为空。");
        if (string.IsNullOrWhiteSpace(machineName)) throw new PdmRuleException("客户端电脑名称不能为空。");
        var ids = documentIds.Where(id => id != Guid.Empty).Distinct().Take(1000).ToArray();
        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var leaseExpiresAt = now.AddMinutes(settings.CheckoutLeaseMinutes);
        var active = await repository.HeartbeatCheckoutSessionAsync(sessionId, actor, machineName.Trim(), ids, leaseExpiresAt, cancellationToken);
        var activeSet = active.ToHashSet();
        return new(sessionId, now, leaseExpiresAt, active, ids.Where(id => !activeSet.Contains(id)).ToArray(), settings);
    }

    public async Task<DocumentCheckInResult> CheckInAsync(
        Guid documentId,
        string actor,
        UserRole role,
        StoredFile file,
        string changeNote,
        IReadOnlyDictionary<string, string?> properties,
        CadReferenceSnapshot snapshot,
        bool isProjectRoot,
        bool forceVersion,
        CancellationToken cancellationToken,
        string? drawingNumber = null,
        string? name = null,
        string? fileName = null,
        Guid? drawingReviewWritebackId = null)
    {
        var document = await repository.FindDocumentAsync(documentId, cancellationToken) ?? throw new PdmNotFoundException("图档不存在。 ");
        if (document.CheckoutSessionId is null) throw new PdmConflictException("当前编辑权限没有有效会话，请重新获取权限。");
        return await CheckInAsync(documentId, actor, role, document.CheckoutSessionId.Value, file, changeNote, properties, snapshot, isProjectRoot, forceVersion, cancellationToken, drawingNumber, name, fileName, drawingReviewWritebackId);
    }

    public async Task<DocumentCheckInResult> CheckInAsync(
        Guid documentId,
        string actor,
        UserRole role,
        Guid checkoutSessionId,
        StoredFile file,
        string changeNote,
        IReadOnlyDictionary<string, string?> properties,
        CadReferenceSnapshot snapshot,
        bool isProjectRoot,
        bool forceVersion,
        CancellationToken cancellationToken,
        string? drawingNumber = null,
        string? name = null,
        string? fileName = null,
        Guid? drawingReviewWritebackId = null)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.DocumentEdit, cancellationToken);
        await RequireDocumentAccessAsync(documentId, actor, role, FolderAccess.View | FolderAccess.Edit, cancellationToken);
        var document = await repository.FindDocumentAsync(documentId, cancellationToken)
            ?? throw new PdmNotFoundException("图档不存在。 ");
        await RequireProjectSubmissionAccessAsync(document.ProjectId, actor, role, "提交存档", cancellationToken);

        if (snapshot.ProjectId != document.ProjectId || snapshot.RootDocumentId != documentId)
        {
            throw new PdmRuleException("引用树快照必须属于当前项目和当前图档。");
        }

        if (!string.Equals(document.CheckedOutBy, actor, StringComparison.OrdinalIgnoreCase) || document.CheckoutSessionId != checkoutSessionId)
        {
            throw new PdmConflictException("编辑会话已经失效，不能提交存档。请另存本地修改或重新获取权限。 ");
        }
        await EnsureDrawingReviewEditAllowedAsync(documentId, drawingReviewWritebackId, "提交存档", cancellationToken);

        var normalizedChangeNote = changeNote?.Trim() ?? string.Empty;
        var normalizedDrawingNumber = drawingNumber?.Trim();
        var normalizedName = name?.Trim();
        var normalizedFileName = fileName?.Trim();
        if (drawingNumber is not null && string.IsNullOrWhiteSpace(normalizedDrawingNumber))
        {
            throw new PdmRuleException("图号不能为空。");
        }
        if (name is not null && string.IsNullOrWhiteSpace(normalizedName))
        {
            throw new PdmRuleException("图档名称不能为空。");
        }
        if (normalizedDrawingNumber?.Length > 160 || normalizedName?.Length > 300)
        {
            throw new PdmRuleException("图号或图档名称超过允许长度。");
        }
        if (fileName is not null
            && (string.IsNullOrWhiteSpace(normalizedFileName)
                || normalizedFileName.Length > 260
                || !string.Equals(Path.GetFileName(normalizedFileName), normalizedFileName, StringComparison.Ordinal)))
        {
            throw new PdmRuleException("文件名无效。");
        }

        if (snapshot.Root.HasBlockingIssue)
        {
            throw new PdmRuleException("设计树存在缺失引用，不能提交存档。 ");
        }

        var latestVersion = (await repository.ListDocumentVersionsAsync(documentId, cancellationToken)).FirstOrDefault();
        var authoritativeProperties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (latestVersion is null)
        {
            foreach (var property in properties ?? new Dictionary<string, string?>())
                authoritativeProperties[property.Key] = property.Value;
        }
        else
        {
            foreach (var property in latestVersion.PropertySnapshot)
                authoritativeProperties[property.Key] = property.Value;

            if (drawingReviewWritebackId.HasValue)
            {
                var writeback = await repository.FindCadPropertyWritebackAsync(drawingReviewWritebackId.Value, cancellationToken)
                    ?? throw new PdmNotFoundException("属性写回任务不存在。");
                if (writeback.SourceDocumentId != documentId || writeback.Status != CadPropertyWritebackStatus.InProgress)
                    throw new PdmConflictException("属性写回任务与当前图档或执行状态不匹配。");
                var prefix = string.IsNullOrWhiteSpace(writeback.SourceConfiguration)
                    ? "全局/"
                    : string.Concat("配置:", writeback.SourceConfiguration.Trim(), "/");
                foreach (var property in writeback.Properties)
                    authoritativeProperties[string.Concat(prefix, property.Key)] = property.Value;
            }

            foreach (var technicalName in new[] { "FileName", "Extension", "LastWriteTimeUtc", "SourceFileSha256" })
                if (properties?.TryGetValue(technicalName, out var technicalValue) == true)
                    authoritativeProperties[technicalName] = technicalValue;

            normalizedDrawingNumber = null;
            normalizedName = null;
        }

        var project = await repository.FindProjectAsync(document.ProjectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。");
        if (isProjectRoot)
        {
            var currentProjectRoot = await repository.GetLatestReferenceSnapshotAsync(document.ProjectId, cancellationToken);
            if (currentProjectRoot is not null && currentProjectRoot.RootDocumentId != documentId)
            {
                throw new PdmRuleException("所选图档不是项目根装配体，普通存档不能替换项目完整结构。");
            }
        }
        await fileStorage.VerifyStoredFileAsync(project, file, cancellationToken);
        var standard = await repository.GetBomAsync(document.ProjectId, BomKind.Standard, cancellationToken);
        var nonStandard = await repository.GetBomAsync(document.ProjectId, BomKind.NonStandard, cancellationToken);
        var legacyMechanical = await repository.GetBomAsync(document.ProjectId, BomKind.Mechanical, cancellationToken);
        var mechanical = standard.Concat(nonStandard).Concat(legacyMechanical).Where(item => !item.IsManuallyExcluded).ToArray();
        var electrical = await repository.GetBomAsync(document.ProjectId, BomKind.Electrical, cancellationToken);
        var result = await repository.CheckInVersionAsync(
            documentId,
            actor,
            checkoutSessionId,
            new DocumentVersionCommit(
                file,
                normalizedChangeNote,
                authoritativeProperties,
                snapshot,
                mechanical,
                electrical,
                IsProjectRoot: isProjectRoot,
                ForceVersion: forceVersion,
                DrawingNumber: normalizedDrawingNumber,
                Name: normalizedName,
                FileName: normalizedFileName),
            drawingReviewWritebackId,
            cancellationToken);
        if (result.VersionCreated && result.Version is not null)
        {
            await AuditAsync(actor, "document.checkin", nameof(DocumentVersion), result.Version.Id.ToString(), result.Version.Revision.Display, cancellationToken);
        }
        else
        {
            await AuditAsync(actor, "document.edit.complete-unchanged", nameof(PdmDocument), documentId.ToString(), result.Document.Revision.Display, cancellationToken);
        }
        if (!string.Equals(document.DrawingNumber, result.Document.DrawingNumber, StringComparison.Ordinal)
            || !string.Equals(document.Name, result.Document.Name, StringComparison.Ordinal))
        {
            await AuditAsync(
                actor,
                "document.identity.update",
                nameof(PdmDocument),
                documentId.ToString(),
                string.Concat(document.DrawingNumber, " / ", document.Name, " -> ", result.Document.DrawingNumber, " / ", result.Document.Name),
                cancellationToken);
        }
        return result;
    }

    private async Task EnsureDrawingReviewEditAllowedAsync(Guid documentId, Guid? drawingReviewWritebackId, string action, CancellationToken cancellationToken)
    {
        if (!await repository.IsDocumentUnderActiveDrawingReviewAsync(documentId, cancellationToken)) return;
        if (drawingReviewWritebackId.HasValue
            && await repository.IsActiveDrawingReviewWritebackAsync(documentId, drawingReviewWritebackId.Value, cancellationToken)) return;
        throw new PdmConflictException($"图档正在进行图纸审核，不能{action}。");
    }

    public async Task<PdmDocument> CompleteEditWithoutChangesAsync(Guid documentId, string actor, UserRole role, string sha256, CancellationToken cancellationToken)
    {
        var document = await repository.FindDocumentAsync(documentId, cancellationToken) ?? throw new PdmNotFoundException("图档不存在。");
        if (document.CheckoutSessionId is null) throw new PdmConflictException("当前编辑权限没有有效会话，请重新获取权限。");
        return await CompleteEditWithoutChangesAsync(documentId, actor, role, document.CheckoutSessionId.Value, sha256, cancellationToken);
    }

    public async Task<PdmDocument> CompleteEditWithoutChangesAsync(Guid documentId, string actor, UserRole role, Guid sessionId, string sha256, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.DocumentEdit, cancellationToken);
        await RequireDocumentAccessAsync(documentId, actor, role, FolderAccess.View | FolderAccess.Edit, cancellationToken);
        if (string.IsNullOrWhiteSpace(sha256))
        {
            throw new PdmRuleException("文件指纹不能为空。");
        }

        var document = await repository.CompleteEditWithoutChangesAsync(documentId, actor, sessionId, sha256.Trim(), cancellationToken);
        await AuditAsync(actor, "document.edit.complete-unchanged", nameof(PdmDocument), documentId.ToString(), document.Revision.Display, cancellationToken);
        return document;
    }

    public async Task<PdmDocument> DiscardCheckoutAsync(Guid documentId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var document = await repository.FindDocumentAsync(documentId, cancellationToken) ?? throw new PdmNotFoundException("图档不存在。");
        if (document.CheckoutSessionId is null) throw new PdmConflictException("当前编辑权限没有有效会话，请重新获取权限。");
        return await DiscardCheckoutAsync(documentId, actor, role, document.CheckoutSessionId.Value, cancellationToken);
    }

    public async Task<PdmDocument> DiscardCheckoutAsync(Guid documentId, string actor, UserRole role, Guid sessionId, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.DocumentEdit, cancellationToken);
        await RequireDocumentAccessAsync(documentId, actor, role, FolderAccess.View | FolderAccess.Edit, cancellationToken);
        var document = await repository.DiscardCheckoutAsync(documentId, actor, sessionId, cancellationToken);
        await AuditAsync(actor, "document.checkout.discard", nameof(PdmDocument), documentId.ToString(), document.Revision.Display, cancellationToken);
        return document;
    }

    public async Task<IReadOnlyList<EditLockSummary>> ListEditLocksAsync(string actor, UserRole role, CancellationToken cancellationToken)
    {
        var canRequestPermission = await repository.HasUserPermissionAsync(actor, role, PermissionCodes.DocumentLockRequestRelease, cancellationToken);
        var canForcePermission = await repository.HasUserPermissionAsync(actor, role, PermissionCodes.DocumentLockForceRelease, cancellationToken);
        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var result = new List<EditLockSummary>();
        foreach (var document in await repository.ListCheckedOutDocumentsAsync(cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(document.CheckedOutBy)) continue;
            var project = await repository.FindProjectAsync(document.ProjectId, cancellationToken);
            if (project is null) continue;
            var owned = string.Equals(document.CheckedOutBy, actor, StringComparison.OrdinalIgnoreCase);
            var canEditDocument = !owned && canRequestPermission
                && await repository.HasDocumentAccessAsync(document.Id, actor, role, FolderAccess.View | FolderAccess.Edit, cancellationToken);
            var lockManager = canForcePermission && IsProjectLockManager(project, actor, role);
            if (!owned && !canEditDocument && !lockManager) continue;

            var checkedOutAt = document.CheckedOutAt ?? document.UpdatedAt;
            var heartbeatAt = document.CheckoutLastHeartbeatAt ?? checkedOutAt;
            var leaseExpiresAt = document.CheckoutLeaseExpiresAt ?? heartbeatAt.AddMinutes(settings.CheckoutLeaseMinutes);
            var connectionState = now <= leaseExpiresAt
                ? EditLockConnectionState.Active
                : now <= heartbeatAt.AddMinutes(settings.CheckoutOfflineGraceMinutes)
                    ? EditLockConnectionState.OfflineGrace
                    : EditLockConnectionState.Offline;
            var attention = AttentionLevel(now - checkedOutAt, settings);
            result.Add(new(
                document.Id, document.ProjectId, project.Code, project.Name, document.DrawingNumber, document.Name, document.FileName,
                document.CheckedOutBy, checkedOutAt, document.CheckoutMachine, heartbeatAt, leaseExpiresAt, connectionState, attention,
                document.CheckoutReleaseRequestedBy, document.CheckoutReleaseRequestedAt, document.CheckoutReleaseRequestReason,
                owned, canEditDocument, lockManager && attention == EditLockAttentionLevel.Reclaimable));
        }
        return result.OrderByDescending(item => item.AttentionLevel).ThenBy(item => item.CheckedOutAt).ToArray();
    }

    public async Task<EditLockSummary> RequestEditLockReleaseAsync(Guid documentId, string actor, UserRole role, string reason, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.DocumentLockRequestRelease, cancellationToken);
        await RequireDocumentAccessAsync(documentId, actor, role, FolderAccess.View | FolderAccess.Edit, cancellationToken);
        var current = await repository.FindDocumentAsync(documentId, cancellationToken) ?? throw new PdmNotFoundException("图档不存在。");
        if (string.IsNullOrWhiteSpace(current.CheckedOutBy)) throw new PdmConflictException("图档当前没有编辑权限可申请释放。");
        if (string.Equals(current.CheckedOutBy, actor, StringComparison.OrdinalIgnoreCase)) throw new PdmRuleException("当前编辑权限属于本人，请在SolidWorks中提交存档或放弃编辑。");
        reason = RequiredReason(reason);
        await repository.RequestCheckoutReleaseAsync(documentId, actor, reason, cancellationToken);
        await AuditAsync(actor, "document.checkout.release-request", nameof(PdmDocument), documentId.ToString(), $"当前编辑人：{current.CheckedOutBy}；原因：{reason}", cancellationToken);
        return (await ListEditLocksAsync(actor, role, cancellationToken)).Single(item => item.DocumentId == documentId);
    }

    public async Task<PdmDocument> ForceReleaseEditLockAsync(Guid documentId, string actor, UserRole role, string reason, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.DocumentLockForceRelease, cancellationToken);
        var current = await repository.FindDocumentAsync(documentId, cancellationToken) ?? throw new PdmNotFoundException("图档不存在。");
        if (string.IsNullOrWhiteSpace(current.CheckedOutBy)) throw new PdmConflictException("图档当前没有编辑权限可释放。");
        var project = await repository.FindProjectAsync(current.ProjectId, cancellationToken) ?? throw new PdmNotFoundException("项目不存在。");
        if (!IsProjectLockManager(project, actor, role)) throw new UnauthorizedAccessException("只有项目经理、设计负责人或系统管理员可以强制释放本项目权限。");
        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        var checkedOutAt = current.CheckedOutAt ?? current.UpdatedAt;
        if (timeProvider.GetUtcNow() < checkedOutAt.AddHours(settings.CheckoutForceReleaseHours))
            throw new PdmRuleException($"编辑权限获取未满{settings.CheckoutForceReleaseHours}小时，只能先催办并申请释放。");
        reason = RequiredReason(reason);
        var priorOwner = current.CheckedOutBy;
        var priorSession = current.CheckoutSessionId;
        var updated = await repository.ForceReleaseCheckoutAsync(documentId, actor, reason, cancellationToken);
        await AuditAsync(actor, "document.checkout.force-release", nameof(PdmDocument), documentId.ToString(), $"原编辑人：{priorOwner}；原会话：{priorSession}；电脑：{current.CheckoutMachine ?? "未知"}；原因：{reason}", cancellationToken);
        return updated;
    }

    public async Task<(PdmDocument Document, DocumentVersion Version)> RestoreVersionAsync(
        Guid documentId,
        Guid sourceVersionId,
        string actor,
        UserRole role,
        string changeNote,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.DocumentEdit, cancellationToken);
        var document = await RequireDocumentAsync(documentId, cancellationToken);
        await RequireDocumentAccessAsync(documentId, actor, role, FolderAccess.View | FolderAccess.Edit, cancellationToken);
        var source = await repository.FindDocumentVersionAsync(documentId, sourceVersionId, cancellationToken)
            ?? throw new PdmNotFoundException("历史版本不存在。");
        var project = await repository.FindProjectAsync(document.ProjectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。");
        var restoredPath = Path.Combine(".versions", document.Id.ToString("N"), Guid.NewGuid().ToString("N"), document.FileName);
        var restoredFile = await fileStorage.CopyVersionAsync(
            project,
            new StoredFile(source.StorageRelativePath, source.FileLength, source.Sha256, source.CreatedAt),
            restoredPath,
            cancellationToken);
        var result = await repository.RestoreVersionAsync(documentId, sourceVersionId, actor, restoredFile, changeNote, cancellationToken);
        await AuditAsync(actor, "document.version.restore", nameof(DocumentVersion), sourceVersionId.ToString(), $"生成{result.Version.Revision.Display}", cancellationToken);
        return result;
    }

    public async Task<DocumentVersionComparison> CompareVersionsAsync(Guid documentId, Guid leftVersionId, Guid rightVersionId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProjectContentView, cancellationToken);
        await RequireDocumentReadAccessAsync(documentId, actor, role, cancellationToken);
        _ = await RequireDocumentAsync(documentId, cancellationToken);
        var left = await repository.FindDocumentVersionAsync(documentId, leftVersionId, cancellationToken)
            ?? throw new PdmNotFoundException("左侧历史版本不存在。");
        var right = await repository.FindDocumentVersionAsync(documentId, rightVersionId, cancellationToken)
            ?? throw new PdmNotFoundException("右侧历史版本不存在。");
        var comparison = DocumentVersionDiff.Compare(left, right);
        await AuditAsync(actor, "document.version.compare", nameof(PdmDocument), documentId.ToString(), $"{left.Revision.Display} -> {right.Revision.Display}", cancellationToken);
        return comparison;
    }

    public Task<DocumentVersion> PublishVersionAsync(Guid documentId, Guid sourceVersionId, Guid releasePackageId, Guid approvalTaskId, string actor, UserRole role, CancellationToken cancellationToken) =>
        throw new PdmRuleException("单图档直接发布已停用；请在发布包最终批准后由服务器统一生成STEP/PDF并形成正式版本。");

    public async Task AuditVersionReadAsync(Guid documentId, Guid versionId, string actor, UserRole role, string action, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ProjectContentView, cancellationToken);
        await RequireDocumentAccessAsync(documentId, actor, role,
            action == "document.version.download" ? FolderAccess.View | FolderAccess.Download : FolderAccess.View, cancellationToken);
        _ = await RequireDocumentAsync(documentId, cancellationToken);
        if (versionId != Guid.Empty)
        {
            _ = await repository.FindDocumentVersionAsync(documentId, versionId, cancellationToken)
                ?? throw new PdmNotFoundException("历史版本不存在。");
        }
        await AuditAsync(actor, action, nameof(DocumentVersion), versionId.ToString(), documentId.ToString(), cancellationToken);
    }

    public async Task<ControlledOpenManifest> CreateControlledOpenManifestAsync(
        Guid documentId,
        Guid? versionId,
        bool releasedOnly,
        bool forEdit,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        var rootDocument = await repository.FindDocumentAsync(documentId, cancellationToken)
            ?? throw new PdmNotFoundException("图档不存在。");
        var project = await repository.FindProjectAsync(rootDocument.ProjectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。");
        if (!await repository.HasProjectContentReadAccessAsync(project.Id, actor, role, cancellationToken))
        {
            throw new UnauthorizedAccessException("当前用户没有该项目目录下图档的对应操作权限。");
        }
        var projectFolders = await repository.ListProjectFoldersAsync(project.Id, actor, role, cancellationToken);
        var currentProjectDocuments = await repository.ListDocumentsAsync(project.Id, cancellationToken);
        var currentProjectDocumentsById = currentProjectDocuments.ToDictionary(document => document.Id);
        void RequireProjectDocumentAccess(PdmDocument document, FolderAccess requiredAccess)
        {
            var folder = document.FolderId is null
                ? projectFolders.FirstOrDefault(item => item.TargetProjectId == document.ProjectId && item.TemplateKey == "mechanical.project")
                : projectFolders.FirstOrDefault(item => item.Id == document.FolderId.Value);
            if (folder is null || (folder.EffectiveAccess & requiredAccess) != requiredAccess)
            {
                throw new UnauthorizedAccessException("当前用户没有该项目目录下图档的对应操作权限。");
            }
        }
        RequireProjectDocumentAccess(rootDocument, forEdit ? FolderAccess.View | FolderAccess.Edit : FolderAccess.View);
        if (forEdit)
        {
            await RequirePermissionAsync(actor, role, PermissionCodes.DocumentEdit, cancellationToken);
            if (releasedOnly || versionId.HasValue)
            {
                throw new PdmRuleException("编辑模式只能获取当前最新受控版本；历史版和正式版只能只读打开。");
            }
        }

        var projectVersions = await repository.ListProjectDocumentVersionsAsync(project.Id, cancellationToken);
        var versionsByDocument = projectVersions
            .GroupBy(version => version.DocumentId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<DocumentVersion>)group.OrderByDescending(version => version.CreatedAt).ToArray());
        var rootVersions = versionsByDocument.GetValueOrDefault(documentId) ?? Array.Empty<DocumentVersion>();
        var rootVersion = versionId.HasValue
            ? rootVersions.SingleOrDefault(item => item.Id == versionId.Value)
            : releasedOnly
                ? rootVersions.FirstOrDefault(item => item.Status == DocumentVersionStatus.Released)
                : rootVersions.FirstOrDefault(item => item.Revision.Display.Equals(rootDocument.Revision.Display, StringComparison.OrdinalIgnoreCase))
                    ?? rootVersions.FirstOrDefault();
        if (rootVersion is null)
        {
            throw new PdmNotFoundException(releasedOnly ? "该图档尚无正式发布版本。" : "该图档尚无可打开的受控版本。");
        }

        if (forEdit
            && rootDocument.CheckedOutBy is not null
            && !rootDocument.CheckedOutBy.Equals(actor, StringComparison.OrdinalIgnoreCase))
        {
            throw new PdmConflictException($"图档正在由{rootDocument.CheckedOutBy}编辑。");
        }

        var files = new List<ControlledOpenFile>();
        var warnings = new List<string>();
        var filesByDocument = new Dictionary<Guid, ControlledOpenFile>();
        var normalizedConflictDocumentIds = new HashSet<Guid>();
        var fileNames = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var allowCurrentVersionFallback = !versionId.HasValue && !releasedOnly;
        var authorizedDocumentIds = new HashSet<Guid> { documentId };
        async Task<IReadOnlyList<DocumentVersion>> ListOpenVersionsAsync(Guid targetDocumentId)
        {
            if (versionsByDocument.TryGetValue(targetDocumentId, out var cachedVersions))
            {
                return cachedVersions;
            }
            var loadedVersions = await repository.ListDocumentVersionsAsync(targetDocumentId, cancellationToken);
            versionsByDocument[targetDocumentId] = loadedVersions;
            return loadedVersions;
        }
        var nodes = FlattenOpenNodes(rootVersion.ReferenceSnapshot).ToArray();
        for (var index = 0; index < nodes.Length; index++)
        {
            var node = nodes[index];
            var isRoot = index == 0;
            var optionalCurrentDrawing = allowCurrentVersionFallback
                && !isRoot
                && node.Kind == DocumentKind.Drawing;
            if (node.Status == ReferenceNodeStatus.Missing)
            {
                if (optionalCurrentDrawing)
                {
                    warnings.Add($"引用文件{node.FileName}缺失，本次按缺失引用打开。");
                    continue;
                }
                throw new PdmRuleException($"引用文件{node.FileName}缺失，不能生成完整打开清单。");
            }
            if (node.Status == ReferenceNodeStatus.Virtual)
            {
                continue;
            }
            Guid referencedDocumentId;
            if (node.DocumentId.HasValue)
            {
                referencedDocumentId = node.DocumentId.Value;
            }
            else
            {
                if (!allowCurrentVersionFallback)
                {
                    throw new PdmRuleException($"引用文件{node.FileName}尚未登记，不能生成完整打开清单。");
                }

                var referencedFileName = Path.GetFileName(node.FileName);
                var matches = currentProjectDocuments
                    .Where(document => document.FileName.Equals(referencedFileName, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (matches.Length == 0)
                {
                    if (optionalCurrentDrawing)
                    {
                        warnings.Add($"引用文件{node.FileName}尚未登记，本次按缺失引用打开。");
                        continue;
                    }
                    throw new PdmRuleException($"引用文件{node.FileName}尚未登记，不能生成完整打开清单。");
                }
                if (matches.Length > 1)
                {
                    throw new PdmRuleException($"项目中存在多个同名图档{node.FileName}，不能安全关联。");
                }

                referencedDocumentId = matches[0].Id;
            }

            if (!isRoot && authorizedDocumentIds.Add(referencedDocumentId))
            {
                if (currentProjectDocumentsById.TryGetValue(referencedDocumentId, out var referencedDocument))
                {
                    RequireProjectDocumentAccess(referencedDocument, FolderAccess.View);
                }
                else
                {
                    await RequireDocumentAccessAsync(referencedDocumentId, actor, role, FolderAccess.View, cancellationToken);
                }
            }

            DocumentVersion? version;
            if (isRoot)
            {
                if (referencedDocumentId != documentId)
                {
                    throw new PdmRuleException("版本引用快照的根图档与所选图档不一致。");
                }
                version = rootVersion;
            }
            else
            {
                var versions = await ListOpenVersionsAsync(referencedDocumentId);
                if (node.Revision is null)
                {
                    if (versionId.HasValue || releasedOnly)
                    {
                        throw new PdmRuleException($"引用文件{node.FileName}的快照未记录版本，不能用当前最新版本替代。");
                    }

                    version = versions.FirstOrDefault();
                    if (version is null && allowCurrentVersionFallback)
                    {
                        warnings.Add($"引用文件{node.FileName}尚未形成受控版本，本次按缺失引用打开。");
                        continue;
                    }
                    if (version is null)
                    {
                        throw new PdmNotFoundException($"引用文件{node.FileName}尚无可用的最新受控版本。");
                    }
                }
                else
                {
                    var referencedRevision = node.Revision.GetValueOrDefault().Display;
                    version = versions.FirstOrDefault(item => item.Revision.Display.Equals(referencedRevision, StringComparison.OrdinalIgnoreCase));
                    if (version is null && allowCurrentVersionFallback)
                    {
                        version = versions.FirstOrDefault();
                        if (version is null)
                        {
                            warnings.Add($"引用文件{node.FileName}尚未形成受控版本，本次按缺失引用打开。");
                            continue;
                        }
                        warnings.Add($"引用文件{node.FileName}的快照版本{referencedRevision}不存在，已使用最新受控版本{version.Revision.Display}。");
                    }
                    if (version is null)
                    {
                        throw new PdmNotFoundException($"引用文件{node.FileName}的受控版本{referencedRevision}不存在。");
                    }
                }
            }

            if (filesByDocument.TryGetValue(referencedDocumentId, out var existing))
            {
                if (normalizedConflictDocumentIds.Contains(referencedDocumentId))
                {
                    continue;
                }
                if (existing.VersionId != version.Id)
                {
                    if (!allowCurrentVersionFallback)
                    {
                        throw new PdmRuleException($"同一图档{node.FileName}在快照中引用了不同版本，不能安全打开。");
                    }

                    var currentDocument = currentProjectDocuments.FirstOrDefault(document => document.Id == referencedDocumentId)
                        ?? throw new PdmNotFoundException($"引用文件{node.FileName}已不在当前项目中。");
                    var currentVersions = await ListOpenVersionsAsync(referencedDocumentId);
                    var currentVersion = currentVersions.FirstOrDefault(item =>
                            item.Revision.Display.Equals(currentDocument.Revision.Display, StringComparison.OrdinalIgnoreCase))
                        ?? currentVersions.FirstOrDefault()
                        ?? throw new PdmNotFoundException($"引用文件{node.FileName}尚无可用的最新受控版本。");
                    await fileStorage.ValidateStoredFileMetadataAsync(
                        project,
                        new StoredFile(currentVersion.StorageRelativePath, currentVersion.FileLength, currentVersion.Sha256, currentVersion.CreatedAt),
                        cancellationToken);
                    var normalized = existing with
                    {
                        VersionId = currentVersion.Id,
                        Revision = currentVersion.Revision.Display,
                        FileLength = currentVersion.FileLength,
                        Sha256 = currentVersion.Sha256
                    };
                    files[files.FindIndex(item => item.DocumentId == referencedDocumentId)] = normalized;
                    filesByDocument[referencedDocumentId] = normalized;
                    normalizedConflictDocumentIds.Add(referencedDocumentId);
                    warnings.Add($"引用文件{node.FileName}在当前快照中记录了多个版本（{existing.Revision}、{version.Revision.Display}），已统一使用最新受控版本{currentVersion.Revision.Display}。");
                }
                continue;
            }

            var fileName = Path.GetFileName(node.FileName);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new PdmRuleException("引用快照包含无效文件名。");
            }
            if (fileNames.TryGetValue(fileName, out var conflictingDocumentId) && conflictingDocumentId != referencedDocumentId)
            {
                throw new PdmRuleException($"项目中存在同名文件{fileName}，不能放入同一受控工作区。");
            }

            await fileStorage.ValidateStoredFileMetadataAsync(
                project,
                new StoredFile(version.StorageRelativePath, version.FileLength, version.Sha256, version.CreatedAt),
                cancellationToken);
            var item = new ControlledOpenFile(
                referencedDocumentId,
                version.Id,
                version.Revision.Display,
                fileName,
                fileName,
                version.FileLength,
                version.Sha256,
                node.Configuration,
                isRoot);
            files.Add(item);
            filesByDocument.Add(referencedDocumentId, item);
            fileNames[fileName] = referencedDocumentId;
        }

        if (allowCurrentVersionFallback)
        {
            var drawingRelations = await repository.ListDocumentRelationsAsync(project.Id, cancellationToken);
            foreach (var relation in drawingRelations)
            {
                if (!filesByDocument.ContainsKey(relation.ModelDocumentId)
                    || filesByDocument.ContainsKey(relation.DrawingDocumentId)
                    || !currentProjectDocumentsById.TryGetValue(relation.DrawingDocumentId, out var drawingDocument))
                {
                    continue;
                }

                if (authorizedDocumentIds.Add(drawingDocument.Id))
                {
                    RequireProjectDocumentAccess(drawingDocument, FolderAccess.View);
                }
                var drawingVersions = await ListOpenVersionsAsync(drawingDocument.Id);
                var drawingVersion = drawingVersions.FirstOrDefault(item =>
                        item.Revision.Display.Equals(drawingDocument.Revision.Display, StringComparison.OrdinalIgnoreCase))
                    ?? drawingVersions.FirstOrDefault();
                if (drawingVersion is null)
                {
                    warnings.Add($"关联工程图{drawingDocument.FileName}尚未形成受控版本，本次未下载。");
                    continue;
                }
                var drawingFileName = Path.GetFileName(drawingDocument.FileName);
                if (string.IsNullOrWhiteSpace(drawingFileName))
                {
                    throw new PdmRuleException("关联工程图包含无效文件名。");
                }
                if (fileNames.TryGetValue(drawingFileName, out var conflictingDocumentId)
                    && conflictingDocumentId != drawingDocument.Id)
                {
                    throw new PdmRuleException($"项目中存在同名文件{drawingFileName}，不能放入同一受控工作区。");
                }

                await fileStorage.ValidateStoredFileMetadataAsync(
                    project,
                    new StoredFile(drawingVersion.StorageRelativePath, drawingVersion.FileLength, drawingVersion.Sha256, drawingVersion.CreatedAt),
                    cancellationToken);
                var drawingFile = new ControlledOpenFile(
                    drawingDocument.Id,
                    drawingVersion.Id,
                    drawingVersion.Revision.Display,
                    drawingFileName,
                    drawingFileName,
                    drawingVersion.FileLength,
                    drawingVersion.Sha256,
                    "图纸",
                    false);
                files.Add(drawingFile);
                filesByDocument.Add(drawingDocument.Id, drawingFile);
                fileNames[drawingFileName] = drawingDocument.Id;
            }
        }

        var rootFile = files.Single(item => item.IsRoot);
        var manifest = new ControlledOpenManifest(
            Guid.NewGuid(),
            project.Id,
            project.Code,
            rootDocument.Id,
            rootVersion.Id,
            rootVersion.Revision.Display,
            rootFile.RelativePath,
            forEdit,
            files,
            warnings.Distinct(StringComparer.Ordinal).ToArray());
        await AuditAsync(
            actor,
            forEdit ? "document.open-manifest.edit" : releasedOnly ? "document.open-manifest.released" : "document.open-manifest.readonly",
            nameof(PdmDocument),
            documentId.ToString(),
            $"{rootVersion.Revision.Display}; files={files.Count}",
            cancellationToken);
        return manifest;
    }

    private static IEnumerable<DocumentReferenceNode> FlattenOpenNodes(DocumentReferenceNode root)
    {
        var pending = new Stack<DocumentReferenceNode>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            yield return node;
            for (var index = node.Children.Count - 1; index >= 0; index--)
            {
                pending.Push(node.Children[index]);
            }
        }
    }

    public async Task<ReleasePackage> CreateReleasePackageAsync(
        Guid projectId,
        Guid? referenceSnapshotId,
        string number,
        string processReviewer,
        string approver,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        var project = await repository.FindProjectAsync(projectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。 ");
        return await CreateReleasePackageAsync(
            projectId, referenceSnapshotId, number, number, "兼容既有发布流程创建的设变",
            project.SerialNumbers.FirstOrDefault() ?? "未指定", null,
            processReviewer, approver, actor, role, cancellationToken);
    }

    public async Task<ReleasePackage> CreateReleasePackageAsync(
        Guid projectId,
        Guid? referenceSnapshotId,
        string number,
        string changeNumber,
        string changeReason,
        string effectiveSerialFrom,
        string? effectiveSerialTo,
        string processReviewer,
        string approver,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ReleaseManage, cancellationToken);
        var project = await repository.FindProjectAsync(projectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。 ");

        if (string.IsNullOrWhiteSpace(number) || number.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new PdmRuleException("发布包编号不能为空，且不能包含文件名非法字符。");
        }

        if (string.IsNullOrWhiteSpace(processReviewer) || string.IsNullOrWhiteSpace(approver))
        {
            throw new PdmRuleException("必须指定工艺审核人和批准人。");
        }
        changeNumber = RequiredComment(changeNumber, "设变编号");
        changeReason = RequiredComment(changeReason, "设变原因");
        effectiveSerialFrom = RequiredComment(effectiveSerialFrom, "生效起始序列号");
        effectiveSerialTo = NullIfWhiteSpace(effectiveSerialTo);
        if (project.SerialNumbers.Count > 0 && effectiveSerialFrom != "未指定" && !project.SerialNumbers.Contains(effectiveSerialFrom, StringComparer.OrdinalIgnoreCase))
            throw new PdmRuleException("生效起始序列号不属于当前项目。");
        if (effectiveSerialTo is not null && project.SerialNumbers.Count > 0 && !project.SerialNumbers.Contains(effectiveSerialTo, StringComparer.OrdinalIgnoreCase))
            throw new PdmRuleException("生效结束序列号不属于当前项目。");
        if (effectiveSerialTo is not null && project.SerialNumbers.Count > 0)
        {
            var startIndex = project.SerialNumbers.ToList().FindIndex(serial => string.Equals(serial, effectiveSerialFrom, StringComparison.OrdinalIgnoreCase));
            var endIndex = project.SerialNumbers.ToList().FindIndex(serial => string.Equals(serial, effectiveSerialTo, StringComparison.OrdinalIgnoreCase));
            if (startIndex < 0 || endIndex < startIndex) throw new PdmRuleException("生效结束序列号不能早于起始序列号。");
        }

        var snapshot = await repository.GetLatestReferenceSnapshotAsync(projectId, cancellationToken)
            ?? throw new PdmRuleException("项目尚无已存档的引用树快照，不能创建发布包。");
        if (referenceSnapshotId.HasValue && referenceSnapshotId.Value != Guid.Empty && referenceSnapshotId.Value != snapshot.SnapshotId)
        {
            throw new PdmConflictException("指定的引用树快照不是项目当前最新快照，请刷新后重试。");
        }

        var standard = await repository.GetBomAsync(projectId, BomKind.Standard, cancellationToken);
        var nonStandard = await repository.GetBomAsync(projectId, BomKind.NonStandard, cancellationToken);
        var unclassified = await repository.GetBomAsync(projectId, BomKind.Unclassified, cancellationToken);
        var legacyMechanical = await repository.GetBomAsync(projectId, BomKind.Mechanical, cancellationToken);
        var mechanical = standard.Concat(nonStandard).Concat(legacyMechanical).Where(item => !item.IsManuallyExcluded).ToArray();
        var electrical = await repository.GetBomAsync(projectId, BomKind.Electrical, cancellationToken);
        var validationRules = (await repository.GetSystemSettingsAsync(cancellationToken)).ValidationRules;
        var legacyMode = legacyMechanical.Count > 0 && standard.Count == 0 && nonStandard.Count == 0;
        if (!legacyMode) await EnsureStandardMaterialMasterReadyAsync(standard, cancellationToken);
        if (unclassified.Any(item => !item.IsManuallyExcluded))
            throw new PdmRuleException("源数据中仍有待分类或待确认物料，请处理完成后再创建发布包。");
        if ((!legacyMode && !BomReady(BomKind.Standard, standard, validationRules))
            || (!legacyMode && !BomReady(BomKind.NonStandard, nonStandard, validationRules))
            || !BomReady(BomKind.Electrical, electrical, validationRules))
        {
            throw new PdmRuleException("标准件BOM、非标件BOM和电气BOM中的有效物料资料必须齐全；空BOM自动按无此类物料处理。");
        }

        var standardVersion = await ResolveBomVersionForReleaseAsync(projectId, BomKind.Standard, standard.Where(item => !item.IsManuallyExcluded).ToArray(), actor, changeNumber, changeReason, effectiveSerialFrom, effectiveSerialTo, validationRules, cancellationToken);
        var nonStandardVersion = await ResolveBomVersionForReleaseAsync(projectId, BomKind.NonStandard, nonStandard.Where(item => !item.IsManuallyExcluded).ToArray(), actor, changeNumber, changeReason, effectiveSerialFrom, effectiveSerialTo, validationRules, cancellationToken);
        var electricalVersion = await ResolveBomVersionForReleaseAsync(projectId, BomKind.Electrical, electrical.Where(item => !item.IsManuallyExcluded).ToArray(), actor, changeNumber, changeReason, effectiveSerialFrom, effectiveSerialTo, validationRules, cancellationToken);

        var packageId = Guid.NewGuid();
        var tasks = new[]
        {
            new ApprovalTask(Guid.NewGuid(), packageId, ApprovalStage.ProcessReview, processReviewer, null, null, null, null) { StepOrder = 1, StepName = "工艺审核" },
            new ApprovalTask(Guid.NewGuid(), packageId, ApprovalStage.Approval, approver, null, null, null, null) { StepOrder = 2, StepName = "批准" }
        };
        var package = new ReleasePackage(
            packageId,
            projectId,
            number,
            ReleasePackageState.Draft,
            snapshot.SnapshotId,
            BomRevision("M", mechanical),
            BomRevision("E", electrical),
            tasks,
            timeProvider.GetUtcNow(),
            null,
            null)
        {
            StandardBomVersionId = standardVersion.Id,
            NonStandardBomVersionId = nonStandardVersion.Id,
            ElectricalBomVersionId = electricalVersion.Id,
            StandardBomRevision = standardVersion.Label,
            NonStandardBomRevision = nonStandardVersion.Label,
            StandardBomSnapshot = standardVersion.Items.ToArray(),
            NonStandardBomSnapshot = nonStandardVersion.Items.ToArray(),
            ChangeNumber = changeNumber,
            ChangeReason = changeReason,
            EffectiveSerialFrom = effectiveSerialFrom,
            EffectiveSerialTo = effectiveSerialTo,
            MechanicalBomSnapshot = mechanical.ToArray(),
            ElectricalBomSnapshot = electrical.ToArray()
        };

        var created = await repository.CreateReleasePackageAsync(package, cancellationToken);
        await publisher.PrepareAsync(created, project, cancellationToken);
        await AuditAsync(actor, "release-package.create", nameof(ReleasePackage), packageId.ToString(), number, cancellationToken);
        return created;
    }

    public async Task<ReleasePackage> CreateScopedReleasePackageAsync(
        Guid projectId,
        Guid? referenceSnapshotId,
        string number,
        string changeNumber,
        string changeReason,
        string effectiveSerialFrom,
        string? effectiveSerialTo,
        ReleaseScope scope,
        IReadOnlyList<Guid>? selectedBomItemIds,
        string actor,
        UserRole role,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<Guid, decimal>? selectedBomItemQuantities = null)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ReleaseManage, cancellationToken);
        if (scope == ReleaseScope.LegacyCombined)
            throw new PdmRuleException("新发布流程必须指定标准件、电气或非标件发布类型。");
        var project = await repository.FindProjectAsync(projectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。");
        var projectNumber = ProjectNumberPolicy.BusinessCode(project);
        var businessTime = timeProvider.GetUtcNow().ToOffset(TimeSpan.FromHours(8));
        number = $"RP-{projectNumber}-{businessTime:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";
        changeNumber = scope is ReleaseScope.StandardSupplement or ReleaseScope.ElectricalSupplement
            ? $"ECN-{projectNumber}-{businessTime:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}"
            : number;
        effectiveSerialFrom = project.SerialNumbers.FirstOrDefault() ?? "未指定";
        effectiveSerialTo = null;

        var snapshot = await repository.GetLatestReferenceSnapshotAsync(projectId, cancellationToken)
            ?? throw new PdmRuleException("项目尚无已存档的引用树快照，不能创建发布包。");
        if (referenceSnapshotId.HasValue && referenceSnapshotId.Value != Guid.Empty && referenceSnapshotId.Value != snapshot.SnapshotId)
            throw new PdmConflictException("指定的引用树快照不是项目当前最新快照，请刷新后重试。");

        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        changeReason = NormalizeScopedReleaseDescription(changeReason, scope, settings.ReleaseChangeReasonTypes);
        var standard = (await repository.GetBomAsync(projectId, BomKind.Standard, cancellationToken)).Where(item => !item.IsManuallyExcluded).ToArray();
        var nonStandard = (await repository.GetBomAsync(projectId, BomKind.NonStandard, cancellationToken)).Where(item => !item.IsManuallyExcluded).ToArray();
        var electrical = (await repository.GetBomAsync(projectId, BomKind.Electrical, cancellationToken)).Where(item => !item.IsManuallyExcluded).ToArray();
        var targetKind = ReleaseScopeBomKind(scope);
        var targetItems = targetKind == BomKind.Standard ? standard : targetKind == BomKind.NonStandard ? nonStandard : electrical;
        if (scope == ReleaseScope.StandardLongLead)
        {
            var selectedIds = (selectedBomItemIds ?? []).Distinct().ToHashSet();
            if (selectedIds.Count == 0) throw new PdmRuleException("长交期发布必须至少选择一个标准件物料。");
            targetItems = standard.Where(item => selectedIds.Contains(item.Id)).ToArray();
            if (targetItems.Length != selectedIds.Count) throw new PdmRuleException("长交期发布选择中包含已删除或不属于标准件BOM的物料。");
            if (selectedBomItemQuantities is not null)
            {
                if (selectedBomItemQuantities.Keys.Any(id => !selectedIds.Contains(id)))
                    throw new PdmRuleException("长交期发布数量中包含未选择的标准件物料。");
                targetItems = targetItems.Select(item =>
                {
                    var quantity = selectedBomItemQuantities.GetValueOrDefault(item.Id, item.Quantity);
                    if (quantity <= 0 || quantity > item.Quantity)
                        throw new PdmRuleException($"物料{item.DrawingNumber}的长交期发布数量必须大于0且不能超过当前BOM数量{item.Quantity}。");
                    return item with { Quantity = quantity };
                }).ToArray();
            }
        }
        if (targetKind == BomKind.Standard) await EnsureStandardMaterialMasterReadyAsync(targetItems, cancellationToken);
        await EnsureReleaseScopeAvailableAsync(projectId, scope, targetItems, standard, cancellationToken);
        if (!BomReady(targetKind, targetItems, settings.ValidationRules))
            throw new PdmRuleException($"{BomKindLabel(targetKind)}BOM仍有资料不完整的物料，不能创建发布包。{MissingBomSummary(targetKind, targetItems, settings.ValidationRules)}");
        if (targetKind != BomKind.Electrical)
        {
            var unresolved = await repository.GetBomAsync(projectId, BomKind.Unclassified, cancellationToken);
            if (unresolved.Any(item => !item.IsManuallyExcluded))
                throw new PdmRuleException("源数据中仍有待分类或待确认物料，请处理完成后再创建机械发布包。");
        }

        BomVersion? standardVersion = null;
        BomVersion? nonStandardVersion = null;
        BomVersion? electricalVersion = null;
        if (scope != ReleaseScope.StandardLongLead)
        {
            var allVersions = await repository.ListBomVersionsAsync(projectId, null, cancellationToken);
            BomVersion? LatestReleased(BomKind kind) => allVersions.FirstOrDefault(version => version.Kind == kind && version.State == BomVersionState.Released);
            standardVersion = targetKind == BomKind.Standard
                ? await ResolveBomVersionForReleaseAsync(projectId, BomKind.Standard, standard, actor, changeNumber, changeReason, effectiveSerialFrom, effectiveSerialTo, settings.ValidationRules, cancellationToken)
                : LatestReleased(BomKind.Standard);
            nonStandardVersion = targetKind == BomKind.NonStandard
                ? await ResolveBomVersionForReleaseAsync(projectId, BomKind.NonStandard, nonStandard, actor, changeNumber, changeReason, effectiveSerialFrom, effectiveSerialTo, settings.ValidationRules, cancellationToken)
                : LatestReleased(BomKind.NonStandard);
            electricalVersion = targetKind == BomKind.Electrical
                ? await ResolveBomVersionForReleaseAsync(projectId, BomKind.Electrical, electrical, actor, changeNumber, changeReason, effectiveSerialFrom, effectiveSerialTo, settings.ValidationRules, cancellationToken)
                : LatestReleased(BomKind.Electrical);
        }

        var workflow = targetKind == BomKind.Electrical ? settings.ApprovalWorkflows.Electrical : settings.ApprovalWorkflows.Mechanical;
        var packageId = Guid.NewGuid();
        var tasks = await BuildApprovalTasksAsync(packageId, workflow, project, actor, cancellationToken);
        var selectedSnapshot = scope == ReleaseScope.StandardLongLead ? targetItems : standardVersion?.Items ?? [];
        var package = new ReleasePackage(
            packageId, projectId, number, ReleasePackageState.Draft, snapshot.SnapshotId,
            BomRevision("M", (standardVersion?.Items ?? []).Concat(nonStandardVersion?.Items ?? []).ToArray()),
            BomRevision("E", electricalVersion?.Items ?? []), tasks, timeProvider.GetUtcNow(), null, null)
        {
            Scope = scope,
            WorkflowCode = workflow.Code,
            WorkflowVersion = workflow.Version,
            SelectedBomItemIds = scope == ReleaseScope.StandardLongLead ? targetItems.Select(item => item.Id).ToArray() : [],
            CreatesManufacturingBaseline = scope != ReleaseScope.StandardLongLead
                && standardVersion is not null && nonStandardVersion is not null && electricalVersion is not null,
            LocksDocuments = scope == ReleaseScope.NonStandardWithDrawing,
            StandardBomVersionId = standardVersion?.Id,
            NonStandardBomVersionId = nonStandardVersion?.Id,
            ElectricalBomVersionId = electricalVersion?.Id,
            StandardBomRevision = scope == ReleaseScope.StandardLongLead ? BomRevision("LL", targetItems) : standardVersion?.Label,
            NonStandardBomRevision = nonStandardVersion?.Label,
            StandardBomSnapshot = selectedSnapshot.ToArray(),
            NonStandardBomSnapshot = nonStandardVersion?.Items.ToArray() ?? [],
            ElectricalBomSnapshot = electricalVersion?.Items.ToArray() ?? [],
            MechanicalBomSnapshot = selectedSnapshot.Concat(nonStandardVersion?.Items ?? []).ToArray(),
            ChangeNumber = changeNumber,
            ChangeReason = changeReason,
            EffectiveSerialFrom = effectiveSerialFrom,
            EffectiveSerialTo = effectiveSerialTo
        };
        var created = await repository.CreateReleasePackageAsync(package, cancellationToken);
        await publisher.PrepareAsync(created, project, cancellationToken);
        await AuditAsync(actor, "release-package.create", nameof(ReleasePackage), packageId.ToString(), $"{number}；{scope}；模板{workflow.Code} v{workflow.Version}", cancellationToken);
        return created;
    }

    public async Task<ReleasePackage> UpdateReleasePackageDraftAsync(
        Guid releasePackageId,
        string? changeReason,
        IReadOnlyList<Guid>? selectedBomItemIds,
        IReadOnlyDictionary<Guid, decimal>? selectedBomItemQuantities,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ReleaseManage, cancellationToken);
        var package = await repository.FindReleasePackageAsync(releasePackageId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包不存在。");
        if (package.State != ReleasePackageState.Draft)
            throw new PdmConflictException("只有草稿发布包可以编辑，请刷新后重试。");
        var project = await repository.FindProjectAsync(package.ProjectId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包对应的项目不存在。");
        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        var normalizedReason = NormalizeScopedReleaseDescription(changeReason, package.Scope, settings.ReleaseChangeReasonTypes);
        var updated = package with { ChangeReason = normalizedReason };

        if (package.Scope == ReleaseScope.StandardLongLead)
        {
            var snapshot = await repository.GetLatestReferenceSnapshotAsync(package.ProjectId, cancellationToken)
                ?? throw new PdmRuleException("项目尚无已存档的引用树快照，不能编辑发布包。");
            var standard = (await repository.GetBomAsync(package.ProjectId, BomKind.Standard, cancellationToken))
                .Where(item => !item.IsManuallyExcluded).ToArray();
            var selectedIds = (selectedBomItemIds ?? []).Distinct().ToHashSet();
            if (selectedIds.Count == 0) throw new PdmRuleException("长交期发布必须至少选择一个标准件物料。");
            var selectedItems = standard.Where(item => selectedIds.Contains(item.Id)).ToArray();
            if (selectedItems.Length != selectedIds.Count)
                throw new PdmRuleException("长交期发布选择中包含已删除或不属于标准件BOM的物料。");
            if (selectedBomItemQuantities is not null)
            {
                if (selectedBomItemQuantities.Keys.Any(id => !selectedIds.Contains(id)))
                    throw new PdmRuleException("长交期发布数量中包含未选择的标准件物料。");
                selectedItems = selectedItems.Select(item =>
                {
                    var quantity = selectedBomItemQuantities.GetValueOrDefault(item.Id, item.Quantity);
                    if (quantity <= 0 || quantity > item.Quantity)
                        throw new PdmRuleException($"物料{item.DrawingNumber}的长交期发布数量必须大于0且不能超过当前BOM数量{item.Quantity}。");
                    return item with { Quantity = quantity };
                }).ToArray();
            }
            await EnsureStandardMaterialMasterReadyAsync(selectedItems, cancellationToken);
            await EnsureReleaseScopeAvailableAsync(package.ProjectId, package.Scope, selectedItems, standard, cancellationToken, package.Id);
            if (!BomReady(BomKind.Standard, selectedItems, settings.ValidationRules))
                throw new PdmRuleException($"标准件BOM仍有资料不完整的物料，不能保存发布草稿。{MissingBomSummary(BomKind.Standard, selectedItems, settings.ValidationRules)}");
            updated = package with
            {
                ReferenceSnapshotId = snapshot.SnapshotId,
                ChangeReason = normalizedReason,
                SelectedBomItemIds = selectedItems.Select(item => item.Id).ToArray(),
                StandardBomRevision = BomRevision("LL", selectedItems),
                StandardBomSnapshot = selectedItems,
                MechanicalBomRevision = BomRevision("M", selectedItems.Concat(package.NonStandardBomSnapshot).ToArray()),
                MechanicalBomSnapshot = selectedItems.Concat(package.NonStandardBomSnapshot).ToArray()
            };
        }

        var saved = await repository.UpdateDraftReleasePackageAsync(updated, cancellationToken);
        await publisher.PrepareAsync(saved, project, cancellationToken);
        await AuditAsync(actor, "release-package.draft.update", nameof(ReleasePackage), saved.Id.ToString(), $"{saved.Number}；{saved.Scope}", cancellationToken);
        return saved;
    }

    public async Task DeleteReleasePackageDraftAsync(Guid releasePackageId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ReleaseManage, cancellationToken);
        var package = await repository.FindReleasePackageAsync(releasePackageId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包不存在。");
        if (package.State != ReleasePackageState.Draft)
            throw new PdmConflictException("只有草稿发布包可以删除，请刷新后重试。");
        var project = await repository.FindProjectAsync(package.ProjectId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包对应的项目不存在。");
        await repository.DeleteDraftReleasePackageAsync(package.Id, cancellationToken);
        string cleanup;
        try
        {
            await publisher.DiscardDraftAsync(package, project, cancellationToken);
            cleanup = "暂存文件已清理";
        }
        catch (Exception exception)
        {
            cleanup = $"暂存文件清理失败：{exception.Message}";
        }
        await AuditAsync(actor, "release-package.draft.delete", nameof(ReleasePackage), package.Id.ToString(), $"{package.Number}；{cleanup}", cancellationToken);
    }

    public async Task<LongLeadU9RecoveryResult> RetryLongLeadU9Async(
        Guid releasePackageId,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ReleaseManage, cancellationToken);
        var package = await repository.FindReleasePackageAsync(releasePackageId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包不存在。");
        if (!await repository.HasProjectContentReadAccessAsync(package.ProjectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的操作权限。");
        if (package.Scope != ReleaseScope.StandardLongLead || package.State != ReleasePackageState.Published)
            throw new PdmRuleException("只有已发布的长交期标准件发布包可以重试U9C串联。");
        if (bomHeaderService is null || approvalU9Automation is null)
            throw new PdmRuleException("U9C自动串联服务尚未启用。");

        var generated = await bomHeaderService.EnsureApplicationsAfterBomApprovalAsync(
            package.ProjectId, ProjectBomHeaderKind.Standard, actor, cancellationToken);
        var automation = await approvalU9Automation.ContinueAfterMaterialSyncAsync(
            package.ProjectId, actor, cancellationToken);
        await AuditAsync(actor, "release-package.long-lead-u9.retry", nameof(ReleasePackage), package.Id.ToString(),
            $"{package.Number}；BOM料号申请新增{generated.GeneratedCount}项、已存在{generated.ExistingCount}项；{automation.Stage}；{automation.Message}", cancellationToken);
        return new(package.Id, package.Number, generated, automation);
    }

    public async Task<IReadOnlyList<BomItem>> GetBomAsync(Guid projectId, BomKind kind, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的读取权限。");
        var items = await repository.GetBomAsync(projectId, kind, cancellationToken);
        if (kind is not (BomKind.Standard or BomKind.NonStandard or BomKind.Electrical))
            return items.Select(item => item with { IsComplete = false }).ToArray();
        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        return items.Select(item => item with { IsComplete = HasRequiredBomValues(item, kind, settings.ValidationRules) }).ToArray();
    }

    public async Task<BomItem> ApplyMaterialCodeToBomAsync(Guid projectId, Guid itemId, string materialCode, string actor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(materialCode)) throw new PdmRuleException("料号不能为空。");
        materialCode = materialCode.Trim();
        var current = await repository.FindBomItemAsync(projectId, itemId, cancellationToken)
            ?? throw new PdmNotFoundException("BOM物料不存在。");
        await EnsureStandardMaterialIdentityAsync(current, materialCode, cancellationToken);
        var item = await repository.UpdateBomMaterialCodeAsync(projectId, itemId, materialCode, cancellationToken);
        item = await RefreshBomItemReconciliationAsync(item, actor, cancellationToken);
        if (item.SourceDocumentId.HasValue && item.Kind != BomKind.Electrical)
        {
            await EnqueueCadPropertyWritebackAsync(item, actor, cancellationToken);
            await EnqueueLinkedDrawingMaterialCodeWritebacksAsync(item, actor, cancellationToken);
        }
        await AuditAsync(actor, "bom.material-code.apply", nameof(BomItem), item.Id.ToString(), materialCode.Trim(), cancellationToken);
        return item;
    }

    public async Task<IReadOnlyList<BomItem>> ReplaceBomAsync(
        Guid projectId,
        BomKind kind,
        IReadOnlyList<BomItemInput> inputs,
        string actor,
        UserRole role,
        CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        if (kind is not (BomKind.Standard or BomKind.NonStandard or BomKind.Electrical))
            throw new PdmRuleException("BOM类型必须是标准件、非标件或电气。");
        _ = await repository.FindProjectAsync(projectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。");
        await EnsureBomChangeAllowedAsync(projectId, cancellationToken, kind);
        var duplicateSequence = inputs.GroupBy(item => item.Sequence).FirstOrDefault(group => group.Count() > 1);
        if (duplicateSequence is not null)
        {
            throw new PdmRuleException($"BOM序号{duplicateSequence.Key}重复。");
        }
        var duplicateId = inputs.Where(item => item.Id.HasValue).GroupBy(item => item.Id).FirstOrDefault(group => group.Count() > 1);
        if (duplicateId is not null) throw new PdmRuleException("同一BOM行不能重复提交。");

        var existing = await repository.GetBomAsync(projectId, kind, cancellationToken);
        var validationRules = (await repository.GetSystemSettingsAsync(cancellationToken)).ValidationRules;
        var recycledConflict = existing.FirstOrDefault(existingItem => existingItem.IsManuallyExcluded && inputs.Any(input =>
            input.Id == existingItem.Id || SameBomSource(input.SourceDocumentId, input.SourceConfiguration, input.SourceInstancePath, existingItem)));
        if (recycledConflict is not null)
            throw new PdmConflictException($"物料“{recycledConflict.DrawingNumber}”已在回收站中，请先从回收站恢复。");
        if (kind is BomKind.Standard or BomKind.NonStandard)
        {
        var omittedSourceItem = existing.FirstOrDefault(existingItem => existingItem.SourceDocumentId.HasValue
                && !existingItem.IsManuallyExcluded
                && !inputs.Any(input => input.Id == existingItem.Id
                    || SameBomSource(input.SourceDocumentId, input.SourceConfiguration, input.SourceInstancePath, existingItem)));
            if (omittedSourceItem is not null)
                throw new PdmRuleException($"图纸来源物料“{omittedSourceItem.DrawingNumber}”不可直接删除；请先从图纸更新，再处理待移除项。");
        }
        var items = inputs.OrderBy(item => item.Sequence).Select(input =>
        {
            var isExistingSource = existing.Any(item => item.SourceDocumentId.HasValue
                && (input.Id == item.Id || SameBomSource(input.SourceDocumentId, input.SourceConfiguration, input.SourceInstancePath, item)));
            if (input.Sequence <= 0 || input.Quantity <= 0
                || (kind == BomKind.Electrical && string.IsNullOrWhiteSpace(input.DrawingNumber))
                || (string.IsNullOrWhiteSpace(input.Name) && (kind == BomKind.Electrical || !isExistingSource))
                || string.IsNullOrWhiteSpace(input.Unit)
                || string.IsNullOrWhiteSpace(input.Revision))
            {
                throw new PdmRuleException(kind == BomKind.Electrical
                    ? "电气BOM序号、物料编码、名称、数量、单位和版本必须有效。"
                    : "BOM序号、名称、数量、单位和版本必须有效。");
            }

            var previous = input.Id.HasValue
                ? existing.FirstOrDefault(item => item.Id == input.Id.Value)
                : input.SourceDocumentId.HasValue
                    ? existing.FirstOrDefault(item => SameBomSource(input.SourceDocumentId, input.SourceConfiguration, input.SourceInstancePath, item))
                    : null;
            var material = NullIfWhiteSpace(input.Material);
            var specification = NullIfWhiteSpace(input.Specification);
            var candidate = new BomItem(
                previous?.Id ?? Guid.NewGuid(), projectId, kind, input.Sequence, input.DrawingNumber.Trim(), input.Name?.Trim() ?? string.Empty, input.Quantity,
                U9UnitCatalog.NormalizeBomUnit(input.Unit), material, specification, input.Revision.Trim(),
                false)
            {
                Remark = NullIfWhiteSpace(input.Remark),
                Brand = NullIfWhiteSpace(input.Brand),
                SurfaceTreatment = NullIfWhiteSpace(input.SurfaceTreatment),
                HeatTreatment = NullIfWhiteSpace(input.HeatTreatment),
                Weight = NullIfWhiteSpace(input.Weight),
                SourceDocumentId = previous?.SourceDocumentId ?? input.SourceDocumentId,
                SourceConfiguration = previous?.SourceConfiguration ?? NullIfWhiteSpace(input.SourceConfiguration),
                SourceInstancePath = previous?.SourceInstancePath ?? NullIfWhiteSpace(input.SourceInstancePath),
                ParentDrawingNumber = NullIfWhiteSpace(input.ParentDrawingNumber) ?? previous?.ParentDrawingNumber,
                Source = previous?.Source ?? "Manual",
                IsManuallyOverridden = previous?.Source == "Auto" || previous?.IsManuallyOverridden == true,
                IsPendingRemoval = false,
                IsPendingClassification = input.IsPendingClassification,
                IsManualUnmatched = input.IsManualUnmatched,
                IsManuallyRetained = input.IsManuallyRetained,
                IsManuallyExcluded = false,
                ReconciliationStatus = previous?.ReconciliationStatus ?? (previous is null ? ReconcileManualAdded : null),
                ReconciliationNote = previous?.ReconciliationNote ?? (previous is null ? "人工新增物料，不来源于当前图档源数据。" : null),
                ReconciliationUpdatedBy = previous?.ReconciliationUpdatedBy ?? (previous is null ? actor : null),
                ReconciliationUpdatedAt = previous?.ReconciliationUpdatedAt ?? (previous is null ? timeProvider.GetUtcNow() : null),
                PropertyWritebackStatus = previous?.PropertyWritebackStatus
            };
            return candidate with { IsComplete = HasRequiredBomValues(candidate, kind, validationRules) };
        }).ToArray();

        items = items.Concat(existing.Where(item => item.IsManuallyExcluded && items.All(savedItem => savedItem.Id != item.Id)))
            .Select((item, index) => item with { Sequence = index + 1 })
            .ToArray();

        var writebackIds = kind == BomKind.Electrical
            ? new HashSet<Guid>()
            : items.Where(item => item.SourceDocumentId.HasValue
                    && (item.PropertyWritebackStatus == CadPropertyWritebackStatus.PendingSave
                        || CadWritableValuesChanged(existing.FirstOrDefault(previous => previous.Id == item.Id), item)))
                .Select(item => item.Id)
                .ToHashSet();
        items = items.Select(item => writebackIds.Contains(item.Id)
            ? item with { PropertyWritebackStatus = CadPropertyWritebackStatus.Pending }
            : item).ToArray();
        var saved = await repository.ReplaceBomAsync(projectId, kind, items, cancellationToken);
        foreach (var item in saved.Where(item => writebackIds.Contains(item.Id)))
            await EnqueueCadPropertyWritebackAsync(item, actor, cancellationToken);
        await repository.SetBomEmptyDeclarationAsync(projectId, kind, false, actor, cancellationToken);
        await repository.SaveBomDraftAsync(projectId, kind, saved.Where(item => !item.IsManuallyExcluded).ToArray(), actor, cancellationToken);
        await AuditAsync(actor, "bom.replace", nameof(BomItem), projectId.ToString(), $"{kind}:{saved.Count}", cancellationToken);
        return await repository.GetBomAsync(projectId, kind, cancellationToken);
    }

    public async Task<BomGenerationResult> GenerateMechanicalBomAsync(Guid projectId, bool apply, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的操作权限。");
        await EnsureBomChangeAllowedAsync(projectId, cancellationToken, BomKind.Standard, BomKind.NonStandard);
        var snapshot = await repository.GetLatestReferenceSnapshotAsync(projectId, cancellationToken)
            ?? throw new PdmRuleException("项目尚无已存档的设计树，不能生成BOM。");
        return await GenerateMechanicalBomFromSnapshotAsync(projectId, snapshot, actor, cancellationToken, apply);
    }

    public async Task<IReadOnlyList<BomItem>> GetBomSourceDataAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的查看权限。");
        var snapshot = await repository.GetLatestReferenceSnapshotAsync(projectId, cancellationToken);
        if (snapshot is null) return Array.Empty<BomItem>();
        var generated = await GenerateMechanicalBomFromSnapshotAsync(projectId, snapshot, actor, cancellationToken, false, false);
        var maintained = (await repository.GetBomAsync(projectId, BomKind.Standard, cancellationToken))
            .Concat(await repository.GetBomAsync(projectId, BomKind.NonStandard, cancellationToken))
            .Concat(await repository.GetBomAsync(projectId, BomKind.Unclassified, cancellationToken))
            .Concat(await repository.GetBomAsync(projectId, BomKind.Virtual, cancellationToken))
            .Where(item => item.SourceDocumentId.HasValue && !item.IsPendingRemoval && !item.IsManuallyExcluded)
            .ToArray();
        var validationRules = (await repository.GetSystemSettingsAsync(cancellationToken)).ValidationRules;
        var now = timeProvider.GetUtcNow();
        return generated.StandardItems
            .Concat(generated.NonStandardItems)
            .Concat(generated.UnclassifiedItems)
            .Concat(generated.VirtualItems)
            .Where(item => item.SourceDocumentId.HasValue && !item.IsPendingRemoval)
            .OrderBy(item => item.DrawingNumber, StringComparer.OrdinalIgnoreCase)
            .Select((item, index) =>
            {
                var current = maintained.FirstOrDefault(candidate => SameBomSource(candidate, item));
                if (current is null)
                {
                    if (item.Kind == BomKind.Virtual)
                    {
                        return item with
                        {
                            Sequence = index + 1,
                            IsComplete = false,
                            IsPendingClassification = false,
                            ReconciliationStatus = "Virtual",
                            ReconciliationNote = "虚拟件仅保留在源数据中，不进入标准件、非标件或电气BOM。",
                            ReconciliationUpdatedBy = actor,
                            ReconciliationUpdatedAt = now
                        };
                    }
                    return item with
                    {
                        Sequence = index + 1,
                        IsComplete = item.Kind is BomKind.Standard or BomKind.NonStandard && HasRequiredBomValues(item, item.Kind, validationRules),
                        ReconciliationStatus = ReconcilePendingClassification,
                        ReconciliationNote = "尚未归入标准件或非标件BOM。",
                        ReconciliationUpdatedBy = actor,
                        ReconciliationUpdatedAt = now
                    };
                }

                var differences = SourceDataDifferences(current, item);
                return item with
                {
                    Id = current.Id,
                    Kind = current.Kind == BomKind.Virtual ? BomKind.Virtual : item.Kind,
                    Sequence = index + 1,
                    IsComplete = current.Kind != BomKind.Virtual && item.Kind is BomKind.Standard or BomKind.NonStandard && HasRequiredBomValues(item, item.Kind, validationRules),
                    IsPendingClassification = current.Kind == BomKind.Virtual ? false : item.IsPendingClassification,
                    ReconciliationStatus = differences.Count == 0 ? "SourceMatched" : "ManualOverrideMismatch",
                    ReconciliationNote = differences.Count == 0
                        ? "BOM维护值已与图档源数据一致。"
                        : $"BOM维护值与图档源数据不一致：{string.Join('、', differences)}。",
                    ReconciliationUpdatedBy = actor,
                    ReconciliationUpdatedAt = now
                };
            })
            .ToArray();
    }

    public async Task<IReadOnlyList<BomItem>> ResolveBomItemAsync(Guid projectId, Guid itemId, ResolveBomItemCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的操作权限。");
        var standard = (await repository.GetBomAsync(projectId, BomKind.Standard, cancellationToken)).ToList();
        var nonStandard = (await repository.GetBomAsync(projectId, BomKind.NonStandard, cancellationToken)).ToList();
        var unclassified = (await repository.GetBomAsync(projectId, BomKind.Unclassified, cancellationToken)).ToList();
        var electrical = (await repository.GetBomAsync(projectId, BomKind.Electrical, cancellationToken)).ToList();
        var validationRules = (await repository.GetSystemSettingsAsync(cancellationToken)).ValidationRules;
        var item = standard.Concat(nonStandard).Concat(unclassified).Concat(electrical).FirstOrDefault(candidate => candidate.Id == itemId)
            ?? throw new PdmNotFoundException("BOM物料不存在。");
        await EnsureBomChangeAllowedAsync(projectId, cancellationToken, item.Kind, command.TargetKind ?? item.Kind);
        var now = timeProvider.GetUtcNow();
        standard.RemoveAll(candidate => candidate.Id == itemId);
        nonStandard.RemoveAll(candidate => candidate.Id == itemId);
        unclassified.RemoveAll(candidate => candidate.Id == itemId);
        electrical.RemoveAll(candidate => candidate.Id == itemId);
        BomItem? resolved = null;
        switch (command.Action.Trim().ToLowerInvariant())
        {
            case "remove":
                break;
            case "retain":
                resolved = item with
                {
                    Source = "Manual",
                    IsPendingRemoval = false,
                    IsManualUnmatched = false,
                    IsManuallyRetained = true,
                    ReconciliationStatus = ReconcileManuallyRetained,
                    ReconciliationNote = $"最新图档源数据中无对应项，已由{actor}确认保留。",
                    ReconciliationUpdatedBy = actor,
                    ReconciliationUpdatedAt = now
                };
                break;
            case "classify":
                if (command.TargetKind is not (BomKind.Standard or BomKind.NonStandard or BomKind.Electrical))
                    throw new PdmRuleException("待分类物料只能归入标准件、非标件或电气BOM。");
                if (item.SourceDocumentId.HasValue && command.TargetKind == BomKind.Electrical)
                    throw new PdmRuleException("图档源数据只能归入标准件或非标件BOM；电气BOM独立维护。");
                resolved = item with
                {
                    Kind = command.TargetKind.Value,
                    IsManuallyOverridden = true,
                    IsPendingClassification = false,
                    IsPendingRemoval = false,
                    IsManualUnmatched = false,
                    IsManuallyRetained = false,
                    IsComplete = HasRequiredBomValues(item with { Kind = command.TargetKind.Value }, command.TargetKind.Value, validationRules),
                    ReconciliationStatus = ReconcileManuallyClassified,
                    ReconciliationNote = $"图档未提供有效分类，已由{actor}归入{BomKindLabel(command.TargetKind.Value)}BOM。",
                    ReconciliationUpdatedBy = actor,
                    ReconciliationUpdatedAt = now,
                    PropertyWritebackStatus = item.SourceDocumentId.HasValue ? CadPropertyWritebackStatus.PendingSave : item.PropertyWritebackStatus
                };
                break;
            default:
                throw new PdmRuleException("不支持的BOM处理操作。");
        }
        if (resolved is not null)
        {
            if (resolved.Kind == BomKind.Standard) standard.Add(resolved);
            else if (resolved.Kind == BomKind.NonStandard) nonStandard.Add(resolved);
            else if (resolved.Kind == BomKind.Electrical) electrical.Add(resolved);
            else unclassified.Add(resolved);
        }
        static BomItem[] Resequence(IEnumerable<BomItem> items) => items.OrderBy(candidate => candidate.DrawingNumber, StringComparer.OrdinalIgnoreCase)
            .Select((candidate, index) => candidate with { Sequence = index + 1 }).ToArray();
        await repository.ReplaceBomAsync(projectId, BomKind.Standard, Resequence(standard), cancellationToken);
        await repository.ReplaceBomAsync(projectId, BomKind.NonStandard, Resequence(nonStandard), cancellationToken);
        await repository.ReplaceBomAsync(projectId, BomKind.Unclassified, Resequence(unclassified), cancellationToken);
        await repository.ReplaceBomAsync(projectId, BomKind.Electrical, Resequence(electrical), cancellationToken);
        foreach (var changedKind in new[] { item.Kind, resolved?.Kind }.Where(candidate => candidate is BomKind.Standard or BomKind.NonStandard or BomKind.Electrical).Distinct())
            await SyncBomDraftAsync(projectId, changedKind!.Value, actor, cancellationToken);
        var auditDetail = command.Action.Equals("remove", StringComparison.OrdinalIgnoreCase)
            ? $"确认删除：{item.DrawingNumber}；原状态：{item.ReconciliationStatus ?? "未记录"}"
            : $"{command.Action}:{command.TargetKind}；{resolved?.ReconciliationNote}";
        await AuditAsync(actor, "bom.reconcile", nameof(BomItem), itemId.ToString(), auditDetail, cancellationToken);
        return (await repository.GetBomAsync(projectId, resolved?.Kind ?? item.Kind, cancellationToken));
    }

    public async Task<IReadOnlyList<BomItem>> BatchUpdateBomItemsAsync(Guid projectId, BatchUpdateBomItemsCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的操作权限。");
        var itemIds = command.ItemIds.Distinct().ToArray();
        if (itemIds.Length == 0) throw new PdmRuleException("请至少选择一条BOM物料。");
        if (itemIds.Length > 500) throw new PdmRuleException("单次最多批量编辑500条BOM物料。");
        var fields = command.Fields.Select(field => field.Trim()).Where(field => field.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (fields.Count == 0) throw new PdmRuleException("请至少选择一个要批量修改的属性。");
        var allowedFields = new HashSet<string>(["kind", "unit", "drawingNumber", "name", "specification", "remark", "brand", "material", "surfaceTreatment", "weight", "quantity", "revision", "parentDrawingNumber"], StringComparer.OrdinalIgnoreCase);
        var unsupported = fields.FirstOrDefault(field => !allowedFields.Contains(field));
        if (unsupported is not null) throw new PdmRuleException($"不支持批量修改属性：{unsupported}。");
        if (fields.Contains("kind") && command.TargetKind is not (BomKind.Standard or BomKind.NonStandard or BomKind.Electrical or BomKind.Virtual))
            throw new PdmRuleException("物料分类只能批量改为标准件、非标件、电气件或虚拟件。");
        if (fields.Contains("quantity") && command.Quantity is null or <= 0)
            throw new PdmRuleException("数量必须大于0。");
        static string Required(string? value, string label)
        {
            value = value?.Trim();
            if (string.IsNullOrWhiteSpace(value)) throw new PdmRuleException($"{label}不能为空。");
            return value;
        }
        static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        string RequiredIfSelected(string field, string? value, string label) => fields.Contains(field) ? Required(value, label) : string.Empty;
        if (fields.Contains("unit")) _ = U9UnitCatalog.Normalize(command.Unit);
        _ = RequiredIfSelected("name", command.Name, "物料名称");
        _ = RequiredIfSelected("revision", command.Revision, "版本");

        var standard = (await repository.GetBomAsync(projectId, BomKind.Standard, cancellationToken)).ToList();
        var nonStandard = (await repository.GetBomAsync(projectId, BomKind.NonStandard, cancellationToken)).ToList();
        var unclassified = (await repository.GetBomAsync(projectId, BomKind.Unclassified, cancellationToken)).ToList();
        var electrical = (await repository.GetBomAsync(projectId, BomKind.Electrical, cancellationToken)).ToList();
        var virtualItems = (await repository.GetBomAsync(projectId, BomKind.Virtual, cancellationToken)).ToList();
        var validationRules = (await repository.GetSystemSettingsAsync(cancellationToken)).ValidationRules;
        var originals = standard.Concat(nonStandard).Concat(unclassified).Concat(electrical).Concat(virtualItems).Where(item => itemIds.Contains(item.Id)).ToDictionary(item => item.Id);
        if (originals.Count != itemIds.Length) throw new PdmNotFoundException("选中的BOM物料已变化，请刷新后重新选择。");
        await EnsureBomChangeAllowedAsync(projectId, cancellationToken, originals.Values.Select(item => item.Kind).Append(command.TargetKind ?? originals.Values.First().Kind).ToArray());
        if (originals.Values.Any(item => item.IsManuallyExcluded))
            throw new PdmRuleException("回收站中的物料不能直接编辑，请先执行恢复。");
        if (fields.Contains("kind") && command.TargetKind == BomKind.Electrical && originals.Values.Any(item => item.SourceDocumentId.HasValue))
            throw new PdmRuleException("图档源数据只能归入标准件或非标件BOM；电气BOM独立维护。");
        if (fields.Contains("kind") && command.TargetKind == BomKind.Virtual && originals.Values.Any(item => !item.SourceDocumentId.HasValue))
            throw new PdmRuleException("虚拟件仅适用于有图档来源的物料。");
        var updatedById = new Dictionary<Guid, BomItem>();
        var reconciliationTime = timeProvider.GetUtcNow();
        foreach (var itemId in itemIds)
        {
            var original = originals[itemId];
            var targetKind = fields.Contains("kind") ? command.TargetKind!.Value : original.Kind;
            var updated = original with
            {
                Kind = targetKind,
                Unit = fields.Contains("unit") ? U9UnitCatalog.Normalize(command.Unit) : original.Unit,
                DrawingNumber = fields.Contains("drawingNumber") ? command.DrawingNumber?.Trim() ?? string.Empty : original.DrawingNumber,
                Name = fields.Contains("name") ? Required(command.Name, "物料名称") : original.Name,
                Specification = fields.Contains("specification") ? Optional(command.Specification) : original.Specification,
                Remark = fields.Contains("remark") ? Optional(command.Remark) : original.Remark,
                Brand = fields.Contains("brand") ? Optional(command.Brand) : original.Brand,
                Material = fields.Contains("material") ? Optional(command.Material) : original.Material,
                SurfaceTreatment = fields.Contains("surfaceTreatment") ? Optional(command.SurfaceTreatment) : original.SurfaceTreatment,
                HeatTreatment = fields.Contains("heatTreatment") ? Optional(command.HeatTreatment) : original.HeatTreatment,
                Weight = fields.Contains("weight") ? Optional(command.Weight) : original.Weight,
                Quantity = fields.Contains("quantity") ? command.Quantity!.Value : original.Quantity,
                Revision = fields.Contains("revision") ? Required(command.Revision, "版本") : original.Revision,
                ParentDrawingNumber = fields.Contains("parentDrawingNumber") ? Optional(command.ParentDrawingNumber) : original.ParentDrawingNumber,
                IsManuallyOverridden = original.IsManuallyOverridden || original.Source == "Auto",
                IsPendingClassification = fields.Contains("kind") ? false : original.IsPendingClassification,
                IsPendingRemoval = fields.Contains("kind") ? false : original.IsPendingRemoval,
                IsManualUnmatched = fields.Contains("kind") ? false : original.IsManualUnmatched,
                IsManuallyRetained = fields.Contains("kind") ? false : original.IsManuallyRetained,
                IsManuallyExcluded = fields.Contains("kind") ? false : original.IsManuallyExcluded,
                ReconciliationStatus = fields.Contains("kind") ? ReconcileManuallyClassified : original.ReconciliationStatus,
                ReconciliationNote = fields.Contains("kind")
                    ? targetKind == BomKind.Virtual
                        ? $"已由{actor}标记为虚拟件，仅保留在源数据中。"
                        : $"已由{actor}人工归入{BomKindLabel(targetKind)}BOM。"
                    : original.ReconciliationNote,
                ReconciliationUpdatedBy = fields.Contains("kind") ? actor : original.ReconciliationUpdatedBy,
                ReconciliationUpdatedAt = fields.Contains("kind") ? reconciliationTime : original.ReconciliationUpdatedAt
            };
            updated = updated with { IsComplete = HasRequiredBomValues(updated, targetKind, validationRules) };
            if (CadWritableValuesChanged(original, updated) && updated.SourceDocumentId.HasValue)
                updated = updated with { PropertyWritebackStatus = CadPropertyWritebackStatus.PendingSave };
            updatedById[itemId] = updated;
        }

        var snapshot = await repository.GetLatestReferenceSnapshotAsync(projectId, cancellationToken);
        if (snapshot is not null)
        {
            var raw = await GenerateMechanicalBomFromSnapshotAsync(projectId, snapshot, actor, cancellationToken, false, false);
            var rawItems = raw.StandardItems.Concat(raw.NonStandardItems).Concat(raw.ElectricalItems).Concat(raw.UnclassifiedItems).Concat(raw.VirtualItems).ToArray();
            foreach (var (id, maintained) in updatedById.ToArray())
            {
                if (!maintained.SourceDocumentId.HasValue) continue;
                var source = rawItems.FirstOrDefault(candidate => SameBomSource(candidate, maintained));
                if (source is null) continue;
                var differences = SourceDataDifferences(maintained, source);
                updatedById[id] = maintained with
                {
                    ReconciliationStatus = differences.Count > 0 ? "ManualOverrideMismatch" : "SourceMatched",
                    ReconciliationNote = differences.Count > 0
                        ? $"BOM维护值与最新图档源数据不一致：{string.Join('、', differences)}。"
                        : "BOM维护值已与最新图档源数据一致。",
                    ReconciliationUpdatedBy = actor,
                    ReconciliationUpdatedAt = reconciliationTime
                };
            }
        }

        standard.RemoveAll(item => itemIds.Contains(item.Id));
        nonStandard.RemoveAll(item => itemIds.Contains(item.Id));
        unclassified.RemoveAll(item => itemIds.Contains(item.Id));
        electrical.RemoveAll(item => itemIds.Contains(item.Id));
        virtualItems.RemoveAll(item => itemIds.Contains(item.Id));
        foreach (var item in updatedById.Values)
            if (item.Kind == BomKind.Standard) standard.Add(item);
            else if (item.Kind == BomKind.NonStandard) nonStandard.Add(item);
            else if (item.Kind == BomKind.Unclassified) unclassified.Add(item);
            else if (item.Kind == BomKind.Electrical) electrical.Add(item);
            else virtualItems.Add(item);
        static BomItem[] Resequence(IEnumerable<BomItem> items) => items.OrderBy(item => item.DrawingNumber, StringComparer.OrdinalIgnoreCase)
            .Select((item, index) => item with { Sequence = index + 1 }).ToArray();
        var updatedStandard = Resequence(standard);
        var updatedNonStandard = Resequence(nonStandard);
        var updatedUnclassified = Resequence(unclassified);
        var updatedElectrical = Resequence(electrical);
        var updatedVirtual = Resequence(virtualItems);
        var now = timeProvider.GetUtcNow();
        var audits = new[]
        {
            new AuditEntry(Guid.NewGuid(), now, actor, "bom.batch-update", nameof(BomItem), projectId.ToString(), $"物料{itemIds.Length}条；属性{string.Join(',', fields.Order())}；待保存BOM")
        };
        await repository.ApplyBomBatchAsync(projectId, updatedStandard, updatedNonStandard, updatedUnclassified, updatedElectrical, updatedVirtual, [], audits, cancellationToken);
        foreach (var changedKind in originals.Values.Select(item => item.Kind).Concat(updatedById.Values.Select(item => item.Kind))
                     .Where(candidate => candidate is BomKind.Standard or BomKind.NonStandard or BomKind.Electrical).Distinct())
            await SyncBomDraftAsync(projectId, changedKind, actor, cancellationToken);
        return updatedStandard.Concat(updatedNonStandard).Concat(updatedUnclassified).Concat(updatedElectrical).Concat(updatedVirtual).Where(item => itemIds.Contains(item.Id)).ToArray();
    }

    public async Task<IReadOnlyList<BomItem>> RestoreBomItemsFromSourceAsync(Guid projectId, RestoreBomItemsFromSourceCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的操作权限。");
        var itemIds = command.ItemIds.Distinct().ToArray();
        if (itemIds.Length == 0) throw new PdmRuleException("请至少选择一条BOM物料。");
        if (itemIds.Length > 500) throw new PdmRuleException("单次最多恢复500条BOM物料。");
        var standard = (await repository.GetBomAsync(projectId, BomKind.Standard, cancellationToken)).ToList();
        var nonStandard = (await repository.GetBomAsync(projectId, BomKind.NonStandard, cancellationToken)).ToList();
        var unclassified = (await repository.GetBomAsync(projectId, BomKind.Unclassified, cancellationToken)).ToList();
        var electrical = (await repository.GetBomAsync(projectId, BomKind.Electrical, cancellationToken)).ToList();
        var virtualItems = (await repository.GetBomAsync(projectId, BomKind.Virtual, cancellationToken)).ToArray();
        var originals = standard.Concat(nonStandard).Where(item => itemIds.Contains(item.Id)).ToDictionary(item => item.Id);
        if (originals.Count != itemIds.Length) throw new PdmRuleException("只能恢复标准件BOM或非标件BOM中的物料。");
        await EnsureBomChangeAllowedAsync(projectId, cancellationToken, originals.Values.Select(item => item.Kind).ToArray());
        if (originals.Values.Any(item => !item.SourceDocumentId.HasValue))
            throw new PdmRuleException("人工新增物料没有图档源数据，不能执行恢复源数据。");

        var snapshot = await repository.GetLatestReferenceSnapshotAsync(projectId, cancellationToken)
            ?? throw new PdmRuleException("项目尚无已存档的设计树，不能恢复图档源数据。");
        var generated = await GenerateMechanicalBomFromSnapshotAsync(projectId, snapshot, actor, cancellationToken, false, false);
        var rawItems = generated.StandardItems.Concat(generated.NonStandardItems).Concat(generated.UnclassifiedItems).Concat(generated.VirtualItems).ToArray();
        var validationRules = (await repository.GetSystemSettingsAsync(cancellationToken)).ValidationRules;
        var now = timeProvider.GetUtcNow();
        var restoredById = new Dictionary<Guid, BomItem>();
        foreach (var itemId in itemIds)
        {
            var original = originals[itemId];
            var source = rawItems.FirstOrDefault(candidate => SameBomSource(candidate, original))
                ?? throw new PdmRuleException($"物料{original.DrawingNumber}在当前图档源数据中不存在，不能恢复。");
            var restored = original with
            {
                Unit = source.Unit,
                DrawingNumber = source.DrawingNumber,
                Name = source.Name,
                Specification = source.Specification,
                Remark = source.Remark,
                Brand = source.Brand,
                Material = source.Material,
                SurfaceTreatment = source.SurfaceTreatment,
                HeatTreatment = source.HeatTreatment,
                Weight = source.Weight,
                Quantity = source.Quantity,
                Revision = source.Revision,
                IsPendingRemoval = false,
                IsManualUnmatched = false,
                IsManuallyRetained = false,
                IsManuallyExcluded = false,
                PropertyWritebackStatus = null,
                ReconciliationUpdatedBy = actor,
                ReconciliationUpdatedAt = now
            };
            var differences = SourceDataDifferences(restored, source)
                .Where(difference => !string.Equals(difference, "物料分类", StringComparison.Ordinal))
                .ToArray();
            restored = restored with
            {
                IsManuallyOverridden = restored.Kind != source.Kind || differences.Length > 0,
                IsComplete = HasRequiredBomValues(restored, original.Kind, validationRules),
                ReconciliationStatus = differences.Length == 0 ? "SourceMatched" : "ManualOverrideMismatch",
                ReconciliationNote = differences.Length == 0
                    ? $"已由{actor}恢复为最新图档源数据；BOM分类与排序保持不变。"
                    : $"已由{actor}恢复图档属性；BOM分类与排序保持不变，仍与图档源数据不一致：{string.Join('、', differences)}。"
            };
            restoredById[itemId] = restored;
        }

        static BomItem[] Apply(IEnumerable<BomItem> items, IReadOnlyDictionary<Guid, BomItem> restored) =>
            items.Select(item => restored.TryGetValue(item.Id, out var replacement) ? replacement : item)
                .OrderBy(item => item.Sequence)
                .ToArray();
        var updatedStandard = Apply(standard, restoredById);
        var updatedNonStandard = Apply(nonStandard, restoredById);
        var updatedUnclassified = unclassified.OrderBy(item => item.Sequence).ToArray();
        var updatedElectrical = electrical.OrderBy(item => item.Sequence).ToArray();
        var audit = new AuditEntry(Guid.NewGuid(), now, actor, "bom.restore-source", nameof(BomItem), projectId.ToString(), $"恢复图档源数据{itemIds.Length}条；保留BOM分类与排序");
        await repository.ApplyBomBatchAsync(projectId, updatedStandard, updatedNonStandard, updatedUnclassified, updatedElectrical, virtualItems, [], [audit], cancellationToken);
        foreach (var changedKind in restoredById.Values.Select(item => item.Kind).Distinct())
            await SyncBomDraftAsync(projectId, changedKind, actor, cancellationToken);
        return restoredById.Values.ToArray();
    }

    public async Task<BomSourceReclassificationPreview> PreviewBomSourceReclassificationAsync(Guid projectId, ReclassifyBomItemsFromSourceCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var plan = await PrepareBomSourceReclassificationAsync(projectId, command, actor, role, cancellationToken);
        return plan.Preview;
    }

    public async Task<IReadOnlyList<BomItem>> ReclassifyBomItemsFromSourceAsync(Guid projectId, ReclassifyBomItemsFromSourceCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var plan = await PrepareBomSourceReclassificationAsync(projectId, command, actor, role, cancellationToken);
        var itemIds = plan.UpdatedById.Keys.ToHashSet();
        BomItem[] ApplyKind(IEnumerable<BomItem> items, BomKind kind) => items.Where(item => !itemIds.Contains(item.Id))
            .Concat(plan.UpdatedById.Values.Where(item => item.Kind == kind))
            .OrderBy(item => item.DrawingNumber, StringComparer.OrdinalIgnoreCase)
            .Select((item, index) => item with { Sequence = index + 1 })
            .ToArray();

        var updatedStandard = ApplyKind(plan.Standard, BomKind.Standard);
        var updatedNonStandard = ApplyKind(plan.NonStandard, BomKind.NonStandard);
        var updatedUnclassified = ApplyKind(plan.Unclassified, BomKind.Unclassified);
        var updatedElectrical = ApplyKind(plan.Electrical, BomKind.Electrical);
        var updatedVirtual = ApplyKind(plan.VirtualItems, BomKind.Virtual);
        var now = timeProvider.GetUtcNow();
        var audit = new AuditEntry(Guid.NewGuid(), now, actor, "bom.reclassify-source", nameof(BomItem), projectId.ToString(), $"重新归类并同步源数据{itemIds.Count}条；目标{command.TargetKind}；正式料号保护{plan.Preview.Items.Count(item => item.OfficialMaterialCodeProtected)}条");
        await repository.ApplyBomBatchAsync(projectId, updatedStandard, updatedNonStandard, updatedUnclassified, updatedElectrical, updatedVirtual, [], [audit], cancellationToken);
        foreach (var changedKind in plan.Originals.Values.Select(item => item.Kind).Append(command.TargetKind)
                     .Where(candidate => candidate is BomKind.Standard or BomKind.NonStandard or BomKind.Electrical).Distinct())
            await SyncBomDraftAsync(projectId, changedKind, actor, cancellationToken);
        return plan.UpdatedById.Values.ToArray();
    }

    private async Task<BomSourceReclassificationPlan> PrepareBomSourceReclassificationAsync(Guid projectId, ReclassifyBomItemsFromSourceCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的操作权限。");
        if (command.TargetKind is not (BomKind.Standard or BomKind.NonStandard))
            throw new PdmRuleException("重新归类只支持标准件BOM或非标件BOM。");
        var itemIds = command.ItemIds.Distinct().ToArray();
        if (itemIds.Length == 0) throw new PdmRuleException("请至少选择一条BOM物料。");
        if (itemIds.Length > 500) throw new PdmRuleException("单次最多重新归类500条BOM物料。");

        var standard = (await repository.GetBomAsync(projectId, BomKind.Standard, cancellationToken)).ToArray();
        var nonStandard = (await repository.GetBomAsync(projectId, BomKind.NonStandard, cancellationToken)).ToArray();
        var unclassified = (await repository.GetBomAsync(projectId, BomKind.Unclassified, cancellationToken)).ToArray();
        var electrical = (await repository.GetBomAsync(projectId, BomKind.Electrical, cancellationToken)).ToArray();
        var virtualItems = (await repository.GetBomAsync(projectId, BomKind.Virtual, cancellationToken)).ToArray();
        var originals = standard.Concat(nonStandard).Concat(unclassified).Concat(virtualItems)
            .Where(item => itemIds.Contains(item.Id)).ToDictionary(item => item.Id);
        if (originals.Count != itemIds.Length) throw new PdmRuleException("选中的BOM物料已变化，或包含不支持重新归类的电气件，请刷新后重试。");
        if (originals.Values.Any(item => item.IsManuallyExcluded)) throw new PdmRuleException("回收站物料不能重新归类。");
        if (originals.Values.Any(item => !item.SourceDocumentId.HasValue)) throw new PdmRuleException("人工新增物料没有图档源数据，不能重新归类并同步。");
        await EnsureBomChangeAllowedAsync(projectId, cancellationToken, originals.Values.Select(item => item.Kind).Append(command.TargetKind).ToArray());

        var snapshot = await repository.GetLatestReferenceSnapshotAsync(projectId, cancellationToken)
            ?? throw new PdmRuleException("项目尚无已存档的设计树，不能重新归类并同步。");
        var generated = await GenerateMechanicalBomFromSnapshotAsync(projectId, snapshot, actor, cancellationToken, false, false);
        var rawItems = generated.StandardItems.Concat(generated.NonStandardItems).Concat(generated.UnclassifiedItems).Concat(generated.VirtualItems).ToArray();
        var validationRules = (await repository.GetSystemSettingsAsync(cancellationToken)).ValidationRules;
        var now = timeProvider.GetUtcNow();
        var updatedById = new Dictionary<Guid, BomItem>();
        var previews = new List<BomSourceReclassificationItemPreview>();
        foreach (var itemId in itemIds)
        {
            var original = originals[itemId];
            var source = rawItems.FirstOrDefault(candidate => SameBomSource(candidate, original))
                ?? throw new PdmRuleException($"物料{original.DrawingNumber}在当前图档源数据中不存在，本次操作已取消。");
            var linkedMaterial = materialRepository is null ? null : await materialRepository.FindMaterialBySourceBomItemAsync(itemId, cancellationToken);
            var protectedCode = linkedMaterial is null ? null
                : linkedMaterial.U9SyncConfirmed && !string.IsNullOrWhiteSpace(linkedMaterial.U9ItemCode) ? linkedMaterial.U9ItemCode.Trim()
                : !string.IsNullOrWhiteSpace(linkedMaterial.MaterialCode) ? linkedMaterial.MaterialCode.Trim() : null;
            var resultCode = protectedCode ?? source.DrawingNumber;
            var updated = original with
            {
                Kind = command.TargetKind,
                Unit = source.Unit,
                DrawingNumber = resultCode,
                Name = source.Name,
                Specification = source.Specification,
                Remark = source.Remark,
                Brand = source.Brand,
                Material = source.Material,
                SurfaceTreatment = source.SurfaceTreatment,
                HeatTreatment = source.HeatTreatment,
                Weight = source.Weight,
                Quantity = source.Quantity,
                Revision = source.Revision,
                IsPendingRemoval = false,
                IsPendingClassification = false,
                IsManualUnmatched = false,
                IsManuallyRetained = false,
                IsManuallyExcluded = false,
                PropertyWritebackStatus = original.Kind == command.TargetKind ? null : CadPropertyWritebackStatus.PendingSave,
                ReconciliationUpdatedBy = actor,
                ReconciliationUpdatedAt = now
            };
            var differences = SourceDataDifferences(original, source).ToArray();
            updated = updated with
            {
                IsManuallyOverridden = protectedCode is not null && !string.Equals(protectedCode, source.DrawingNumber, StringComparison.OrdinalIgnoreCase),
                IsComplete = HasRequiredBomValues(updated, command.TargetKind, validationRules),
                ReconciliationStatus = "SourceMatched",
                ReconciliationNote = protectedCode is null
                    ? $"已由{actor}重新归类为{command.TargetKind}并同步最新图档源数据。"
                    : $"已由{actor}重新归类为{command.TargetKind}并同步最新图档源数据；正式料号{protectedCode}受保护。"
            };
            updatedById[itemId] = updated;
            previews.Add(new(itemId, original.Kind, command.TargetKind, original.DrawingNumber, source.DrawingNumber, resultCode,
                original.Name, source.Name, differences, protectedCode is not null));
        }
        var preview = new BomSourceReclassificationPreview(command.TargetKind, itemIds.Length, previews.Count(item => item.ChangedFields.Count > 0), previews);
        return new(standard, nonStandard, unclassified, electrical, virtualItems, originals, updatedById, preview);
    }

    private sealed record BomSourceReclassificationPlan(
        IReadOnlyList<BomItem> Standard,
        IReadOnlyList<BomItem> NonStandard,
        IReadOnlyList<BomItem> Unclassified,
        IReadOnlyList<BomItem> Electrical,
        IReadOnlyList<BomItem> VirtualItems,
        IReadOnlyDictionary<Guid, BomItem> Originals,
        IReadOnlyDictionary<Guid, BomItem> UpdatedById,
        BomSourceReclassificationPreview Preview);

    public async Task<IReadOnlyList<BomItem>> BatchDeleteBomItemsAsync(Guid projectId, BatchDeleteBomItemsCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的操作权限。");
        var itemIds = command.ItemIds.Distinct().ToHashSet();
        if (itemIds.Count == 0) throw new PdmRuleException("请至少选择一条BOM物料。");
        if (itemIds.Count > 500) throw new PdmRuleException("单次最多删除500条BOM物料。");
        var reason = string.IsNullOrWhiteSpace(command.Reason) ? "未填写删除原因" : command.Reason.Trim();
        if (reason.Length > 500) throw new PdmRuleException("删除原因不能超过500个字符。");
        var standard = (await repository.GetBomAsync(projectId, BomKind.Standard, cancellationToken)).ToList();
        var nonStandard = (await repository.GetBomAsync(projectId, BomKind.NonStandard, cancellationToken)).ToList();
        var unclassified = (await repository.GetBomAsync(projectId, BomKind.Unclassified, cancellationToken)).ToList();
        var electrical = (await repository.GetBomAsync(projectId, BomKind.Electrical, cancellationToken)).ToList();
        var virtualItems = (await repository.GetBomAsync(projectId, BomKind.Virtual, cancellationToken)).ToArray();
        var all = standard.Concat(nonStandard).Concat(unclassified).Concat(electrical).ToArray();
        var selected = all.Where(item => itemIds.Contains(item.Id)).ToArray();
        if (selected.Length != itemIds.Count) throw new PdmNotFoundException("选中的BOM物料已变化，请刷新后重新选择。");
        await EnsureBomChangeAllowedAsync(projectId, cancellationToken, selected.Select(item => item.Kind).ToArray());
        if (selected.Any(item => item.IsManuallyExcluded)) throw new PdmRuleException("选中的BOM物料已在回收站中，请刷新后重试。");

        var deletedAt = timeProvider.GetUtcNow();
        IReadOnlyList<BomItem> Apply(IEnumerable<BomItem> source)
        {
            return source.OrderBy(item => item.Sequence).Select(item => !itemIds.Contains(item.Id) ? item : item with
                {
                    IsManuallyExcluded = true,
                    IsPendingRemoval = false,
                    IsPendingClassification = false,
                    IsManualUnmatched = false,
                    IsManuallyRetained = false,
                    ReconciliationStatus = item.SourceDocumentId.HasValue ? ReconcileManuallyExcluded : ReconcileDeleted,
                    ReconciliationNote = $"已由{actor}移入回收站。原因：{reason}",
                    ReconciliationUpdatedBy = actor,
                    ReconciliationUpdatedAt = deletedAt,
                    DeletedAt = deletedAt,
                    DeletedBy = actor,
                    DeleteReason = reason
                })
                .OrderBy(item => item.IsManuallyExcluded).ThenBy(item => item.Sequence)
                .Select((item, index) => item with { Sequence = index + 1 }).ToArray();
        }

        var updatedStandard = Apply(standard);
        var updatedNonStandard = Apply(nonStandard);
        var updatedUnclassified = Apply(unclassified);
        var updatedElectrical = Apply(electrical);
        var sourceCount = selected.Count(item => item.SourceDocumentId.HasValue);
        var manualCount = selected.Length - sourceCount;
        var removedDetails = string.Join('、', selected.Select(item => item.DrawingNumber));
        var audit = new AuditEntry(Guid.NewGuid(), deletedAt, actor, "bom.batch-delete", nameof(BomItem), projectId.ToString(), $"移入回收站{itemIds.Count}条；有源{sourceCount}条；人工{manualCount}条；原因：{reason}；物料：{removedDetails}");
        await repository.ApplyBomBatchAsync(projectId, updatedStandard, updatedNonStandard, updatedUnclassified, updatedElectrical, virtualItems, [], [audit], cancellationToken);
        foreach (var changedKind in selected.Select(item => item.Kind).Where(candidate => candidate is BomKind.Standard or BomKind.NonStandard or BomKind.Electrical).Distinct())
            await SyncBomDraftAsync(projectId, changedKind, actor, cancellationToken);
        return updatedStandard.Concat(updatedNonStandard).Concat(updatedUnclassified).Concat(updatedElectrical).ToArray();
    }

    public async Task<IReadOnlyList<BomItem>> BatchRestoreBomItemsAsync(Guid projectId, BatchRestoreBomItemsCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的操作权限。");
        var itemIds = command.ItemIds.Distinct().ToHashSet();
        if (itemIds.Count == 0) throw new PdmRuleException("请至少选择一条回收站物料。");
        if (itemIds.Count > 500) throw new PdmRuleException("单次最多恢复500条BOM物料。");
        var mode = command.Mode?.Trim() ?? "Original";
        if (mode is not ("Original" or "AsManual")) throw new PdmRuleException("恢复方式无效。");

        var standard = (await repository.GetBomAsync(projectId, BomKind.Standard, cancellationToken)).ToList();
        var nonStandard = (await repository.GetBomAsync(projectId, BomKind.NonStandard, cancellationToken)).ToList();
        var unclassified = (await repository.GetBomAsync(projectId, BomKind.Unclassified, cancellationToken)).ToList();
        var electrical = (await repository.GetBomAsync(projectId, BomKind.Electrical, cancellationToken)).ToList();
        var virtualItems = (await repository.GetBomAsync(projectId, BomKind.Virtual, cancellationToken)).ToArray();
        var all = standard.Concat(nonStandard).Concat(unclassified).Concat(electrical).ToArray();
        var selected = all.Where(item => itemIds.Contains(item.Id)).ToArray();
        if (selected.Length != itemIds.Count) throw new PdmNotFoundException("选中的回收站物料已变化，请刷新后重新选择。");
        await EnsureBomChangeAllowedAsync(projectId, cancellationToken, selected.Select(item => item.Kind).ToArray());
        if (selected.Any(item => !item.IsManuallyExcluded)) throw new PdmRuleException("选中的物料不在回收站中，请刷新后重试。");
        if (mode == "AsManual" && selected.Any(item => !item.SourceDocumentId.HasValue))
            throw new PdmRuleException("“转人工恢复”只适用于有图档来源的物料。");

        if (mode == "Original")
        {
            foreach (var item in selected.Where(item => item.SourceDocumentId.HasValue))
                if (await repository.FindDocumentAsync(item.SourceDocumentId!.Value, cancellationToken) is null)
                    throw new PdmRuleException($"物料“{item.DrawingNumber}”的源图档已不存在，请改用“转人工恢复”。");
        }

        var active = all.Where(item => !item.IsManuallyExcluded && !itemIds.Contains(item.Id)).ToArray();
        var selectedSourceConflict = selected.Where(item => mode == "Original" && item.SourceDocumentId.HasValue)
            .GroupBy(BomSourceKey, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (selectedSourceConflict is not null)
            throw new PdmConflictException("选中的回收站物料之间存在相同装配实例，不能批量恢复。");
        foreach (var item in selected)
        {
            var sourceConflict = mode == "Original" && item.SourceDocumentId.HasValue && active.Any(candidate =>
                SameBomSource(item, candidate));
            if (sourceConflict)
                throw new PdmConflictException($"物料“{item.DrawingNumber}”的装配实例已在当前BOM中，不能恢复。");
        }

        var restoredAt = timeProvider.GetUtcNow();
        IReadOnlyList<BomItem> Apply(IEnumerable<BomItem> source) => source.Select(item => !itemIds.Contains(item.Id) ? item : item with
            {
                SourceDocumentId = mode == "AsManual" ? null : item.SourceDocumentId,
                SourceConfiguration = mode == "AsManual" ? null : item.SourceConfiguration,
                SourceInstancePath = mode == "AsManual" ? null : item.SourceInstancePath,
                Source = mode == "AsManual" ? "Manual" : item.Source,
                IsManuallyOverridden = mode == "AsManual" || item.IsManuallyOverridden,
                IsManuallyExcluded = false,
                IsPendingRemoval = false,
                IsPendingClassification = false,
                IsManualUnmatched = false,
                IsManuallyRetained = false,
                ReconciliationStatus = ReconcileRestored,
                ReconciliationNote = mode == "AsManual" ? $"源图档关系已解除，由{actor}转为人工物料恢复。" : $"已由{actor}从回收站恢复。",
                ReconciliationUpdatedBy = actor,
                ReconciliationUpdatedAt = restoredAt,
                DeletedAt = null,
                DeletedBy = null,
                DeleteReason = null
            })
            .OrderBy(item => item.IsManuallyExcluded).ThenBy(item => item.Sequence)
            .Select((item, index) => item with { Sequence = index + 1 }).ToArray();

        var updatedStandard = Apply(standard);
        var updatedNonStandard = Apply(nonStandard);
        var updatedUnclassified = Apply(unclassified);
        var updatedElectrical = Apply(electrical);
        var audit = new AuditEntry(Guid.NewGuid(), restoredAt, actor, "bom.batch-restore", nameof(BomItem), projectId.ToString(), $"恢复{itemIds.Count}条；方式：{mode}；物料：{string.Join('、', selected.Select(item => item.DrawingNumber))}");
        await repository.ApplyBomBatchAsync(projectId, updatedStandard, updatedNonStandard, updatedUnclassified, updatedElectrical, virtualItems, [], [audit], cancellationToken);
        foreach (var changedKind in selected.Select(item => item.Kind).Where(candidate => candidate is BomKind.Standard or BomKind.NonStandard or BomKind.Electrical).Distinct())
            await SyncBomDraftAsync(projectId, changedKind, actor, cancellationToken);
        return updatedStandard.Concat(updatedNonStandard).Concat(updatedUnclassified).Concat(updatedElectrical).ToArray();
    }

    public async Task<IReadOnlyList<CadPropertyWriteback>> ListCadPropertyWritebacksAsync(Guid projectId, bool activeOnly, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的读取权限。");
        var items = await repository.ListCadPropertyWritebacksAsync(projectId, cancellationToken);
        return activeOnly ? items.Where(item => item.Status is CadPropertyWritebackStatus.Pending or CadPropertyWritebackStatus.InProgress).ToArray() : items;
    }

    public async Task<IReadOnlyList<DrawingReviewPackage>> ListDrawingReviewPackagesAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的读取权限。");
        return await repository.ListDrawingReviewPackagesAsync(projectId, cancellationToken);
    }

    public async Task<IReadOnlyList<DrawingReviewCandidate>> ListDrawingReviewCandidatesAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的读取权限。");
        return (await BuildDrawingReviewCandidatesAsync(projectId, cancellationToken))
            .Select(candidate => candidate.Candidate)
            .ToArray();
    }

    public Task<DrawingReviewPackage> CreateDrawingReviewPackageAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken) =>
        CreateDrawingReviewPackageAsync(projectId, null, actor, role, cancellationToken);

    public async Task<DrawingReviewPackage> CreateDrawingReviewPackageAsync(Guid projectId, IReadOnlyCollection<Guid>? modelDocumentIds, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.DrawingReviewSubmit, cancellationToken);
        if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的操作权限。");
        var project = await repository.FindProjectAsync(projectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。");
        var candidates = await BuildDrawingReviewCandidatesAsync(projectId, cancellationToken);
        DrawingReviewCandidateBuild[] selected;
        if (modelDocumentIds is null)
        {
            selected = candidates.Where(candidate => candidate.Candidate.Selectable).ToArray();
        }
        else
        {
            var requested = modelDocumentIds.Distinct().ToArray();
            if (requested.Length == 0) throw new PdmRuleException("请至少选择一组3D/2D图档进行审核。");
            var byModel = candidates
                .Where(candidate => candidate.Candidate.ModelDocumentId.HasValue)
                .ToDictionary(candidate => candidate.Candidate.ModelDocumentId!.Value);
            var missing = requested.Where(id => !byModel.ContainsKey(id)).ToArray();
            if (missing.Length > 0) throw new PdmRuleException("所选图档不在当前项目的可审核范围内，请刷新后重试。");
            selected = requested.Select(id => byModel[id]).ToArray();
            var unavailable = selected.Where(candidate => !candidate.Candidate.Selectable).Select(candidate =>
                $"{candidate.Candidate.DrawingNumber}：{candidate.Candidate.Reason ?? "当前不可发起"}").ToArray();
            if (unavailable.Length > 0)
                throw new PdmRuleException($"以下图档不能进入图纸审核：{string.Join("；", unavailable.Take(12))}{(unavailable.Length > 12 ? $"；另有{unavailable.Length - 12}项" : string.Empty)}。");
        }
        if (selected.Length == 0)
        {
            if (candidates.Count > 0 && candidates.All(candidate => candidate.Candidate.State == DrawingReviewCandidateState.InReview))
                throw new PdmConflictException("当前项目图档均已在审核中，请勿重复发起。");
            throw new PdmRuleException("当前项目没有可发起审核的3D/2D图档。");
        }

        var packageId = Guid.NewGuid();
        var items = selected.Select(candidate => new DrawingReviewItem
        {
            Id = Guid.NewGuid(),
            PackageId = packageId,
            BomItemId = candidate.ReviewSourceId,
            DrawingNumber = candidate.Candidate.DrawingNumber,
            Name = candidate.Candidate.Name,
            Configuration = candidate.Candidate.Configuration,
            ModelDocumentId = candidate.Candidate.ModelDocumentId!.Value,
            ModelVersionId = candidate.ModelVersion!.Id,
            ModelRevision = candidate.ModelVersion.Revision.Display,
            ModelSha256 = candidate.ModelVersion.Sha256,
            ModelCreatedBy = candidate.ModelVersion.CreatedBy,
            DrawingDocumentId = candidate.Candidate.DrawingDocumentId,
            DrawingVersionId = candidate.DrawingVersion?.Id,
            DrawingRevision = candidate.DrawingVersion?.Revision.Display,
            DrawingSha256 = candidate.DrawingVersion?.Sha256,
            DrawingCreatedBy = candidate.DrawingVersion?.CreatedBy,
            DrawingState = candidate.Candidate.DrawingDocumentId.HasValue ? DrawingReviewTargetState.Pending : DrawingReviewTargetState.NotRequired
        }).ToArray();
        var now = timeProvider.GetUtcNow();
        var package = new DrawingReviewPackage
        {
            Id = packageId,
            ProjectId = projectId,
            Number = $"DR-{ProjectNumberPolicy.BusinessCode(project)}-{now:yyyyMMdd}-{packageId.ToString("N")[..8].ToUpperInvariant()}",
            State = DrawingReviewPackageState.InReview,
            CreatedBy = actor,
            CreatedAt = now,
            Items = items
        };
        package = await repository.CreateDrawingReviewPackageAsync(package, cancellationToken);
        await AuditAsync(actor, "drawing-review.create", nameof(DrawingReviewPackage), package.Id.ToString(), $"{package.Number}；选择3D/2D {package.Items.Count}组", cancellationToken);
        return package;
    }

    public async Task<DrawingReviewPackage> WithdrawDrawingReviewPackageAsync(Guid packageId, string reason, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.DrawingReviewSubmit, cancellationToken);
        var package = await repository.FindDrawingReviewPackageAsync(packageId, cancellationToken)
            ?? throw new PdmNotFoundException("图纸审核单不存在。");
        if (!await repository.HasProjectContentReadAccessAsync(package.ProjectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的操作权限。");
        var project = await repository.FindProjectAsync(package.ProjectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。");
        var canWithdraw = role == UserRole.Administrator
            || string.Equals(package.CreatedBy, actor, StringComparison.OrdinalIgnoreCase)
            || string.Equals(project.PrimaryProjectManager, actor, StringComparison.OrdinalIgnoreCase)
            || project.CollaborativeProjectManagers.Contains(actor, StringComparer.OrdinalIgnoreCase);
        if (!canWithdraw) throw new UnauthorizedAccessException("只有审核发起人、项目经理或系统管理员可以撤销图纸审核。");
        reason = RequiredComment(reason, "撤销原因");
        var withdrawn = await repository.WithdrawDrawingReviewPackageAsync(packageId, actor, timeProvider.GetUtcNow(), reason, cancellationToken);
        await AuditAsync(actor, "drawing-review.withdraw", nameof(DrawingReviewPackage), packageId.ToString(), $"{withdrawn.Number}；{reason}", cancellationToken);
        return withdrawn;
    }

    private async Task<IReadOnlyList<DrawingReviewCandidateBuild>> BuildDrawingReviewCandidatesAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var packages = await repository.ListDrawingReviewPackagesAsync(projectId, cancellationToken);
        var activePackages = packages.Where(package => package.State is DrawingReviewPackageState.InReview or DrawingReviewPackageState.WritingProperties).ToArray();
        var activeDocumentIds = activePackages.SelectMany(package => package.Items).SelectMany(item => item.DrawingDocumentId.HasValue
            ? new[] { item.ModelDocumentId, item.DrawingDocumentId.Value }
            : new[] { item.ModelDocumentId }).ToHashSet();
        var approvedPackages = packages.Where(package => package.State == DrawingReviewPackageState.Approved).ToArray();

        var effectiveBomItems = new List<BomItem>();
        foreach (var bomKind in new[] { BomKind.Standard, BomKind.NonStandard, BomKind.Unclassified, BomKind.Electrical })
            effectiveBomItems.AddRange((await repository.GetBomAsync(projectId, bomKind, cancellationToken))
                .Where(item => !item.IsManuallyExcluded && !item.IsPendingRemoval && !item.DeletedAt.HasValue));
        var sources = effectiveBomItems
            .Where(item => item.SourceDocumentId.HasValue)
            .GroupBy(item => item.SourceDocumentId!.Value)
            .Select(group =>
            {
                var preferred = group.OrderBy(item => item.Kind == BomKind.NonStandard ? 0 : 1).ThenBy(item => item.Sequence).First();
                return new DrawingReviewCandidateSource(preferred.Id, preferred.DrawingNumber, preferred.Name, preferred.SourceConfiguration,
                    preferred.SourceDocumentId!.Value, group.Select(item => item.Kind).Distinct().OrderBy(kind => kind).ToArray());
            })
            .ToList();

        var result = effectiveBomItems
            .Where(item => item.Kind == BomKind.NonStandard && !item.SourceDocumentId.HasValue)
            .Select(item => new DrawingReviewCandidateBuild(item.Id, new DrawingReviewCandidate
            {
                CandidateId = item.Id,
                BomItemId = item.Id,
                ModelDocumentId = null,
                DrawingNumber = item.DrawingNumber,
                Name = item.Name,
                Configuration = item.SourceConfiguration,
                BomKinds = [BomKind.NonStandard],
                ModelRevision = "—",
                State = DrawingReviewCandidateState.Unavailable,
                Reason = "非标BOM没有来源3D模型，必须补齐3D及唯一2D工程图关系"
            }, null, null))
            .ToList();

        var documents = await repository.ListDocumentsAsync(projectId, cancellationToken);
        var documentById = documents.ToDictionary(document => document.Id);
        var referenceTree = await repository.GetReferenceTreeAsync(projectId, cancellationToken);
        var knownModelIds = sources.Select(source => source.ModelDocumentId).ToHashSet();
        if (referenceTree is not null)
        {
            foreach (var node in FlattenOpenNodes(referenceTree)
                         .Where(node => node.DocumentId.HasValue && node.Kind == DocumentKind.Assembly)
                         .GroupBy(node => node.DocumentId!.Value)
                         .Select(group => group.First()))
            {
                var modelDocumentId = node.DocumentId!.Value;
                if (!knownModelIds.Add(modelDocumentId) || !documentById.TryGetValue(modelDocumentId, out var assembly)) continue;
                sources.Add(new DrawingReviewCandidateSource(assembly.Id, assembly.DrawingNumber, assembly.Name, node.Configuration, assembly.Id, []));
            }
        }

        var relations = await repository.ListDocumentRelationsAsync(projectId, cancellationToken);
        foreach (var source in sources.OrderBy(source => source.DrawingNumber, StringComparer.OrdinalIgnoreCase))
        {
            if (!documentById.TryGetValue(source.ModelDocumentId, out var model)
                || model.Kind is not (DocumentKind.Part or DocumentKind.Assembly))
            {
                result.Add(new DrawingReviewCandidateBuild(source.ReviewSourceId, new DrawingReviewCandidate
                {
                    CandidateId = source.ReviewSourceId,
                    BomItemId = source.BomKinds.Count > 0 ? source.ReviewSourceId : null,
                    ModelDocumentId = source.ModelDocumentId,
                    DrawingNumber = source.DrawingNumber,
                    Name = source.Name,
                    Configuration = source.Configuration,
                    BomKinds = source.BomKinds,
                    ModelRevision = "—",
                    State = DrawingReviewCandidateState.Unavailable,
                    Reason = "来源3D图档不存在或类型无效"
                }, null, null));
                continue;
            }
            var relatedDrawingIds = relations.Where(relation => relation.ModelDocumentId == model.Id)
                .Select(relation => relation.DrawingDocumentId)
                .Distinct()
                .ToArray();
            var relatedDrawings = relatedDrawingIds
                .Where(id => documentById.TryGetValue(id, out var related) && related.Kind == DocumentKind.Drawing)
                .Select(id => documentById[id])
                .ToArray();
            var strictNonStandard = source.BomKinds.Contains(BomKind.NonStandard);
            var drawing = strictNonStandard
                ? relatedDrawings.Length == 1 ? relatedDrawings[0] : null
                : relatedDrawings.FirstOrDefault(related => string.Equals(related.DrawingNumber, source.DrawingNumber, StringComparison.OrdinalIgnoreCase))
                    ?? (relatedDrawings.Length == 1 ? relatedDrawings[0] : null);
            var modelVersions = (await repository.ListDocumentVersionsAsync(model.Id, cancellationToken)).OrderByDescending(version => version.CreatedAt).ToArray();
            var drawingVersions = drawing is null
                ? []
                : (await repository.ListDocumentVersionsAsync(drawing.Id, cancellationToken)).OrderByDescending(version => version.CreatedAt).ToArray();
            var modelVersion = modelVersions.FirstOrDefault();
            var drawingVersion = drawingVersions.FirstOrDefault();
            var state = DrawingReviewCandidateState.Ready;
            string? reason = null;
            if (strictNonStandard && relatedDrawings.Length != 1)
            {
                state = DrawingReviewCandidateState.Unavailable;
                reason = relatedDrawings.Length == 0
                    ? "非标BOM缺少关联2D工程图"
                    : $"非标BOM关联了{relatedDrawings.Length}张2D工程图，必须保持唯一";
            }
            else if (activeDocumentIds.Contains(model.Id) || drawing is not null && activeDocumentIds.Contains(drawing.Id))
            {
                state = DrawingReviewCandidateState.InReview;
                reason = "已在其他审核单中";
            }
            else if (drawing is null && model.Kind != DocumentKind.Assembly)
            {
                state = DrawingReviewCandidateState.Unavailable;
                reason = "未找到唯一关联的2D工程图";
            }
            else if (!string.IsNullOrWhiteSpace(model.CheckedOutBy) || drawing is not null && !string.IsNullOrWhiteSpace(drawing.CheckedOutBy))
            {
                state = DrawingReviewCandidateState.Unavailable;
                reason = "图档仍处于签出编辑状态";
            }
            else if (modelVersion is null || drawing is not null && drawingVersion is null)
            {
                state = DrawingReviewCandidateState.Unavailable;
                reason = "图档尚无已存档版本";
            }
            else if (approvedPackages.Any(package => package.Items.Any(item => item.ModelDocumentId == model.Id
                         && item.ModelState == DrawingReviewTargetState.Marked
                         && MatchesReviewedVersion(modelVersion, item.EffectiveModelVersionId, modelVersions)
                         && (drawing is null
                             ? item.DrawingState == DrawingReviewTargetState.NotRequired
                             : item.DrawingDocumentId == drawing.Id && item.DrawingState == DrawingReviewTargetState.Marked
                               && item.EffectiveDrawingVersionId.HasValue
                               && MatchesReviewedVersion(drawingVersion!, item.EffectiveDrawingVersionId.Value, drawingVersions)))))
            {
                state = DrawingReviewCandidateState.ApprovedCurrent;
                reason = "当前版本已审核，可选择重新审核";
            }
            result.Add(new DrawingReviewCandidateBuild(source.ReviewSourceId, new DrawingReviewCandidate
            {
                CandidateId = source.ReviewSourceId,
                BomItemId = source.BomKinds.Count > 0 ? source.ReviewSourceId : null,
                ModelDocumentId = model.Id,
                DrawingDocumentId = drawing?.Id,
                DrawingNumber = source.DrawingNumber,
                Name = source.Name,
                Configuration = source.Configuration,
                BomKinds = source.BomKinds,
                ModelRevision = modelVersion?.Revision.Display ?? "—",
                DrawingRevision = drawingVersion?.Revision.Display,
                State = state,
                Reason = reason
            }, modelVersion, drawingVersion));
        }
        return result.OrderBy(candidate => candidate.Candidate.DrawingNumber, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private sealed record DrawingReviewCandidateSource(Guid ReviewSourceId, string DrawingNumber, string Name, string? Configuration,
        Guid ModelDocumentId, IReadOnlyList<BomKind> BomKinds);

    private sealed record DrawingReviewCandidateBuild(Guid ReviewSourceId, DrawingReviewCandidate Candidate,
        DocumentVersion? ModelVersion, DocumentVersion? DrawingVersion);

    public async Task<DrawingReviewPackage> AddDrawingReviewMarkupAsync(Guid packageId, AddDrawingReviewMarkupCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.DrawingReviewAnnotate, cancellationToken);
        var package = await repository.FindDrawingReviewPackageAsync(packageId, cancellationToken)
            ?? throw new PdmNotFoundException("图纸审核单不存在。");
        if (!await repository.HasProjectContentReadAccessAsync(package.ProjectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的操作权限。");
        if (package.State != DrawingReviewPackageState.InReview)
            throw new PdmConflictException("当前图纸审核单不允许继续添加批注。");
        if (!package.Items.Any(item => item.Id == command.ItemId))
            throw new PdmNotFoundException("图纸审核项不存在。");
        var text = RequiredComment(command.Text, "批注内容");
        if (text.Length > 2000) throw new PdmRuleException("批注内容不能超过2000个字符。");
        if (command.NormalizedX is < 0 or > 1 || command.NormalizedY is < 0 or > 1)
            throw new PdmRuleException("批注坐标必须位于图纸可视区域内。");
        var markup = new DrawingReviewMarkup
        {
            Id = Guid.NewGuid(),
            PackageId = packageId,
            ItemId = command.ItemId,
            Target = command.Target,
            ViewName = string.IsNullOrWhiteSpace(command.ViewName) ? null : command.ViewName.Trim(),
            NormalizedX = command.NormalizedX,
            NormalizedY = command.NormalizedY,
            Text = text,
            Severity = command.Severity,
            State = DrawingReviewMarkupState.Open,
            CreatedBy = actor,
            CreatedAt = timeProvider.GetUtcNow()
        };
        var updated = await repository.AddDrawingReviewMarkupAsync(markup, cancellationToken);
        await AuditAsync(actor, "drawing-review.markup.add", nameof(DrawingReviewMarkup), markup.Id.ToString(), $"{command.Target}；{command.Severity}；{text}", cancellationToken);
        return updated;
    }

    public async Task<DrawingReviewPackage> ResolveDrawingReviewMarkupAsync(Guid packageId, Guid markupId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.DrawingReviewAnnotate, cancellationToken);
        var package = await repository.FindDrawingReviewPackageAsync(packageId, cancellationToken)
            ?? throw new PdmNotFoundException("图纸审核单不存在。");
        if (!await repository.HasProjectContentReadAccessAsync(package.ProjectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的操作权限。");
        if (!package.Markups.Any(markup => markup.Id == markupId)) throw new PdmNotFoundException("图纸批注不存在。");
        var updated = await repository.ResolveDrawingReviewMarkupAsync(markupId, actor, timeProvider.GetUtcNow(), cancellationToken);
        await AuditAsync(actor, "drawing-review.markup.resolve", nameof(DrawingReviewMarkup), markupId.ToString(), package.Number, cancellationToken);
        return updated;
    }

    public async Task<DrawingReviewPackage> DecideDrawingReviewTargetAsync(Guid packageId, Guid itemId, DecideDrawingReviewTargetCommand command, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.DrawingReviewDecide, cancellationToken);
        var package = await repository.FindDrawingReviewPackageAsync(packageId, cancellationToken)
            ?? throw new PdmNotFoundException("图纸审核单不存在。");
        if (!await repository.HasProjectContentReadAccessAsync(package.ProjectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的操作权限。");
        if (package.State != DrawingReviewPackageState.InReview)
            throw new PdmConflictException("当前图纸审核单不允许继续审核。");
        var item = package.Items.SingleOrDefault(candidate => candidate.Id == itemId)
            ?? throw new PdmNotFoundException("图纸审核项不存在。");
        if (command.Target == DrawingReviewTarget.Drawing2D && !item.RequiresDrawingReview)
            throw new PdmRuleException("该装配体没有关联工程图，仅需审核3D模型。");
        var targetState = command.Target == DrawingReviewTarget.Model3D ? item.ModelState : item.DrawingState;
        if (targetState != DrawingReviewTargetState.Pending)
            throw new PdmConflictException("该3D或2D图档已经完成审核，请刷新后重试。");
        var createdBy = command.Target == DrawingReviewTarget.Model3D ? item.ModelCreatedBy : item.DrawingCreatedBy;
        var developerSelfReviewAllowed = TenantContext.Current?.HasRole("developer") == true;
        if (!developerSelfReviewAllowed && string.Equals(createdBy, actor, StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("设计者不能审核自己生成的图档版本，请由其他审核人处理。");
        var comment = string.IsNullOrWhiteSpace(command.Comment) ? null : command.Comment.Trim();
        if (command.Decision == DrawingReviewDecision.RequestChanges)
            comment = RequiredComment(comment ?? string.Empty, "退改说明");
        if (command.Decision == DrawingReviewDecision.Approve && package.Markups.Any(markup => markup.ItemId == itemId
                && markup.Target == command.Target
                && markup.Severity == DrawingReviewMarkupSeverity.Blocking
                && markup.State == DrawingReviewMarkupState.Open))
            throw new PdmRuleException("该图档仍有未关闭的阻断批注，不能审核通过。");
        var reviewerName = (await repository.FindUserAsync(actor, cancellationToken))?.DisplayName ?? actor;
        var now = timeProvider.GetUtcNow();
        var state = command.Decision == DrawingReviewDecision.Approve ? DrawingReviewTargetState.Approved : DrawingReviewTargetState.ChangesRequested;
        package = await repository.DecideDrawingReviewTargetAsync(itemId, command.Target, state, actor, reviewerName, now, comment, cancellationToken);
        await AuditAsync(actor, "drawing-review.decide", nameof(DrawingReviewItem), itemId.ToString(), $"{command.Target}；{command.Decision}；{comment}", cancellationToken);
        if (command.Decision != DrawingReviewDecision.Approve
            || package.Items.Any(candidate => candidate.ModelState != DrawingReviewTargetState.Approved
                || candidate.DrawingState is not (DrawingReviewTargetState.Approved or DrawingReviewTargetState.NotRequired)))
            return package;

        var writebacks = package.Items.Select(candidate =>
        {
            var modelId = Guid.NewGuid();
            var modelProperties = DrawingReviewProperties(package, DrawingReviewTarget.Model3D, candidate.ModelReviewer!, candidate.ModelReviewerName!, candidate.ModelReviewedAt!.Value, candidate.ModelRevision);
            CadPropertyWriteback? drawingWriteback = null;
            if (candidate.RequiresDrawingReview)
            {
                var drawingId = Guid.NewGuid();
                var drawingProperties = DrawingReviewProperties(package, DrawingReviewTarget.Drawing2D, candidate.DrawingReviewer!, candidate.DrawingReviewerName!, candidate.DrawingReviewedAt!.Value, candidate.DrawingRevision!);
                drawingWriteback = new CadPropertyWriteback(drawingId, package.ProjectId, drawingId, candidate.DrawingDocumentId!.Value, null, candidate.DrawingVersionId!.Value, candidate.DrawingRevision!, drawingProperties, CadPropertyWritebackStatus.Pending, candidate.DrawingReviewer!, now);
            }
            return new DrawingReviewWritebackRequest(
                candidate.Id,
                new CadPropertyWriteback(modelId, package.ProjectId, modelId, candidate.ModelDocumentId, null, candidate.ModelVersionId, candidate.ModelRevision, modelProperties, CadPropertyWritebackStatus.Pending, candidate.ModelReviewer!, now),
                drawingWriteback);
        }).ToArray();
        package = await repository.QueueDrawingReviewWritebacksAsync(package.Id, writebacks, cancellationToken);
        var writebackCount = writebacks.Sum(request => 1 + (request.Drawing is null ? 0 : 1));
        await AuditAsync(actor, "drawing-review.writeback.queue", nameof(DrawingReviewPackage), package.Id.ToString(), $"图档审核属性写回{writebackCount}项", cancellationToken);
        return package;
    }

    public async Task<CadPropertyWriteback> StartCadPropertyWritebackAsync(Guid id, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var request = await RequireCadPropertyWritebackAccessAsync(id, actor, role, cancellationToken);
        if (request.Status != CadPropertyWritebackStatus.Pending) throw new PdmConflictException("属性写回任务已被处理，请刷新后重试。");
        var latest = (await repository.ListDocumentVersionsAsync(request.SourceDocumentId, cancellationToken)).FirstOrDefault();
        if (latest?.Id != request.ExpectedVersionId)
        {
            await repository.UpdateCadPropertyWritebackAsync(id, CadPropertyWritebackStatus.Conflict, null, "图档已产生新版本，请在客户端重新保存BOM后再写回。", cancellationToken);
            await repository.RecordDrawingReviewWritebackResultAsync(id, null, false, cancellationToken);
            throw new PdmConflictException("图档版本已变化，属性写回已转为冲突状态。");
        }
        return await repository.UpdateCadPropertyWritebackAsync(id, CadPropertyWritebackStatus.InProgress, null, null, cancellationToken);
    }

    public async Task<CadPropertyWriteback> CompleteCadPropertyWritebackAsync(Guid id, Guid resultVersionId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var request = await RequireCadPropertyWritebackAccessAsync(id, actor, role, cancellationToken);
        if (request.Status != CadPropertyWritebackStatus.InProgress) throw new PdmConflictException("属性写回任务不在执行中。");
        var result = await repository.FindDocumentVersionAsync(request.SourceDocumentId, resultVersionId, cancellationToken)
            ?? throw new PdmRuleException("写回结果版本不存在。");
        var updated = await repository.UpdateCadPropertyWritebackAsync(id, CadPropertyWritebackStatus.Succeeded, result.Id, null, cancellationToken);
        var bomItem = await repository.FindBomItemAsync(request.ProjectId, request.BomItemId, cancellationToken);
        if (bomItem is not null)
            _ = await RefreshBomItemReconciliationAsync(bomItem, actor, cancellationToken);
        var review = await repository.RecordDrawingReviewWritebackResultAsync(id, result.Id, true, cancellationToken);
        await AuditAsync(actor, "cad-property-writeback.complete", nameof(CadPropertyWriteback), id.ToString(), result.Revision.Display, cancellationToken);
        if (review?.State == DrawingReviewPackageState.Approved)
            await AuditAsync(actor, "drawing-review.approved", nameof(DrawingReviewPackage), review.Id.ToString(), $"{review.Number}；3D和2D审核属性已写回", cancellationToken);
        return updated;
    }

    public async Task<CadPropertyWriteback> FailCadPropertyWritebackAsync(Guid id, string error, bool conflict, string actor, UserRole role, CancellationToken cancellationToken)
    {
        _ = await RequireCadPropertyWritebackAccessAsync(id, actor, role, cancellationToken);
        var status = conflict ? CadPropertyWritebackStatus.Conflict : CadPropertyWritebackStatus.Failed;
        var updated = await repository.UpdateCadPropertyWritebackAsync(id, status, null, RequiredReason(error), cancellationToken);
        await repository.RecordDrawingReviewWritebackResultAsync(id, null, false, cancellationToken);
        await AuditAsync(actor, "cad-property-writeback.fail", nameof(CadPropertyWriteback), id.ToString(), $"{status}:{error}", cancellationToken);
        return updated;
    }

    public async Task<BomEmptyDeclaration> SetBomEmptyDeclarationAsync(Guid projectId, BomKind kind, bool declaredEmpty, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.BomEdit, cancellationToken);
        await EnsureBomChangeAllowedAsync(projectId, cancellationToken, kind);
        if (kind is not (BomKind.Standard or BomKind.NonStandard or BomKind.Electrical))
            throw new PdmRuleException("BOM类型必须是标准件、非标件或电气。");
        if (declaredEmpty)
        {
            var items = await repository.GetBomAsync(projectId, kind, cancellationToken);
            if (items.Any(item => !item.IsPendingRemoval && !item.IsManuallyExcluded)) throw new PdmRuleException("当前分类仍有有效物料，不能声明为空。");
        }
        var result = await repository.SetBomEmptyDeclarationAsync(projectId, kind, declaredEmpty, actor, cancellationToken);
        await SyncBomDraftAsync(projectId, kind, actor, cancellationToken);
        await AuditAsync(actor, "bom.empty-declaration", nameof(BomItem), projectId.ToString(), $"{kind}:{declaredEmpty}", cancellationToken);
        return result;
    }

    public async Task<ReleasePackage> SubmitReleasePackageAsync(Guid releasePackageId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ReleaseManage, cancellationToken);
        var package = await repository.FindReleasePackageAsync(releasePackageId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包不存在。");
        var project = await repository.FindProjectAsync(package.ProjectId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包对应的项目不存在。");
        var validationRules = (await repository.GetSystemSettingsAsync(cancellationToken)).ValidationRules;
        var changeNumber = package.ChangeNumber ?? package.Number;
        var changeReason = package.ChangeReason ?? "兼容既有发布流程创建的设变";
        var effectiveSerialFrom = package.EffectiveSerialFrom ?? project.SerialNumbers.FirstOrDefault() ?? "未指定";
        var versionsForReview = new List<BomVersion>();

        if (package.Scope == ReleaseScope.LegacyCombined)
        {
            var standard = (await repository.GetBomAsync(package.ProjectId, BomKind.Standard, cancellationToken)).Where(item => !item.IsManuallyExcluded).ToArray();
            var nonStandard = (await repository.GetBomAsync(package.ProjectId, BomKind.NonStandard, cancellationToken)).Where(item => !item.IsManuallyExcluded).ToArray();
            var electrical = (await repository.GetBomAsync(package.ProjectId, BomKind.Electrical, cancellationToken)).Where(item => !item.IsManuallyExcluded).ToArray();
            var unclassified = await repository.GetBomAsync(package.ProjectId, BomKind.Unclassified, cancellationToken);
            await EnsureStandardMaterialMasterReadyAsync(standard, cancellationToken);
            if (unclassified.Any(item => !item.IsManuallyExcluded)
                || !BomReady(BomKind.Standard, standard, validationRules)
                || !BomReady(BomKind.NonStandard, nonStandard, validationRules)
                || !BomReady(BomKind.Electrical, electrical, validationRules))
            {
                var missing = new[]
                {
                    MissingBomSummary(BomKind.Standard, standard, validationRules),
                    MissingBomSummary(BomKind.NonStandard, nonStandard, validationRules),
                    MissingBomSummary(BomKind.Electrical, electrical, validationRules)
                }.Where(summary => summary is not null).ToArray();
                var detail = missing.Length == 0 ? string.Empty : $" 缺项：{string.Join("；", missing)}。";
                throw new PdmRuleException($"三个独立BOM中仍有待处理或资料不完整的物料，不能提交设变审批。{detail}");
            }
            var standardVersion = await ResolveBomVersionForReleaseAsync(package.ProjectId, BomKind.Standard, standard, actor, changeNumber, changeReason, effectiveSerialFrom, package.EffectiveSerialTo, validationRules, cancellationToken);
            var nonStandardVersion = await ResolveBomVersionForReleaseAsync(package.ProjectId, BomKind.NonStandard, nonStandard, actor, changeNumber, changeReason, effectiveSerialFrom, package.EffectiveSerialTo, validationRules, cancellationToken);
            var electricalVersion = await ResolveBomVersionForReleaseAsync(package.ProjectId, BomKind.Electrical, electrical, actor, changeNumber, changeReason, effectiveSerialFrom, package.EffectiveSerialTo, validationRules, cancellationToken);
            package = await repository.UpdateReleasePackageBomVersionsAsync(package.Id, standardVersion, nonStandardVersion, electricalVersion, cancellationToken);
            versionsForReview.AddRange([standardVersion, nonStandardVersion, electricalVersion]);
        }
        else if (package.Scope == ReleaseScope.StandardLongLead)
        {
            await EnsureStandardMaterialMasterReadyAsync(package.StandardBomSnapshot, cancellationToken);
            if (!BomReady(BomKind.Standard, package.StandardBomSnapshot, validationRules))
                throw new PdmRuleException("长交期标准件清单中仍有资料不完整的物料，不能提交审批。");
        }
        else
        {
            var targetKind = ReleaseScopeBomKind(package.Scope);
            var targetItems = (await repository.GetBomAsync(package.ProjectId, targetKind, cancellationToken)).Where(item => !item.IsManuallyExcluded).ToArray();
            if (targetKind == BomKind.Standard) await EnsureStandardMaterialMasterReadyAsync(targetItems, cancellationToken);
            if (!BomReady(targetKind, targetItems, validationRules))
                throw new PdmRuleException($"{BomKindLabel(targetKind)}BOM仍有资料不完整的物料，不能提交审批。");
            if (targetKind != BomKind.Electrical && (await repository.GetBomAsync(package.ProjectId, BomKind.Unclassified, cancellationToken)).Any(item => !item.IsManuallyExcluded))
                throw new PdmRuleException("源数据中仍有待分类或待确认物料，请处理完成后再提交机械发布包。");
            var targetVersionId = targetKind == BomKind.Standard ? package.StandardBomVersionId
                : targetKind == BomKind.NonStandard ? package.NonStandardBomVersionId
                : package.ElectricalBomVersionId;
            var targetVersion = targetVersionId.HasValue
                ? await repository.FindBomVersionAsync(package.ProjectId, targetVersionId.Value, cancellationToken)
                : null;
            if (targetVersion is null) throw new PdmConflictException($"发布包绑定的{BomKindLabel(targetKind)}BOM版本不存在。");
            if (!BomSnapshotsEqual(targetVersion.Items, targetItems))
                throw new PdmConflictException($"{BomKindLabel(targetKind)}BOM在发布包创建后已变化，请撤销该草稿并重新创建。");
            versionsForReview.Add(targetVersion);
        }
        if (package.Scope is ReleaseScope.LegacyCombined or ReleaseScope.NonStandardWithDrawing)
            await EnsureNonStandardDrawingReviewReadyAsync(package.ProjectId, package.NonStandardBomSnapshot, cancellationToken);
        await publisher.PrepareAsync(package, project, cancellationToken);
        await publisher.ValidateAsync(package, project, cancellationToken);
        var submitted = await repository.SubmitReleasePackageAsync(releasePackageId, actor, cancellationToken);
        var drafts = versionsForReview.Where(version => version.State == BomVersionState.Draft).Select(version => version.Id).Distinct().ToArray();
        await repository.SetBomVersionStateAsync(drafts, BomVersionState.InReview, actor, null, cancellationToken);
        await AuditAsync(actor, package.State == ReleasePackageState.Rejected ? "release-package.resubmit" : "release-package.submit", nameof(ReleasePackage), package.Id.ToString(), $"{package.Number}；{package.Scope}", cancellationToken);
        return submitted;
    }

    public async Task<ReleasePackage> WithdrawReleasePackageAsync(Guid releasePackageId, string actor, UserRole role, string comment, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ReleaseManage, cancellationToken);
        comment = RequiredComment(comment, "撤回原因");
        var package = await repository.FindReleasePackageAsync(releasePackageId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包不存在。");
        if (!await repository.HasProjectContentReadAccessAsync(package.ProjectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的操作权限。");
        var withdrawn = await repository.WithdrawReleasePackageAsync(releasePackageId, actor, cancellationToken);
        await repository.SetBomVersionStateAsync(await PackageBomVersionIdsInStateAsync(package, BomVersionState.InReview, cancellationToken), BomVersionState.Draft, actor, null, cancellationToken);
        await AuditAsync(actor, "release-package.withdraw", nameof(ReleasePackage), package.Id.ToString(), $"{package.Number}；{comment}", cancellationToken);
        return withdrawn;
    }

    public async Task<PdmDocument> ObsoleteDocumentAsync(Guid documentId, string actor, UserRole role, string comment, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ReleaseManage, cancellationToken);
        await RequireDocumentAccessAsync(documentId, actor, role, FolderAccess.View | FolderAccess.Edit | FolderAccess.Publish, cancellationToken);
        comment = RequiredComment(comment, "作废原因");
        var obsolete = await repository.ObsoleteDocumentAsync(documentId, actor, cancellationToken);
        await AuditAsync(actor, "document.obsolete", nameof(PdmDocument), documentId.ToString(), $"{obsolete.DrawingNumber}；{comment}", cancellationToken);
        return obsolete;
    }

    public async Task<IReadOnlyList<DocumentWhereUsed>> ListWhereUsedAsync(Guid documentId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireDocumentReadAccessAsync(documentId, actor, role, cancellationToken);
        var usages = await repository.ListWhereUsedAsync(documentId, cancellationToken);
        var visible = new List<DocumentWhereUsed>();
        foreach (var usage in usages)
        {
            if (await repository.HasProjectContentReadAccessAsync(usage.ProjectId, actor, role, cancellationToken)) visible.Add(usage);
        }
        return visible;
    }

    public async Task<ReleasePackage> DecideAsync(Guid taskId, string actor, UserRole role, ApprovalDecision decision, string? comment, CancellationToken cancellationToken)
    {
        await EnsureApprovalTaskActorAsync(taskId, actor, cancellationToken);
        return await CompleteApprovalDecisionAsync(taskId, actor, decision, comment, false, null, cancellationToken);
    }

    public async Task EnsureApprovalTaskActorAsync(Guid taskId, string actor, CancellationToken cancellationToken)
    {
        var package = await repository.FindReleasePackageByApprovalTaskAsync(taskId, cancellationToken)
            ?? throw new PdmNotFoundException("审批任务不存在。");
        var task = package.ApprovalTasks.FirstOrDefault(item => item.Id == taskId)
            ?? throw new PdmNotFoundException("审批任务不存在。");
        var currentTask = package.ApprovalTasks.OrderBy(item => item.StepOrder).FirstOrDefault(item => item.Decision is null);
        if (task.Decision is not null || currentTask?.Id != taskId || package.State is not (ReleasePackageState.ProcessReview or ReleasePackageState.Approval))
            throw new PdmConflictException("当前发布包尚未到达该审批节点。 ");
        if (!string.Equals(task.Assignee, actor, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(actor, "admin", StringComparison.OrdinalIgnoreCase))
            throw new PdmRuleException("只能处理分配给自己的审批任务。 ");
    }

    public async Task<IReadOnlyList<ApprovalTransferCandidate>> ListApprovalTransferCandidatesAsync(
        Guid taskId, string actor, CancellationToken cancellationToken)
    {
        await EnsureApprovalTaskActorAsync(taskId, actor, cancellationToken);
        var package = await repository.FindReleasePackageByApprovalTaskAsync(taskId, cancellationToken)
            ?? throw new PdmNotFoundException("审批任务不存在。");
        var candidates = new List<ApprovalTransferCandidate>();
        foreach (var user in await repository.ListUsersAsync(cancellationToken))
        {
            if (!user.IsActive || string.Equals(user.Username, actor, StringComparison.OrdinalIgnoreCase)) continue;
            if (!await repository.HasUserPermissionAsync(user.Username, user.Role, PermissionCodes.ApprovalDecide, cancellationToken)) continue;
            if (!await repository.HasProjectContentReadAccessAsync(package.ProjectId, user.Username, user.Role, cancellationToken)) continue;
            candidates.Add(new ApprovalTransferCandidate(user.Username, user.DisplayName));
        }
        return candidates.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<ReleasePackage> TransferApprovalAsync(
        Guid taskId, string actor, string targetUsername, string? comment, CancellationToken cancellationToken)
    {
        await EnsureApprovalTaskActorAsync(taskId, actor, cancellationToken);
        targetUsername = targetUsername?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(targetUsername)) throw new PdmRuleException("请选择转交人员。");
        var package = await repository.FindReleasePackageByApprovalTaskAsync(taskId, cancellationToken)
            ?? throw new PdmNotFoundException("审批任务不存在。");
        var target = await repository.FindUserAsync(targetUsername, cancellationToken)
            ?? throw new PdmNotFoundException("转交目标用户不存在。");
        if (!target.IsActive) throw new PdmRuleException("转交目标用户已停用。");
        if (!await repository.HasUserPermissionAsync(target.Username, target.Role, PermissionCodes.ApprovalDecide, cancellationToken))
            throw new PdmRuleException("转交目标用户没有审批权限。");
        if (!await repository.HasProjectContentReadAccessAsync(package.ProjectId, target.Username, target.Role, cancellationToken))
            throw new PdmRuleException("转交目标用户没有该项目的访问权限。");

        var transferred = await repository.TransferApprovalAsync(taskId, actor, target.Username, cancellationToken);
        await AuditAsync(actor, "approval.transfer", nameof(ApprovalTask), taskId.ToString(),
            $"{actor} -> {target.Username}{(string.IsNullOrWhiteSpace(comment) ? string.Empty : $"；{comment.Trim()}")}", cancellationToken);
        return transferred;
    }

    public async Task<IReadOnlyList<Guid>> GetNonStandardItemsForFinalApprovalAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var package = await repository.FindReleasePackageByApprovalTaskAsync(taskId, cancellationToken)
            ?? throw new PdmNotFoundException("审批任务不存在。");
        if (package.Scope is not (ReleaseScope.NonStandardWithDrawing or ReleaseScope.LegacyCombined)) return [];
        var task = package.ApprovalTasks.FirstOrDefault(item => item.Id == taskId)
            ?? throw new PdmNotFoundException("审批任务不存在。");
        if (task.Decision is not null || task.StepOrder != package.ApprovalTasks.Max(item => item.StepOrder)) return [];
        if (package.ApprovalTasks.Any(item => item.StepOrder < task.StepOrder && item.Decision != ApprovalDecision.Approved)) return [];
        var selectedIds = package.SelectedBomItemIds.Count > 0
            ? package.SelectedBomItemIds.ToHashSet()
            : package.NonStandardBomSnapshot.Select(item => item.Id).ToHashSet();
        var ids = new List<Guid>();
        foreach (var itemId in selectedIds)
        {
            var current = await repository.FindBomItemAsync(package.ProjectId, itemId, cancellationToken);
            if (current is { Kind: BomKind.NonStandard } && string.IsNullOrWhiteSpace(current.DrawingNumber)) ids.Add(itemId);
        }
        return ids;
    }

    public async Task<ReleasePackage> FindReleasePackageByApprovalTaskAsync(Guid taskId, CancellationToken cancellationToken) =>
        await repository.FindReleasePackageByApprovalTaskAsync(taskId, cancellationToken)
        ?? throw new PdmNotFoundException("审批任务不存在。");

    public Task<ReleasePackage> ApplyReleasePackageMaterialCodesAsync(Guid releasePackageId, IReadOnlyDictionary<Guid, string> materialCodes, string actor, CancellationToken cancellationToken) =>
        repository.ApplyReleasePackageMaterialCodesAsync(releasePackageId, materialCodes, actor, cancellationToken);

    public async Task<ReleasePackage> EmergencyDecideAsync(Guid taskId, string actor, UserRole role, ApprovalDecision decision, string reason, CancellationToken cancellationToken)
    {
        await RequirePermissionAsync(actor, role, PermissionCodes.ApprovalEmergencySubstitute, cancellationToken);
        reason = RequiredComment(reason, "紧急代批原因");
        return await CompleteApprovalDecisionAsync(taskId, actor, decision, reason, true, reason, cancellationToken);
    }

    private async Task<ReleasePackage> CompleteApprovalDecisionAsync(
        Guid taskId,
        string actor,
        ApprovalDecision decision,
        string? comment,
        bool emergencySubstitute,
        string? emergencyReason,
        CancellationToken cancellationToken)
    {
        comment = decision == ApprovalDecision.Rejected
            ? RequiredComment(comment, "退回原因")
            : string.IsNullOrWhiteSpace(comment) ? "同意" : comment.Trim();
        var pendingPackage = await repository.FindReleasePackageByApprovalTaskAsync(taskId, cancellationToken)
            ?? throw new PdmNotFoundException("审批任务不存在。");
        if (decision == ApprovalDecision.Approved
            && pendingPackage.Scope is ReleaseScope.LegacyCombined or ReleaseScope.NonStandardWithDrawing)
            await EnsureNonStandardDrawingReviewReadyAsync(pendingPackage.ProjectId, pendingPackage.NonStandardBomSnapshot, cancellationToken);
        var package = await repository.DecideApprovalAsync(taskId, actor, decision, comment, emergencySubstitute, emergencyReason, cancellationToken);
        await AuditAsync(actor, emergencySubstitute ? "approval.emergency-substitute" : "approval.decide", nameof(ApprovalTask), taskId.ToString(), emergencySubstitute ? $"{decision}；原因：{emergencyReason}" : decision.ToString(), cancellationToken);

        if (package.State != ReleasePackageState.Publishing)
        {
            if (package.State == ReleasePackageState.Rejected)
            {
                await repository.SetBomVersionStateAsync(await PackageBomVersionIdsInStateAsync(package, BomVersionState.InReview, cancellationToken), BomVersionState.Draft, actor, null, cancellationToken);
                await CreateReleaseRejectedNotificationsAsync(package, taskId, actor, comment, cancellationToken);
            }
            return package;
        }

        var project = await repository.FindProjectAsync(package.ProjectId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包对应的项目不存在。 ");
        ReleasePublication publication;
        try
        {
            var previewSources = package.LocksDocuments
                ? await repository.ListReleasePreviewSourcesAsync(package.Id, cancellationToken)
                : [];
            publication = await publisher.PublishAsync(package, project, previewSources, cancellationToken);
        }
        catch (Exception exception)
        {
            await repository.MarkPublishFailedAsync(package.Id, exception.Message, cancellationToken);
            await AuditAsync(actor, "release-package.publish-failed", nameof(ReleasePackage), package.Id.ToString(), exception.Message, cancellationToken);
            throw;
        }

        var publishedAt = timeProvider.GetUtcNow();
        var publishedPath = publication.PublishedPath;
        var finalApprovalTask = package.ApprovalTasks.OrderBy(task => task.StepOrder).Last(task => task.Decision == ApprovalDecision.Approved);
        var releasedVersions = package.LocksDocuments
            ? await repository.PublishReleasePackageVersionsAsync(package.Id, finalApprovalTask.Id, actor, publication.Previews, cancellationToken)
            : [];
        foreach (var version in releasedVersions)
        {
            await AuditAsync(actor, "document.version.publish", nameof(DocumentVersion), version.Id.ToString(), version.Revision.Display, cancellationToken);
        }
        if (package.CreatesManufacturingBaseline)
        {
            var baseline = await repository.MarkPublishedWithBomBaselineAsync(package, publishedPath, publishedAt, actor, cancellationToken);
            await AuditAsync(actor, "bom-baseline.publish", nameof(ManufacturingBomBaseline), baseline.Id.ToString(), $"{baseline.Label}:{baseline.ChangeNumber}:{baseline.EffectiveSerialFrom}-{baseline.EffectiveSerialTo ?? "以后"}", cancellationToken);
        }
        else
        {
            await repository.MarkPublishedAsync(package.Id, publishedPath, publishedAt, cancellationToken);
            await AuditAsync(actor, "release-package.long-lead-output", nameof(ReleasePackage), package.Id.ToString(), $"{package.Number}；标准件{package.StandardBomSnapshot.Count}项；待U9C接口消费", cancellationToken);
        }
        await AuditAsync(actor, "release-package.publish", nameof(ReleasePackage), package.Id.ToString(), publishedPath, cancellationToken);
        if (bomHeaderService is not null)
        {
            foreach (var approvedKind in ApprovedBomHeaderKinds(package.Scope))
            {
                try
                {
                    var generated = await bomHeaderService.EnsureApplicationsAfterBomApprovalAsync(project.Id, approvedKind, actor, cancellationToken);
                    await AuditAsync(actor, "bom.header.application.auto-trigger", nameof(ReleasePackage), package.Id.ToString(),
                        $"{approvedKind}BOM批准后自动申请：新增{generated.GeneratedCount}项，已存在{generated.ExistingCount}项。", cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    await AuditAsync(actor, "bom.header.application.auto-trigger-failed", nameof(ReleasePackage), package.Id.ToString(),
                        $"{approvedKind}BOM已批准发布，但自动创建BOM料号申请失败：{exception.Message}", cancellationToken);
                }
            }
        }
        if (approvalU9Automation is not null)
        {
            try
            {
                var automation = await approvalU9Automation.ContinueAfterMaterialSyncAsync(project.Id, actor, cancellationToken);
                await AuditAsync(actor, "u9.bom-approval-automation", nameof(ReleasePackage), package.Id.ToString(),
                    $"BOM审核发布后自动同步：{automation.Stage}；{automation.Message}", cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await AuditAsync(actor, "u9.bom-approval-automation-failed", nameof(ReleasePackage), package.Id.ToString(),
                    $"BOM已审核发布，但U9C子件自动同步失败：{exception.Message}", cancellationToken);
            }
        }
        return (await repository.FindReleasePackageAsync(package.Id, cancellationToken))!;
    }

    private static IReadOnlyList<ProjectBomHeaderKind> ApprovedBomHeaderKinds(ReleaseScope scope) => scope switch
    {
        ReleaseScope.StandardLongLead or ReleaseScope.StandardFormal or ReleaseScope.StandardSupplement => [ProjectBomHeaderKind.Standard],
        ReleaseScope.NonStandardWithDrawing => [ProjectBomHeaderKind.NonStandard],
        ReleaseScope.ElectricalFormal or ReleaseScope.ElectricalSupplement => [ProjectBomHeaderKind.Electrical],
        ReleaseScope.LegacyCombined =>
            [ProjectBomHeaderKind.Standard, ProjectBomHeaderKind.NonStandard, ProjectBomHeaderKind.Electrical],
        _ => []
    };

    private static void ValidateCheckoutSettings(PdmSystemSettings settings)
    {
        if (settings.CheckoutHeartbeatSeconds is < 30 or > 600) throw new PdmRuleException("编辑心跳间隔必须为30到600秒。");
        if (settings.CheckoutLeaseMinutes is < 2 or > 60 || settings.CheckoutLeaseMinutes * 60 < settings.CheckoutHeartbeatSeconds * 2)
            throw new PdmRuleException("编辑租约必须为2到60分钟，且至少是心跳间隔的两倍。");
        if (settings.CheckoutOfflineGraceMinutes is < 5 or > 1440) throw new PdmRuleException("离线宽限必须为5到1440分钟。");
        if (settings.CheckoutReminderHours < 1
            || settings.CheckoutStrongReminderHours <= settings.CheckoutReminderHours
            || settings.CheckoutOverdueHours <= settings.CheckoutStrongReminderHours
            || settings.CheckoutForceReleaseHours <= settings.CheckoutOverdueHours
            || settings.CheckoutForceReleaseHours > 720)
            throw new PdmRuleException("提醒、强提醒、超时和强制释放时间必须依次增大，且不超过720小时。");
    }

    private static EditLockAttentionLevel AttentionLevel(TimeSpan elapsed, PdmSystemSettings settings) =>
        elapsed >= TimeSpan.FromHours(settings.CheckoutForceReleaseHours) ? EditLockAttentionLevel.Reclaimable
        : elapsed >= TimeSpan.FromHours(settings.CheckoutOverdueHours) ? EditLockAttentionLevel.Overdue
        : elapsed >= TimeSpan.FromHours(settings.CheckoutStrongReminderHours) ? EditLockAttentionLevel.StrongReminder
        : elapsed >= TimeSpan.FromHours(settings.CheckoutReminderHours) ? EditLockAttentionLevel.Reminder
        : EditLockAttentionLevel.Normal;

    private async Task<BomGenerationResult> GenerateMechanicalBomFromSnapshotAsync(
        Guid projectId,
        CadReferenceSnapshot snapshot,
        string actor,
        CancellationToken cancellationToken,
        bool apply,
        bool preserveManualOverrides = true)
    {
        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        var documents = (await repository.ListDocumentsAsync(projectId, cancellationToken)).ToDictionary(item => item.Id);
        var electrical = (await repository.GetBomAsync(projectId, BomKind.Electrical, cancellationToken)).ToArray();
        var existing = (await repository.GetBomAsync(projectId, BomKind.Standard, cancellationToken))
            .Concat(await repository.GetBomAsync(projectId, BomKind.NonStandard, cancellationToken))
            .Concat(await repository.GetBomAsync(projectId, BomKind.Unclassified, cancellationToken))
            .Concat(await repository.GetBomAsync(projectId, BomKind.Virtual, cancellationToken))
            .ToArray();
        var sourceCache = new Dictionary<Guid, (PdmDocument Document, DocumentVersion? Version)>();
        var candidates = new List<BomItem>();
        var virtualCount = 0;
        var reconciliationTime = timeProvider.GetUtcNow();

        async Task<(PdmDocument Document, DocumentVersion? Version)?> SourceAsync(Guid? documentId)
        {
            if (!documentId.HasValue || !documents.TryGetValue(documentId.Value, out var document)) return null;
            if (sourceCache.TryGetValue(documentId.Value, out var cached)) return cached;
            var latest = (await repository.ListDocumentVersionsAsync(documentId.Value, cancellationToken)).FirstOrDefault();
            var result = (document, latest);
            sourceCache[documentId.Value] = result;
            return result;
        }

        async Task VisitAsync(DocumentReferenceNode node, decimal parentQuantity, bool isRoot, string? parentDrawingNumber)
        {
            if (node.Status == ReferenceNodeStatus.Suppressed || node.Status == ReferenceNodeStatus.Missing) return;
            var quantity = parentQuantity * Math.Max(node.Quantity, 1);
            if (node.Kind == DocumentKind.Drawing) return;

            var source = await SourceAsync(node.DocumentId);
            var properties = source?.Version?.PropertySnapshot;
            var currentDrawingNumber = source.HasValue
                ? PropertyValue(properties, node.Configuration, settings.BomDrawingNumberProperty) ?? string.Empty
                : parentDrawingNumber;
            var classificationProperty = BomPropertyMappingCatalog.SolidWorksProperty(settings, "kind", "物料分类");
            var classification = PropertyValue(properties, node.Configuration, classificationProperty);
            var isVirtual = node.Status == ReferenceNodeStatus.Virtual || string.Equals(classification, "虚拟件", StringComparison.OrdinalIgnoreCase);
            if (isVirtual) virtualCount++;

            var kind = isVirtual ? BomKind.Virtual
                : string.Equals(classification, "标准件", StringComparison.OrdinalIgnoreCase) ? BomKind.Standard
                : string.Equals(classification, "非标件", StringComparison.OrdinalIgnoreCase) ? BomKind.NonStandard
                : (BomKind?)null;

            var includeCurrent = !isRoot && source.HasValue;
            var purchasedAssembly = !isRoot && source.HasValue && node.Kind == DocumentKind.Assembly && kind == BomKind.Standard;
            if (includeCurrent)
            {
                var (document, version) = source!.Value;
                var previous = existing.FirstOrDefault(candidate => SameBomSource(document.Id, node.Configuration, node.InstancePath, candidate));
                var hasManualClassification = previous?.IsManuallyOverridden == true
                    && !previous.IsPendingClassification
                    && previous.Kind is BomKind.Standard or BomKind.NonStandard or BomKind.Virtual;
                var resolvedKind = preserveManualOverrides && hasManualClassification
                    ? previous!.Kind
                    : kind ?? BomKind.Unclassified;
                var drawingNumber = currentDrawingNumber ?? string.Empty;
                var name = PropertyValue(properties, node.Configuration, settings.BomNameProperty)
                    ?? string.Empty;
                var remark = PropertyValue(properties, node.Configuration, settings.BomDescriptionProperty);
                var brand = PropertyValue(properties, node.Configuration, settings.BomBrandProperty);
                var material = PropertyValue(properties, node.Configuration, settings.BomMaterialProperty);
                var specification = PropertyValue(properties, node.Configuration, settings.BomSpecificationProperty);
                var unit = PropertyValue(properties, node.Configuration, settings.BomUnitProperty) ?? "个";
                var surfaceTreatment = PropertyValue(properties, node.Configuration, settings.BomSurfaceTreatmentProperty);
                var heatTreatment = PropertyValue(properties, node.Configuration, BomPropertyMappingCatalog.SolidWorksProperty(settings, "heatTreatment", "热处理"));
                var weight = PropertyValue(properties, node.Configuration, settings.BomWeightProperty);
                var revision = document.Revision.Display;
                var reconciliationStatus = previous?.ReconciliationStatus;
                var reconciliationNote = previous?.ReconciliationNote;
                var reconciliationUpdatedBy = previous?.ReconciliationUpdatedBy;
                var reconciliationUpdatedAt = previous?.ReconciliationUpdatedAt;
                if (!kind.HasValue && !(preserveManualOverrides && hasManualClassification))
                {
                    reconciliationStatus = ReconcilePendingClassification;
                    reconciliationNote = "图档源数据未填写有效的物料分类，等待人工归入标准件或非标件BOM。";
                    reconciliationUpdatedBy = actor;
                    reconciliationUpdatedAt = reconciliationTime;
                }
                else if (!kind.HasValue)
                {
                    if (!string.Equals(reconciliationStatus, ReconcileManuallyClassified, StringComparison.Ordinal))
                    {
                        reconciliationStatus = ReconcileManuallyClassified;
                        reconciliationNote = $"图档源数据未填写有效分类，继续沿用人工分类：{BomKindLabel(resolvedKind)}。";
                        reconciliationUpdatedBy = actor;
                        reconciliationUpdatedAt = reconciliationTime;
                    }
                }
                else if (previous is null)
                {
                    reconciliationStatus = ReconcileAutoAdded;
                    reconciliationNote = resolvedKind == BomKind.Virtual
                        ? "图档源数据新增，已识别为虚拟件，仅保留在源数据中。"
                        : $"图档源数据新增，已根据物料分类自动进入{BomKindLabel(resolvedKind)}BOM。";
                    reconciliationUpdatedBy = actor;
                    reconciliationUpdatedAt = reconciliationTime;
                }
                else if (previous.IsPendingRemoval || previous.IsManualUnmatched)
                {
                    reconciliationStatus = ReconcileRestored;
                    reconciliationNote = resolvedKind == BomKind.Virtual
                        ? "图档源数据中已重新出现，已恢复为仅保留在源数据中的虚拟件。"
                        : $"图档源数据中已重新出现，已恢复到{BomKindLabel(resolvedKind)}BOM。";
                    reconciliationUpdatedBy = actor;
                    reconciliationUpdatedAt = reconciliationTime;
                }
                else if (previous.IsPendingClassification || previous.Kind != resolvedKind)
                {
                    reconciliationStatus = ReconcileClassificationChanged;
                    reconciliationNote = previous.IsPendingClassification
                        ? resolvedKind == BomKind.Virtual
                            ? "图档源数据已补充分类，已识别为虚拟件，仅保留在源数据中。"
                            : $"图档源数据已补充分类，已自动进入{BomKindLabel(resolvedKind)}BOM。"
                        : resolvedKind == BomKind.Virtual
                            ? $"图档源数据分类由{BomKindLabel(previous.Kind)}变更为虚拟件，已移出维护BOM。"
                            : $"图档源数据分类由{BomKindLabel(previous.Kind)}变更为{BomKindLabel(resolvedKind)}，已自动迁移。";
                    reconciliationUpdatedBy = actor;
                    reconciliationUpdatedAt = reconciliationTime;
                }
                var candidate = new BomItem(Guid.NewGuid(), projectId, resolvedKind, 0, drawingNumber.Trim(), name.Trim(), quantity, U9UnitCatalog.NormalizeBomUnit(unit), NullIfWhiteSpace(material), NullIfWhiteSpace(specification), revision, false)
                {
                    Remark = NullIfWhiteSpace(remark),
                    Brand = NullIfWhiteSpace(brand),
                    SurfaceTreatment = NullIfWhiteSpace(surfaceTreatment),
                    HeatTreatment = NullIfWhiteSpace(heatTreatment),
                    Weight = NullIfWhiteSpace(weight),
                    SourceDocumentId = document.Id,
                    SourceConfiguration = NullIfWhiteSpace(node.Configuration),
                    SourceInstancePath = NullIfWhiteSpace(node.InstancePath),
                    ParentDrawingNumber = NullIfWhiteSpace(parentDrawingNumber),
                    Source = "Auto",
                    IsPendingClassification = !kind.HasValue && !(preserveManualOverrides && hasManualClassification),
                    ReconciliationStatus = reconciliationStatus,
                    ReconciliationNote = reconciliationNote,
                    ReconciliationUpdatedBy = reconciliationUpdatedBy,
                    ReconciliationUpdatedAt = reconciliationUpdatedAt
                };
                if (preserveManualOverrides && previous?.IsManuallyOverridden == true)
                {
                    var differences = SourceDataDifferences(previous, candidate).ToList();
                    var sourceKind = kind ?? BomKind.Unclassified;
                    if (previous.Kind != sourceKind && !differences.Contains("物料分类", StringComparer.Ordinal))
                        differences.Insert(0, "物料分类");
                    if (differences.Count > 0)
                    {
                        candidate = candidate with
                        {
                            ReconciliationStatus = "ManualOverrideMismatch",
                            ReconciliationNote = $"BOM维护值与最新图档源数据不一致：{string.Join('、', differences)}。",
                            ReconciliationUpdatedBy = actor,
                            ReconciliationUpdatedAt = reconciliationTime
                        };
                    }
                    else if (string.Equals(previous.ReconciliationStatus, "ManualOverrideMismatch", StringComparison.Ordinal))
                    {
                        candidate = candidate with
                        {
                            ReconciliationStatus = "SourceMatched",
                            ReconciliationNote = "BOM维护值已与最新图档源数据一致。",
                            ReconciliationUpdatedBy = actor,
                            ReconciliationUpdatedAt = reconciliationTime
                        };
                    }
                }
                candidates.Add(candidate with
                {
                    IsComplete = (kind.HasValue || preserveManualOverrides && hasManualClassification)
                        && HasRequiredBomValues(candidate, resolvedKind, settings.ValidationRules)
                });
            }

            if (isVirtual)
                foreach (var child in node.Children) await VisitAsync(child, quantity, false, parentDrawingNumber);
            else if (!purchasedAssembly)
                foreach (var child in node.Children) await VisitAsync(child, quantity, false, currentDrawingNumber);
        }

        await VisitAsync(snapshot.Root, 1, true, null);
        var generated = candidates
            .GroupBy(item => $"{item.Kind}|{BomSourceKey(item)}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First() with { Quantity = group.Sum(item => item.Quantity) })
            .ToArray();
        var matchedIds = new HashSet<Guid>();
        var merged = new List<BomItem>();
        foreach (var item in generated)
        {
            var previous = existing.FirstOrDefault(candidate => !matchedIds.Contains(candidate.Id) && SameBomSource(item, candidate));
            if (previous is not null) matchedIds.Add(previous.Id);
            merged.Add(preserveManualOverrides && previous?.IsManuallyOverridden == true
                ? previous with
                {
                    Kind = item.Kind,
                    Quantity = item.Quantity,
                    Revision = item.Revision,
                    SourceInstancePath = item.SourceInstancePath,
                    ParentDrawingNumber = item.ParentDrawingNumber,
                    IsPendingRemoval = false,
                    IsPendingClassification = item.IsPendingClassification,
                    IsManualUnmatched = false,
                    ReconciliationStatus = item.ReconciliationStatus,
                    ReconciliationNote = item.ReconciliationNote,
                    ReconciliationUpdatedBy = item.ReconciliationUpdatedBy,
                    ReconciliationUpdatedAt = item.ReconciliationUpdatedAt
                }
                : item with
                {
                    Id = previous?.Id ?? item.Id,
                    IsManuallyOverridden = preserveManualOverrides && previous?.IsManuallyOverridden == true,
                    IsManuallyExcluded = preserveManualOverrides && previous?.IsManuallyExcluded == true,
                    DeletedAt = preserveManualOverrides ? previous?.DeletedAt : null,
                    DeletedBy = preserveManualOverrides ? previous?.DeletedBy : null,
                    DeleteReason = preserveManualOverrides ? previous?.DeleteReason : null,
                    PropertyWritebackStatus = preserveManualOverrides ? previous?.PropertyWritebackStatus : null
                });
        }
        merged.AddRange(existing.Where(item => !matchedIds.Contains(item.Id)).Select(item =>
            item.Source == "Auto"
                ? item with
                {
                    IsPendingRemoval = true,
                    IsComplete = false,
                    ReconciliationStatus = ReconcilePendingRemoval,
                    ReconciliationNote = "最新图档源数据中已不存在，等待确认删除或人工保留。",
                    ReconciliationUpdatedBy = actor,
                    ReconciliationUpdatedAt = reconciliationTime
                }
                : item.IsManuallyRetained ? item : item with
                {
                    IsManualUnmatched = true,
                    IsComplete = false,
                    ReconciliationStatus = ReconcileManualUnmatched,
                    ReconciliationNote = "BOM中存在，但最新图档源数据中无对应项，等待确认删除或人工保留。",
                    ReconciliationUpdatedBy = actor,
                    ReconciliationUpdatedAt = reconciliationTime
                }));
        var unclassifiedCount = merged.Count(item => item.IsPendingClassification && !item.IsManuallyExcluded);

        static BomItem[] PrepareKind(IEnumerable<BomItem> items, BomKind kind) => items
            .Where(item => item.Kind == kind)
            .OrderBy(item => item.IsManuallyExcluded)
            .ThenBy(item => item.IsPendingRemoval)
            .ThenBy(item => item.DrawingNumber, StringComparer.OrdinalIgnoreCase)
            .Select((item, index) => item with { Sequence = index + 1 })
            .ToArray();

        var standard = PrepareKind(merged, BomKind.Standard);
        var nonStandard = PrepareKind(merged, BomKind.NonStandard);
        var unclassified = PrepareKind(merged, BomKind.Unclassified);
        var virtualItems = PrepareKind(merged, BomKind.Virtual);
        virtualCount = Math.Max(virtualCount, virtualItems.Length);
        var pendingRemovalCount = merged.Count(item => item.IsPendingRemoval && !item.IsManuallyExcluded);
        var manualUnmatchedCount = merged.Count(item => item.IsManualUnmatched && !item.IsManuallyExcluded);
        if (materialRepository is not null)
        {
            var materialCodes = standard
                .Where(item => !item.IsPendingRemoval && !item.IsManuallyExcluded && !string.IsNullOrWhiteSpace(item.DrawingNumber))
                .Select(item => item.DrawingNumber.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var materialGroups = (await materialRepository.FindMaterialsByCodesAsync(materialCodes, cancellationToken))
                .GroupBy(item => item.MaterialCode.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var duplicateCodes = materialGroups
                .Where(group => group.Skip(1).Any())
                .Select(group => group.Key)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (duplicateCodes.Length > 0)
                throw new PdmConflictException($"料品主档存在重复物料编码：{string.Join('、', duplicateCodes)}，无法保证物料号、型号、品牌一一对应，已取消BOM对账。");
            var materialByCode = materialGroups
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
            standard = standard.Select(item =>
            {
                if (item.IsPendingRemoval || item.IsManuallyExcluded) return item;
                var source = generated.FirstOrDefault(candidate => SameBomSource(candidate, item));
                var differences = source is null ? [] : SourceDataDifferences(item, source).ToList();
                materialByCode.TryGetValue(item.DrawingNumber.Trim(), out var material);
                differences.AddRange(MaterialService.StandardBomMaterialMasterDifferenceFields(item, material));
                differences = differences.Distinct(StringComparer.Ordinal).ToList();
                if (differences.Count > 0)
                {
                    return item with
                    {
                        ReconciliationStatus = "ManualOverrideMismatch",
                        ReconciliationNote = $"BOM维护值与最新图档源数据或料品主档不一致：{string.Join('、', differences)}。",
                        ReconciliationUpdatedBy = actor,
                        ReconciliationUpdatedAt = reconciliationTime
                    };
                }
                return string.Equals(item.ReconciliationStatus, "ManualOverrideMismatch", StringComparison.Ordinal)
                    ? item with
                    {
                        ReconciliationStatus = "SourceMatched",
                        ReconciliationNote = "BOM维护值已与最新图档源数据及料品主档一致。",
                        ReconciliationUpdatedBy = actor,
                        ReconciliationUpdatedAt = reconciliationTime
                    }
                    : item;
            }).ToArray();
        }
        if (apply)
        {
            await repository.ApplyBomBatchAsync(projectId, standard, nonStandard, unclassified, electrical, virtualItems, [], [], cancellationToken);
            await repository.SaveBomDraftAsync(projectId, BomKind.Standard, standard.Where(item => !item.IsManuallyExcluded).ToArray(), actor, cancellationToken);
            await repository.SaveBomDraftAsync(projectId, BomKind.NonStandard, nonStandard.Where(item => !item.IsManuallyExcluded).ToArray(), actor, cancellationToken);
            if (standard.Any(item => !item.IsPendingRemoval && !item.IsManuallyExcluded)) await repository.SetBomEmptyDeclarationAsync(projectId, BomKind.Standard, false, actor, cancellationToken);
            if (nonStandard.Any(item => !item.IsPendingRemoval && !item.IsManuallyExcluded)) await repository.SetBomEmptyDeclarationAsync(projectId, BomKind.NonStandard, false, actor, cancellationToken);
            await AuditAsync(actor, "bom.generate", nameof(BomItem), projectId.ToString(), $"标准件{standard.Length}；非标件{nonStandard.Length}；电气BOM独立维护；虚拟件{virtualCount}；待分类{unclassifiedCount}；待移除{pendingRemovalCount}；人工待确认{manualUnmatchedCount}", cancellationToken);
        }
        return new BomGenerationResult(standard, nonStandard, electrical, unclassified, virtualItems, virtualCount, unclassifiedCount, pendingRemovalCount, manualUnmatchedCount, apply);
    }

    private static IReadOnlyList<string> SourceDataDifferences(BomItem maintained, BomItem source)
    {
        var differences = new List<string>();
        static bool DifferentWhenSourcePresent(string? maintainedValue, string? sourceValue) =>
            !string.IsNullOrWhiteSpace(sourceValue)
            && !string.Equals(maintainedValue?.Trim() ?? string.Empty, sourceValue.Trim(), StringComparison.OrdinalIgnoreCase);
        if (source.Kind != BomKind.Unclassified && maintained.Kind != source.Kind) differences.Add("物料分类");
        if (maintained.Kind == BomKind.Standard)
        {
            if (DifferentWhenSourcePresent(maintained.Specification, source.Specification)) differences.Add("型号");
            if (DifferentWhenSourcePresent(maintained.Brand, source.Brand)) differences.Add("品牌");
        }
        else if (maintained.Kind == BomKind.NonStandard)
        {
            if (DifferentWhenSourcePresent(maintained.Specification, source.Specification)) differences.Add("型号");
            if (DifferentWhenSourcePresent(maintained.Material, source.Material)) differences.Add("材质");
            if (DifferentWhenSourcePresent(maintained.SurfaceTreatment, source.SurfaceTreatment)) differences.Add("表面处理");
        }
        return differences;
    }

    private async Task<BomItem> RefreshBomItemReconciliationAsync(BomItem item, string actor, CancellationToken cancellationToken)
    {
        if (!item.SourceDocumentId.HasValue || item.Kind is not (BomKind.Standard or BomKind.NonStandard)) return item;
        var latest = (await repository.ListDocumentVersionsAsync(item.SourceDocumentId.Value, cancellationToken)).FirstOrDefault();
        if (latest is null) return item;
        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        var configuration = item.SourceConfiguration ?? string.Empty;
        var classificationProperty = BomPropertyMappingCatalog.SolidWorksProperty(settings, "kind", "物料分类");
        var classification = PropertyValue(latest.PropertySnapshot, configuration, classificationProperty);
        var sourceKind = string.Equals(classification, "标准件", StringComparison.OrdinalIgnoreCase) ? BomKind.Standard
            : string.Equals(classification, "非标件", StringComparison.OrdinalIgnoreCase) ? BomKind.NonStandard
            : string.Equals(classification, "虚拟件", StringComparison.OrdinalIgnoreCase) ? BomKind.Virtual
            : BomKind.Unclassified;
        var source = item with
        {
            Kind = sourceKind,
            DrawingNumber = PropertyValue(latest.PropertySnapshot, configuration, settings.BomDrawingNumberProperty) ?? string.Empty,
            Specification = NullIfWhiteSpace(PropertyValue(latest.PropertySnapshot, configuration, settings.BomSpecificationProperty)),
            Brand = NullIfWhiteSpace(PropertyValue(latest.PropertySnapshot, configuration, settings.BomBrandProperty)),
            Material = NullIfWhiteSpace(PropertyValue(latest.PropertySnapshot, configuration, settings.BomMaterialProperty)),
            SurfaceTreatment = NullIfWhiteSpace(PropertyValue(latest.PropertySnapshot, configuration, settings.BomSurfaceTreatmentProperty)),
            HeatTreatment = NullIfWhiteSpace(PropertyValue(latest.PropertySnapshot, configuration, BomPropertyMappingCatalog.SolidWorksProperty(settings, "heatTreatment", "热处理")))
        };
        var differences = SourceDataDifferences(item, source).ToList();
        if (item.Kind == BomKind.Standard && materialRepository is not null)
        {
            var material = string.IsNullOrWhiteSpace(item.DrawingNumber)
                ? null
                : await materialRepository.FindMaterialByCodeAsync(item.DrawingNumber.Trim(), cancellationToken);
            differences.AddRange(MaterialService.StandardBomMaterialMasterDifferenceFields(item, material));
        }
        differences = differences.Distinct(StringComparer.Ordinal).ToList();
        return await repository.UpdateBomReconciliationAsync(
            item.ProjectId,
            item.Id,
            differences.Count == 0 ? "SourceMatched" : "ManualOverrideMismatch",
            differences.Count == 0
                ? "BOM维护值已与最新图档源数据一致。"
                : $"BOM维护值与最新图档源数据或料品主档不一致：{string.Join('、', differences)}。",
            actor,
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    private async Task EnsureStandardMaterialIdentityAsync(BomItem item, string materialCode, CancellationToken cancellationToken)
    {
        if (item.Kind != BomKind.Standard || materialRepository is null) return;
        var material = await materialRepository.FindMaterialByCodeAsync(materialCode, cancellationToken);
        var issues = MaterialService.StandardBomMaterialMasterIssues(item, material);
        if (issues.Count == 0) return;
        throw new PdmRuleException(
            $"料号 {materialCode} 与当前标准件的型号、品牌未形成唯一对应，已取消写入：{string.Join('、', issues)}。");
    }

    private static bool SameBomSource(BomItem left, BomItem right) =>
        SameBomSource(left.SourceDocumentId, left.SourceConfiguration, left.SourceInstancePath, right);

    private static bool SameBomSource(Guid? sourceDocumentId, string? sourceConfiguration, string? sourceInstancePath, BomItem right)
    {
        if (!sourceDocumentId.HasValue || sourceDocumentId != right.SourceDocumentId) return false;
        if (!string.IsNullOrWhiteSpace(sourceInstancePath) && !string.IsNullOrWhiteSpace(right.SourceInstancePath))
            return string.Equals(sourceInstancePath.Trim(), right.SourceInstancePath.Trim(), StringComparison.OrdinalIgnoreCase);
        return string.Equals(sourceConfiguration ?? string.Empty, right.SourceConfiguration ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static string BomSourceKey(BomItem item) => !string.IsNullOrWhiteSpace(item.SourceInstancePath)
        ? $"occurrence:{item.SourceInstancePath.Trim()}"
        : $"legacy:{item.SourceDocumentId:N}:{(item.SourceConfiguration ?? string.Empty).Trim()}";

    private static string? PropertyValue(IReadOnlyDictionary<string, string?>? properties, string configuration, string propertyName)
        => CadPropertyCardSnapshot.Read(properties, configuration, propertyName);

    private static bool HasRequiredBomValues(BomItem item, BomKind kind, BomValidationRules validationRules) =>
        kind is BomKind.Standard or BomKind.NonStandard or BomKind.Electrical
        && BomValidationFieldCatalog.MissingFields(item, ReleaseRequiredFields(kind, validationRules)).Count == 0;

    private static IReadOnlyList<string> ReleaseRequiredFields(BomKind kind, BomValidationRules validationRules) =>
        kind == BomKind.NonStandard
            ? validationRules.RequiredFields(kind).Where(field => !field.Equals(BomValidationFieldCatalog.DrawingNumber, StringComparison.OrdinalIgnoreCase)).ToArray()
            : validationRules.RequiredFields(kind);

    private static string BomKindLabel(BomKind kind) => kind switch
    {
        BomKind.Standard => "标准件",
        BomKind.NonStandard => "非标件",
        BomKind.Unclassified => "待分类",
        BomKind.Electrical => "电气",
        BomKind.Virtual => "虚拟件",
        _ => kind.ToString()
    };

    private static bool CadWritableValuesChanged(BomItem? previous, BomItem current)
    {
        if (previous is null) return current.SourceDocumentId.HasValue;
        static bool Different(string? left, string? right) => !string.Equals(left?.Trim() ?? string.Empty, right?.Trim() ?? string.Empty, StringComparison.Ordinal);
        return previous.Kind != current.Kind
            || Different(previous.DrawingNumber, current.DrawingNumber)
            || Different(previous.Name, current.Name)
            || Different(previous.Unit, current.Unit)
            || Different(previous.Specification, current.Specification)
            || Different(previous.Remark, current.Remark)
            || Different(previous.Brand, current.Brand)
            || Different(previous.Material, current.Material)
            || Different(previous.SurfaceTreatment, current.SurfaceTreatment)
            || Different(previous.HeatTreatment, current.HeatTreatment)
            || Different(previous.Weight, current.Weight);
    }

    private async Task<CadPropertyWriteback?> EnqueueCadPropertyWritebackAsync(BomItem item, string actor, CancellationToken cancellationToken)
    {
        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        var request = await CreateCadPropertyWritebackAsync(item, actor, settings, cancellationToken);
        if (request is null) return null;
        var saved = await repository.EnqueueCadPropertyWritebackAsync(request, cancellationToken);
        await AuditAsync(actor, "cad-property-writeback.enqueue", nameof(CadPropertyWriteback), saved.Id.ToString(), $"{item.DrawingNumber}:{saved.ExpectedRevision}", cancellationToken);
        return saved;
    }

    private async Task<CadPropertyWriteback?> CreateCadPropertyWritebackAsync(BomItem item, string actor, PdmSystemSettings settings, CancellationToken cancellationToken)
    {
        if (!item.SourceDocumentId.HasValue || item.Kind == BomKind.Electrical) return null;
        var latest = (await repository.ListDocumentVersionsAsync(item.SourceDocumentId.Value, cancellationToken)).FirstOrDefault();
        if (latest is null) return null;
        var properties = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        void Set(string propertyName, string? value)
        {
            if (!string.IsNullOrWhiteSpace(propertyName)) properties[propertyName.Trim()] = value?.Trim();
        }
        if (item.Kind is BomKind.Standard or BomKind.NonStandard or BomKind.Virtual)
            Set(BomPropertyMappingCatalog.SolidWorksProperty(settings, "kind", "物料分类"), item.Kind == BomKind.Standard ? "标准件" : item.Kind == BomKind.NonStandard ? "非标件" : "虚拟件");
        Set(settings.BomUnitProperty, item.Unit);
        Set(settings.BomDrawingNumberProperty, item.DrawingNumber);
        Set(settings.BomNameProperty, item.Name);
        Set(settings.BomSpecificationProperty, item.Specification);
        Set(settings.BomDescriptionProperty, item.Remark);
        Set(settings.BomBrandProperty, item.Brand);
        Set(settings.BomMaterialProperty, item.Material);
        Set(settings.BomSurfaceTreatmentProperty, item.SurfaceTreatment);
        Set(BomPropertyMappingCatalog.SolidWorksProperty(settings, "heatTreatment", "热处理"), item.HeatTreatment);
        Set(settings.BomWeightProperty, item.Weight);
        return new CadPropertyWriteback(
            Guid.NewGuid(), item.ProjectId, item.Id, item.SourceDocumentId.Value, item.SourceConfiguration,
            latest.Id, latest.Revision.Display, properties, CadPropertyWritebackStatus.Pending, actor, timeProvider.GetUtcNow());
    }

    private async Task EnqueueLinkedDrawingMaterialCodeWritebacksAsync(BomItem item, string actor, CancellationToken cancellationToken)
    {
        if (!item.SourceDocumentId.HasValue) return;
        var settings = await repository.GetSystemSettingsAsync(cancellationToken);
        var propertyName = settings.BomDrawingNumberProperty?.Trim();
        if (string.IsNullOrWhiteSpace(propertyName)) return;
        var relations = await repository.ListDocumentRelationsAsync(item.ProjectId, cancellationToken);
        foreach (var drawingDocumentId in relations.Where(relation => relation.ModelDocumentId == item.SourceDocumentId.Value)
                     .Select(relation => relation.DrawingDocumentId).Distinct())
        {
            var latest = (await repository.ListDocumentVersionsAsync(drawingDocumentId, cancellationToken)).FirstOrDefault();
            if (latest is null) continue;
            var request = new CadPropertyWriteback(Guid.NewGuid(), item.ProjectId, item.Id, drawingDocumentId, null,
                latest.Id, latest.Revision.Display, new Dictionary<string, string?> { [propertyName] = item.DrawingNumber },
                CadPropertyWritebackStatus.Pending, actor, timeProvider.GetUtcNow());
            var saved = await repository.EnqueueCadPropertyWritebackAsync(request, cancellationToken);
            await AuditAsync(actor, "cad-property-writeback.enqueue-drawing", nameof(CadPropertyWriteback), saved.Id.ToString(), $"{item.DrawingNumber}:{saved.ExpectedRevision}", cancellationToken);
        }
    }

    private async Task CreateReleaseRejectedNotificationsAsync(
        ReleasePackage package,
        Guid rejectedTaskId,
        string actor,
        string comment,
        CancellationToken cancellationToken)
    {
        var project = await repository.FindProjectAsync(package.ProjectId, cancellationToken)
            ?? throw new PdmNotFoundException("发布包对应的项目不存在。");
        var recipients = package.ApprovalTasks
            .Where(task => task.DecisionBy is not null)
            .Select(task => task.DecisionBy!)
            .Append(package.ApprovalTasks.OrderBy(task => task.StepOrder).FirstOrDefault()?.Assignee)
            .Append(project.PrimaryProjectManager)
            .Append(actor)
            .Where(username => !string.IsNullOrWhiteSpace(username))
            .Select(username => username!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var createdAt = timeProvider.GetUtcNow();
        var sourceKey = $"release-package:{package.Id:N}:rejected:{rejectedTaskId:N}";
        var notifications = recipients.Select(recipient => new UserNotification(
            Guid.NewGuid(),
            recipient,
            "ReleaseApprovalRejected",
            "BOM发布审批已退回",
            $"{project.Code} · {package.Number} 被 {actor} 退回：{comment}",
            project.Id,
            package.Id,
            sourceKey,
            createdAt,
            null)).ToArray();
        await repository.CreateUserNotificationsAsync(notifications, cancellationToken);
    }

    private async Task<CadPropertyWriteback> RequireCadPropertyWritebackAccessAsync(Guid id, string actor, UserRole role, CancellationToken cancellationToken)
    {
        var request = await repository.FindCadPropertyWritebackAsync(id, cancellationToken)
            ?? throw new PdmNotFoundException("属性写回任务不存在。");
        if (!await repository.HasProjectContentReadAccessAsync(request.ProjectId, actor, role, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目的读取权限。");
        return request;
    }

    private static bool BomReady(BomKind kind, IReadOnlyList<BomItem> items, BomValidationRules validationRules)
    {
        var included = items.Where(item => !item.IsManuallyExcluded).ToArray();
        var active = included.Where(item => !item.IsPendingRemoval).ToArray();
        if (included.Any(item => item.IsPendingRemoval || item.IsPendingClassification || item.IsManualUnmatched)) return false;
        return active.Length == 0 || active.All(item => HasRequiredBomValues(item, kind, validationRules));
    }

    private async Task EnsureStandardMaterialMasterReadyAsync(IReadOnlyList<BomItem> items, CancellationToken cancellationToken)
    {
        if (materialRepository is null) return;
        var targets = items.Where(item => item.Kind == BomKind.Standard
                && item.SourceDocumentId.HasValue
                && !item.IsManuallyExcluded
                && !item.IsPendingRemoval)
            .ToArray();
        if (targets.Length == 0) return;
        var byCode = (await materialRepository.FindMaterialsByCodesAsync(
                targets.Select(item => item.DrawingNumber).ToArray(), cancellationToken))
            .ToDictionary(material => material.MaterialCode, StringComparer.OrdinalIgnoreCase);
        var invalid = targets.Select(item =>
            {
                byCode.TryGetValue(item.DrawingNumber.Trim(), out var material);
                return (Item: item, Issues: MaterialService.StandardBomMaterialMasterIssues(item, material));
            })
            .Where(result => result.Issues.Count > 0)
            .ToArray();
        if (invalid.Length == 0) return;
        var detail = string.Join("；", invalid.Take(12).Select(result =>
            $"{(string.IsNullOrWhiteSpace(result.Item.DrawingNumber) ? result.Item.Name : result.Item.DrawingNumber)}（{string.Join('、', result.Issues)}）"));
        if (invalid.Length > 12) detail += $"等{invalid.Length}项";
        throw new PdmRuleException($"图纸上传的标准件料号尚未通过料品主档型号、品牌校验，需人工维护：{detail}。");
    }

    private static string? MissingBomSummary(BomKind kind, IReadOnlyList<BomItem> items, BomValidationRules validationRules)
    {
        var missing = items.Where(item => !item.IsManuallyExcluded && !item.IsPendingRemoval)
            .SelectMany(item => BomValidationFieldCatalog.MissingFields(item, ReleaseRequiredFields(kind, validationRules)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(BomValidationFieldCatalog.Label)
            .ToArray();
        return missing.Length == 0 ? null : $"{BomKindLabel(kind)}BOM缺少{string.Join('、', missing)}";
    }

    private static bool IsProjectLockManager(Project project, string actor, UserRole role) =>
        role == UserRole.Administrator
        || string.Equals(project.PrimaryProjectManager, actor, StringComparison.OrdinalIgnoreCase)
        || project.CollaborativeProjectManagers.Contains(actor, StringComparer.OrdinalIgnoreCase)
        || project.DesignLeads.Contains(actor, StringComparer.OrdinalIgnoreCase)
        || string.Equals(project.DesignLead, actor, StringComparison.OrdinalIgnoreCase);

    private static string RequiredReason(string reason)
    {
        reason = reason?.Trim() ?? string.Empty;
        if (reason.Length == 0) throw new PdmRuleException("请填写申请或强制释放原因。");
        if (reason.Length > 500) throw new PdmRuleException("释放原因不能超过500个字符。");
        return reason;
    }

    private static string RequiredComment(string? comment, string label)
    {
        comment = comment?.Trim() ?? string.Empty;
        if (comment.Length == 0) throw new PdmRuleException($"请填写{label}。");
        if (comment.Length > 500) throw new PdmRuleException($"{label}不能超过500个字符。");
        return comment;
    }

    private static string NormalizeSha256(string value)
    {
        var normalized = value?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
            throw new PdmRuleException("待入库图档的SHA-256指纹无效。");
        return normalized;
    }

    private static DocumentRegistrationMatch ToRegistrationMatch(
        string candidateKey,
        DocumentRegistrationMatchKind matchKind,
        DocumentContentFingerprint fingerprint,
        IReadOnlyDictionary<Guid, Project> projects)
    {
        projects.TryGetValue(fingerprint.Document.ProjectId, out var project);
        return new DocumentRegistrationMatch(
            candidateKey,
            matchKind,
            fingerprint.Document.Id,
            fingerprint.Document.ProjectId,
            project?.Code,
            project?.Name,
            fingerprint.Document.DrawingNumber,
            fingerprint.Document.FileName,
            fingerprint.Document.Revision.Display);
    }

    private async Task<RolePermissionSettings> FindRoleAsync(string roleCode, CancellationToken cancellationToken)
    {
        roleCode = roleCode?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(roleCode)) throw new PdmRuleException("请选择系统角色。");
        return (await repository.GetRolePermissionDirectoryAsync(cancellationToken)).Roles
            .SingleOrDefault(item => string.Equals(item.Role, roleCode, StringComparison.OrdinalIgnoreCase))
            ?? throw new PdmRuleException("所选系统角色不存在。");
    }

    private async Task<IReadOnlyList<RolePermissionSettings>> FindRolesAsync(IReadOnlyList<string> roleCodes, CancellationToken cancellationToken)
    {
        var normalized = roleCodes.Select(code => code?.Trim() ?? string.Empty)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length == 0) throw new PdmRuleException("请至少选择一个系统角色。");
        var roles = (await repository.GetRolePermissionDirectoryAsync(cancellationToken)).Roles;
        return normalized.Select(code => roles.SingleOrDefault(role => string.Equals(role.Role, code, StringComparison.OrdinalIgnoreCase))
            ?? throw new PdmRuleException($"所选系统角色不存在：{code}。")).ToArray();
    }

    private static bool RoleSetsEqual(IEnumerable<string> left, IEnumerable<string> right) =>
        left.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(right);

    private static Guid ResolveManagedCompany(Guid requestedCompanyId, OrganizationDirectory directory)
    {
        var tenant = TenantContext.Current;
        var companyId = requestedCompanyId == Guid.Empty
            ? tenant?.PrimaryCompanyId ?? directory.Organizations.FirstOrDefault(item => item.IsActive)?.Id ?? Guid.Empty
            : requestedCompanyId;
        if (!directory.Organizations.Any(item => item.Id == companyId && item.IsActive))
            throw new PdmRuleException("所属公司不存在或已停用。");
        if (tenant is not null && !tenant.IsPlatformAdministrator && companyId != tenant.PrimaryCompanyId)
            throw new UnauthorizedAccessException("无权管理其他公司。");
        return companyId;
    }

    private static Guid[] NormalizeAccessibleCompanies(IReadOnlyList<Guid>? companyIds, Guid primaryCompanyId, OrganizationDirectory directory)
    {
        var normalized = (companyIds ?? Array.Empty<Guid>()).Where(id => id != primaryCompanyId).Distinct().ToArray();
        if (normalized.Any(id => !directory.Organizations.Any(item => item.Id == id && item.IsActive)))
            throw new PdmRuleException("跨公司授权目标不存在或已停用。");
        return normalized;
    }

    private static void RequireActiveCompany(Guid companyId)
    {
        if (TenantContext.CompanyId is Guid activeCompanyId && activeCompanyId != companyId)
            throw new UnauthorizedAccessException("只能操作当前公司的业务数据。");
    }

    private static void RequirePrimaryCompany(Guid companyId)
    {
        if (TenantContext.Current is { } tenant && tenant.PrimaryCompanyId != companyId)
            throw new UnauthorizedAccessException("无权管理其他公司。");
    }

    private async Task RequirePermissionAsync(string actor, UserRole role, string permissionCode, CancellationToken cancellationToken)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, permissionCode, cancellationToken))
            throw new UnauthorizedAccessException("当前角色未配置执行此操作的权限。");
    }

    private async Task<PdmDocument> RequireDocumentAsync(Guid documentId, CancellationToken cancellationToken) =>
        await repository.FindDocumentAsync(documentId, cancellationToken) ?? throw new PdmNotFoundException("图档不存在。");

    private async Task RequireDocumentReadAccessAsync(Guid documentId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        await RequireDocumentAccessAsync(documentId, actor, role, FolderAccess.View, cancellationToken);
    }

    private async Task RequireDocumentAccessAsync(Guid documentId, string actor, UserRole role, FolderAccess requiredAccess, CancellationToken cancellationToken)
    {
        if (!await repository.HasDocumentAccessAsync(documentId, actor, role, requiredAccess, cancellationToken))
            throw new UnauthorizedAccessException("当前用户没有该项目目录下图档的对应操作权限。");
    }

    public async Task<bool> CanSubmitArchiveAsync(Guid projectId, string actor, UserRole role, CancellationToken cancellationToken)
    {
        if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.DocumentEdit, cancellationToken)
            || !await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken))
            return false;

        var project = await repository.FindProjectAsync(projectId, cancellationToken);
        return project is not null
            && ProjectSubmissionPolicy.CanSubmitArchive(project, actor, IsSubmissionAdministrator(role));
    }

    public async Task<Project> RequireProjectSubmissionAccessAsync(
        Guid projectId,
        string actor,
        UserRole role,
        string action,
        CancellationToken cancellationToken)
    {
        var project = await repository.FindProjectAsync(projectId, cancellationToken)
            ?? throw new PdmNotFoundException("项目不存在。");
        if (!await CanSubmitArchiveAsync(projectId, actor, role, cancellationToken))
        {
            var target = project.ParentProjectId is null ? "主项目图档" : string.Concat("子项目“", project.Code, "”");
            throw new UnauthorizedAccessException(string.Concat(
                "当前账号未被分配为", target, "的项目经理、主设或工程师，不能", action, "。"));
        }

        return project;
    }

    private static bool IsSubmissionAdministrator(UserRole role) =>
        role is UserRole.Administrator or UserRole.PlatformAdministrator
        || TenantContext.Current?.IsPlatformAdministrator == true;

    private Task AuditAsync(string actor, string action, string entityType, string entityId, string detail, CancellationToken cancellationToken) =>
        repository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), timeProvider.GetUtcNow(), actor, action, entityType, entityId, detail), cancellationToken);

    private static IReadOnlyDictionary<string, string?> DrawingReviewProperties(
        DrawingReviewPackage package,
        DrawingReviewTarget target,
        string reviewer,
        string reviewerName,
        DateTimeOffset reviewedAt,
        string reviewedRevision) => new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["校对"] = reviewerName,
            ["PLM_审图状态"] = "已审核",
            ["PLM_审图单号"] = package.Number,
            ["PLM_审图对象"] = target == DrawingReviewTarget.Model3D ? "3D" : "2D",
            ["PLM_审核人账号"] = reviewer,
            ["PLM_审核时间"] = reviewedAt.ToString("yyyy-MM-dd HH:mm:ss zzz"),
            ["PLM_原审版本"] = reviewedRevision
        };

    private async Task EnsureNonStandardDrawingReviewReadyAsync(Guid projectId, IReadOnlyList<BomItem> snapshot, CancellationToken cancellationToken)
    {
        var effectiveItems = snapshot
            .Where(item => !item.IsManuallyExcluded && !item.IsPendingRemoval && !item.DeletedAt.HasValue)
            .ToArray();
        var withoutDrawingSource = effectiveItems.Where(item => !item.SourceDocumentId.HasValue).Select(item => item.DrawingNumber).ToArray();
        if (withoutDrawingSource.Length > 0)
            throw new PdmRuleException($"以下非标件没有3D/2D图档关系，不能进入正式BOM审核发布：{string.Join("、", withoutDrawingSource.Take(12))}。");
        var referenceSnapshot = await repository.GetLatestReferenceSnapshotAsync(projectId, cancellationToken)
            ?? throw new PdmRuleException("项目尚无最新设计树，不能核对非标件BOM数量。");
        var generated = await GenerateMechanicalBomFromSnapshotAsync(projectId, referenceSnapshot, "release-validation", cancellationToken, false, false);
        var currentSources = generated.StandardItems
            .Concat(generated.NonStandardItems)
            .Concat(generated.UnclassifiedItems)
            .Where(item => item.SourceDocumentId.HasValue && !item.IsPendingRemoval && !item.IsManualUnmatched && !item.IsManuallyExcluded)
            .ToArray();
        var missingSources = new List<string>();
        var quantityMismatches = new List<string>();
        foreach (var item in effectiveItems)
        {
            var source = currentSources.FirstOrDefault(candidate => SameBomSource(item, candidate));
            if (source is null)
            {
                missingSources.Add(item.DrawingNumber);
                continue;
            }
            if (item.Quantity != source.Quantity)
                quantityMismatches.Add($"{item.DrawingNumber}（BOM {item.Quantity} / 源 {source.Quantity}）");
        }
        if (missingSources.Count > 0)
            throw new PdmRuleException($"以下非标件未在最新设计树中找到对应BOM来源，不能发布：{string.Join("、", missingSources.Take(12))}{(missingSources.Count > 12 ? $"等{missingSources.Count}项" : string.Empty)}。");
        if (quantityMismatches.Count > 0)
            throw new PdmRuleException($"以下非标件BOM数量与最新设计树数量不一致，不能发布：{string.Join("、", quantityMismatches.Take(12))}{(quantityMismatches.Count > 12 ? $"等{quantityMismatches.Count}项" : string.Empty)}。");
        var sourceItems = effectiveItems
            .GroupBy(item => item.SourceDocumentId!.Value)
            .Select(group => group.OrderBy(item => item.Sequence).First())
            .ToArray();
        if (sourceItems.Length == 0) return;
        var approvedPackages = (await repository.ListDrawingReviewPackagesAsync(projectId, cancellationToken))
            .Where(package => package.State == DrawingReviewPackageState.Approved)
            .ToArray();
        if (approvedPackages.Length == 0)
            throw new PdmRuleException("非标件BOM尚无已完成的3D和2D图纸审核，不能进入正式BOM审核发布。");
        var relations = await repository.ListDocumentRelationsAsync(projectId, cancellationToken);
        var missing = new List<string>();
        foreach (var sourceItem in sourceItems)
        {
            var modelDocumentId = sourceItem.SourceDocumentId!.Value;
            var drawingDocumentIds = relations
                .Where(relation => relation.ModelDocumentId == modelDocumentId)
                .Select(relation => relation.DrawingDocumentId)
                .Distinct()
                .ToArray();
            if (drawingDocumentIds.Length != 1)
            {
                missing.Add(drawingDocumentIds.Length == 0
                    ? $"{sourceItem.DrawingNumber}（缺少2D工程图）"
                    : $"{sourceItem.DrawingNumber}（关联{drawingDocumentIds.Length}张2D工程图）");
                continue;
            }
            var requiredDrawingDocumentId = drawingDocumentIds[0];
            var modelVersions = await repository.ListDocumentVersionsAsync(modelDocumentId, cancellationToken);
            var latestModel = modelVersions.OrderByDescending(version => version.CreatedAt).FirstOrDefault();
            var matched = false;
            if (latestModel is not null)
            {
                foreach (var package in approvedPackages)
                {
                    foreach (var reviewItem in package.Items.Where(item => item.ModelDocumentId == modelDocumentId
                                 && MatchesReviewedVersion(latestModel, item.EffectiveModelVersionId, modelVersions)
                                 && item.ModelState == DrawingReviewTargetState.Marked
                                 && item.DrawingDocumentId == requiredDrawingDocumentId
                                 && item.DrawingState == DrawingReviewTargetState.Marked))
                    {
                        var drawingVersions = await repository.ListDocumentVersionsAsync(requiredDrawingDocumentId, cancellationToken);
                        var latestDrawing = drawingVersions.OrderByDescending(version => version.CreatedAt).FirstOrDefault();
                        if (latestDrawing is null || !reviewItem.EffectiveDrawingVersionId.HasValue
                            || !MatchesReviewedVersion(latestDrawing, reviewItem.EffectiveDrawingVersionId.Value, drawingVersions)) continue;
                        if (package.Markups.Any(markup => markup.ItemId == reviewItem.Id
                                && markup.Severity == DrawingReviewMarkupSeverity.Blocking
                                && markup.State == DrawingReviewMarkupState.Open))
                            continue;
                        matched = true;
                        break;
                    }
                    if (matched) break;
                }
            }
            if (!matched) missing.Add(sourceItem.DrawingNumber);
        }
        if (missing.Count > 0)
            throw new PdmRuleException($"以下非标件的3D/2D审核标记缺失或审核后版本已变化：{string.Join("、", missing.Take(12))}{(missing.Count > 12 ? $"等{missing.Count}项" : string.Empty)}。请重新发起图纸审核。");
    }

    private static bool MatchesReviewedVersion(
        DocumentVersion latest,
        Guid reviewedVersionId,
        IReadOnlyList<DocumentVersion> versions)
    {
        if (latest.Id == reviewedVersionId) return true;
        if (latest.Status != DocumentVersionStatus.Released || latest.SourceVersionId != reviewedVersionId) return false;
        var reviewed = versions.FirstOrDefault(version => version.Id == reviewedVersionId);
        return reviewed is not null && string.Equals(latest.Sha256, reviewed.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static BomKind ReleaseScopeBomKind(ReleaseScope scope) => scope switch
    {
        ReleaseScope.StandardLongLead or ReleaseScope.StandardFormal or ReleaseScope.StandardSupplement => BomKind.Standard,
        ReleaseScope.ElectricalFormal or ReleaseScope.ElectricalSupplement => BomKind.Electrical,
        ReleaseScope.NonStandardWithDrawing => BomKind.NonStandard,
        _ => throw new PdmRuleException("不支持的发布范围。")
    };

    private async Task<IReadOnlyList<ApprovalTask>> BuildApprovalTasksAsync(
        Guid packageId,
        ApprovalWorkflowTemplate workflow,
        Project project,
        string actor,
        CancellationToken cancellationToken)
    {
        var users = await repository.ListUsersAsync(cancellationToken);
        OrganizationDirectory? directory = null;
        OrganizationUnit? primaryUnit = null;
        if (workflow.Steps.Any(step => step.AssigneeSource is ApprovalAssigneeSource.PrimaryUnitManager or ApprovalAssigneeSource.ParentUnitManager))
        {
            directory = await repository.GetOrganizationDirectoryAsync(cancellationToken);
            var primaryMembership = directory.Memberships.FirstOrDefault(membership =>
                membership.IsPrimary && string.Equals(membership.Username, actor, StringComparison.OrdinalIgnoreCase));
            if (primaryMembership is null)
                throw new PdmRuleException($"提交人{actor}尚未配置主部门，无法生成组织审批节点。");
            primaryUnit = directory.Units.FirstOrDefault(unit => unit.Id == primaryMembership.UnitId && unit.IsActive)
                ?? throw new PdmRuleException($"提交人{actor}的主部门不存在或已停用，无法生成组织审批节点。");
            if (project.OrganizationId.HasValue && primaryUnit.OrganizationId != project.OrganizationId.Value)
                throw new PdmRuleException($"提交人{actor}的主部门不属于项目公司，无法生成组织审批节点。");
        }

        string? UnitManager(ApprovalWorkflowStepTemplate step)
        {
            if (directory is null || primaryUnit is null) return null;
            var targetUnit = primaryUnit;
            if (step.AssigneeSource == ApprovalAssigneeSource.ParentUnitManager)
            {
                if (primaryUnit.ParentUnitId is null)
                    throw new PdmRuleException($"组织“{primaryUnit.Name}”没有上级部门，无法生成“{step.Name}”审批节点。");
                targetUnit = directory.Units.FirstOrDefault(unit => unit.Id == primaryUnit.ParentUnitId.Value && unit.IsActive)
                    ?? throw new PdmRuleException($"组织“{primaryUnit.Name}”的上级部门不存在或已停用，无法生成“{step.Name}”审批节点。");
            }

            var manager = NullIfWhiteSpace(directory.Managers.FirstOrDefault(item => item.UnitId == targetUnit.Id)?.PrimaryManager);
            if (manager is null)
                throw new PdmRuleException($"组织“{targetUnit.Name}”尚未配置主负责人，无法生成“{step.Name}”审批节点。");
            if (!IsActiveMemberOfOrganization(directory, manager, targetUnit.OrganizationId))
                throw new PdmRuleException($"组织“{targetUnit.Name}”的主负责人{manager}不存在、已停用或不属于该公司。");
            return manager;
        }

        var tasks = new List<ApprovalTask>(workflow.Steps.Count);
        for (var index = 0; index < workflow.Steps.Count; index++)
        {
            var step = workflow.Steps[index];
            var assignee = step.AssigneeSource switch
            {
                ApprovalAssigneeSource.Submitter => actor,
                ApprovalAssigneeSource.ProjectDesignLead => project.DesignLead,
                ApprovalAssigneeSource.FixedUser => step.FixedAssignee,
                ApprovalAssigneeSource.PrimaryUnitManager or ApprovalAssigneeSource.ParentUnitManager => UnitManager(step),
                _ => null
            };
            assignee = NullIfWhiteSpace(assignee);
            if (assignee is null)
                throw new PdmRuleException($"审批模板“{workflow.Name}”的“{step.Name}”尚未配置审批人。");
            if (!users.Any(user => user.IsActive && string.Equals(user.Username, assignee, StringComparison.OrdinalIgnoreCase)))
                throw new PdmRuleException($"审批节点“{step.Name}”的账号{assignee}不存在或已停用。");
            tasks.Add(new ApprovalTask(Guid.NewGuid(), packageId, step.Stage, assignee, null, null, null, null)
            {
                StepOrder = index + 1,
                StepName = step.Name
            });
        }
        return tasks;
    }

    private async Task EnsureReleaseScopeAvailableAsync(
        Guid projectId,
        ReleaseScope requestedScope,
        IReadOnlyCollection<BomItem> requestedBomItems,
        IReadOnlyCollection<BomItem> currentStandardBomItems,
        CancellationToken cancellationToken,
        Guid? excludedReleasePackageId = null)
    {
        var packages = (await repository.ListReleasePackagesAsync(projectId, cancellationToken))
            .Where(package => package.Id != excludedReleasePackageId).ToArray();
        if (requestedScope == ReleaseScope.StandardFormal
            && packages.Any(package => package.Scope == ReleaseScope.StandardFormal && package.State == ReleasePackageState.Published))
            throw new PdmRuleException("标准件正式版已发布，后续只能发起增补/变更。");
        if (requestedScope == ReleaseScope.StandardLongLead)
        {
            var requestedIds = requestedBomItems.Select(item => item.Id).ToHashSet();
            static string MaterialKey(BomItem item) => $"{item.DrawingNumber.Trim()}|{item.Unit.Trim()}";
            var requestedKeys = requestedBomItems.Select(MaterialKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var currentQuantities = currentStandardBomItems
                .GroupBy(MaterialKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity), StringComparer.OrdinalIgnoreCase);
            var publishedQuantities = packages
                .Where(package => package.Scope == ReleaseScope.StandardLongLead && package.State == ReleasePackageState.Published)
                .SelectMany(package => package.StandardBomSnapshot)
                .GroupBy(MaterialKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity), StringComparer.OrdinalIgnoreCase);
            var requestedQuantities = requestedBomItems
                .GroupBy(MaterialKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity), StringComparer.OrdinalIgnoreCase);
            foreach (var key in requestedKeys)
            {
                var currentQuantity = currentQuantities.GetValueOrDefault(key);
                var publishedQuantity = publishedQuantities.GetValueOrDefault(key);
                var requestedQuantity = requestedQuantities.GetValueOrDefault(key);
                if (requestedQuantity + publishedQuantity > currentQuantity)
                {
                    var drawingNumber = requestedBomItems.First(item => string.Equals(MaterialKey(item), key, StringComparison.OrdinalIgnoreCase)).DrawingNumber;
                    throw new PdmRuleException($"物料{drawingNumber}当前BOM数量{currentQuantity}，已提前发布{publishedQuantity}，本次申请{requestedQuantity}，超过剩余可发布数量{Math.Max(0, currentQuantity - publishedQuantity)}。");
                }
            }
            var activeLongLead = packages.FirstOrDefault(package =>
                package.Scope == ReleaseScope.StandardLongLead
                && package.State != ReleasePackageState.Published
                && (package.SelectedBomItemIds.Any(requestedIds.Contains)
                    || package.StandardBomSnapshot.Any(item => requestedKeys.Contains(MaterialKey(item)))));
            if (activeLongLead is not null)
                throw new PdmConflictException($"发布包{activeLongLead.Number}已包含本次选择的长交期物料；同一物料不能同时进入多个长交期发布包。");
            return;
        }
        var targetKind = ReleaseScopeBomKind(requestedScope);
        var active = packages.FirstOrDefault(package =>
            package.State != ReleasePackageState.Published
            && (package.Scope == ReleaseScope.LegacyCombined && PackageBomVersionIds(package).Length == 3
                || package.Scope is not (ReleaseScope.LegacyCombined or ReleaseScope.StandardLongLead) && ReleaseScopeBomKind(package.Scope) == targetKind));
        if (active is not null)
            throw new PdmConflictException($"发布包{active.Number}正在审批或发布，{BomKindLabel(targetKind)}BOM暂时锁定。");
    }

    private async Task EnsureBomChangeAllowedAsync(Guid projectId, CancellationToken cancellationToken, params BomKind[] changedKinds)
    {
        var normalizedKinds = changedKinds.Where(kind => kind is BomKind.Standard or BomKind.NonStandard or BomKind.Electrical).Distinct().ToHashSet();
        var active = (await repository.ListReleasePackagesAsync(projectId, cancellationToken)).FirstOrDefault(package =>
            package.State is (ReleasePackageState.ProcessReview or ReleasePackageState.Approval or ReleasePackageState.Publishing)
            && (package.Scope == ReleaseScope.LegacyCombined && PackageBomVersionIds(package).Length == 3
                || package.Scope is not (ReleaseScope.LegacyCombined or ReleaseScope.StandardLongLead)
                    && (normalizedKinds.Count == 0 || normalizedKinds.Contains(ReleaseScopeBomKind(package.Scope)))));
        if (active is not null)
        {
            var scopeLabel = active.Scope == ReleaseScope.LegacyCombined ? "三个BOM" : $"{BomKindLabel(ReleaseScopeBomKind(active.Scope))}BOM";
            throw new PdmConflictException($"发布包{active.ChangeNumber ?? active.Number}正在审批或发布，{scopeLabel}已锁定；请先完成、驳回或撤回。");
        }
    }

    private static Guid[] PackageBomVersionIds(ReleasePackage package) =>
        new[] { package.StandardBomVersionId, package.NonStandardBomVersionId, package.ElectricalBomVersionId }
            .Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToArray();

    private async Task<Guid[]> PackageBomVersionIdsInStateAsync(ReleasePackage package, BomVersionState state, CancellationToken cancellationToken)
    {
        var ids = PackageBomVersionIds(package).ToHashSet();
        if (ids.Count == 0) return [];
        return (await repository.ListBomVersionsAsync(package.ProjectId, null, cancellationToken))
            .Where(version => ids.Contains(version.Id) && version.State == state).Select(version => version.Id).ToArray();
    }

    private async Task SyncBomDraftAsync(Guid projectId, BomKind kind, string actor, CancellationToken cancellationToken)
    {
        var items = (await repository.GetBomAsync(projectId, kind, cancellationToken)).Where(item => !item.IsManuallyExcluded).ToArray();
        await repository.SaveBomDraftAsync(projectId, kind, items, actor, cancellationToken);
    }

    private async Task<BomVersion> ResolveBomVersionForReleaseAsync(
        Guid projectId,
        BomKind kind,
        IReadOnlyList<BomItem> currentItems,
        string actor,
        string changeNumber,
        string changeReason,
        string effectiveSerialFrom,
        string? effectiveSerialTo,
        BomValidationRules validationRules,
        CancellationToken cancellationToken)
    {
        var versions = await repository.ListBomVersionsAsync(projectId, kind, cancellationToken);
        var draft = versions.FirstOrDefault(version => version.State == BomVersionState.Draft);
        var latestReleased = versions.FirstOrDefault(version => version.State == BomVersionState.Released);
        BomVersion selected;
        if (draft is not null)
        {
            selected = await repository.SaveBomDraftAsync(projectId, kind, currentItems, actor, cancellationToken);
        }
        else if (latestReleased is not null && BomSnapshotsEqual(latestReleased.Items, currentItems))
        {
            return latestReleased;
        }
        else
        {
            selected = await repository.SaveBomDraftAsync(projectId, kind, currentItems, actor, cancellationToken);
        }
        return await repository.UpdateBomVersionReleaseInfoAsync(
            selected.Id,
            changeNumber,
            changeReason,
            effectiveSerialFrom,
            effectiveSerialTo,
            validationRules.RequiredFields(kind),
            cancellationToken);
    }

    private static bool BomSnapshotsEqual(IReadOnlyList<BomItem> left, IReadOnlyList<BomItem> right) =>
        string.Equals(BomRevision("V", left), BomRevision("V", right), StringComparison.Ordinal);

    private static string BomRevision(string prefix, IReadOnlyList<BomItem> items)
    {
        var json = JsonSerializer.Serialize(items, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return $"{prefix}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))[..8]}";
    }

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsPlatformManagedRole(string roleCode) =>
        string.Equals(roleCode, "platform_admin", StringComparison.OrdinalIgnoreCase)
        || string.Equals(roleCode, "developer", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeUsername(string? value)
    {
        var username = value?.Trim() ?? string.Empty;
        if (username.Length is < 2 or > 100 || username.Any(character => !char.IsLetterOrDigit(character) && character is not ('.' or '-' or '_' or '@')))
            throw new PdmRuleException("账号必须为2到100位，只能包含字母、数字、点、短横线、下划线和@。");
        return username;
    }

    private static string NormalizeDisplayName(string? value)
    {
        var displayName = value?.Trim() ?? string.Empty;
        if (displayName.Length is < 1 or > 100) throw new PdmRuleException("姓名不能为空且不能超过100个字符。");
        return displayName;
    }

    private static string[] NormalizeUsers(IEnumerable<string> usernames) => usernames
        .Select(username => username?.Trim() ?? string.Empty)
        .Where(username => username.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static bool IsActiveMemberOfOrganization(OrganizationDirectory directory, string username, Guid organizationId) =>
        directory.Users.Any(user => user.IsActive && string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase))
        && directory.Memberships.Any(membership => string.Equals(membership.Username, username, StringComparison.OrdinalIgnoreCase)
            && directory.Units.Any(unit => unit.Id == membership.UnitId && unit.IsActive && unit.OrganizationId == organizationId));

    private static bool IsActiveTechnicalMemberOfOrganization(OrganizationDirectory directory, string username, Guid organizationId)
    {
        if (!IsActiveMemberOfOrganization(directory, username, organizationId)) return false;
        var user = directory.Users.First(item => string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase));
        if (!user.EffectiveRoleCodes.Any(roleCode => roleCode is "Engineer" or "ElectricalEngineer" or "CommissioningEngineer" or "HardwareEngineer"
                or "MechanicalManager" or "TechnicalAssistant" or "ProcessReviewer" or "Approver")) return false;
        return directory.Memberships.Where(item => string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase))
            .Select(item => directory.Units.FirstOrDefault(unit => unit.Id == item.UnitId))
            .Any(unit => unit is not null && (IsTechnicalUnitName(unit.Name)
                || unit.Kind == OrganizationUnitKind.BusinessDivision && !IsNonTechnicalUnitName(unit.Name)));
    }

    private static bool IsTechnicalUnitName(string name) =>
        new[] { "机械", "电气", "硬件", "标准化", "技术", "设计", "研发" }.Any(keyword => name.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private static bool IsNonTechnicalUnitName(string name) =>
        new[] { "采购", "供应链", "生产", "装配", "机加", "财务", "行政", "人事部", "销售", "计划", "质量", "仓储", "物流" }.Any(keyword => name.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private static bool IsActiveUserAvailableToOrganization(OrganizationDirectory directory, string username, Guid organizationId)
    {
        if (!directory.Users.Any(user => user.IsActive && (user.CompanyId is null || user.CompanyId == organizationId) && string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase))) return false;
        var memberships = directory.Memberships.Where(membership => string.Equals(membership.Username, username, StringComparison.OrdinalIgnoreCase)).ToArray();
        return memberships.Length == 0 || memberships.All(membership => directory.Units.Any(unit => unit.Id == membership.UnitId && unit.OrganizationId == organizationId));
    }

    private static bool IsActiveMemberOfDivision(OrganizationDirectory directory, string username, Guid divisionId) =>
        directory.Users.Any(user => user.IsActive && string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase))
        && directory.Memberships.Any(membership => string.Equals(membership.Username, username, StringComparison.OrdinalIgnoreCase)
            && IsUnitWithin(directory.Units, membership.UnitId, divisionId));

    private static bool IsUnitWithin(IReadOnlyList<OrganizationUnit> units, Guid unitId, Guid ancestorId)
    {
        var current = units.SingleOrDefault(unit => unit.Id == unitId && unit.IsActive);
        while (current is not null)
        {
            if (current.Id == ancestorId) return true;
            current = current.ParentUnitId is null ? null : units.SingleOrDefault(unit => unit.Id == current.ParentUnitId && unit.IsActive);
        }
        return false;
    }

    private static int GetOrganizationUnitDepth(IReadOnlyList<OrganizationUnit> units, Guid unitId)
    {
        var depth = 0;
        var current = units.SingleOrDefault(unit => unit.Id == unitId);
        while (current is not null)
        {
            depth++;
            current = current.ParentUnitId is null ? null : units.SingleOrDefault(unit => unit.Id == current.ParentUnitId);
        }
        return depth;
    }

    private static int GetOrganizationSubtreeHeight(IReadOnlyList<OrganizationUnit> units, Guid unitId)
    {
        var childHeights = units.Where(unit => unit.ParentUnitId == unitId)
            .Select(unit => GetOrganizationSubtreeHeight(units, unit.Id))
            .ToArray();
        return childHeights.Length == 0 ? 1 : childHeights.Max() + 1;
    }

    private static void ValidateProjectDetails(string? name, string? projectAlias, int quantity)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new PdmRuleException("项目名称不能为空。");
        if (name.Trim().Length > 200 || projectAlias?.Trim().Length > 200) throw new PdmRuleException("项目名称或项目别名超过允许长度。");
        if (quantity is < 1 or > 10000) throw new PdmRuleException("数量必须在1到10000之间。");
    }
}
