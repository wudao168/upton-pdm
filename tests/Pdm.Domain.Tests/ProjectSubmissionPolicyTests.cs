using Upton.Pdm.Domain;

namespace Upton.Pdm.Tests;

public sealed class ProjectSubmissionPolicyTests
{
    [Theory]
    [InlineData("engineer", true)]
    [InlineData(" ENGINEER ", true)]
    [InlineData("other", false)]
    [InlineData("owner", false)]
    [InlineData("", false)]
    public void MainProject_RequiresExplicitEngineerAssignment(string actor, bool expected)
    {
        var root = Project(null, "P700005") with
        {
            PrimaryProjectManager = "manager",
            DesignLeads = ["lead"],
            Designers = ["engineer"]
        };

        Assert.Equal(expected, ProjectSubmissionPolicy.CanSubmitArchive(root, actor, false));
        Assert.False(ProjectSubmissionPolicy.CanSubmitArchive(
            root with { Designers = [] }, "engineer", false));
        Assert.False(ProjectSubmissionPolicy.CanSubmitArchive(
            Project(root.Id, "P700005-1"), "engineer", false));
    }

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
            CollaborativeProjectManagers = ["collaborator"],
            DesignLeads = ["lead"]
        };

        Assert.True(ProjectSubmissionPolicy.CanSubmitArchive(root, "manager", false));
        Assert.True(ProjectSubmissionPolicy.CanSubmitArchive(root, "collaborator", false));
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
