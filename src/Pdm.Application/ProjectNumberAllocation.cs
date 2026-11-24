namespace Upton.Pdm.Application;

public static class ProjectNumberAllocation
{
    public static string FormatMainCode(string projectTypeCode, string companyCode, int sequence) =>
        $"{projectTypeCode}{companyCode}{sequence:D5}";

    public static int SelectMainSequence(string projectTypeCode, int current, int? released)
    {
        if (released is int sequence) return sequence;
        if (current >= 99999) throw new PdmRuleException($"该组织的{projectTypeCode}类5位项目流水号已用尽。");
        return current + 1;
    }
}
