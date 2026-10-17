using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class BomApprovalSettingsApiTests
{
    [Fact]
    public async Task SystemSettings_SavesSeparateBomTemplatesAndReturnsThemAfterReload()
    {
        await using var factory = new PdmApiFactory();
        var client = factory.CreateClient();
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("upton-pdm", "upton-pdm-clients",
            [new Claim(ClaimTypes.Name, "admin"), new Claim(ClaimTypes.Role, "Administrator")], expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256)));
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var settings = (await client.GetFromJsonAsync<PdmSystemSettings>("/api/system-settings"))!;
        Assert.NotNull(settings.ApprovalWorkflows.Standard);
        Assert.NotNull(settings.ApprovalWorkflows.NonStandard);
        var labelsResponse = await client.PutAsJsonAsync("/api/system-settings", settings with {
            ReleaseChangeReasonLabels = new Dictionary<string, string> { ["物料问题 / 交期不满足"] = "  库存交期不足  ", ["正式补充"] = "计划内正式补充" }
        });
        labelsResponse.EnsureSuccessStatusCode();
        settings = (await client.GetFromJsonAsync<PdmSystemSettings>("/api/system-settings"))!;
        Assert.Equal("库存交期不足", settings.ReleaseChangeReasonLabels["物料问题 / 交期不满足"]);
        Assert.Equal("计划内正式补充", settings.ReleaseChangeReasonLabels["正式补充"]);
        Assert.Equal(PdmSystemSettings.DefaultReleaseChangeReasonTypes, settings.ReleaseChangeReasonTypes);
        var customKey = "物料问题 / custom-inventory";
        var customTypes = settings.ReleaseChangeReasonTypes.Where(key => key != "物料问题 / 交期不满足").Append(customKey).ToArray();
        var customResponse = await client.PutAsJsonAsync("/api/system-settings", settings with {
            ReleaseChangeReasonTypes = customTypes,
            ReleaseChangeReasonLabels = new Dictionary<string, string> { [customKey] = "临时库存替代" }
        });
        customResponse.EnsureSuccessStatusCode();
        var customized = (await client.GetFromJsonAsync<PdmSystemSettings>("/api/system-settings"))!;
        Assert.DoesNotContain("物料问题 / 交期不满足", customized.ReleaseChangeReasonTypes);
        Assert.Contains(customKey, customized.ReleaseChangeReasonTypes);
        Assert.Equal("临时库存替代", customized.ReleaseChangeReasonLabels[customKey]);
        var original = settings.ApprovalWorkflows;
        var response = await client.PutAsJsonAsync("/api/system-settings", settings with { ApprovalWorkflows = original with
        {
            MaterialCodeApplication = MaterialCodeWorkflowDefaults.Template() with { EmergencyRoleCodes = ["Administrator"] },
            ValidationPlan = original.ValidationPlan! with { EmergencyRoleCodes = ["BusinessUnitManager"], Steps = [original.ValidationPlan.Steps[0],
                new(ApprovalStage.HardwareSupervisor, "验证计划上级批准", ApprovalAssigneeSource.ParentUnitManager)] },
            Standard = original.Standard! with { EmergencyRoleCodes = [], Steps = [original.Standard.Steps[0],
                new(ApprovalStage.MainDesigner, "标准件上级批准", ApprovalAssigneeSource.ParentUnitManager)] },
            NonStandard = original.NonStandard! with { Steps = [original.NonStandard.Steps[0],
                new(ApprovalStage.MainDesigner, "非标主设审核", ApprovalAssigneeSource.ProjectDesignLead),
                new(ApprovalStage.MechanicalSupervisor, "非标主管批准", ApprovalAssigneeSource.PrimaryUnitManager)] }
        } });
        response.EnsureSuccessStatusCode();
        var saved = (await client.GetFromJsonAsync<PdmSystemSettings>("/api/system-settings"))!.ApprovalWorkflows;
        Assert.Equal(["Administrator"], saved.MaterialCodeApplication!.EmergencyRoleCodes);
        Assert.Equal(ApprovalAssigneeSource.Role, saved.MaterialCodeApplication.Steps[1].AssigneeSource);
        Assert.Equal(["ProcessReviewer", "Approver"], saved.MaterialCodeApplication.Steps[1].RoleCodes);
        Assert.Equal(["BusinessUnitManager"], saved.ValidationPlan!.EmergencyRoleCodes);
        Assert.Empty(saved.Standard!.EmergencyRoleCodes!);
        Assert.Equal(2, saved.ValidationPlan!.Steps.Count);
        Assert.Equal(ApprovalAssigneeSource.ParentUnitManager, saved.ValidationPlan.Steps[1].AssigneeSource);
        Assert.Equal(original.ValidationPlan!.Version + 1, saved.ValidationPlan.Version);
        Assert.Equal(2, saved.Standard!.Steps.Count);
        Assert.Equal("标准件上级批准", saved.Standard.Steps[1].Name);
        Assert.Equal(ApprovalAssigneeSource.ParentUnitManager, saved.Standard.Steps[1].AssigneeSource);
        Assert.Equal(3, saved.NonStandard!.Steps.Count);
        Assert.Equal(original.Standard.Version + 1, saved.Standard.Version);
        Assert.Equal(original.Electrical.Version, saved.Electrical.Version);
        Assert.Equal(original.Electrical.Steps, saved.Electrical.Steps);

        var repository = factory.Services.GetRequiredService<IPdmRepository>();
        await repository.CreateUserAsync(new UserAccount(Guid.NewGuid(), "fixed-reviewer", "指定审核员", "unused", UserRole.Approver, true), default);
        var unit = await repository.SaveOrganizationUnitAsync(new(null, Guid.Parse("70000000-0000-0000-0000-000000000001"), null,
            "FIXED-API", "指定审批部门", OrganizationUnitKind.Department, true, 0), default);
        await repository.SetOrganizationUnitManagersAsync(unit.Id, "fixed-reviewer", [], default);
        var current = (await client.GetFromJsonAsync<PdmSystemSettings>("/api/system-settings"))!;
        response = await client.PutAsJsonAsync("/api/system-settings", current with { ApprovalWorkflows = saved with {
            Standard = saved.Standard with { Steps = [saved.Standard.Steps[0],
                new(ApprovalStage.MainDesigner, "指定人员审核", ApprovalAssigneeSource.FixedUser, "fixed-reviewer")] },
            NonStandard = saved.NonStandard with { Steps = [saved.NonStandard.Steps[0],
                new(ApprovalStage.MainDesigner, "指定部门审核", ApprovalAssigneeSource.FixedUnitManager, FixedUnitId: unit.Id)] },
            ValidationPlan = saved.ValidationPlan with { Steps = [saved.ValidationPlan.Steps[0],
                new(ApprovalStage.HardwareSupervisor, "验证计划指定人员", ApprovalAssigneeSource.FixedUser, "fixed-reviewer"),
                new(ApprovalStage.MainDesigner, "验证计划指定部门", ApprovalAssigneeSource.FixedUnitManager, FixedUnitId: unit.Id)] },
            Electrical = saved.Electrical with { Steps = [saved.Electrical.Steps[0],
                new(ApprovalStage.HardwareSupervisor, "电气指定审核", ApprovalAssigneeSource.FixedUser, "fixed-reviewer")] }
        } });
        response.EnsureSuccessStatusCode();
        var reloaded = (await client.GetFromJsonAsync<PdmSystemSettings>("/api/system-settings"))!.ApprovalWorkflows;
        Assert.Equal(3, reloaded.ValidationPlan!.Steps.Count);
        Assert.Equal("fixed-reviewer", reloaded.ValidationPlan.Steps[1].FixedAssignee);
        Assert.Equal(unit.Id, reloaded.ValidationPlan.Steps[2].FixedUnitId);
        Assert.Equal("fixed-reviewer", reloaded.Standard!.Steps[1].FixedAssignee);
        Assert.Equal(ApprovalAssigneeSource.FixedUnitManager, reloaded.NonStandard!.Steps[1].AssigneeSource);
        Assert.Equal(unit.Id, reloaded.NonStandard.Steps[1].FixedUnitId);
        Assert.Equal(ApprovalAssigneeSource.FixedUser, reloaded.Electrical.Steps[1].AssigneeSource);
        Assert.Equal("fixed-reviewer", reloaded.Electrical.Steps[1].FixedAssignee);
    }
}
