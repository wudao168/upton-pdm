namespace Upton.Pdm.Domain;

public static class ProjectNumberPolicy
{
    public static string BusinessCode(Project project)
    {
        var code = project.Code.Trim();
        if (!project.ParentProjectId.HasValue)
            return code.EndsWith("-0", StringComparison.OrdinalIgnoreCase) ? code : $"{code}-0";

        if (project.ChildSequence is not int sequence)
            return code;

        var suffix = $"-{sequence}";
        return code.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? code : $"{code}{suffix}";
    }
}
