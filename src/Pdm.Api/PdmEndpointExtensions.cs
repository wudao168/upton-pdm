using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
            IOptions<AuthenticationOptions> authenticationOptions,
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
            return Results.Ok(new LoginResponse(
                tokenIssuer.Issue(account, lifetime),
                expiresAt,
                persistentSessions.Issue(account),
                account.Username,
                account.DisplayName,
                account.EffectiveRoleCode,
                (await repository.GetUserPermissionsAsync(account.Username, account.Role, cancellationToken)).Order().ToArray()));
        }).AllowAnonymous();

        app.MapPost("/api/auth/resume", async (
            ResumeSessionRequest request,
            IPdmRepository repository,
            ITokenIssuer tokenIssuer,
            IPersistentSessionTokenService persistentSessions,
            IOptions<AuthenticationOptions> authenticationOptions,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            if (!persistentSessions.TryRead(request.ResumeToken, out var ticket)) return Results.Unauthorized();
            var account = await repository.FindUserAsync(ticket.Username, cancellationToken);
            if (account is null || !account.IsActive || account.TokenVersion != ticket.TokenVersion) return Results.Unauthorized();

            var lifetime = TimeSpan.FromHours(authenticationOptions.Value.TokenLifetimeHours);
            var expiresAt = timeProvider.GetUtcNow().Add(lifetime);
            return Results.Ok(new LoginResponse(
                tokenIssuer.Issue(account, lifetime),
                expiresAt,
                persistentSessions.Issue(account),
                account.Username,
                account.DisplayName,
                account.EffectiveRoleCode,
                (await repository.GetUserPermissionsAsync(account.Username, account.Role, cancellationToken)).Order().ToArray()));
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
            var profile = await repository.UpdateUserProfileAsync(actor, nickname, gender, landline, mobilePhone, email, cancellationToken);
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
            return Results.Ok(new LoginResponse(
                tokenIssuer.Issue(updatedAccount, lifetime),
                timeProvider.GetUtcNow().Add(lifetime),
                persistentSessions.Issue(updatedAccount),
                account.Username,
                account.DisplayName,
                updatedAccount.EffectiveRoleCode,
                (await repository.GetUserPermissionsAsync(updatedAccount.Username, updatedAccount.Role, cancellationToken)).Order().ToArray()));
        });

        api.MapGet("/password-reset-requests", async (HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return role != UserRole.Administrator
                ? Results.Forbid()
                : Results.Ok(await repository.ListPasswordResetTasksAsync(cancellationToken));
        });

        api.MapPut("/password-reset-requests/{taskId:guid}/reset", async (Guid taskId, HttpContext context, IPdmRepository repository, IPasswordService passwords, TimeProvider timeProvider, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (role != UserRole.Administrator) return Results.Forbid();
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
            Results.Ok(await repository.GetProjectNumberingOptionsAsync(cancellationToken)));

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
            var users = await repository.ListUsersAsync(cancellationToken);
            return Results.Ok(users.Select(user => new { user.Username, user.DisplayName, Role = user.EffectiveRoleCode, user.IsActive }));
        });

        api.MapPost("/users", async (CreateManagedUserRequest request, HttpContext context, PdmWorkflowService workflow, IPasswordService passwords, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8) throw new PdmRuleException("初始密码至少需要8位。");
            var (actor, role) = CurrentUser(context.User);
            var saved = await workflow.CreateManagedUserAsync(new(request.Username, request.DisplayName, passwords.Hash(request.Password), request.Role, request.IsActive), actor, role, cancellationToken);
            return Results.Created($"/api/users/{Uri.EscapeDataString(saved.Username)}", new { saved.Username, saved.DisplayName, Role = saved.EffectiveRoleCode, saved.IsActive });
        });

        api.MapPut("/users/{username}", async (string username, UpdateManagedUserRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var saved = await workflow.UpdateManagedUserAsync(new(username, request.DisplayName, request.Role, request.IsActive), actor, role, cancellationToken);
            return Results.Ok(new { saved.Username, saved.DisplayName, Role = saved.EffectiveRoleCode, saved.IsActive });
        });

        api.MapPut("/users/{username}/reset-password", async (string username, HttpContext context, PdmWorkflowService workflow, IPasswordService passwords, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var saved = await workflow.ResetManagedUserPasswordAsync(username, passwords.Hash("11111111"), actor, role, cancellationToken);
            return Results.Ok(new { saved.Username, saved.DisplayName, Role = saved.EffectiveRoleCode, saved.IsActive });
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
            return Results.Ok(MapRolePermissionDirectory(await workflow.UpdateRolePermissionsAsync(targetRole, request.Permissions, actor, role, cancellationToken)));
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

        api.MapGet("/organization-directory", async (IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var directory = await repository.GetOrganizationDirectoryAsync(cancellationToken);
            return Results.Ok(new
            {
                directory.Organizations,
                Units = directory.Units.Select(unit => new { unit.Id, unit.OrganizationId, unit.ParentUnitId, unit.Code, unit.Name, Kind = unit.Kind.ToString(), unit.IsActive, unit.SortOrder }),
                directory.Memberships,
                directory.Managers,
                Users = directory.Users.Select(user => new { user.Username, user.DisplayName, Role = user.EffectiveRoleCode, user.IsActive })
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
            var saved = await workflow.SaveOrganizationUnitAsync(new(null, request.OrganizationId, request.ParentUnitId, request.Code, request.Name, request.Kind, request.IsActive, request.SortOrder), actor, role, cancellationToken);
            return Results.Created($"/api/organization-units/{saved.Id}", saved);
        });

        api.MapPut("/organization-units/{unitId:guid}", async (Guid unitId, SaveOrganizationUnitRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.SaveOrganizationUnitAsync(new(unitId, request.OrganizationId, request.ParentUnitId, request.Code, request.Name, request.Kind, request.IsActive, request.SortOrder), actor, role, cancellationToken));
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
                ValidationRules = validationRules
            };
            return Results.Ok(await workflow.UpdateSystemSettingsAsync(settings, actor, role, cancellationToken));
        });

        api.MapGet("/bom-validation-rules", async (IPdmRepository repository, CancellationToken cancellationToken) =>
            Results.Ok((await repository.GetSystemSettingsAsync(cancellationToken)).ValidationRules));

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
                    string.Empty),
                actor,
                role,
                cancellationToken);
            return Results.Created($"/api/projects/{project.Id}", project);
        });

        api.MapPost("/projects/{projectId:guid}/children", async (Guid projectId, CreateSubprojectRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var project = await workflow.CreateSubprojectAsync(
                new CreateSubprojectCommand(projectId, request.Name, request.ProjectAlias, request.Quantity),
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
            if (!await repository.HasProjectReadAccessAsync(projectId, actor, role, cancellationToken)) return Results.Forbid();
            var project = await repository.FindProjectAsync(projectId, cancellationToken);
            return project is null ? Results.NotFound() : Results.Ok(project with { CanReadContent = await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken) });
        });

        api.MapPut("/projects/{projectId:guid}/execution-unit", async (Guid projectId, UpdateProjectExecutionUnitRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.SetProjectExecutionUnitAsync(projectId, request.ExecutionUnitId, actor, role, cancellationToken));
        });

        api.MapPut("/projects/{projectId:guid}/staffing", async (Guid projectId, UpdateMainProjectStaffingRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.SetMainProjectStaffingAsync(projectId, new(request.PrimaryProjectManager, request.CollaborativeProjectManagers, request.DesignLead), actor, role, cancellationToken));
        });

        api.MapPut("/projects/{projectId:guid}/designers", async (Guid projectId, UpdateChildProjectDesignersRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.SetChildProjectDesignersAsync(projectId, request.Designers, actor, role, cancellationToken));
        });

        api.MapGet("/projects/{projectId:guid}/documents", async (Guid projectId, HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken)) return Results.Forbid();
            var folders = await repository.ListProjectFoldersAsync(projectId, actor, role, cancellationToken);
            var visibleFolderIds = folders.Where(folder => (folder.EffectiveAccess & FolderAccess.View) != 0).Select(folder => folder.Id).ToHashSet();
            var documents = await repository.ListDocumentsAsync(projectId, cancellationToken);
            return Results.Ok(documents.Where(document => document.FolderId is Guid folderId && visibleFolderIds.Contains(folderId)));
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
            foreach (var document in documents)
                if (document.FolderId is Guid folderId && visibleFolderIds.Contains(folderId)
                    && await repository.HasProjectReadAccessAsync(document.ProjectId, actor, role, cancellationToken)) visible.Add(document);
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

        api.MapGet("/documents/{documentId:guid}/versions", async (Guid documentId, HttpContext context, IPdmRepository repository, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            await workflow.AuditVersionReadAsync(documentId, Guid.Empty, actor, role, "document.version.list", cancellationToken);
            var versions = await repository.ListDocumentVersionsAsync(documentId, cancellationToken);
            return Results.Ok(versions);
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
            await storage.VerifyStoredFileAsync(project, new StoredFile(version.StorageRelativePath, version.FileLength, version.Sha256, version.CreatedAt), cancellationToken);
            var path = StorageLocationPolicy.ResolveUnder(project.VaultLocation, version.StorageRelativePath);
            var stream = await storage.OpenReadAsync(path, cancellationToken);
            return Results.File(stream, "application/octet-stream", download ? document.FileName : null, enableRangeProcessing: true);
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
                cancellationToken));
        });

        api.MapGet("/documents/{documentId:guid}/versions/compare", async (Guid documentId, Guid left, Guid right, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.CompareVersionsAsync(documentId, left, right, actor, role, cancellationToken));
        });

        api.MapPost("/documents/{documentId:guid}/versions/{versionId:guid}/restore", async (Guid documentId, Guid versionId, RestoreVersionRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var result = await workflow.RestoreVersionAsync(documentId, versionId, actor, role, request.ChangeNote, cancellationToken);
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
            return tree is null ? Results.NotFound() : Results.Ok(tree);
        });

        api.MapGet("/projects/{projectId:guid}/boms/{kind}", async (Guid projectId, string kind, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!Enum.TryParse<BomKind>(kind, true, out var bomKind))
            {
                return Results.BadRequest(new { message = "BOM类型必须是Standard、NonStandard、Unclassified或Electrical。" });
            }

            return Results.Ok(await workflow.GetBomAsync(projectId, bomKind, actor, role, cancellationToken));
        });

        api.MapGet("/projects/{projectId:guid}/bom-source-data", async (Guid projectId, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.GetBomSourceDataAsync(projectId, actor, role, cancellationToken));
        });

        api.MapPut("/projects/{projectId:guid}/boms/{kind}", async (Guid projectId, string kind, ReplaceBomRequest request, HttpContext context, IPdmRepository repository, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse<BomKind>(kind, true, out var bomKind) || bomKind is not (BomKind.Standard or BomKind.NonStandard or BomKind.Electrical)) return Results.BadRequest(new { message = "BOM类型必须是Standard、NonStandard或Electrical。" });
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken)) return Results.Forbid();
            return Results.Ok(await workflow.ReplaceBomAsync(projectId, bomKind, request.Items, actor, role, cancellationToken));
        });

        api.MapPost("/projects/{projectId:guid}/boms/{kind}/import", async (Guid projectId, string kind, IFormFile file, HttpContext context, IPdmRepository repository, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse<BomKind>(kind, true, out var bomKind) || bomKind is not (BomKind.Standard or BomKind.NonStandard or BomKind.Electrical)) return Results.BadRequest(new { message = "BOM类型必须是Standard、NonStandard或Electrical。" });
            if (!string.Equals(Path.GetExtension(file.FileName), ".xlsx", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new { message = "只支持标准XLSX格式的BOM文件。" });
            await using var input = file.OpenReadStream();
            var items = BomWorkbook.Read(input);
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken)) return Results.Forbid();
            return Results.Ok(await workflow.ReplaceBomAsync(projectId, bomKind, items, actor, role, cancellationToken));
        }).DisableAntiforgery();

        api.MapGet("/projects/{projectId:guid}/boms/{kind}/export", async (Guid projectId, string kind, HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasProjectContentReadAccessAsync(projectId, actor, role, cancellationToken)) return Results.Forbid();
            if (!Enum.TryParse<BomKind>(kind, true, out var bomKind) || bomKind is not (BomKind.Standard or BomKind.NonStandard or BomKind.Electrical)) return Results.BadRequest(new { message = "BOM类型必须是Standard、NonStandard或Electrical。" });
            var items = (await repository.GetBomAsync(projectId, bomKind, cancellationToken)).Where(item => !item.IsManuallyExcluded).ToArray();
            return Results.File(BomWorkbook.Write(items), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"{kind.ToLowerInvariant()}-bom.xlsx");
        });

        api.MapPost("/projects/{projectId:guid}/boms/generate", async (Guid projectId, bool? apply, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.GenerateMechanicalBomAsync(projectId, apply ?? true, actor, role, cancellationToken));
        });

        api.MapPost("/projects/{projectId:guid}/boms/items/{itemId:guid}/resolve", async (Guid projectId, Guid itemId, ResolveBomItemRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.ResolveBomItemAsync(projectId, itemId, new ResolveBomItemCommand(request.Action, request.TargetKind), actor, role, cancellationToken));
        });

        api.MapPatch("/projects/{projectId:guid}/boms/items/batch", async (Guid projectId, BatchUpdateBomItemsRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var command = new BatchUpdateBomItemsCommand(
                request.ItemIds, request.Fields, request.TargetKind, request.Unit, request.DrawingNumber, request.Name,
                request.Specification, request.Remark, request.Brand, request.Material, request.SurfaceTreatment,
                request.Weight, request.Quantity, request.Revision, request.Complete);
            return Results.Ok(await workflow.BatchUpdateBomItemsAsync(projectId, command, actor, role, cancellationToken));
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

        api.MapPost("/projects/{projectId:guid}/boms/items/restore-source", async (Guid projectId, RestoreBomItemsFromSourceRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.RestoreBomItemsFromSourceAsync(projectId, new RestoreBomItemsFromSourceCommand(request.ItemIds), actor, role, cancellationToken));
        });

        api.MapGet("/projects/{projectId:guid}/cad-property-writebacks", async (Guid projectId, bool? activeOnly, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.ListCadPropertyWritebacksAsync(projectId, activeOnly ?? false, actor, role, cancellationToken));
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

        api.MapGet("/projects/{projectId:guid}/bom-versions", async (Guid projectId, string? kind, HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
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
            return Results.Ok(await repository.ListBomVersionsAsync(projectId, bomKind, cancellationToken));
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
            return Results.Ok(await workflow.CheckoutAsync(documentId, actor, role, request.SessionId, request.MachineName, cancellationToken));
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
                request.FileName);
            return Results.Ok(new
            {
                document = result.Document,
                version = result.Version,
                versionCreated = result.VersionCreated,
                bomUpdate = result.BomUpdate,
                bomUpdateError = result.BomUpdateError
            });
        });

        api.MapPost("/release-packages", async (CreateReleasePackageRequest request, HttpContext context, IPdmRepository repository, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            if (!await repository.HasProjectContentReadAccessAsync(request.ProjectId, actor, role, cancellationToken)) return Results.Forbid();
            return Results.Ok(await workflow.CreateReleasePackageAsync(
                request.ProjectId,
                request.ReferenceSnapshotId,
                request.Number,
                request.ChangeNumber ?? request.Number,
                request.ChangeReason ?? "兼容既有发布流程创建的设变",
                request.EffectiveSerialFrom ?? "未指定",
                request.EffectiveSerialTo,
                request.ProcessReviewer,
                request.Approver,
                actor,
                role,
                cancellationToken));
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

        api.MapPost("/approval-tasks/{taskId:guid}/decision", async (Guid taskId, ApprovalRequest request, HttpContext context, PdmWorkflowService workflow, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return Results.Ok(await workflow.DecideAsync(taskId, actor, role, request.Decision, request.Comment, cancellationToken));
        });

        api.MapGet("/approval-tasks/mine", async (HttpContext context, IPdmRepository repository, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            var projects = await repository.ListProjectsForUserAsync(actor, role, cancellationToken);
            var results = new List<MyApprovalTaskResponse>();
            foreach (var project in projects)
            {
                var packages = await repository.ListReleasePackagesAsync(project.Id, cancellationToken);
                foreach (var package in packages)
                {
                    var expectedStage = package.State switch
                    {
                        ReleasePackageState.ProcessReview => ApprovalStage.ProcessReview,
                        ReleasePackageState.Approval => ApprovalStage.Approval,
                        _ => (ApprovalStage?)null
                    };
                    if (expectedStage is null) continue;
                    results.AddRange(package.ApprovalTasks
                        .Where(task => task.Decision is null
                            && task.Stage == expectedStage
                            && string.Equals(task.Assignee, actor, StringComparison.OrdinalIgnoreCase))
                        .Select(task => new MyApprovalTaskResponse(
                            task.Id, project.Id, project.Code, project.Name, package.Id, package.Number,
                            task.Stage, package.State, package.CreatedAt)));
                }
            }
            return Results.Ok(results.OrderBy(item => item.CreatedAt));
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

        api.MapPost("/uploads/sessions", async (StartUploadRequest request, HttpContext context, IPdmRepository repository, IFileStorage storage, CancellationToken cancellationToken) =>
        {
            var (actor, role) = CurrentUser(context.User);
            return !await repository.HasProjectContentReadAccessAsync(request.ProjectId, actor, role, cancellationToken)
                ? Results.Forbid()
                : Results.Ok(await storage.StartUploadAsync(request.ProjectId, request.FileName, request.TotalLength, request.Sha256, cancellationToken));
        });

        api.MapGet("/uploads/sessions/{sessionId:guid}", async (Guid sessionId, IFileStorage storage, CancellationToken cancellationToken) =>
            Results.Ok(await storage.GetUploadSessionAsync(sessionId, cancellationToken)));

        api.MapPut("/uploads/sessions/{sessionId:guid}/chunks/{chunkIndex:int}", async (Guid sessionId, int chunkIndex, HttpRequest request, IFileStorage storage, CancellationToken cancellationToken) =>
            Results.Ok(await storage.WriteChunkAsync(sessionId, chunkIndex, request.Body, cancellationToken)));

        api.MapPost("/uploads/sessions/{sessionId:guid}/complete", async (Guid sessionId, CompleteUploadRequest request, IFileStorage storage, CancellationToken cancellationToken) =>
            Results.Ok(await storage.CompleteUploadAsync(sessionId, request.RelativeTargetPath, cancellationToken)));

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
            role.Permissions,
            role.UserCount
        })
    };

    private static string? TrimToNull(string? value, int maxLength, string fieldName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized)) return null;
        if (normalized.Length > maxLength) throw new PdmRuleException($"{fieldName}不能超过{maxLength}个字符。");
        return normalized;
    }
}
