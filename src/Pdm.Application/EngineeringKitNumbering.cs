namespace Upton.Pdm.Application;

public static class EngineeringKitNumbering
{
    public static string CategoryCode(long number) => $"UTJ-{number:D2}";

    public static string Model(string? categoryCode, long sequence)
    {
        if (string.IsNullOrWhiteSpace(categoryCode) || !System.Text.RegularExpressions.Regex.IsMatch(categoryCode, "^UTJ-[0-9]{2,}$"))
            throw new PdmRuleException("所选分类的一级分类编号尚未生成，请刷新分类后重试。");
        if (sequence is < 1 or > 9999)
            throw new PdmRuleException("该一级分类的套件型号4位流水号已用完，请联系管理员处理。");
        return $"{categoryCode}-{sequence:D4}";
    }
}
