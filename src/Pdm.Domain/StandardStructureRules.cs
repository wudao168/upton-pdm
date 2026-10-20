using System;
using System.Globalization;

namespace Upton.Pdm.Domain;

public static class StandardStructureRules
{
    public static readonly Guid GroupId = new Guid("55505354-0000-0000-0000-000000000001");
    public static readonly Guid UnclassifiedId = new Guid("55505354-0000-0000-0000-000000000002");
    public const string IdentityProperty = "UPLM标准库标识";
    public const string SourceNameProperty = "UPLM标准库原文件名";
    public static bool IsDrawingType(int type) => type == 1 || type == 2 || type == 3 || type == 4 || type == 9;
    public static string TypeName(int type) => type switch
    {
        1 => "标准组件", 2 => "标准零件", 3 => "组件模板", 4 => "零件模板", 9 => "其他图档", _ => "请选择图纸类型"
    };
    public static bool MatchesKind(int type, string kind) => type == 9
        || (type == 1 || type == 3) && kind == "Assembly"
        || (type == 2 || type == 4) && kind == "Part";
    public static string Code(int type, int sequence)
    {
        if (!IsDrawingType(type) || sequence < 1 || sequence > 9999) throw new InvalidOperationException("标准库编号范围无效。");
        return "UPST-" + type.ToString(CultureInfo.InvariantCulture) + "-" + sequence.ToString("0000", CultureInfo.InvariantCulture);
    }
    public static string Version(int version) => "V" + version.ToString("00", CultureInfo.InvariantCulture);
}
