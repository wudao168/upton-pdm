using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using System.Net.Mail;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api;

public static class PdmEndpointExtensions
{
    public static void MapPdmEndpoints(this WebApplication app)
    {
        app.MapGet("/health", HealthAsync).AllowAnonymous();

        app.MapPost("/api/auth/login", async (
            LoginRequest request,
            IPdmRepository repository,
            IPasswordService passwords,
            ITokenIssuer tokenIssuer,
            IPersistentSessionTokenService persistentSessions,
            CompanySessionService companySessions,
            IOptions<AuthenticationOptions> authenticationOptions,
            HttpContext context,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var account = await repository.FindUserAsync(request.Username.Trim(), cancellationToken);
            if (account is null || !account.IsActive || !passwords.Verify(request.Password, account.PasswordHash))
            {
                return Results.Unauthorized();
            }

            var lifetime = TimeSpan.FromHours(authenticationOptions.Value.TokenLifetimeHours);
            var expiresAt = timeProvider.GetUtcNow().Add(lifetime);
            var company = await companySessions.ResolveAsync(account, context.Request.Headers["X-Company-Id"].FirstOrDefault(), cancellationToken);
            return Results.Ok(new LoginResponse(
                tokenIssuer.Issue(account, lifetime),
                expiresAt,
                persistentSessions.Issue(account),
                account.Username,
                account.DisplayName,
                account.EffectiveRoleCode,
                (await repository.GetUserPermissionsAsync(account.Username, account.Role, cancellationToken)).Order().ToArray(),
                company.PrimaryCompanyId,
                company.ActiveCompanyId,
                company.ActiveCompanyName,
                company.CrossCompanyView,
                company.AccessibleCompanies,
                account.EffectiveRoleCodes));
        }).AllowAnonymous();

        app.MapPost("/api/auth/resume", async (
            ResumeSessionRequest request,
            IPdmRepository repository,
            ITokenIssuer tokenIssuer,
            IPersistentSessionTokenService persistentSessions,
            CompanySessionService companySessions,
            IOptions<AuthenticationOptions> authenticationOptions,
            HttpContext context,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!persistentSessions.TryRead(request.ResumeToken, out var ticket)) return Results.Unauthorized();
            var account = await repository.FindUserAsync(ticket.Username, cancellationToken);
            if (account is null || !account.IsActive || account.TokenVersion != ticket.TokenVersion) return Results.Unauthorized();

            var lifetime = TimeSpan.FromHours(authenticationOptions.Value.TokenLifetimeHours);
            var expiresAt = timeProvider.GetUtcNow().Add(lifetime);
            var company = await companySessions.ResolveAsync(account, context.Request.Headers["X-Company-Id"].FirstOrDefault(), cancellationToken);
            return Results.Ok(new LoginResponse(
                tokenIssuer.Issue(account, lifetime),
                expiresAt,
                persistentSessions.Issue(account),
                account.Username,
                account.DisplayName,
                account.EffectiveRoleCode,
                (await repository.GetUserPermissionsAsync(account.Username, account.Role, cancellationToken)).Order().ToArray(),
                company.PrimaryCompanyId,
                company.ActiveCompanyId,
                company.ActiveCompanyName,
                company.CrossCompanyView,
                company.AccessibleCompanies,
                account.EffectiveRoleCodes));
        }).AllowAnonymous();

        app.MapPost("/api/auth/password-reset-request", async (
            PasswordResetRequest request,
            IPdmRepository repository,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var username = request.Username?.Trim() ?? string.Empty;
            var displayName = request.DisplayName?.Trim() ?? string.Empty;
            if (username.Length is < 1 or > 80 || displayName.Length is < 1 or > 80)
                throw new PdmRuleException("请输入账号和姓名。");

            var account = await repository.FindUserAsync(username, cancellationToken);
            if (account is not null && account.IsActive && string.Equals(account.DisplayName.Trim(), displayName, StringComparison.Ordinal))
                await repository.CreatePasswordResetRequestAsync(account, timeProvider.GetUtcNow(), cancellationToken);
            return Results.Ok(true);
        }).AllowAnonymous();

        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/system-connections", async (SystemConnectionStatus status, CancellationToken cancellationToken) =>
            Results.Ok(await status.ReadAsync(cancellationToken)));

        api.MapGet("/auth/me", async (HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, _) = CurrentUser(context.User);
            return Results.Ok(await repository.FindUserProfileAsync(actor, cancellationToken)
                ?? throw new PdmNotFoundException("用户不存在。"));
        });

        api.MapPut("/auth/profile", async (UpdateProfileRequest request, HttpContext context, IPdmRepository repository, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            var (actor, _) = CurrentUser(context.User);
            var nickname = TrimToNull(request.Nickname, 80, "昵称");
            var landline = TrimToNull(request.Landline, 40, "固定电话");
            var mobilePhone = TrimToNull(request.MobilePhone, 40, "移动电话");
            var email = TrimToNull(request.Email, 120, "邮箱");
            var gender = string.IsNullOrWhiteSpace(request.Gender) ? "unspecified" : request.Gender.Trim();
            if (gender is not ("male" or "female" or "unspecified")) throw new PdmRuleException("性别选项无效。");
            if (email is not null && !MailAddress.TryCreate(email, out _)) throw new PdmRuleException("邮箱格式不正确。");
            var theme = string.IsNullOrWhiteSpace(request.Theme) ? null : request.Theme.Trim();
            if (theme is not null && theme is not ("a" or "c" or "p" or "o" or "g")) throw new PdmRuleException("主题选项无效。");
            var profile = await repository.UpdateUserProfileAsync(actor, nickname, gender, landline, mobilePhone, email, theme, cancellationToken);
            await repository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), timeProvider.GetUtcNow(), actor, "user.profile.update", nameof(UserProfile), actor, "更新个人资料"), cancellationToken);
            return Results.Ok(profile);
        });

        api.MapPut("/auth/password", async (
            ChangePasswordRequest request,
            HttpContext context,
            IPdmRepository repository,
            IPasswordService passwords,
            ITokenIssuer tokenIssuer,
            IPersistentSessionTokenService persistentSessions,
            CompanySessionService companySessions,
            IOptions<AuthenticationOptions> authenticationOptions,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var (actor, _) = CurrentUser(context.User);
            var account = await repository.FindUserAsync(actor, cancellationToken) ?? throw new PdmNotFoundException("用户不存在。");
            if (!passwords.Verify(request.CurrentPassword, account.PasswordHash)) throw new PdmRuleException("当前密码错误。");
            if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8
                || !request.Password.Any(char.IsLetter) || !request.Password.Any(char.IsDigit))
                throw new PdmRuleException("密码至少 8 位，且必须包含字母和数字。");
            var updatedAccount = await repository.UpdateUserPasswordAsync(actor, passwords.Hash(request.Password), cancellationToken);
            await repository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), timeProvider.GetUtcNow(), actor, "user.password.change", nameof(UserAccount), account.Id.ToString(), "用户修改登录密码"), cancellationToken);
            var lifetime = TimeSpan.FromHours(authenticationOptions.Value.TokenLifetimeHours);
            var company = await companySessions.ResolveAsync(updatedAccount, TenantContext.CompanyId?.ToString(), cancellationToken);
            return Results.Ok(new LoginResponse(
                tokenIssuer.Issue(updatedAccount, lifetime),
                timeProvider.GetUtcNow().Add(lifetime),
                persistentSessions.Issue(updatedAccount),
                account.Username,
                account.DisplayName,
                updatedAccount.EffectiveRoleCode,
                (await repository.GetUserPermissionsAsync(updatedAccount.Username, updatedAccount.Role, cancellationToken)).Order().ToArray(),
                company.PrimaryCompanyId,
                company.ActiveCompanyId,
                company.ActiveCompanyName,
                company.CrossCompanyView,
                company.AccessibleCompanies,
                updatedAccount.EffectiveRoleCodes));
        });

        api.MapGet("/password-reset-requests", async (HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return !CanManagePasswordResetRequests(role)
                ? Results.Forbid()
                : Results.Ok(await repository.ListPasswordResetTasksAsync(cancellationToken));
        });

        api.MapPut("/password-reset-requests/{taskId:guid}/reset", async (Guid taskId, HttpContext context, IPdmRepository repository, IPasswordService passwords, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!CanManagePasswordResetRequests(role)) return Results.Forbid();
            var now = timeProvider.GetUtcNow();
            await repository.CompletePasswordResetTaskAsync(taskId, passwords.Hash("11111111"), actor, now, cancellationToken);
            await repository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), now, actor, "user.password.reset", nameof(UserAccount), taskId.ToString(), "管理员将用户密码重置为初始密码"), cancellationToken);
            return Results.Ok(true);
        });

        api.MapGet("/projects", async (HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await repository.ListProjectsForUserAsync(actor, role, cancellationToken));
        });

        api.MapGet("/project-numbering/options", async (IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var options = await repository.GetProjectNumberingOptionsAsync(cancellationToken);
            return Results.Ok(TenantContext.CompanyId is not Guid companyId
                ? options
                : options with { Organizations = options.Organizations.Where(item => item.Id == companyId).ToArray() });
        });

        api.MapGet("/customers", async (HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var includeInactive = await repository.HasUserPermissionAsync(actor, role, PermissionCodes.CustomerSettingsManage, cancellationToken);
            return Results.Ok(await repository.ListCustomersAsync(includeInactive, cancellationToken));
        });

        api.MapGet("/crm-integration", async (HttpContext context, CrmCustomerIntegrationService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.GetSettingsAsync(actor, role, cancellationToken));
        });

        api.MapPut("/crm-integration", async (UpdateCrmIntegrationRequest request, HttpContext context, CrmCustomerIntegrationService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.UpdateSettingsAsync(request.BaseUrl, request.Username, request.Password, request.AutoSyncEnabled, request.AutoSyncIntervalMinutes, actor, role, cancellationToken));
        });

        api.MapPost("/crm-integration/test", async (HttpContext context, CrmCustomerIntegrationService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.TestConnectionAsync(actor, role, cancellationToken));
        });

        api.MapPost("/crm-integration/sync", async (HttpContext context, CrmCustomerIntegrationService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.SyncCustomersAsync(actor, role, cancellationToken));
        });

        api.MapGet("/users", async (HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.OrganizationSettingsManage, cancellationToken)
                && !await repository.HasUserPermissionAsync(actor, role, PermissionCodes.RoleSettingsView, cancellationToken)) return Results.Forbid();
            var companyId = TenantContext.Current?.IsPlatformAdministrator == true ? TenantContext.CompanyId : TenantContext.Current?.PrimaryCompanyId;
            var users = (await repository.ListUsersAsync(cancellationToken)).Where(user => companyId is null || user.CompanyId == companyId);
            var mapped = new List<object>();
            foreach (var user in users)
            {
                mapped.Add(MapManagedUser(user, await repository.GetUserCompanyScopeAsync(user.Username, cancellationToken)));
            }
            return Results.Ok(mapped);
        });

        api.MapPost("/users", async (CreateManagedUserRequest request, HttpContext context, PdmWorkflowService workflow, IPdmRepository repository, IPasswordService passwords, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8) throw new PdmRuleException("初始密码至少需要8位。");
            var (actor, role) = CurrentUser(context.User);
            var saved = await workflow.CreateManagedUserAsync(new(request.Username, request.DisplayName, passwords.Hash(request.Password), request.Role, request.IsActive,
                request.CompanyId, request.CrossCompanyView, request.AccessibleCompanyIds, request.Roles), actor, role, cancellationToken);
            var scope = await repository.GetUserCompanyScopeAsync(saved.Username, cancellationToken);
            return Results.Created($"/api/users/{Uri.EscapeDataString(saved.Username)}", MapManagedUser(saved, scope));
        });

        api.MapPut("/users/{username}", async (string username, UpdateManagedUserRequest request, HttpContext context, PdmWorkflowService workflow, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var saved = await workflow.UpdateManagedUserAsync(new(username, request.DisplayName, request.Role, request.IsActive,
                request.CompanyId, request.CrossCompanyView, request.AccessibleCompanyIds, request.Roles), actor, role, cancellationToken);
            var scope = await repository.GetUserCompanyScopeAsync(saved.Username, cancellationToken);
            return Results.Ok(MapManagedUser(saved, scope));
        });

        api.MapPut("/users/{username}/reset-password", async (string username, HttpContext context, PdmWorkflowService workflow, IPasswordService passwords, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var saved = await workflow.ResetManagedUserPasswordAsync(username, passwords.Hash("11111111"), actor, role, cancellationToken);
            return Results.Ok(MapManagedUser(saved));
        });

        api.MapGet("/role-permissions", async (HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return !await repository.HasUserPermissionAsync(actor, role, PermissionCodes.RoleSettingsView, cancellationToken)
                ? Results.Forbid()
                : Results.Ok(MapRolePermissionDirectory(await repository.GetRolePermissionDirectoryAsync(cancellationToken)));
        });

        api.MapPut("/role-permissions/{targetRole}", async (string targetRole, UpdateRolePermissionsRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapRolePermissionDirectory(await workflow.UpdateRolePermissionsAsync(targetRole, request.SchemaVersion >= 2 ? request.Permissions.Concat([FunctionPermissionCatalog.SchemaMarker]).ToArray() : request.Permissions, actor, role, cancellationToken)));
        });

        api.MapPost("/role-permissions", async (CreateRoleRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Created("/api/role-permissions", MapRolePermissionDirectory(await workflow.CreateRoleAsync(request.Name, request.Description, request.SourceRoleCode, actor, role, cancellationToken)));
        });

        api.MapDelete("/role-permissions/{targetRole}", async (string targetRole, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(MapRolePermissionDirectory(await workflow.DeleteRoleAsync(targetRole, actor, role, cancellationToken)));
        });

        api.MapGet("/project-permissions", async (HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return !await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ProjectPermissionSettingsView, cancellationToken)
                ? Results.Forbid()
                : Results.Ok(MapProjectPermissionDirectory(await repository.GetProjectPermissionSettingsAsync(cancellationToken), await repository.GetProjectPositionUsageAsync(cancellationToken)));
        });

        api.MapPut("/project-permissions", async (UpdateProjectPermissionSettingsRequest request, HttpContext context, IPdmRepository repository, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var previous = await repository.GetProjectPermissionSettingsAsync(cancellationToken);
            var custom = request.CustomPositions ?? previous.CustomPositions;
            var rules = request.CustomPositions is null ? previous.Rules.Concat(request.Rules).GroupBy(item => item.Key).ToDictionary(group => group.Key, group => group.Last().Value) : request.Rules;
            var saved = await workflow.UpdateProjectPermissionSettingsAsync(new(rules, custom, request.SchemaVersion), actor, role, cancellationToken);
            return Results.Ok(MapProjectPermissionDirectory(saved, await repository.GetProjectPositionUsageAsync(cancellationToken)));
        });

        api.MapGet("/function-permissions", () => Results.Ok(RolePermissionCatalog.Permissions));
        api.MapGet("/projects/{projectId:guid}/position-members", async (Guid projectId, HttpContext context, IPdmRepository repository, CancellationToken ct) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, ct)) return Results.Forbid();
            var project = await repository.FindProjectAsync(projectId, ct) ?? throw new PdmNotFoundException("项目不存在。");
            var settings = await repository.GetProjectPermissionSettingsAsync(ct);
            return Results.Ok(new { Positions = settings.CustomPositions ?? [], Members = project.CustomPositionMembers });
        });
        api.MapPut("/projects/{projectId:guid}/position-members", async (Guid projectId, SetProjectPositionMembersRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken ct) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.SetCustomProjectPositionsAsync(projectId, request.Members, actor, role, ct));
        });

        api.MapGet("/organization-directory", async (IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var directory = ScopeOrganizationDirectory(await repository.GetOrganizationDirectoryAsync(cancellationToken));
            return Results.Ok(new
            {
                directory.Organizations,
                Units = directory.Units.Select(unit => new { unit.Id, unit.OrganizationId, unit.ParentUnitId, unit.Code, unit.Name, Kind = unit.Kind.ToString(), unit.CanManufacture, unit.CanArchiveStandardLibrary, unit.IsActive, unit.SortOrder }),
                directory.Memberships,
                directory.Managers,
                Users = directory.Users.Select(user => new
                {
                    user.Username,
                    user.DisplayName,
                    Role = user.EffectiveRoleCode,
                    Roles = user.EffectiveRoleCodes,
                    user.IsActive,
                    user.CompanyId,
                    user.CrossCompanyView,
                    AccessibleCompanyIds = user.AccessibleCompanyIds ?? Array.Empty<Guid>()
                })
            });
        });

        api.MapPost("/organizations", async (SaveProjectOrganizationRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var saved = await workflow.SaveProjectOrganizationAsync(new(null, request.Name, request.ProjectCompanyCode, request.ModelCompanyCode, request.IsActive), actor, role, cancellationToken);
            return Results.Created($"/api/organizations/{saved.Id}", saved);
        });

        api.MapPut("/organizations/{organizationId:guid}", async (Guid organizationId, SaveProjectOrganizationRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.SaveProjectOrganizationAsync(new(organizationId, request.Name, request.ProjectCompanyCode, request.ModelCompanyCode, request.IsActive), actor, role, cancellationToken));
        });

        api.MapPost("/organization-units", async (SaveOrganizationUnitRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var saved = await workflow.SaveOrganizationUnitAsync(new(null, request.OrganizationId, request.ParentUnitId, request.Code, request.Name, request.Kind, request.IsActive, request.SortOrder, request.CanManufacture, request.CanArchiveStandardLibrary), actor, role, cancellationToken);
            return Results.Created($"/api/organization-units/{saved.Id}", saved);
        });

        api.MapPut("/organization-units/{unitId:guid}", async (Guid unitId, SaveOrganizationUnitRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.SaveOrganizationUnitAsync(new(unitId, request.OrganizationId, request.ParentUnitId, request.Code, request.Name, request.Kind, request.IsActive, request.SortOrder, request.CanManufacture, request.CanArchiveStandardLibrary), actor, role, cancellationToken));
        });

        api.MapPut("/organization-users/{username}/memberships", async (string username, UpdateOrganizationMembershipsRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.SetOrganizationMembershipsAsync(username, request.UnitIds, request.PrimaryUnitId, actor, role, cancellationToken));
        });

        api.MapPut("/organization-units/{unitId:guid}/managers", async (Guid unitId, UpdateOrganizationUnitManagersRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.SetOrganizationUnitManagersAsync(unitId, request.PrimaryManager, request.CollaborativeManagers, actor, role, cancellationToken));
        });

        api.MapGet("/system-settings", async (HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, cancellationToken)) return Results.Forbid();
            return Results.Ok(await repository.GetSystemSettingsAsync(cancellationToken));
        });

        api.MapPut("/system-settings", async (UpdateSystemSettingsRequest request, HttpContext context, PdmWorkflowService workflow, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var currentSettings = await repository.GetSystemSettingsAsync(cancellationToken);
            var validationRules = request.ValidationRules ?? currentSettings.ValidationRules;
            var settings = new PdmSystemSettings(request.VaultRoot, request.ReleaseRoot)
            {
                MaterialAttachmentRoot = request.MaterialAttachmentRoot ?? currentSettings.MaterialAttachmentRoot,
                CheckoutHeartbeatSeconds = request.CheckoutHeartbeatSeconds,
                CheckoutLeaseMinutes = request.CheckoutLeaseMinutes,
                CheckoutOfflineGraceMinutes = request.CheckoutOfflineGraceMinutes,
                CheckoutReminderHours = request.CheckoutReminderHours,
                CheckoutStrongReminderHours = request.CheckoutStrongReminderHours,
                CheckoutOverdueHours = request.CheckoutOverdueHours,
                CheckoutForceReleaseHours = request.CheckoutForceReleaseHours,
                BomDrawingNumberProperty = request.BomDrawingNumberProperty,
                BomNameProperty = request.BomNameProperty,
                BomDescriptionProperty = request.BomDescriptionProperty,
                BomMaterialProperty = request.BomMaterialProperty,
                BomSpecificationProperty = request.BomSpecificationProperty,
                BomUnitProperty = request.BomUnitProperty,
                BomBrandProperty = request.BomBrandProperty,
                BomSurfaceTreatmentProperty = request.BomSurfaceTreatmentProperty,
                BomWeightProperty = request.BomWeightProperty,
                BomPropertyMappings = request.BomPropertyMappings ?? currentSettings.BomPropertyMappings,
                ValidationRules = validationRules,
                ApprovalWorkflows = request.ApprovalWorkflows ?? currentSettings.ApprovalWorkflows,
                MaterialCodeApproval = request.MaterialCodeApproval ?? currentSettings.MaterialCodeApproval,
                ReleaseChangeReasonTypes = request.ReleaseChangeReasonTypes ?? currentSettings.ReleaseChangeReasonTypes,
                ReleaseChangeReasonLabels = request.ReleaseChangeReasonLabels ?? currentSettings.ReleaseChangeReasonLabels,
                FormalSupplementPolicies = request.FormalSupplementPolicies ?? currentSettings.FormalSupplementPolicies,
                DrawingQrPolicy = request.DrawingQrPolicy ?? currentSettings.DrawingQrPolicy,
                PreviewConversion = request.PreviewConversion ?? currentSettings.PreviewConversion
            };
            return Results.Ok(await workflow.UpdateSystemSettingsAsync(settings, actor, role, cancellationToken));
        });

        api.MapPost("/system-settings/preview-agent/register", async (PreviewAgentRegisterRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var settings = await workflow.RegisterPreviewAgentAsync(request.AgentUrl, request.AgentToken, request.TimeoutMinutes, actor, role, cancellationToken, request.Name);
            return Results.Ok(new PreviewAgentRegisterResult(true, settings.PreviewConversion.NormalizedAgentUrl, settings.PreviewConversion.TimeoutMinutes,
                $"已登记为转图服务器：{settings.PreviewConversion.NormalizedAgentUrl}"));
        });

        api.MapGet("/system-settings/preview-agent/queue", async (HttpContext context, PdmWorkflowService workflow, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, cancellationToken)) return Results.Forbid();
            var pending = new List<string>();
            foreach (var id in await repository.ListPublishingReleasePackageIdsAsync(100, cancellationToken))
            {
                var package = await repository.FindReleasePackageAsync(id, cancellationToken);
                if (package is not null) pending.Add($"{package.Number}：{ReleasePublishingProgress.Describe(id)}");
            }
            return Results.Ok(new { scheduling = PreviewAgentPool.Snapshot(), pendingPublishing = pending });
        });

        api.MapPost("/system-settings/preview-agent/unregister", async (PreviewAgentUnregisterRequest? request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var settings = await workflow.ClearPreviewAgentAsync(actor, role, cancellationToken, request?.AgentUrl);
            return Results.Ok(new PreviewAgentRegisterResult(true, settings.PreviewConversion.AgentUrl, settings.PreviewConversion.TimeoutMinutes,
                settings.PreviewConversion.Mode == PreviewConversionMode.Local ? "已恢复为API服务器本机转换。" : "已移除本机，其余转图服务器继续使用。"));
        });

        api.MapPost("/system-settings/preview-agent/test", async (PreviewAgentProbeRequest? request, HttpContext context, IPdmRepository repository, IHttpClientFactory httpClientFactory, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, cancellationToken)) return Results.Forbid();
            var saved = (await repository.GetSystemSettingsAsync(cancellationToken)).PreviewConversion;
            // 前端可能只提交地址/令牌；填了地址就按"远程"测试，未填地址则沿用已保存的方式。
            var requestedMode = request?.Mode
                ?? (string.IsNullOrWhiteSpace(request?.AgentUrl) ? saved.Mode : PreviewConversionMode.Remote);
            var conversion = request is null ? saved : saved with
            {
                Mode = requestedMode,
                AgentUrl = string.IsNullOrWhiteSpace(request.AgentUrl) ? saved.AgentUrl : request.AgentUrl.Trim(),
                AgentToken = request.AgentToken ?? saved.AgentToken,
                TimeoutMinutes = request.TimeoutMinutes is > 0 ? request.TimeoutMinutes.Value : saved.TimeoutMinutes
            };
            if (conversion.Mode != PreviewConversionMode.Remote)
                return Results.Ok(new PreviewAgentProbeResult(true, "当前为本机转换（API服务器调用SolidWorks），无需转图服务器。", null, null, null));
            var url = conversion.NormalizedAgentUrl;
            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
                return Results.Ok(new PreviewAgentProbeResult(false, "转图服务器地址未配置或格式不正确，请填写如 http://192.168.2.50:5199 的地址。", null, null, null));
            try
            {
                using var client = httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(20);
                using var probeRequest = new HttpRequestMessage(HttpMethod.Get, url + PreviewAgentProtocol.HealthPath);
                if (!string.IsNullOrWhiteSpace(conversion.AgentToken))
                    probeRequest.Headers.Add(PreviewAgentProtocol.TokenHeader, conversion.AgentToken);
                using var response = await client.SendAsync(probeRequest, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return Results.Ok(new PreviewAgentProbeResult(false, $"转图服务器返回{(int)response.StatusCode}：{PreviewAgentProtocol.DescribeError(body)}", null, null, null));
                var payload = JsonSerializer.Deserialize<JsonElement>(body);
                var machine = payload.TryGetProperty("machine", out var machineElement) ? machineElement.GetString() : null;
                var workerPath = payload.TryGetProperty("workerPath", out var workerElement) ? workerElement.GetString() : null;
                var workerExists = payload.TryGetProperty("workerExists", out var existsElement) && existsElement.ValueKind == JsonValueKind.True;
                var message = workerExists
                    ? $"已连接转图服务器{machine}，SolidWorks转换程序就绪。"
                    : $"已连接{machine}，但该电脑上未找到SolidWorks转换程序，请检查转图代理配置。";
                return Results.Ok(new PreviewAgentProbeResult(workerExists, message, machine, workerExists, workerPath));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Results.Ok(new PreviewAgentProbeResult(false, $"无法连接转图服务器{url}：{exception.Message}", null, null, null));
            }
        });

        api.MapGet("/bom-validation-rules", async (IPdmRepository repository, CancellationToken cancellationToken) =>
            Results.Ok((await repository.GetSystemSettingsAsync(cancellationToken)).ValidationRules));

        api.MapGet("/bom-property-mappings", async (IPdmRepository repository, CancellationToken cancellationToken) =>
            Results.Ok((await repository.GetSystemSettingsAsync(cancellationToken)).BomPropertyMappings));

        api.MapGet("/drawing-qr-policy", async (IPdmRepository repository, CancellationToken cancellationToken) =>
            Results.Ok((await repository.GetSystemSettingsAsync(cancellationToken)).DrawingQrPolicy));

        api.MapGet("/system-settings/equipment-types", async (HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, cancellationToken)) return Results.Forbid();
            return Results.Ok(await repository.ListEquipmentTypesAsync(true, cancellationToken));
        });

        api.MapPut("/system-settings/equipment-types/{code:int}", async (int code, SaveEquipmentTypeRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.SaveEquipmentTypeAsync(code, request.Name, request.IsActive, actor, role, cancellationToken));
        });

        api.MapPut("/project-numbering/organizations/{organizationId:guid}/counters", async (Guid organizationId, UpdateOrganizationCountersRequest request, HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.StorageSettingsManage, cancellationToken)) return Results.Forbid();
            if (request.CurrentProjectSequence is < 0 or > 99999 || request.CurrentSerialSequence is < 0 or > 9999999)
                throw new PdmRuleException("项目流水必须为0到99999，序列流水必须为0到9999999。");
            return Results.Ok(await repository.AdvanceOrganizationCountersAsync(
                organizationId, request.CurrentProjectSequence, request.CurrentSerialSequence, cancellationToken));
        });

        api.MapPost("/projects", async (CreateProjectRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var project = await workflow.CreateNumberedProjectAsync(
                new CreateNumberedProjectCommand(
                    request.OrganizationId,
                    request.ProjectTypeCode,
                    request.EquipmentTypeCode,
                    request.CustomerId,
                    request.Name,
                    request.ProjectAlias,
                    request.SignedDate,
                    request.Quantity,
                    actor,
                    string.Empty,
                    string.Empty,
                    request.BomItemCategoryCode),
                actor,
                role,
                cancellationToken);
            return Results.Created($"/api/projects/{project.Id}", project);
        });

        api.MapPost("/projects/{projectId:guid}/copy-preview", async (
            Guid projectId,
            CopyProjectContentRequest request,
            HttpContext context,
            ProjectCopyService service,
            CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.PreviewAsync(request.SourceProjectId, projectId,
                new(request.CopyModels, request.CopyDrawings, request.CopyBom, request.CopyValidationItems, request.FolderIds),
                actor, role, cancellationToken));
        });

        api.MapPost("/projects/{projectId:guid}/copy", async (
            Guid projectId,
            CopyProjectContentRequest request,
            HttpContext context,
            ProjectCopyService service,
            CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await service.ExecuteAsync(request.SourceProjectId, projectId,
                new(request.CopyModels, request.CopyDrawings, request.CopyBom, request.CopyValidationItems, request.FolderIds),
                actor, role, cancellationToken));
        });

        api.MapPost("/projects/{projectId:guid}/children", async (Guid projectId, CreateSubprojectRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var project = await workflow.CreateSubprojectAsync(
                new CreateSubprojectCommand(projectId, request.Name, request.ProjectAlias, request.Quantity, EquipmentTypeCode: request.EquipmentTypeCode),
                actor,
                role,
                cancellationToken);
            return Results.Created($"/api/projects/{project.Id}", project);
        });

        api.MapPut("/projects/{projectId:guid}/details", async (Guid projectId, UpdateProjectDetailsRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.UpdateProjectDetailsAsync(projectId, new(
                request.OrganizationId,
                request.ProjectTypeCode,
                request.EquipmentTypeCode,
                request.CustomerId,
                request.Name,
                request.ProjectAlias,
                request.SignedDate,
                request.Quantity), actor, role, cancellationToken));
        });

        api.MapDelete("/projects/{projectId:guid}", async (Guid projectId, HttpContext context, IPdmRepository repository, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ProjectDelete, cancellationToken)) return Results.Forbid();
            var project = await workflow.DeleteProjectAsync(projectId, actor, role, cancellationToken);
            return Results.Ok(new { project.Id, project.Code });
        });

        api.MapGet("/projects/{projectId:guid}", async (Guid projectId, HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ProjectView, cancellationToken)) return Results.Forbid();
            var project = (await repository.ListProjectsForUserAsync(actor, role, cancellationToken)).FirstOrDefault(item => item.Id == projectId);
            return project is null ? Results.NotFound() : Results.Ok(project);
        });

        api.MapPut("/projects/{projectId:guid}/execution-unit", async (Guid projectId, UpdateProjectExecutionUnitRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.SetProjectExecutionUnitAsync(projectId, request.ExecutionUnitId, actor, role, cancellationToken));
        });

        api.MapPut("/projects/{projectId:guid}/staffing", async (Guid projectId, UpdateMainProjectStaffingRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var designLeads = request.DesignLeads is { Count: > 0 }
                ? request.DesignLeads
                : string.IsNullOrWhiteSpace(request.DesignLead) ? [] : [request.DesignLead];
            return Results.Ok(await workflow.SetMainProjectStaffingAsync(projectId, new(request.PrimaryProjectManager, request.CollaborativeProjectManagers, designLeads), actor, role, cancellationToken));
        });

        api.MapPut("/projects/{projectId:guid}/designers", async (Guid projectId, UpdateChildProjectDesignersRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.SetChildProjectDesignersAsync(projectId, request.Designers, actor, role, cancellationToken));
        });

        api.MapPut("/projects/{projectId:guid}/phase-owners", async (Guid projectId, UpdateProjectPhaseOwnersRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.SetProjectPhaseOwnersAsync(projectId, new(request.PhaseOwners), actor, role, cancellationToken));
        });

        api.MapPut("/projects/{projectId:guid}/manager", async (Guid projectId, UpdateChildProjectManagerRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.SetChildProjectManagerAsync(projectId, request.ProjectManager, actor, role, cancellationToken));
        });

        api.MapGet("/projects/{projectId:guid}/documents", async (Guid projectId, HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken)) return Results.Forbid();
            var folders = await repository.ListProjectFoldersAsync(projectId, actor, role, cancellationToken);
            var visibleFolderIds = folders.Where(folder => (folder.EffectiveAccess & FolderAccess.View) != 0).Select(folder => folder.Id).ToHashSet();
            var documents = await repository.ListDocumentsAsync(projectId, cancellationToken);
            var drawingReviewLockedIds = await repository.ListActiveDrawingReviewDocumentIdsAsync(projectId, cancellationToken);
            return Results.Ok(documents
                .Where(document => document.FolderId is Guid folderId && visibleFolderIds.Contains(folderId))
                .Select(document => document with { DrawingReviewLocked = drawingReviewLockedIds.Contains(document.Id) }));
        });

        api.MapGet("/projects/{projectId:guid}/folders", async (Guid projectId, HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken)) return Results.Forbid();
            return Results.Ok(await repository.ListProjectFoldersAsync(projectId, actor, role, cancellationToken));
        });

        api.MapGet("/projects/{projectId:guid}/folder-documents", async (Guid projectId, HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken)) return Results.Forbid();
            var folders = await repository.ListProjectFoldersAsync(projectId, actor, role, cancellationToken);
            var visibleFolderIds = folders.Where(folder => (folder.EffectiveAccess & FolderAccess.View) != 0).Select(folder => folder.Id).ToHashSet();
            var documents = await repository.ListProjectTreeDocumentsAsync(projectId, cancellationToken);
            var visible = new List<PdmDocument>();
            var projectAccess = new Dictionary<Guid, bool>();
            foreach (var document in documents)
            {
                if (document.FolderId is not Guid folderId || !visibleFolderIds.Contains(folderId)) continue;
                if (!projectAccess.TryGetValue(document.ProjectId, out var canRead))
                    projectAccess[document.ProjectId] = canRead = await repository.HasProjectReadAccessAsync(document.ProjectId, actor, role, cancellationToken);
                if (canRead) visible.Add(document);
            }
            return Results.Ok(visible);
        });

        api.MapPut("/projects/{projectId:guid}/folders/{folderId:guid}/permissions", async (Guid projectId, Guid folderId, SaveProjectFolderPermissionsRequest request, HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.FolderSettingsManage, cancellationToken)) return Results.Forbid();
            var permissions = request.Permissions.Select(item => new SaveFolderPermissionCommand(item.PrincipalType, item.PrincipalKey, item.Access)).ToArray();
            return Results.Ok(await repository.SetProjectFolderPermissionsAsync(projectId, folderId, permissions, actor, role, cancellationToken));
        });

        api.MapGet("/folder-template", async (HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return !await repository.HasUserPermissionAsync(actor, role, PermissionCodes.FolderSettingsManage, cancellationToken) ? Results.Forbid() : Results.Ok(await repository.ListFolderTemplateAsync(cancellationToken));
        });

        api.MapPut("/folder-template", async (SaveFolderTemplateRequest request, HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.FolderSettingsManage, cancellationToken)) return Results.Forbid();
            var nodes = request.Nodes.Select(node => new SaveFolderTemplateNodeCommand(node.FolderKey, node.Name, node.SortOrder, node.InheritPermissions,
                node.Permissions.Select(item => new SaveFolderPermissionCommand(item.PrincipalType, item.PrincipalKey, item.Access)).ToArray())).ToArray();
            return Results.Ok(await repository.SaveFolderTemplateAsync(nodes, cancellationToken));
        });

        api.MapPost("/projects/{projectId:guid}/documents/register", async (Guid projectId, RegisterDocumentRequest request, HttpContext context, IPdmRepository repository, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken)) return Results.Forbid();
            return Results.Ok(await workflow.RegisterDocumentAsync(
                new RegisterDocumentCommand(projectId, request.DrawingNumber, request.Name, request.FileName, request.Kind, request.FolderId, request.RelatedModelDocumentId, request.SourceSha256, request.AllowDuplicateContent, request.DuplicateReason),
                actor,
                role,
                cancellationToken));
        });

        api.MapPost("/projects/{projectId:guid}/documents/registration-preflight", async (Guid projectId, DocumentRegistrationPreflightRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var candidates = (request.Candidates ?? Array.Empty<DocumentRegistrationCandidateRequest>())
                .Select(candidate => new DocumentRegistrationCandidate(candidate.CandidateKey, candidate.FileName, candidate.Kind, candidate.SourceSha256))
                .ToArray();
            return Results.Ok(await workflow.PreflightDocumentRegistrationAsync(projectId, candidates, actor, role, cancellationToken));
        });

        api.MapGet("/projects/{projectId:guid}/document-relations", async (Guid projectId, HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken)) return Results.Forbid();
            return Results.Ok(await repository.ListDocumentRelationsAsync(projectId, cancellationToken));
        });

        api.MapPost("/documents/version-verifications", async (DocumentVersionVerificationBatchRequest request,
            HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await workflow.VerifyDocumentVersionsAsync(request.Documents, actor, role, cancellationToken));
        });

        api.MapGet("/documents/{documentId:guid}/versions", async (Guid documentId, int? take, HttpContext context, IPdmRepository repository, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            await workflow.AuditVersionReadAsync(documentId, Guid.Empty, actor, role, "document.version.list", cancellationToken);
            var versions = await repository.ListDocumentVersionsAsync(documentId, cancellationToken, take);
            return Results.Ok(versions);
        });

        api.MapGet("/documents/{documentId:guid}/preview-source-version", async (Guid documentId, Guid? versionId, HttpContext context, IPdmRepository repository, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            await workflow.AuditVersionReadAsync(documentId, Guid.Empty, actor, role, "document.version.preview-source", cancellationToken);
            var versions = await repository.ListDocumentVersionsAsync(documentId, cancellationToken);
            var version = versionId.HasValue
                ? versions.SingleOrDefault(item => item.Id == versionId.Value)
                : versions.OrderByDescending(item => item.CreatedAt).FirstOrDefault();
            if (version is null)
            {
                return Results.NotFound(new
                {
                    detail = versionId.HasValue
                        ? "图纸审核绑定的版本不存在，不能继续审核。"
                        : "该图档已登记，但尚未提交首个存档版本。装配体中可见的可能只是SolidWorks缓存几何，不是可下载的源文件；请从原始工作目录找回文件后完成首次存档。"
                });
            }

            return Results.Ok(new DocumentPreviewSourceVersionResponse(
                version.Id,
                version.FileLength,
                version.Sha256,
                version.CreatedAt));
        });

        api.MapGet("/documents/{documentId:guid}/versions/{versionId:guid}", async (Guid documentId, Guid versionId, HttpContext context, IPdmRepository repository, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var version = await repository.FindDocumentVersionAsync(documentId, versionId, cancellationToken);
            if (version is null) return Results.NotFound();
            await workflow.AuditVersionReadAsync(documentId, versionId, actor, role, "document.version.view", cancellationToken);
            return Results.Ok(version);
        });

        api.MapGet("/documents/{documentId:guid}/versions/{versionId:guid}/file", async (Guid documentId, Guid versionId, bool download, HttpContext context, IPdmRepository repository, IFileStorage storage, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var document = await repository.FindDocumentAsync(documentId, cancellationToken);
            var version = await repository.FindDocumentVersionAsync(documentId, versionId, cancellationToken);
            if (document is null || version is null) return Results.NotFound();
            var project = await repository.FindProjectAsync(document.ProjectId, cancellationToken);
            if (project is null) return Results.NotFound();
            await workflow.AuditVersionReadAsync(documentId, versionId, actor, role, download ? "document.version.download" : "document.version.read", cancellationToken);
            await storage.VerifyVersionReadAsync(project, new StoredFile(version.StorageRelativePath, version.FileLength, version.Sha256, version.CreatedAt), cancellationToken);
            var path = StorageLocationPolicy.ResolveUnder(project.VaultLocation, version.StorageRelativePath);
            var stream = await storage.OpenReadAsync(path, cancellationToken);
            return Results.File(stream, "application/octet-stream", download ? document.FileName : null, enableRangeProcessing: true);
        });

        api.MapGet("/documents/{documentId:guid}/versions/{versionId:guid}/preview", async (Guid documentId, Guid versionId, HttpContext context, IPdmRepository repository, IFileStorage storage, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var document = await repository.FindDocumentAsync(documentId, cancellationToken);
            var version = await repository.FindDocumentVersionAsync(documentId, versionId, cancellationToken);
            if (document is null || version?.Preview is null) return Results.NotFound();
            if (document.Kind == DocumentKind.Drawing && version.Status == DocumentVersionStatus.Released
                && version.SourceVersionId.HasValue)
            {
                var source = await repository.FindDocumentVersionAsync(documentId, version.SourceVersionId.Value, cancellationToken);
                var legacy = source is null || string.Equals(source.StorageRelativePath, version.StorageRelativePath, StringComparison.OrdinalIgnoreCase);
                if (!legacy && !string.Equals(version.Preview.SourceSha256, version.Sha256, StringComparison.OrdinalIgnoreCase))
                    return Results.NotFound();
            }
            var project = await repository.FindProjectAsync(document.ProjectId, cancellationToken);
            if (project is null) return Results.NotFound();
            await workflow.AuditVersionReadAsync(documentId, versionId, actor, role, "document.preview.read", cancellationToken);
            await storage.VerifyPreviewFileAsync(project, version.Preview, cancellationToken);
            var path = StorageLocationPolicy.ResolveUnder(project.VaultLocation, version.Preview.StorageRelativePath);
            var stream = await storage.OpenReadAsync(path, cancellationToken);
            var contentType = version.Preview.Format == DocumentPreviewFormat.Pdf ? "application/pdf" : "model/step";
            return Results.File(stream, contentType, enableRangeProcessing: true);
        });

        api.MapGet("/production-drawings", async (bool includeHistory, Guid? projectId, bool? indexOnly, HttpContext context, ProductionDrawingService drawings, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(indexOnly == true ? await drawings.ListIndexAsync(actor, role, includeHistory, cancellationToken)
                : await drawings.ListAsync(actor, role, includeHistory, projectId, cancellationToken));
        });

        api.MapGet("/production-drawings/{versionId:guid}", async (Guid versionId, HttpContext context, ProductionDrawingService drawings, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var item = (await drawings.ListAsync(actor, role, true, null, cancellationToken)).FirstOrDefault(value => value.VersionId == versionId);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        api.MapGet("/production-drawings/{documentId:guid}/versions/{versionId:guid}/preview", async (
            Guid documentId, Guid versionId, Guid? modelDocumentId, Guid? modelVersionId, HttpContext context,
            ProductionDrawingService drawings, IPdmRepository repository, IFileStorage storage,
            PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            await workflow.AuditVersionReadAsync(documentId, versionId, actor, role, "document.production.download", cancellationToken);
            var version = await drawings.EnsureProductionUseAllowedAsync(documentId, versionId, cancellationToken);
            var document = await repository.FindDocumentAsync(documentId, cancellationToken);
            if (document?.Kind != DocumentKind.Drawing) return Results.BadRequest(new { detail = "请选择正式生产图纸。" });
            var project = await repository.FindProjectAsync(document.ProjectId, cancellationToken);
            if (project is null) return Results.NotFound();
            var item = (await drawings.ListAsync(actor, role, true, project.Id, cancellationToken))
                .FirstOrDefault(value => value.VersionId == versionId);
            if (item is null) return Results.NotFound();
            var expectedFormat = DocumentPreviewFormat.Pdf;
            if (modelDocumentId.HasValue || modelVersionId.HasValue)
            {
                var model = item.ModelFiles.FirstOrDefault(value => value.DocumentId == modelDocumentId && value.VersionId == modelVersionId);
                if (!modelDocumentId.HasValue || !modelVersionId.HasValue || model is null)
                    return Results.BadRequest(new { detail = "所选三维模型不属于该图纸的同一发布包。" });
                await workflow.AuditVersionReadAsync(modelDocumentId.Value, modelVersionId.Value, actor, role, "document.production.download", cancellationToken);
                version = await drawings.EnsureProductionUseAllowedAsync(modelDocumentId.Value, modelVersionId.Value, cancellationToken);
                if (version.ReleasePackageId != item.ReleasePackageId || !model.StepReady)
                    throw new PdmConflictException("同包正式 STEP 尚未就绪或已经作废，下载已停止。");
                expectedFormat = DocumentPreviewFormat.Step;
            }
            else if (!item.PdfReady)
                throw new PdmConflictException("正式 PDF 尚未就绪，不能用旧版替代。");
            if (version.Preview?.Format != expectedFormat)
                throw new PdmConflictException("生产文件格式与正式版本不匹配，请刷新后重试。");
            await storage.VerifyPreviewFileAsync(project, version.Preview, cancellationToken);
            await drawings.EnsureProductionUseAllowedAsync(documentId, versionId, cancellationToken);
            if (modelDocumentId.HasValue)
                await drawings.EnsureProductionUseAllowedAsync(modelDocumentId.Value, modelVersionId!.Value, cancellationToken);
            var path = StorageLocationPolicy.ResolveUnder(project.VaultLocation, version.Preview.StorageRelativePath);
            var stream = await storage.OpenReadAsync(path, cancellationToken);
            context.Response.Headers.CacheControl = "no-store";
            return Results.File(stream, expectedFormat == DocumentPreviewFormat.Pdf ? "application/pdf" : "model/step", enableRangeProcessing: true);
        });

        api.MapPost("/projects/{projectId:guid}/production-drawings/archive", async (
            Guid projectId, ProductionDrawingArchiveRequest request, HttpContext context, ProductionDrawingService drawings,
            IPdmRepository repository, IFileStorage storage, PdmWorkflowService workflow,
            IOptions<PdmStorageOptions> storageOptions, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var project = await repository.FindProjectAsync(projectId, cancellationToken);
            if (project is null) return Results.NotFound();
            if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken)) return Results.Forbid();
            var ids = (request.VersionIds ?? []).Where(id => id != Guid.Empty).Distinct().ToArray();
            if (ids.Length == 0 || ids.Length > 200) return Results.BadRequest(new { detail = "请选择 1–200 张正式图纸。" });
            if (request.Format is not ("Pdf" or "Step")) return Results.BadRequest(new { detail = "下载格式只能是 PDF 或正式 STEP。" });
            var available = (await drawings.ListAsync(actor, role, true, projectId, cancellationToken))
                .ToDictionary(item => item.VersionId);
            if (ids.Any(id => !available.ContainsKey(id))) return Results.BadRequest(new { detail = "所选图纸已作废、已删除或无法访问，不能用于生产。" });
            var items = ids.Select(id => available[id]).ToArray();
            foreach (var item in items)
                await drawings.EnsureProductionUseAllowedAsync(item.DocumentId, item.VersionId, cancellationToken);
            var archiveEntries = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            if (request.Format == "Pdf" && items.Any(item => !item.PdfReady))
                return Results.BadRequest(new { detail = "所选图纸中有 PDF 待转换，不能用旧版替代。" });
            if (request.Format == "Step" && items.Any(item => item.ModelFiles.Count == 0
                || item.ModelFiles.Any(model => !model.StepReady || !model.VersionId.HasValue)))
                return Results.BadRequest(new { detail = "所选图纸存在未关联同包三维模型或 STEP 待转换项，不能用 SolidWorks 源文件替代。" });

            var tempRoot = StorageLocationPolicy.Normalize(storageOptions.Value.UploadTempRoot);
            Directory.CreateDirectory(tempRoot);
            var archivePath = Path.Combine(tempRoot, $"production-drawings-{Guid.NewGuid():N}.zip");
            var stream = new FileStream(archivePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.DeleteOnClose);
            try
            {
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var packageItems in items.GroupBy(item => item.ReleasePackageId))
                    {
                        var bomItems = packageItems.SelectMany(item => item.BomItems).DistinctBy(item => item.Id).ToArray();
                        var quantities = packageItems.SelectMany(item => item.BomQuantities).DistinctBy(item => item.MaterialKey).ToArray();
                        if (bomItems.Length == 0)
                            throw new PdmRuleException("所选图纸缺少发布包冻结的BOM数据，无法下载对应BOM，请刷新后重试。");
                        var packageNumber = SafeArchiveSegment(packageItems.First().ReleasePackageNumber);
                        var bomEntry = archive.CreateEntry($"{packageNumber}/{packageNumber}-BOM.xlsx", CompressionLevel.Fastest);
                        await using var bomTarget = bomEntry.Open();
                        var packageProject = await repository.FindProjectAsync(packageItems.First().ProjectId, cancellationToken) ?? project;
                        var main = await repository.FindProjectAsync(packageProject.RootProjectId ?? packageProject.ParentProjectId ?? packageProject.Id, cancellationToken) ?? packageProject;
                        var directory = await repository.GetOrganizationDirectoryAsync(cancellationToken);
                        var metadata = ProjectWorkbookMetadata.ForProject(packageProject, main, directory, "发布包BOM清单", "发布包版本", packageNumber,
                            timeProvider.GetUtcNow().ToOffset(TimeSpan.FromHours(8)), actor, "选中图纸 · 发布冻结BOM");
                        var package = (await repository.ListReleasePackagesAsync(packageProject.Id, cancellationToken)).FirstOrDefault(item => item.Id == packageItems.Key);
                        if (package is not null) metadata = metadata with {
                            Fields = metadata.Fields.Select(field => field.Key == "整套数量" ? new KeyValuePair<string, string>(field.Key, $"{Math.Max(1, package.WholeSetMultiplier)}套") : field).ToArray(),
                            Notes = $"发布日期：{package.PublishedAt?.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd") ?? "未发布"}" };
                        await bomTarget.WriteAsync(ProjectWorkbookHeader.Apply(BomWorkbook.WriteProductionRelease(bomItems, quantities), metadata), cancellationToken);
                    }
                    foreach (var item in items)
                    {
                        if (request.Format == "Pdf")
                        {
                            var version = await drawings.EnsureProductionUseAllowedAsync(item.DocumentId, item.VersionId, cancellationToken);
                            var preview = version.Preview;
                            if (preview?.Format != DocumentPreviewFormat.Pdf)
                                throw new PdmRuleException("所选图纸中有 PDF 待转换，不能用旧版替代。");
                            await workflow.AuditVersionReadAsync(item.DocumentId, item.VersionId, actor, role, "document.production.download", cancellationToken);
                            await storage.VerifyPreviewFileAsync(project, preview, cancellationToken);
                            var entryName = $"{SafeArchiveSegment(item.ReleasePackageNumber)}/{SafeArchiveSegment(item.DrawingNumber)}.pdf";
                            if (archiveEntries.TryGetValue(entryName, out var existingVersion))
                            {
                                if (existingVersion == item.VersionId) continue;
                                throw new PdmConflictException("所选文件包含同一图号的不同版本，请按发布包分别下载。");
                            }
                            archiveEntries[entryName] = item.VersionId;
                            var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                            await using var target = entry.Open();
                            await using var source = await storage.OpenReadAsync(StorageLocationPolicy.ResolveUnder(project.VaultLocation, preview.StorageRelativePath), cancellationToken);
                            await source.CopyToAsync(target, cancellationToken);
                            continue;
                        }

                        foreach (var model in item.ModelFiles)
                        {
                            var modelDocument = await repository.FindDocumentAsync(model.DocumentId, cancellationToken);
                            var modelVersion = model.VersionId.HasValue
                                ? await drawings.EnsureProductionUseAllowedAsync(model.DocumentId, model.VersionId.Value, cancellationToken)
                                : null;
                            if (modelDocument is null || modelVersion is null)
                                throw new PdmConflictException($"三维模型 {model.DrawingNumber} 的正式 STEP 不存在，下载已停止。");
                            var preview = modelVersion.Preview;
                            if (modelDocument.Kind is not (DocumentKind.Assembly or DocumentKind.Part)
                                || modelVersion.Status != DocumentVersionStatus.Released
                                || modelVersion.ReleasePackageId != item.ReleasePackageId
                                || preview?.Format != DocumentPreviewFormat.Step
                                || !string.Equals(preview.SourceSha256, modelVersion.Sha256, StringComparison.OrdinalIgnoreCase))
                                throw new PdmConflictException($"三维模型 {model.DrawingNumber} 的正式 STEP 与发布包不匹配，下载已停止。");

                            await workflow.AuditVersionReadAsync(model.DocumentId, model.VersionId!.Value, actor, role, "document.production.download", cancellationToken);
                            await storage.VerifyPreviewFileAsync(project, preview, cancellationToken);
                            var entryName = $"{SafeArchiveSegment(item.ReleasePackageNumber)}/{SafeArchiveSegment(model.DrawingNumber)}.step";
                            if (archiveEntries.TryGetValue(entryName, out var existingModelVersion))
                            {
                                if (existingModelVersion == model.VersionId.Value) continue;
                                throw new PdmConflictException("所选文件包含同一图号的不同版本，请按发布包分别下载。");
                            }
                            archiveEntries[entryName] = model.VersionId.Value;
                            var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                            await using var target = entry.Open();
                            await using var source = await storage.OpenReadAsync(StorageLocationPolicy.ResolveUnder(project.VaultLocation, preview.StorageRelativePath), cancellationToken);
                            await source.CopyToAsync(target, cancellationToken);
                        }
                    }
                }
                // 打包期间可能有人作废图纸；交付前重新核对，避免发送已失效的整包。
                foreach (var item in items)
                {
                    await drawings.EnsureProductionUseAllowedAsync(item.DocumentId, item.VersionId, cancellationToken);
                    if (request.Format == "Step")
                        foreach (var model in item.ModelFiles)
                            await drawings.EnsureProductionUseAllowedAsync(model.DocumentId, model.VersionId!.Value, cancellationToken);
                }
            }
            catch
            {
                await stream.DisposeAsync();
                throw;
            }
            stream.Position = 0;
            context.Response.Headers.CacheControl = "no-store";
            return Results.Stream(stream, "application/zip", $"生产图纸-{(request.Format == "Pdf" ? "PDF" : "STEP")}-{SafeArchiveSegment(project.Code)}-{timeProvider.GetLocalNow():yyyyMMdd-HHmmss}.zip");
        });

        api.MapPut("/production-drawings/{releasePackageId:guid}/{documentId:guid}/delivery", async (
            Guid releasePackageId, Guid documentId, UpdateDrawingDeliveryRequest request,
            HttpContext context, ProductionDrawingService drawings, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await drawings.UpdateDeliveryAsync(releasePackageId, documentId,
                new DrawingDeliveryOverride(request.Priority, request.RequiredOn), actor, role, cancellationToken));
        });

        api.MapGet("/documents/{documentId:guid}/versions/{versionId:guid}/markup", async (Guid documentId, Guid versionId, HttpContext context, IPdmRepository repository, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var document = await repository.FindDocumentAsync(documentId, cancellationToken);
            var version = await repository.FindDocumentVersionAsync(documentId, versionId, cancellationToken);
            if (document is null || version is null) return Results.NotFound();
            if (!await repository.HasProjectContentReadAccessAsync(document.ProjectId, actor, role, cancellationToken)) return Results.Forbid();
            var project = await repository.FindProjectAsync(document.ProjectId, cancellationToken);
            if (project is null) return Results.NotFound();
            var path = StorageLocationPolicy.ResolveUnder(project.VaultLocation, Path.Combine(".markups", documentId.ToString("N"), versionId.ToString("N"), "review.markup"));
            if (!File.Exists(path)) return Results.NotFound();
            await workflow.AuditVersionReadAsync(documentId, versionId, actor, role, "document.markup.read", cancellationToken);
            return Results.File(path, "application/octet-stream", enableRangeProcessing: true);
        });

        api.MapPut("/documents/{documentId:guid}/versions/{versionId:guid}/markup", async (Guid documentId, Guid versionId, HttpRequest request, HttpContext context, IPdmRepository repository, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            const long maximumMarkupLength = 50L * 1024 * 1024;
            var (actor, role) = CurrentUser(context.User);
            var document = await repository.FindDocumentAsync(documentId, cancellationToken);
            var version = await repository.FindDocumentVersionAsync(documentId, versionId, cancellationToken);
            if (document is null || version is null) return Results.NotFound();
            if (!await repository.HasProjectContentReadAccessAsync(document.ProjectId, actor, role, cancellationToken)
                || !await repository.HasUserPermissionAsync(actor, role, PermissionCodes.DrawingReviewAnnotate, cancellationToken)) return Results.Forbid();
            if (request.ContentLength is > maximumMarkupLength) return Results.BadRequest(new { detail = "批注文件不能超过50MB。" });
            var project = await repository.FindProjectAsync(document.ProjectId, cancellationToken);
            if (project is null) return Results.NotFound();

            var path = StorageLocationPolicy.ResolveUnder(project.VaultLocation, Path.Combine(".markups", documentId.ToString("N"), versionId.ToString("N"), "review.markup"));
            var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("批注存储目录无效。");
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory, $"review-{Guid.NewGuid():N}.upload");
            long length = 0;
            try
            {
                await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
                {
                    var buffer = new byte[128 * 1024];
                    int read;
                    while ((read = await request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                    {
                        length += read;
                        if (length > maximumMarkupLength) throw new PdmRuleException("批注文件不能超过50MB。");
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    }
                    await output.FlushAsync(cancellationToken);
                }
                if (length == 0) throw new PdmRuleException("批注文件不能为空。");
                File.Move(temporaryPath, path, true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }

            await repository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), timeProvider.GetUtcNow(), actor, "document.markup.save", nameof(DocumentVersion), versionId.ToString(), $"保存图档批注：{document.FileName}；{length}字节"), cancellationToken);
            return Results.Ok(new { length });
        });

        api.MapPost("/documents/{documentId:guid}/open-manifest", async (Guid documentId, CreateControlledOpenManifestRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.CreateControlledOpenManifestAsync(
                documentId,
                request.VersionId,
                request.ReleasedOnly,
                request.ForEdit,
                actor,
                role,
                cancellationToken,
                request.ResolveLegacyReferences,
                request.TreeScopeOnly));
        });

        api.MapGet("/documents/{documentId:guid}/versions/compare", async (Guid documentId, Guid left, Guid right, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.CompareVersionsAsync(documentId, left, right, actor, role, cancellationToken));
        });

        api.MapPost("/documents/{documentId:guid}/versions/{versionId:guid}/restore", async (Guid documentId, Guid versionId, RestoreVersionRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var result = await workflow.RestoreVersionAsync(documentId, versionId, actor, role, request.ChangeNote, cancellationToken,
                request.OperationId, request.ExpectedVersionId, request.CheckoutSessionId);
            return Results.Ok(new { document = result.Document, version = result.Version });
        });

        api.MapPost("/documents/{documentId:guid}/versions/publish", async (Guid documentId, PublishDocumentVersionRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.PublishVersionAsync(documentId, request.SourceVersionId, request.ReleasePackageId, request.ApprovalTaskId, actor, role, cancellationToken));
        });

        api.MapGet("/projects/{projectId:guid}/reference-tree", async (Guid projectId, HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken)) return Results.Forbid();
            var tree = await repository.GetReferenceTreeAsync(projectId, cancellationToken);
            return tree is null ? Results.NoContent() : Results.Ok(tree);
        });

        api.MapGet("/projects/{projectId:guid}/boms/{kind}", async (Guid projectId, string kind, bool? summary, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!Enum.TryParse<BomKind>(kind, true, out var bomKind))
            {
                return Results.BadRequest(new { message = "BOM类型必须是Standard、NonStandard、Unclassified或Electrical。" });
            }

            var items = await workflow.GetBomAsync(projectId, bomKind, actor, role, cancellationToken);
            return Results.Ok(summary == true && bomKind is (BomKind.Standard or BomKind.NonStandard or BomKind.Electrical) ? BomSummaryProjection.Group(items) : items);
        });

        api.MapGet("/projects/{projectId:guid}/bom-source-data", async (Guid projectId, Guid? rootDocumentId, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.GetBomSourceDataAsync(projectId, actor, role, cancellationToken, rootDocumentId));
        });

        api.MapPut("/projects/{projectId:guid}/boms/{kind}", async (Guid projectId, string kind, bool? includeDraft, bool? summary, ReplaceBomRequest request, HttpContext context, IPdmRepository repository, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse<BomKind>(kind, true, out var bomKind) || bomKind is not (BomKind.Standard or BomKind.NonStandard or BomKind.Electrical)) return Results.BadRequest(new { message = "BOM类型必须是Standard、NonStandard或Electrical。" });
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken)) return Results.Forbid();
            var inputs = request.SummaryRows is null ? request.Items : BomSummaryProjection.Expand(
                await workflow.GetBomAsync(projectId, bomKind, actor, role, cancellationToken), request.SummaryRows, request.Items, request.SummaryOrder);
            var saved = await workflow.ReplaceBomAsync(projectId, bomKind, inputs, actor, role, cancellationToken);
            if (summary == true)
            {
                var summaryDraft = (await repository.ListBomVersionsAsync(projectId, bomKind, cancellationToken))
                    .Where(version => version.State == BomVersionState.Draft).MaxBy(version => version.VersionNumber);
                return Results.Ok(new {
                    items = BomSummaryProjection.Group(saved),
                    draft = summaryDraft is null ? null : summaryDraft with { Items = BomSummaryProjection.Group(summaryDraft.Items) }
                });
            }
            if (includeDraft != true) return Results.Ok(saved);
            var draft = (await repository.ListBomVersionsAsync(projectId, bomKind, cancellationToken))
                .Where(version => version.State == BomVersionState.Draft).MaxBy(version => version.VersionNumber);
            var savedById = saved.ToDictionary(item => item.Id);
            // Reuse returned rows, but preserve any snapshot values that differ from the working BOM.
            static BomItem SnapshotValues(BomItem item) => item with { ReleaseReviewToken = null, ReleaseReviewTokens = null, ReleaseConfirmation = null };
            var overrides = draft?.Items.Where(item => !savedById.TryGetValue(item.Id, out var current) || SnapshotValues(current) != SnapshotValues(item)).ToArray() ?? [];
            return Results.Ok(new
            {
                items = saved,
                draft = draft is null ? null : draft with { Items = [] },
                draftItemIds = draft?.Items.Select(item => item.Id).ToArray() ?? [],
                draftItemOverrides = overrides
            });
        });

        api.MapPost("/projects/{projectId:guid}/boms/{kind}/import", async (Guid projectId, string kind, bool? summary, IFormFile file, HttpContext context, IPdmRepository repository, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse<BomKind>(kind, true, out var bomKind) || bomKind is not (BomKind.Standard or BomKind.NonStandard or BomKind.Electrical)) return Results.BadRequest(new { message = "BOM类型必须是Standard、NonStandard或Electrical。" });
            if (!string.Equals(Path.GetExtension(file.FileName), ".xlsx", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new { message = "只支持标准XLSX格式的BOM文件。" });
            await using var input = file.OpenReadStream();
            var items = BomWorkbook.Read(input);
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken)) return Results.Forbid();
            var saved = await workflow.ReplaceBomAsync(projectId, bomKind, items, actor, role, cancellationToken);
            return Results.Ok(summary == true ? BomSummaryProjection.Group(saved) : saved);
        }).DisableAntiforgery();

        api.MapGet("/projects/{projectId:guid}/export-metadata", async (Guid projectId, Guid? releasePackageId, HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken)) return Results.Forbid();
            var project = await repository.FindProjectAsync(projectId, cancellationToken);
            if (project is null) return Results.NotFound();
            var main = await repository.FindProjectAsync(project.RootProjectId ?? project.ParentProjectId ?? project.Id, cancellationToken) ?? project;
            var directory = await repository.GetOrganizationDirectoryAsync(cancellationToken);
            var metadata = ProjectWorkbookMetadata.ForProject(project, main, directory, "项目资料", "资料版本", "—", DateTimeOffset.Now, actor);
            if (releasePackageId.HasValue)
            {
                var package = (await repository.ListReleasePackagesAsync(projectId, cancellationToken)).FirstOrDefault(item => item.Id == releasePackageId);
                if (package is null) return Results.NotFound();
                metadata = metadata with {
                    Fields = metadata.Fields.Select(field => field.Key == "整套数量" ? new KeyValuePair<string, string>(field.Key, $"{Math.Max(1, package.WholeSetMultiplier)}套") : field).ToArray(),
                    Notes = $"发布日期：{package.PublishedAt?.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd") ?? "未发布"}" };
            }
            return Results.Ok(metadata);
        });
        api.MapGet("/projects/{projectId:guid}/boms/wear-parts/export", async (Guid projectId, string? mode, HttpContext context, IPdmRepository repository, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!Enum.TryParse<BomWorkbookExportMode>(mode ?? nameof(BomWorkbookExportMode.Summary), true, out var exportMode)) return Results.BadRequest(new { message = "导出方式必须是Summary或Structure。" });
            var project = await repository.FindProjectAsync(projectId, cancellationToken);
            if (project is null) return Results.NotFound();
            var items = await workflow.GetWearPartBomAsync(projectId, actor, role, cancellationToken);
            var mainProjectId = project.RootProjectId ?? project.ParentProjectId ?? project.Id;
            var mainProject = mainProjectId == project.Id ? project : await repository.FindProjectAsync(mainProjectId, cancellationToken) ?? project;
            var directory = await repository.GetOrganizationDirectoryAsync(cancellationToken);
            var exportedAt = DateTimeOffset.Now;
            var contextData = new BomWorkbookExportContext(
                "易损件BOM（统计）",
                mainProject.Code,
                mainProject.Name,
                project.Id == mainProject.Id ? "—" : project.Code,
                project.Id == mainProject.Id ? "—" : project.Name,
                "自动汇总，不用于发布",
                project.ExecutionUnitName ?? mainProject.ExecutionUnitName ?? "—",
                DisplayUserNames(
                    new[] { project.PrimaryProjectManager ?? mainProject.PrimaryProjectManager }
                        .Concat(project.CollaborativeProjectManagers.Count > 0 ? project.CollaborativeProjectManagers : mainProject.CollaborativeProjectManagers),
                    directory),
                DisplayUserNames(project.DesignLeads.Concat(project.Designers), directory),
                [],
                null,
                exportedAt);
            contextData = contextData with { Header = ProjectWorkbookMetadata.ForProject(project, mainProject, directory, "易损件BOM", "BOM版本", contextData.BomVersion, exportedAt, actor, (project.Id == mainProject.Id ? "当前主项目 · " : "当前子项目 · ") + (exportMode == BomWorkbookExportMode.Summary ? "汇总导出" : "结构导出")) };
            var fileName = BomWorkbook.ExportFileName(project.Code, "易损件BOM", "统计", exportedAt);
            return Results.File(BomWorkbook.WriteExport(items, contextData, exportMode), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        });

        api.MapGet("/projects/{projectId:guid}/boms/{kind}/export", async (Guid projectId, string kind, string? mode, HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken)) return Results.Forbid();
            if (!Enum.TryParse<BomKind>(kind, true, out var bomKind) || bomKind is not (BomKind.Standard or BomKind.NonStandard or BomKind.Electrical)) return Results.BadRequest(new { message = "BOM类型必须是Standard、NonStandard或Electrical。" });
            if (!Enum.TryParse<BomWorkbookExportMode>(mode ?? nameof(BomWorkbookExportMode.Summary), true, out var exportMode)) return Results.BadRequest(new { message = "导出方式必须是Summary或Structure。" });
            var project = await repository.FindProjectAsync(projectId, cancellationToken);
            if (project is null) return Results.NotFound();
            var items = (await repository.GetBomAsync(projectId, bomKind, cancellationToken)).Where(item => !item.IsManuallyExcluded).ToArray();
            var mainProjectId = project.RootProjectId ?? project.ParentProjectId ?? project.Id;
            var mainProject = mainProjectId == project.Id ? project : await repository.FindProjectAsync(mainProjectId, cancellationToken) ?? project;
            var versions = await repository.ListBomVersionsAsync(projectId, bomKind, cancellationToken);
            var latestVersion = versions.OrderByDescending(item => item.UpdatedAt).FirstOrDefault();
            var versionLabel = latestVersion?.Label
                ?? items.Select(item => item.Revision).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?? "—";
            var latestPackage = (await repository.ListReleasePackagesAsync(projectId, cancellationToken))
                .OrderByDescending(item => item.CreatedAt)
                .FirstOrDefault();
            var directory = await repository.GetOrganizationDirectoryAsync(cancellationToken);
            var bomName = BomExportName(bomKind);
            var exportedAt = DateTimeOffset.Now;
            var contextData = new BomWorkbookExportContext(
                bomName,
                mainProject.Code,
                mainProject.Name,
                project.Id == mainProject.Id ? "—" : project.Code,
                project.Id == mainProject.Id ? "—" : project.Name,
                versionLabel,
                project.ExecutionUnitName ?? mainProject.ExecutionUnitName ?? "—",
                DisplayUserNames(
                    new[] { project.PrimaryProjectManager ?? mainProject.PrimaryProjectManager }
                        .Concat(project.CollaborativeProjectManagers.Count > 0 ? project.CollaborativeProjectManagers : mainProject.CollaborativeProjectManagers),
                    directory),
                DisplayUserNames(project.DesignLeads.Concat(project.Designers), directory),
                ApprovalPeople(latestPackage, directory),
                latestVersion?.ReleasedAt,
                exportedAt);
            contextData = contextData with { Header = ProjectWorkbookMetadata.ForProject(project, mainProject, directory, bomName, "BOM版本", versionLabel, exportedAt, actor, (project.Id == mainProject.Id ? "当前主项目 · " : "当前子项目 · ") + (exportMode == BomWorkbookExportMode.Summary ? "汇总导出" : "结构导出")) };
            var fileName = BomWorkbook.ExportFileName(project.Code, bomName, versionLabel, exportedAt);
            return Results.File(BomWorkbook.WriteExport(items, contextData, exportMode), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        });

        api.MapPost("/projects/{projectId:guid}/boms/generate", async (Guid projectId, bool? apply, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.GenerateMechanicalBomAsync(projectId, apply == true, actor, role, cancellationToken));
        });

        api.MapPost("/projects/{projectId:guid}/boms/items/{itemId:guid}/resolve", async (Guid projectId, Guid itemId, ResolveBomItemRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.ResolveBomItemAsync(projectId, itemId, new ResolveBomItemCommand(request.Action, request.TargetKind), actor, role, cancellationToken));
        });

        api.MapPatch("/projects/{projectId:guid}/boms/items/batch", async (Guid projectId, BatchUpdateBomItemsRequest request, HttpContext context, PdmWorkflowService workflow, MaterialService materials, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var command = new BatchUpdateBomItemsCommand(
                request.ItemIds, request.Fields, request.TargetKind, request.Unit, request.DrawingNumber, request.Name,
                request.Specification, request.Remark, request.Brand, request.Material, request.SurfaceTreatment,
                request.Weight, request.Quantity, request.Revision, request.Complete, HeatTreatment: request.HeatTreatment,
                ImpactStage: request.ImpactStage);
            var updated = await workflow.BatchUpdateBomItemsAsync(projectId, command, actor, role, cancellationToken);
            if (request.ItemIds.Count == 1
                && request.Fields.Contains("drawingNumber", StringComparer.OrdinalIgnoreCase)
                && (request.Fields.Contains("specification", StringComparer.OrdinalIgnoreCase) || request.Fields.Contains("brand", StringComparer.OrdinalIgnoreCase)))
                await materials.SetBomMaterialLinkByCodeAsync(projectId, request.ItemIds[0], request.DrawingNumber, actor, role, cancellationToken);
            return Results.Ok(updated);
        });

        api.MapPost("/projects/{projectId:guid}/boms/items/batch-delete", async (Guid projectId, BatchDeleteBomItemsRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.BatchDeleteBomItemsAsync(projectId, new BatchDeleteBomItemsCommand(request.ItemIds, request.Reason), actor, role, cancellationToken));
        });

        api.MapPost("/projects/{projectId:guid}/boms/items/batch-restore", async (Guid projectId, BatchRestoreBomItemsRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.BatchRestoreBomItemsAsync(projectId, new BatchRestoreBomItemsCommand(request.ItemIds, request.Mode), actor, role, cancellationToken));
        });

        api.MapPost("/projects/{projectId:guid}/boms/items/permanently-delete-manual", async (Guid projectId, PermanentlyDeleteManualBomItemsRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.PermanentlyDeleteManualBomItemsAsync(projectId, new PermanentlyDeleteManualBomItemsCommand(request.ItemIds), actor, role, cancellationToken));
        });

        api.MapPost("/projects/{projectId:guid}/boms/items/release-confirmation", async (Guid projectId, ConfirmBomReleaseRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            await workflow.ConfirmBomReleaseNotRequiredAsync(projectId, request.Tokens, request.Confirmed, request.Reason, actor, role, cancellationToken);
            return Results.Ok();
        });

        api.MapPost("/projects/{projectId:guid}/boms/items/release-exclusion", async (Guid projectId, SetBomReleaseExclusionRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.SetBomReleaseExclusionAsync(
                projectId,
                new SetBomReleaseExclusionCommand(request.ItemIds, request.Excluded, request.Reason),
                actor,
                role,
                cancellationToken));
        });

        api.MapPost("/projects/{projectId:guid}/boms/items/restore-source", async (Guid projectId, RestoreBomItemsFromSourceRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.RestoreBomItemsFromSourceAsync(projectId, new RestoreBomItemsFromSourceCommand(request.ItemIds), actor, role, cancellationToken));
        });

        api.MapPost("/projects/{projectId:guid}/boms/items/reclassify-source/preview", async (Guid projectId, ReclassifyBomItemsFromSourceRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.PreviewBomSourceReclassificationAsync(projectId, new ReclassifyBomItemsFromSourceCommand(request.ItemIds, request.TargetKind), actor, role, cancellationToken));
        });

        api.MapPost("/projects/{projectId:guid}/boms/items/reclassify-source", async (Guid projectId, ReclassifyBomItemsFromSourceRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.ReclassifyBomItemsFromSourceAsync(projectId, new ReclassifyBomItemsFromSourceCommand(request.ItemIds, request.TargetKind), actor, role, cancellationToken));
        });

        api.MapGet("/projects/{projectId:guid}/cad-property-writebacks", async (Guid projectId, bool? activeOnly, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.ListCadPropertyWritebacksAsync(projectId, activeOnly ?? false, actor, role, cancellationToken));
        });

        api.MapGet("/projects/{projectId:guid}/cad-property-writeback-versions", async (Guid projectId, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.ListCadPropertyWritebackVersionsAsync(projectId, actor, role, cancellationToken));
        });

        api.MapPost("/cad-property-writebacks/{id:guid}/start", async (Guid id, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.StartCadPropertyWritebackAsync(id, actor, role, cancellationToken));
        });

        api.MapPost("/cad-property-writebacks/{id:guid}/complete", async (Guid id, CompleteCadPropertyWritebackRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.CompleteCadPropertyWritebackAsync(id, request.ResultVersionId, actor, role, cancellationToken));
        });

        api.MapPost("/cad-property-writebacks/{id:guid}/fail", async (Guid id, FailCadPropertyWritebackRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.FailCadPropertyWritebackAsync(id, request.Error, request.Conflict, actor, role, cancellationToken));
        });

        api.MapGet("/projects/{projectId:guid}/drawing-reviews", async (Guid projectId, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.ListDrawingReviewPackagesAsync(projectId, actor, role, cancellationToken));
        });

        api.MapGet("/projects/{projectId:guid}/drawing-review-candidates", async (Guid projectId, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.ListDrawingReviewCandidatesAsync(projectId, actor, role, cancellationToken));
        });

        api.MapGet("/projects/{projectId:guid}/drawing-reviewers", async (Guid projectId, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var candidates = await workflow.ListDrawingReviewersAsync(projectId, actor, role, cancellationToken);
            return Results.Ok(candidates.Select(item => new ApprovalTransferCandidateResponse(item.Username, item.DisplayName)));
        });

        api.MapPost("/projects/{projectId:guid}/drawing-reviews", async (Guid projectId, CreateDrawingReviewRequest? request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (request is null) return Results.BadRequest(new { message = "请求内容不完整。" });
            IReadOnlyList<string>? assignedReviewers = request.AssignedReviewers is { Count: > 0 }
                ? request.AssignedReviewers
                : string.IsNullOrWhiteSpace(request.AssignedReviewer) ? null : [request.AssignedReviewer];
            if (assignedReviewers is null || assignedReviewers.All(string.IsNullOrWhiteSpace))
                return Results.BadRequest(new { message = "发起图纸审核时必须至少指定一位审核人。" });
            return Results.Ok(await workflow.CreateDrawingReviewPackageAsync(projectId, request.ModelDocumentIds, assignedReviewers, actor, role, cancellationToken));
        });

        api.MapPost("/drawing-reviews/{packageId:guid}/withdraw", async (Guid packageId, WithdrawDrawingReviewRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.WithdrawDrawingReviewPackageAsync(packageId, request.Reason, actor, role, cancellationToken));
        });

        api.MapPost("/drawing-reviews/{packageId:guid}/abandon-writebacks", async (Guid packageId, AbandonDrawingReviewWritebacksRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.AbandonDrawingReviewWritebacksAsync(packageId, request.Reason, actor, role, cancellationToken));
        });

        api.MapPost("/drawing-reviews/{packageId:guid}/markups", async (Guid packageId, AddDrawingReviewMarkupRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.AddDrawingReviewMarkupAsync(packageId, new AddDrawingReviewMarkupCommand(request.ItemId, request.Target, request.ViewName, request.NormalizedX, request.NormalizedY, request.Text, request.Severity), actor, role, cancellationToken));
        });

        api.MapPost("/drawing-reviews/{packageId:guid}/markups/{markupId:guid}/resolve", async (Guid packageId, Guid markupId, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.ResolveDrawingReviewMarkupAsync(packageId, markupId, actor, role, cancellationToken));
        });

        api.MapPost("/drawing-reviews/{packageId:guid}/items/{itemId:guid}/decision", async (Guid packageId, Guid itemId, DecideDrawingReviewTargetRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.DecideDrawingReviewTargetAsync(packageId, itemId, new DecideDrawingReviewTargetCommand(request.Target, request.Decision, request.Comment), actor, role, cancellationToken));
        });

        api.MapPost("/drawing-reviews/{packageId:guid}/items/{itemId:guid}/resubmit", async (Guid packageId, Guid itemId, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.ResubmitDrawingReviewItemAsync(packageId, itemId, actor, role, cancellationToken));
        });

        api.MapGet("/projects/{projectId:guid}/boms/empty-declarations", async (Guid projectId, HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken)) return Results.Forbid();
            return Results.Ok(await repository.GetBomEmptyDeclarationsAsync(projectId, cancellationToken));
        });

        api.MapPut("/projects/{projectId:guid}/boms/{kind}/empty-declaration", async (Guid projectId, string kind, SetBomEmptyDeclarationRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse<BomKind>(kind, true, out var bomKind)) return Results.BadRequest(new { message = "BOM类型必须是Standard、NonStandard或Electrical。" });
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.SetBomEmptyDeclarationAsync(projectId, bomKind, request.DeclaredEmpty, actor, role, cancellationToken));
        });

        api.MapGet("/projects/{projectId:guid}/release-packages", async (Guid projectId, HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return !await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken)
                ? Results.Forbid()
                : Results.Ok(await repository.ListReleasePackagesAsync(projectId, cancellationToken));
        });

        api.MapGet("/projects/{projectId:guid}/bom-versions", async (Guid projectId, string? kind, bool? summary, HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken)) return Results.Forbid();
            BomKind? bomKind = null;
            if (!string.IsNullOrWhiteSpace(kind))
            {
                if (!Enum.TryParse<BomKind>(kind, true, out var parsed) || parsed is not (BomKind.Standard or BomKind.NonStandard or BomKind.Electrical))
                    return Results.BadRequest(new { message = "BOM类型必须是Standard、NonStandard或Electrical。" });
                bomKind = parsed;
            }
            var versions = await repository.ListBomVersionsAsync(projectId, bomKind, cancellationToken);
            return Results.Ok(summary == true ? versions.Select(version => version with { Items = BomSummaryProjection.Group(version.Items) }) : versions);
        });

        api.MapGet("/projects/{projectId:guid}/bom-baselines", async (Guid projectId, HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return !await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken)
                ? Results.Forbid()
                : Results.Ok(await repository.ListManufacturingBomBaselinesAsync(projectId, cancellationToken));
        });

        api.MapGet("/edit-locks", async (HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.ListEditLocksAsync(actor, role, cancellationToken));
        });

        api.MapPost("/edit-sessions/{sessionId:guid}/heartbeat", async (Guid sessionId, EditSessionHeartbeatRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.HeartbeatEditSessionAsync(sessionId, actor, role, request.MachineName, request.DocumentIds ?? [], cancellationToken));
        });

        api.MapPost("/documents/{documentId:guid}/checkout", async (Guid documentId, CheckoutRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.CheckoutAsync(documentId, actor, role, request.SessionId, request.MachineName, cancellationToken, request.DrawingReviewWritebackId));
        });

        api.MapPost("/documents/{documentId:guid}/complete-edit", async (Guid documentId, CompleteEditRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.CompleteEditWithoutChangesAsync(documentId, actor, role, request.CheckoutSessionId, request.Sha256, cancellationToken));
        });

        api.MapPost("/documents/{documentId:guid}/discard-checkout", async (Guid documentId, DiscardCheckoutRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.DiscardCheckoutAsync(documentId, actor, role, request.CheckoutSessionId, cancellationToken));
        });

        api.MapPost("/documents/{documentId:guid}/close-edit", async (Guid documentId, CloseEditRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.CloseEditAsync(documentId, actor, role, request.SessionId, request.CheckedOutAt, request.Sha256, request.DraftPreserved, cancellationToken));
        });

        api.MapPost("/documents/{documentId:guid}/request-release", async (Guid documentId, EditLockActionRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.RequestEditLockReleaseAsync(documentId, actor, role, request.Reason, cancellationToken));
        });

        api.MapPost("/documents/{documentId:guid}/force-release", async (Guid documentId, EditLockActionRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.ForceReleaseEditLockAsync(documentId, actor, role, request.Reason, cancellationToken));
        });

        api.MapGet("/documents/{documentId:guid}/where-used", async (Guid documentId, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.ListWhereUsedAsync(documentId, actor, role, cancellationToken));
        });

        api.MapPost("/documents/{documentId:guid}/obsolete", async (Guid documentId, LifecycleActionRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.ObsoleteDocumentAsync(documentId, actor, role, request.Comment, cancellationToken));
        });

        api.MapPost("/archive-batches", async (ArchiveBatchRequest request, HttpContext context, PdmWorkflowService workflow, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var requestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));
            var inputs = request.Files.Select(file => new ArchiveBatchInput(file.DocumentId,
                new StoredFile(file.Request.StorageRelativePath, file.Request.FileLength, file.Request.Sha256, timeProvider.GetUtcNow()),
                file.Request.Properties ?? new Dictionary<string, string?>(),
                new CadReferenceSnapshot(Guid.NewGuid(), file.Request.ProjectId, file.DocumentId, timeProvider.GetUtcNow(), actor, file.Request.Root,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(file.Request.Root))))),
                file.Request.Comment, file.Request.IsProjectRoot, file.Request.ExpectedVersionId,
                file.Request.DrawingNumber, file.Request.Name, file.Request.FileName, file.Request.DrawingReviewWritebackId)).ToArray();
            return Results.Ok(await workflow.CheckInBatchAsync(request.BatchId, requestHash, actor, role, request.CheckoutSessionId, inputs, cancellationToken));
        });
        api.MapGet("/archive-batches/{batchId:guid}", async (Guid batchId, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.GetArchiveBatchAsync(batchId, actor, role, cancellationToken));
        });
        api.MapPost("/archive-batches/{batchId:guid}/projects/{projectId:guid}/registrations", async (Guid batchId, Guid projectId,
            ReserveArchiveDocumentRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var r = request.Document;
            return Results.Ok(await workflow.RegisterDocumentAsync(new RegisterDocumentCommand(projectId, r.DrawingNumber, r.Name, r.FileName,
                r.Kind, r.FolderId, r.RelatedModelDocumentId, r.SourceSha256, r.AllowDuplicateContent, r.DuplicateReason), actor, role, cancellationToken,
                new ArchiveRegistrationContext(batchId, request.SessionId, request.MachineName)));
        });
        api.MapPost("/archive-batches/{batchId:guid}/cancel-registration", async (Guid batchId, CancelArchiveRegistrationRequest request,
            HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, _) = CurrentUser(context.User);
            await repository.CancelArchiveRegistrationAsync(batchId, actor, request.SessionId, cancellationToken);
            return Results.Ok(new { cancelled = true });
        });

        api.MapPost("/documents/{documentId:guid}/checkin/preflight", async (Guid documentId, CheckInPreflightRequest request, HttpContext context, PdmWorkflowService workflow, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var rootJson = JsonSerializer.Serialize(request.Root, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var snapshot = new CadReferenceSnapshot(
                Guid.NewGuid(),
                request.ProjectId,
                documentId,
                timeProvider.GetUtcNow(),
                actor,
                request.Root,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rootJson))));
            await workflow.PreflightCheckInAsync(
                documentId,
                actor,
                role,
                request.CheckoutSessionId,
                request.Properties ?? new Dictionary<string, string?>(),
                snapshot,
                request.IsProjectRoot,
                cancellationToken,
                request.DrawingNumber,
                request.Name,
                request.FileName,
                request.DrawingReviewWritebackId,
                request.ExpectedVersionId,
                true,
                request.PlannedDocumentIds);
            return Results.Ok(new { valid = true });
        });

        api.MapPost("/drawing-reviews/{packageId:guid}/supervisor-decision", async (Guid packageId, DecideDrawingReviewSupervisorRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.DecideDrawingReviewSupervisorAsync(packageId, new DecideDrawingReviewSupervisorCommand(request.Decision, request.Comment), actor, role, cancellationToken));
        });

        api.MapPost("/documents/{documentId:guid}/checkin", async (Guid documentId, CheckInRequest request, HttpContext context, PdmWorkflowService workflow, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var rootJson = JsonSerializer.Serialize(request.Root, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var snapshot = new CadReferenceSnapshot(
                Guid.NewGuid(),
                request.ProjectId,
                documentId,
                timeProvider.GetUtcNow(),
                actor,
                request.Root,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rootJson))));
            var file = new StoredFile(request.StorageRelativePath, request.FileLength, request.Sha256, timeProvider.GetUtcNow());
            var result = await workflow.CheckInAsync(
                documentId,
                actor,
                role,
                request.CheckoutSessionId,
                file,
                request.Comment,
                request.Properties ?? new Dictionary<string, string?>(),
                snapshot,
                request.IsProjectRoot,
                request.ForceVersion,
                cancellationToken,
                request.DrawingNumber,
                request.Name,
                request.FileName,
                request.DrawingReviewWritebackId,
                request.ExpectedVersionId,
                true,
                request.DeferBomRefresh);
            return Results.Ok(new
            {
                document = result.Document,
                version = result.Version,
                versionCreated = result.VersionCreated,
                bomUpdate = result.BomUpdate,
                bomUpdateError = result.BomUpdateError,
                bomRefreshDeferred = result.BomRefreshDeferred,
                affectedAssemblies = result.AffectedAssemblies
            });
        });

        api.MapPost("/release-packages", async (CreateReleasePackageRequest request, HttpContext context, IPdmRepository repository, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasProjectContentReadAccessAsync(request.ProjectId, actor, role, cancellationToken)) return Results.Forbid();
            if (request.Scope == ReleaseScope.LegacyCombined)
                return Results.BadRequest(new { detail = "新发布必须选择标准件、非标件或电气件，并明确勾选本次范围；历史发布包仍可继续查看和处理。" });
            if (request.Scope is (ReleaseScope.NonStandardWithDrawing or ReleaseScope.NonStandardSupplement) && request.DrawingRequiredOn is null)
                return Results.BadRequest(new { detail = "图纸发布必须填写生产需求日期。" });
            return Results.Ok(await workflow.CreateScopedReleasePackageAsync(
                    request.ProjectId, request.ReferenceSnapshotId, string.Empty,
                    string.Empty,
                    request.ChangeReason ?? string.Empty,
                    "未指定", null,
                    request.Scope, request.SelectedBomItemIds,
                    actor, role, cancellationToken, request.SelectedBomItemQuantities, request.WholeSetMultiplier,
                    request.DrawingPriority, request.DrawingRequiredOn, request.DrawingDeliveryOverrides, request.BomItemDeliveryOverrides, request.RootDocumentId, request.ConfirmedRemovedBomItemIds, request.RelatedOplIssueIds));
        });

        api.MapPut("/release-packages/{releasePackageId:guid}/draft", async (Guid releasePackageId, UpdateReleasePackageDraftRequest request, HttpContext context, IPdmRepository repository, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var package = await repository.FindReleasePackageAsync(releasePackageId, cancellationToken);
            if (package is null) return Results.NotFound();
            if (package.Scope is (ReleaseScope.NonStandardWithDrawing or ReleaseScope.NonStandardSupplement)
                && (request.DrawingRequiredOn ?? package.DrawingRequiredOn) is null)
                return Results.BadRequest(new { detail = "图纸发布必须填写生产需求日期。" });
            return Results.Ok(await workflow.UpdateReleasePackageDraftAsync(
                releasePackageId,
                request.ChangeReason,
                request.SelectedBomItemIds,
                request.SelectedBomItemQuantities,
                actor,
                role,
                cancellationToken,
                request.WholeSetMultiplier,
                request.DrawingPriority,
                request.DrawingRequiredOn ?? package.DrawingRequiredOn,
                request.DrawingDeliveryOverrides,
                request.BomItemDeliveryOverrides,
                request.ConfirmedRemovedBomItemIds,
                request.RelatedOplIssueIds));
        });

        api.MapDelete("/release-packages/{releasePackageId:guid}/draft", async (Guid releasePackageId, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            await workflow.DeleteReleasePackageDraftAsync(releasePackageId, actor, role, cancellationToken);
            return Results.NoContent();
        });

        api.MapPost("/release-packages/{releasePackageId:guid}/u9-retry", async (Guid releasePackageId, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.RetryLongLeadU9Async(releasePackageId, actor, role, cancellationToken));
        });

        api.MapGet("/projects/{projectId:guid}/u9-sync-blockers", async (Guid projectId, HttpContext context, IPdmRepository repository, ProjectBomU9SyncService service, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken)) return Results.Forbid();
            return Results.Ok(await service.ListU9SyncBlockersAsync(projectId, cancellationToken));
        });

        api.MapPost("/release-packages/{releasePackageId:guid}/preview/retry", async (Guid releasePackageId, HttpContext context, PdmWorkflowService workflow, ReleasePreviewCoordinator coordinator, ReleasePublishingCoordinator publishingCoordinator, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var package = await workflow.DemandReleasePreviewRetryAsync(releasePackageId, actor, role, cancellationToken);
            if (package.State == ReleasePackageState.Publishing)
            {
                publishingCoordinator.TryStart("ManualConversionRetry");
                return Results.Accepted($"/api/projects/{package.ProjectId}/release-packages", new { Message = "已重新排队转图，完成后继续发布，无需重新审批。" });
            }
            if (!coordinator.TryStart("Manual"))
                return Results.Ok(new { Message = "转图任务已在后台运行，本次重试已排队。", Package = package.Number });
            return Results.Accepted($"/api/projects/{package.ProjectId}/release-packages", new { Message = "转图任务已在后台启动。" });
        });

        api.MapGet("/projects/{projectId:guid}/release-preview-items", async (Guid projectId, Guid? releasePackageId, HttpContext context, IPdmRepository repository, ReleasePreviewService previewService, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken)) return Results.Forbid();
            var items = await previewService.ListProjectItemsAsync(projectId, releasePackageId, cancellationToken);
            return Results.Ok(items.Select(MapReleasePreviewItem).ToArray());
        });

        api.MapPost("/release-packages/{releasePackageId:guid}/preview-items/{documentId:guid}/retry", async (Guid releasePackageId, Guid documentId, HttpContext context, IPdmRepository repository, ReleasePreviewService previewService, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var package = await repository.FindReleasePackageAsync(releasePackageId, cancellationToken);
            if (package is null) return Results.NotFound();
            if (!await repository.HasProjectContentReadAccessAsync(package.ProjectId, actor, role, cancellationToken)) return Results.Forbid();
            if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ReleaseManage, cancellationToken)) return Results.Forbid();
            var item = await previewService.RetryItemAsync(releasePackageId, documentId, actor, cancellationToken);
            return Results.Ok(new ReleasePreviewItemRetryResult(item.Succeeded, MapReleasePreviewItem(item),
                item.Succeeded ? "已重新转出该图档。" : "该图档转出失败，请查看该项说明；其他已成功图档不受影响。"));
        });

        // 批量重试：多个图档合成一次转换请求（引用树只下发一次），比逐条重试快得多。
        api.MapPost("/release-packages/{releasePackageId:guid}/preview-items/retry", async (Guid releasePackageId, ReleasePreviewRetryRequest request, HttpContext context, IPdmRepository repository, ReleasePreviewService previewService, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var package = await repository.FindReleasePackageAsync(releasePackageId, cancellationToken);
            if (package is null) return Results.NotFound();
            if (!await repository.HasProjectContentReadAccessAsync(package.ProjectId, actor, role, cancellationToken)) return Results.Forbid();
            if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.ReleaseManage, cancellationToken)) return Results.Forbid();
            var documentIds = request.DocumentIds?.Where(id => id != Guid.Empty).Distinct().ToArray() ?? [];
            if (documentIds.Length == 0) return Results.BadRequest(new { detail = "请先选择要重试的图档。" });
            var items = await previewService.RetryItemsAsync(releasePackageId, documentIds, actor, cancellationToken);
            var retried = items.Where(item => documentIds.Contains(item.DocumentId)).Select(MapReleasePreviewItem).ToArray();
            var failed = retried.Count(item => !item.Succeeded);
            return Results.Ok(new ReleasePreviewItemsRetryResult(true, retried,
                failed == 0 ? $"已重新转出 {retried.Length} 项。" : $"重试完成：成功 {retried.Length - failed} 项，失败 {failed} 项。"));
        });

        api.MapPost("/projects/{projectId:guid}/release-preview-items/archive", async (Guid projectId, ReleasePreviewArchiveRequest request, HttpContext context, IPdmRepository repository, IFileStorage storage, ReleasePreviewService previewService, IOptions<PdmStorageOptions> storageOptions, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken)) return Results.Forbid();
            var project = await repository.FindProjectAsync(projectId, cancellationToken);
            if (project is null) return Results.NotFound();
            var items = (await previewService.ListProjectItemsAsync(projectId, request.ReleasePackageId, cancellationToken))
                .Where(item => item.Succeeded && !string.IsNullOrWhiteSpace(item.PreviewRelativePath))
                .Where(item => request.DocumentIds is not { Count: > 0 } || request.DocumentIds.Contains(item.DocumentId))
                .ToArray();
            if (items.Length == 0) return Results.BadRequest(new { detail = "所选图档还没有可下载的STEP/PDF文件。" });

            var vaultRoot = StorageLocationPolicy.Normalize(project.VaultLocation);
            var tempRoot = StorageLocationPolicy.Normalize(storageOptions.Value.UploadTempRoot);
            Directory.CreateDirectory(tempRoot);
            var archivePath = Path.Combine(tempRoot, $"release-preview-{Guid.NewGuid():N}.zip");
            var stream = new FileStream(archivePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.DeleteOnClose);
            try
            {
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var item in items)
                    {
                        await storage.VerifyPreviewFileAsync(project, new DocumentPreviewArtifact(item.Format, item.PreviewRelativePath, item.FileLength, item.Sha256, item.Sha256), cancellationToken);
                        var extension = item.Format == DocumentPreviewFormat.Pdf ? ".pdf" : ".step";
                        var entry = archive.CreateEntry($"{item.ReleasePackageNumber}/{item.DrawingNumber}{extension}", CompressionLevel.Fastest);
                        await using var target = entry.Open();
                        await using var source = await storage.OpenReadAsync(StorageLocationPolicy.ResolveUnder(vaultRoot, item.PreviewRelativePath), cancellationToken);
                        await source.CopyToAsync(target, cancellationToken);
                    }
                }
            }
            catch
            {
                await stream.DisposeAsync();
                throw;
            }
            stream.Position = 0;
            return Results.Stream(stream, "application/zip", $"图纸转出-{timeProvider.GetLocalNow():yyyyMMdd-HHmmss}.zip");
        });

        api.MapPost("/release-packages/{releasePackageId:guid}/submit", async (Guid releasePackageId, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.SubmitReleasePackageAsync(releasePackageId, actor, role, cancellationToken));
        });

        api.MapPost("/release-packages/{releasePackageId:guid}/withdraw", async (Guid releasePackageId, LifecycleActionRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.WithdrawReleasePackageAsync(releasePackageId, actor, role, request.Comment, cancellationToken));
        });

        api.MapGet("/release-packages/{releasePackageId:guid}/item-comments", async (Guid releasePackageId, HttpContext context, ReleaseItemCommentService comments, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await comments.ListAsync(releasePackageId, actor, role, cancellationToken));
        });

        api.MapPost("/release-packages/{releasePackageId:guid}/item-comments", async (Guid releasePackageId, AddReleaseItemCommentRequest request, HttpContext context, ReleaseItemCommentService comments, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await comments.AddAsync(releasePackageId, request.BomItemId, request.Comment, actor, role, cancellationToken));
        });

        api.MapPost("/approval-tasks/{taskId:guid}/decision", async (Guid taskId, ApprovalRequest request, HttpContext context, PdmWorkflowService workflow, ReleasePublishingCoordinator publishingCoordinator, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            await workflow.EnsureApprovalTaskActorAsync(taskId, actor, cancellationToken);
            var decided = await workflow.DecideAsync(taskId, actor, role, request.Decision, request.Comment, cancellationToken, publishImmediately: false);
            if (decided.State == ReleasePackageState.Publishing) publishingCoordinator.TryStart("FinalApproval");
            return Results.Ok(decided);
        });

        api.MapGet("/approval-tasks/{taskId:guid}/transfer-candidates", async (Guid taskId, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, _) = CurrentUser(context.User);
            var candidates = await workflow.ListApprovalTransferCandidatesAsync(taskId, actor, cancellationToken);
            return Results.Ok(candidates.Select(item => new ApprovalTransferCandidateResponse(item.Username, item.DisplayName)));
        });

        api.MapPost("/approval-tasks/{taskId:guid}/transfer", async (Guid taskId, ApprovalTransferRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, _) = CurrentUser(context.User);
            return Results.Ok(await workflow.TransferApprovalAsync(taskId, actor, request.TargetUsername, request.Comment, cancellationToken));
        });

        api.MapPost("/approval-tasks/{taskId:guid}/emergency-decision", async (Guid taskId, EmergencyApprovalRequest request, HttpContext context, PdmWorkflowService workflow, ReleasePublishingCoordinator publishingCoordinator, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var decided = await workflow.EmergencyDecideAsync(taskId, actor, role, request.Decision, request.Reason, cancellationToken, publishImmediately: false);
            if (decided.State == ReleasePackageState.Publishing) publishingCoordinator.TryStart("EmergencyFinalApproval");
            return Results.Ok(decided);
        });

        api.MapGet("/approval-tasks/{taskId:guid}/emergency-capability", async (Guid taskId, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(new { CanDecide = await workflow.CanEmergencyDecideAsync(taskId, actor, role, cancellationToken) });
        });

        api.MapGet("/approval-tasks/mine", async (HttpContext context, IPdmRepository repository, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var projects = await repository.ListProjectsForUserAsync(actor, role, cancellationToken);
            var results = new List<MyApprovalTaskResponse>();
            foreach (var project in projects)
            {
                var tasks = await repository.ListPendingApprovalTasksAsync(project.Id, cancellationToken);
                foreach (var task in tasks)
                {
                    if (!string.Equals(task.Assignee, actor, StringComparison.OrdinalIgnoreCase)
                        && !await workflow.CanEmergencyDecideAsync(task.Id, actor, role, cancellationToken)) continue;
                    results.Add(new MyApprovalTaskResponse(
                        task.Id, project.Id, project.Code, project.Name, task.ReleasePackageId, task.ReleasePackageNumber,
                        task.Stage, task.PackageState, task.CreatedAt));
                }
            }
            return Results.Ok(results.OrderBy(item => item.CreatedAt));
        });

        api.MapGet("/notifications/mine", async (int? take, HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, _) = CurrentUser(context.User);
            return Results.Ok(await repository.ListUserNotificationsAsync(actor, take ?? 100, cancellationToken));
        });

        api.MapPost("/notifications/{notificationId:guid}/read", async (Guid notificationId, HttpContext context, IPdmRepository repository, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            var (actor, _) = CurrentUser(context.User);
            await repository.MarkUserNotificationReadAsync(notificationId, actor, timeProvider.GetUtcNow(), cancellationToken);
            return Results.NoContent();
        });

        api.MapPost("/notifications/read-all", async (HttpContext context, IPdmRepository repository, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            var (actor, _) = CurrentUser(context.User);
            await repository.MarkAllUserNotificationsReadAsync(actor, timeProvider.GetUtcNow(), cancellationToken);
            return Results.NoContent();
        });

        api.MapGet("/projects/{projectId:guid}/versions", async (Guid projectId, HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken)) return Results.Forbid();
            var folders = await repository.ListProjectFoldersAsync(projectId, actor, role, cancellationToken);
            var visibleFolderIds = folders.Where(folder => (folder.EffectiveAccess & FolderAccess.View) != 0).Select(folder => folder.Id).ToHashSet();
            var documents = (await repository.ListDocumentsAsync(projectId, cancellationToken))
                .Where(document => document.FolderId is Guid folderId && visibleFolderIds.Contains(folderId));
            var versions = new List<ProjectVersionResponse>();
            foreach (var document in documents)
            {
                var documentVersions = await repository.ListDocumentVersionsAsync(document.Id, cancellationToken);
                versions.AddRange(documentVersions.Select(version => new ProjectVersionResponse(
                    version.Id, document.Id, document.DrawingNumber, document.Name, document.FileName,
                    version.Revision, version.Status, version.CreatedBy, version.CreatedAt, version.ChangeNote)));
            }
            return Results.Ok(versions.OrderByDescending(item => item.CreatedAt));
        });

        api.MapGet("/projects/{projectId:guid}/audit", async (Guid projectId, int? take, HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return !await repository.HasProjectReadAccessAsync(projectId, actor, role, cancellationToken)
                ? Results.Forbid()
                : Results.Ok(await repository.ListProjectAuditAsync(projectId, take ?? 100, cancellationToken));
        });

        api.MapPost("/projects/{projectId:guid}/manager-notes", async (Guid projectId, AddProjectManagerNoteRequest request, HttpContext context, IPdmRepository repository, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var content = TrimToNull(request.Content, 1000, "备注");
            if (content is null) return Results.BadRequest(new { message = "请输入备注内容。" });
            var project = await repository.FindProjectAsync(projectId, cancellationToken);
            if (project is null) return Results.NotFound();
            var rootProjectId = project.RootProjectId ?? project.ParentProjectId ?? project.Id;
            var rootProject = rootProjectId == project.Id ? project : await repository.FindProjectAsync(rootProjectId, cancellationToken) ?? project;
            if (!CanMaintainProjectRecord(project, rootProject, actor, role)) return Results.Forbid();
            var note = new AuditEntry(Guid.NewGuid(), timeProvider.GetUtcNow(), actor, "project.manager-note", nameof(Project), projectId.ToString(), content);
            await repository.AppendAuditAsync(note, cancellationToken);
            return Results.Ok(note);
        });

        api.MapPost("/projects/{projectId:guid}/todos", async (Guid projectId, CreateProjectTodoRequest request, HttpContext context, IPdmRepository repository, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var content = TrimToNull(request.Content, 1000, "待办内容");
            if (content is null) return Results.BadRequest(new { message = "请输入待办内容。" });
            var project = await repository.FindProjectAsync(projectId, cancellationToken);
            if (project is null) return Results.NotFound();
            var rootProjectId = project.RootProjectId ?? project.ParentProjectId ?? project.Id;
            var rootProject = rootProjectId == project.Id ? project : await repository.FindProjectAsync(rootProjectId, cancellationToken) ?? project;
            if (!CanMaintainProjectRecord(project, rootProject, actor, role)) return Results.Forbid();

            var recipients = (request.RecipientUsernames ?? [])
                .Select(username => username?.Trim())
                .Where(username => !string.IsNullOrWhiteSpace(username))
                .Select(username => username!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (recipients.Length == 0) return Results.BadRequest(new { message = "请选择至少一名待办接收人。" });
            var users = (await repository.ListUsersAsync(cancellationToken)).ToDictionary(user => user.Username, StringComparer.OrdinalIgnoreCase);
            var unavailable = recipients.FirstOrDefault(username => !users.TryGetValue(username, out var user) || !user.IsActive);
            if (unavailable is not null) return Results.BadRequest(new { message = $"待办接收人不存在或已停用：{unavailable}" });

            var now = timeProvider.GetUtcNow();
            var sourceKey = $"project-todo:{projectId:N}:{Guid.NewGuid():N}";
            var title = $"{project.Code} 待办事项";
            var notifications = recipients.Select(recipient => new UserNotification(
                Guid.NewGuid(), recipient, "ProjectTodo", title, content, projectId, null, sourceKey, now, null, request.DueDate)).ToArray();
            await repository.CreateUserNotificationsAsync(notifications, cancellationToken);
            var recipientNames = recipients.Select(username => users[username].DisplayName).ToArray();
            var dueDateText = request.DueDate is DateOnly dueDate ? $"；截止 {dueDate:yyyy-MM-dd}" : string.Empty;
            await repository.AppendAuditAsync(new AuditEntry(Guid.NewGuid(), now, actor, "project.todo.create", nameof(Project), projectId.ToString(),
                $"创建待办并推送给 {string.Join("、", recipientNames)}{dueDateText}：{content}"), cancellationToken);
            return Results.Ok(notifications);
        });

        api.MapPost("/uploads/sessions", async (StartUploadRequest request, HttpContext context, PdmWorkflowService workflow, IFileStorage storage, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            await workflow.RequireProjectSubmissionAccessAsync(request.ProjectId, actor, role, "上传存档文件", cancellationToken);
            if (request.BatchId.HasValue || request.DocumentId.HasValue || request.CheckoutSessionId.HasValue)
            {
                if (request.BatchId is not Guid batchId || batchId == Guid.Empty || request.DocumentId is not Guid documentId || documentId == Guid.Empty
                    || request.CheckoutSessionId is not Guid sessionId || sessionId == Guid.Empty)
                    throw new PdmRuleException("整批上传需要完整的批次、图档和编辑会话编号，请刷新后重试。");
                return Results.Ok(await storage.StartArchiveUploadAsync(request.ProjectId, request.FileName, request.TotalLength, request.Sha256,
                    actor, new ArchiveUploadContext(batchId, documentId, sessionId), cancellationToken));
            }
            return Results.Ok(await storage.StartUploadAsync(request.ProjectId, request.FileName, request.TotalLength, request.Sha256, cancellationToken, actor));
        });

        api.MapGet("/uploads/sessions/{sessionId:guid}", async (Guid sessionId, HttpContext context, IPdmRepository repository, IFileStorage storage, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var session = await storage.GetUploadSessionAsync(sessionId, cancellationToken);
            session.RequireOwner(actor);
            if (!await repository.HasProjectContentReadAccessAsync(session.ProjectId, actor, role, cancellationToken)) return Results.Forbid();
            return Results.Ok(session);
        });

        api.MapPut("/uploads/sessions/{sessionId:guid}/chunks/{chunkIndex:int}", async (Guid sessionId, int chunkIndex, HttpRequest request, HttpContext context, IPdmRepository repository, PdmWorkflowService workflow, IFileStorage storage, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var session = await storage.GetUploadSessionAsync(sessionId, cancellationToken);
            session.RequireOwner(actor);
            if (session.Owner is null)
                await workflow.RequireProjectSubmissionAccessAsync(session.ProjectId, actor, role, "上传存档文件", cancellationToken);
            else if (!await repository.HasProjectContentReadAccessAsync(session.ProjectId, actor, role, cancellationToken))
                return Results.Forbid();
            return Results.Ok(await storage.WriteChunkAsync(sessionId, chunkIndex, request.Body, cancellationToken));
        });

        api.MapPost("/uploads/sessions/{sessionId:guid}/complete", async (Guid sessionId, CompleteUploadRequest request, HttpContext context, PdmWorkflowService workflow, IFileStorage storage, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var session = await storage.GetUploadSessionAsync(sessionId, cancellationToken);
            session.RequireOwner(actor);
            await workflow.RequireProjectSubmissionAccessAsync(session.ProjectId, actor, role, "完成存档上传", cancellationToken);
            return Results.Ok(await storage.CompleteUploadAsync(sessionId, request.RelativeTargetPath, cancellationToken));
        });

        api.MapGet("/projects/{projectId:guid}/storage-status", async (Guid projectId, HttpContext context, IPdmRepository repository, IFileStorage storage, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasProjectReadAccessAsync(projectId, actor, role, cancellationToken)) return Results.Forbid();
            var project = await repository.FindProjectAsync(projectId, cancellationToken);
            if (project is null)
            {
                return Results.NotFound();
            }

            var vaultAvailable = await storage.IsAvailableAsync(project.VaultLocation, cancellationToken);
            var releaseAvailable = await storage.IsAvailableAsync(project.ReleaseLocation, cancellationToken);
            return Results.Ok(new { projectId, vaultAvailable, releaseAvailable });
        });

        api.MapGet("/audit", async (int? take, HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasUserPermissionAsync(actor, role, PermissionCodes.AuditView, cancellationToken)) return Results.Forbid();
            return Results.Ok(await repository.ListAuditAsync(actor, role, take ?? 100, cancellationToken));
        });
    }

    private static async Task<IResult> HealthAsync(IOptions<PdmDatabaseOptions> options, CancellationToken cancellationToken)
    {
        var provider = options.Value.Provider;
        if (!string.Equals(provider, "MySql", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Ok(new { status = "ok", service = "upton-pdm-api", database = provider, apiPort = 5080, mysqlPort = 3308 });
        }

        try
        {
            await using var connection = new MySqlConnection(options.Value.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT DATABASE()";
            var databaseName = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
            return Results.Ok(new { status = "ok", service = "upton-pdm-api", database = "MySql", databaseName, apiPort = 5080, mysqlPort = 3308 });
        }
        catch (Exception exception)
        {
            return Results.Json(new { status = "degraded", service = "upton-pdm-api", database = "MySql", error = exception.Message, apiPort = 5080, mysqlPort = 3308 }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static (string Actor, UserRole Role) CurrentUser(ClaimsPrincipal principal)
    {
        var actor = principal.Identity?.Name ?? throw new UnauthorizedAccessException("登录信息无效。 ");
        var roleValue = principal.FindFirstValue(ClaimTypes.Role) ?? throw new UnauthorizedAccessException("角色信息无效。 ");
        return (actor, Enum.Parse<UserRole>(roleValue));
    }

    private static bool CanManagePasswordResetRequests(UserRole role) =>
        role is UserRole.Administrator or UserRole.PlatformAdministrator
        || TenantContext.Current?.HasRole(nameof(UserRole.Administrator)) == true
        || TenantContext.Current?.HasRole("platform_admin") == true
        || TenantContext.Current?.HasRole("developer") == true;

    private static bool CanMaintainProjectRecord(Project project, Project rootProject, string actor, UserRole role)
    {
        var isProjectManager = new[] { project.PrimaryProjectManager, rootProject.PrimaryProjectManager }
            .Concat(project.CollaborativeProjectManagers)
            .Concat(rootProject.CollaborativeProjectManagers)
            .Any(username => !string.IsNullOrWhiteSpace(username)
                && string.Equals(username, actor, StringComparison.OrdinalIgnoreCase));
        return isProjectManager
            || role is UserRole.Administrator or UserRole.PlatformAdministrator
            || TenantContext.Current?.HasRole(nameof(UserRole.Administrator)) == true
            || TenantContext.Current?.HasRole("platform_admin") == true
            || TenantContext.Current?.HasRole("developer") == true;
    }

    private static OrganizationDirectory ScopeOrganizationDirectory(OrganizationDirectory directory)
    {
        var tenant = TenantContext.Current;
        if (tenant is null || tenant.IsPlatformAdministrator) return directory;
        var unitIds = directory.Units.Where(item => item.OrganizationId == tenant.CompanyId).Select(item => item.Id).ToHashSet();
        return new OrganizationDirectory(
            directory.Organizations.Where(item => item.Id == tenant.CompanyId).ToArray(),
            directory.Units.Where(item => unitIds.Contains(item.Id)).ToArray(),
            directory.Memberships.Where(item => unitIds.Contains(item.UnitId)).ToArray(),
            directory.Managers.Where(item => unitIds.Contains(item.UnitId)).ToArray(),
            directory.Users.Where(item => item.CompanyId == tenant.CompanyId).ToArray());
    }

    private static ReleasePreviewItemResult MapReleasePreviewItem(ReleasePreviewItem item) =>
        new(item.ReleasePackageId, item.ReleasePackageNumber, item.DocumentId, item.DrawingNumber, item.FileName,
            item.Kind.ToString(), item.Format.ToString(), item.Succeeded, item.FileLength, item.Error);

    private static object MapManagedUser(UserAccount user, UserCompanyScope? scope = null) => new
    {
        user.Username,
        user.DisplayName,
        Role = user.EffectiveRoleCode,
        Roles = user.EffectiveRoleCodes,
        user.IsActive,
        CompanyId = scope?.PrimaryCompanyId ?? user.CompanyId,
        CrossCompanyView = scope?.CrossCompanyView ?? user.CrossCompanyView,
        AccessibleCompanyIds = scope?.AccessibleCompanyIds ?? Array.Empty<Guid>()
    };

    private static object MapRolePermissionDirectory(RolePermissionDirectory directory) => new
    {
        directory.Permissions,
        Roles = directory.Roles.Select(role => new
        {
            role.Role,
            role.Name,
            role.Description,
            BaseRole = role.BaseRole.ToString(),
            role.IsSystem,
            role.IsSystemAdministrator,
            Permissions = role.Permissions.Where(code => RolePermissionCatalog.Permissions.Any(item => item.Code == code && !item.Hidden)),
            role.UserCount
        })
    };

    private static object MapProjectPermissionDirectory(ProjectPermissionSettings settings, IReadOnlyDictionary<string, int> usage) => new
    {
        settings.Rules, settings.CustomPositions, settings.SchemaVersion,
        Positions = settings.Positions.Select(item => new { item.Key, item.Name, item.Description, item.Scope, item.IsSystem, item.IsActive, item.SortOrder, ProjectCount = usage.GetValueOrDefault(item.Key) }),
        Permissions = RolePermissionCatalog.Permissions.Where(item => item.Scope == "project" && !item.Hidden)
    };

    private static string? TrimToNull(string? value, int maxLength, string fieldName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized)) return null;
        if (normalized.Length > maxLength) throw new PdmRuleException($"{fieldName}不能超过{maxLength}个字符。");
        return normalized;
    }

    private static string SafeArchiveSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(value.Select(character => invalid.Contains(character) || character is '/' or '\\' ? '_' : character).ToArray());
        return name is "." or ".." ? "_" : name;
    }

    private static string BomExportName(BomKind kind) => kind switch
    {
        BomKind.Standard => "标准件BOM",
        BomKind.NonStandard => "非标件BOM",
        BomKind.Electrical => "电气BOM",
        _ => "BOM"
    };

    private static string DisplayUserNames(IEnumerable<string?> usernames, OrganizationDirectory directory)
    {
        var names = directory.Users.ToDictionary(item => item.Username, item => item.DisplayName, StringComparer.OrdinalIgnoreCase);
        var values = usernames
            .Where(username => !string.IsNullOrWhiteSpace(username))
            .Select(username => names.TryGetValue(username!, out var displayName) && !string.IsNullOrWhiteSpace(displayName) ? displayName : username!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return values.Length == 0 ? "—" : string.Join('、', values);
    }

    private static IReadOnlyList<BomWorkbookApprovalEntry> ApprovalPeople(ReleasePackage? package, OrganizationDirectory directory)
    {
        if (package is null || package.ApprovalTasks.Count == 0) return [];
        return package.ApprovalTasks
            .OrderBy(item => item.StepOrder)
            .Select(task => new BomWorkbookApprovalEntry(
                BomExportApprovalLabel(task),
                DisplayUserNames([task.DecisionBy ?? task.Assignee], directory)))
            .ToArray();
    }

    private static string BomExportApprovalLabel(ApprovalTask task) => task.Stage switch
    {
        ApprovalStage.MechanicalEngineer or ApprovalStage.HardwareEngineer => "工程师",
        ApprovalStage.MainDesigner => "主设",
        ApprovalStage.MechanicalSupervisor => "机械主管",
        ApprovalStage.HardwareSupervisor => "硬件主管",
        ApprovalStage.StandardizationSupervisor => "标准化主管",
        _ => string.IsNullOrWhiteSpace(task.StepName) ? task.Stage.ToString() : task.StepName
    };

}
