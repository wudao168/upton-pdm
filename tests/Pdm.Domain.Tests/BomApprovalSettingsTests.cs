using System.Text.Json;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Domain.Tests;

public sealed class BomApprovalSettingsTests
{
    [Fact]
    public void LegacyJson_InheritsMechanicalStepsIntoSeparateBomTemplates()
    {
        var legacy = ReleaseApprovalSettings.Default with { Mechanical = ReleaseApprovalSettings.Default.Mechanical with
        {
            Version = 7,
            Steps = [
                new(ApprovalStage.MechanicalEngineer, "原自检", ApprovalAssigneeSource.Submitter),
                new(ApprovalStage.MainDesigner, "原主设审核", ApprovalAssigneeSource.ProjectDesignLead),
                new(ApprovalStage.MechanicalSupervisor, "原主管批准", ApprovalAssigneeSource.FixedUser, "old-account")]
        } };
        var json = JsonSerializer.Serialize(legacy);
        var upgraded = ReleaseApprovalSettings.UseOrganizationHierarchy(JsonSerializer.Deserialize<ReleaseApprovalSettings>(json));
        Assert.Equal("standard-release", upgraded.Standard!.Code);
        Assert.Equal("nonstandard-release", upgraded.NonStandard!.Code);
        Assert.Equal(8, upgraded.Standard.Version);
        Assert.Equal(upgraded.Mechanical.Steps, upgraded.Standard.Steps);
        Assert.Equal(upgraded.Mechanical.Steps, upgraded.NonStandard.Steps);
        Assert.Equal(ApprovalAssigneeSource.PrimaryUnitManager, upgraded.Standard.Steps[2].AssigneeSource);
        Assert.Null(upgraded.Standard.Steps[2].FixedAssignee);
        Assert.Equal(upgraded, ReleaseApprovalSettings.UseOrganizationHierarchy(upgraded));
    }

    [Fact]
    public void ConfiguredAssigneeSources_SurviveJsonRoundTripAndRepositoryUpgrade()
    {
        var settings = ReleaseApprovalSettings.UseOrganizationHierarchy(null);
        settings = settings with {
            Standard = settings.Standard! with { Version = 5, Steps = [
                settings.Standard.Steps[0], new(ApprovalStage.MainDesigner, "部门审核", ApprovalAssigneeSource.PrimaryUnitManager)] },
            Electrical = settings.Electrical with { Version = 3, Steps = [
                settings.Electrical.Steps[0], new(ApprovalStage.HardwareSupervisor, "指定人员批准", ApprovalAssigneeSource.FixedUser, "approver")] },
            ValidationPlan = settings.ValidationPlan! with { Version = 6, Steps = [settings.ValidationPlan.Steps[0],
                new(ApprovalStage.MainDesigner, "指定人员审核", ApprovalAssigneeSource.FixedUser, "reviewer"),
                new(ApprovalStage.MechanicalSupervisor, "上级批准", ApprovalAssigneeSource.ParentUnitManager)] },
            NonStandard = settings.NonStandard! with { Steps = [settings.NonStandard.Steps[0],
                new(ApprovalStage.MainDesigner, "指定部门批准", ApprovalAssigneeSource.FixedUnitManager, FixedUnitId: Guid.Parse("30000000-0000-0000-0000-000000000001"))] }
        };
        var restored = ReleaseApprovalSettings.UseOrganizationHierarchy(JsonSerializer.Deserialize<ReleaseApprovalSettings>(JsonSerializer.Serialize(settings)));
        Assert.Equal(5, restored.Standard!.Version);
        Assert.Equal(3, restored.Electrical.Version);
        Assert.Equal(settings.Standard.Steps, restored.Standard.Steps);
        Assert.Equal(settings.Electrical.Steps, restored.Electrical.Steps);
        Assert.Equal(settings.NonStandard!.Steps, restored.NonStandard!.Steps);
        Assert.Equal(6, restored.ValidationPlan!.Version);
        Assert.Equal(settings.ValidationPlan!.Steps, restored.ValidationPlan.Steps);
    }
}
