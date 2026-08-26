namespace Upton.Pdm.Domain;

public static class ProjectSubmissionPolicy
{
    public static bool CanSubmitArchive(Project project, string actor, bool isAdministrator)
    {
        if (isAdministrator) return true;

        var username = actor?.Trim() ?? string.Empty;
        if (username.Length == 0) return false;

        var managesProject = string.Equals(project.PrimaryProjectManager, username, StringComparison.OrdinalIgnoreCase)
            || project.CollaborativeProjectManagers.Contains(username, StringComparer.OrdinalIgnoreCase);
        if (managesProject) return true;

        if (project.ParentProjectId is null)
        {
            return project.DesignLeads.Contains(username, StringComparer.OrdinalIgnoreCase)
                || string.Equals(project.DesignLead, username, StringComparison.OrdinalIgnoreCase);
        }

        return project.Designers.Contains(username, StringComparer.OrdinalIgnoreCase);
    }
}
