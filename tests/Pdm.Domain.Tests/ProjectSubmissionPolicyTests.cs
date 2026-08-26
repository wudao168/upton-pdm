using Upton.Pdm.Domain;

namespace Upton.Pdm.Tests;

public sealed class ProjectSubmissionPolicyTests
{
    [Fact]
    public void AssignedEngineer_CanSubmitOnlyToAssignedChildProject()
    {
        var rootId = Guid.NewGuid();
        var assigned = Project(rootId, "P700001-1") with { Designers = ["engineer"] };
        var unassigned = Project(rootId, "P700001-2") with { Designers = ["other"] };
        var root = Project(null, "P700001");

        Assert.True(ProjectSubmissionPolicy.CanSubmitArchive(assigned, "engineer", false));
        Assert.False(ProjectSubmissionPolicy.CanSubmitArchive(unassigned, "engineer", false));
        Assert.False(ProjectSubmissionPolicy.CanSubmitArchive(root, "engineer", false));
    }

    [Fact]
    public void ProjectManagementAndAdministrator_RetainSubmissionAccess()
    {
        var root = Project(null, "P700001") with
        {
            PrimaryProjectManager = "manager",
            DesignLeads = ["lead"]
        };

        Assert.True(ProjectSubmissionPolicy.CanSubmitArchive(root, "manager", false));
        Assert.True(ProjectSubmissionPolicy.CanSubmitArchive(root, "lead", false));
        Assert.True(ProjectSubmissionPolicy.CanSubmitArchive(root, "administrator", true));
    }

    [Fact]
    public void DesignLead_NeedsDesignerAssignmentForChildProject()
    {
        var rootId = Guid.NewGuid();
        var root = Project(null, "P700001") with { DesignLeads = ["lead"] };
        var unassignedChild = Project(rootId, "P700001-1") with { DesignLeads = ["lead"] };
        var assignedChild = unassignedChild with { Designers = ["lead"] };

        Assert.True(ProjectSubmissionPolicy.CanSubmitArchive(root, "lead", false));
        Assert.False(ProjectSubmissionPolicy.CanSubmitArchive(unassignedChild, "lead", false));
        Assert.True(ProjectSubmissionPolicy.CanSubmitArchive(assignedChild, "lead", false));
    }

    private static Project Project(Guid? parentProjectId, string code) => new(
        Guid.NewGuid(),
        code,
        code,
        "owner",
        @"D:\PDM\Vault",
        @"D:\PDM\Release",
        true)
    {
        ParentProjectId = parentProjectId,
        RootProjectId = parentProjectId
    };
}
