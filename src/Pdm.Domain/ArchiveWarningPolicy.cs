#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;

namespace Upton.Pdm.Domain;

// Shared by the API and the native add-in; no CAD mutation or authorization is performed here.
public sealed class ArchiveWarningDefinition
{
    public string Code { get; set; }
    public string Name { get; set; }
    public string Source { get; set; }
    public bool Supported { get; set; }
    public bool AlwaysBlocks { get; set; }
}

public sealed class ArchiveWarningPolicy
{
    public long Revision { get; set; }
    public bool BlockAll { get; set; }
    public string[] BlockingCodes { get; set; } = Array.Empty<string>();

    public bool Blocks(string code)
    {
        var definition = Catalog.FirstOrDefault(item => item.Code == code);
        return definition != null && definition.Supported && (definition.AlwaysBlocks || BlockAll
            || (BlockingCodes ?? Array.Empty<string>()).Contains(code, StringComparer.Ordinal));
    }

    public static string Format(string code, string detail) => "存档警告[" + code + "]：" + detail;
    public static string CodeOf(string message)
    {
        if (message == null) return null;
        foreach (var item in Catalog)
            if (message.StartsWith("存档警告[" + item.Code + "]：", StringComparison.Ordinal)) return item.Code;
        // Preserve enforcement for warnings reported by older clients.
        if (message.StartsWith("属性索引待补齐：", StringComparison.Ordinal)) return "PropertyIndexPending";
        if (message.StartsWith("存档引用提示：", StringComparison.Ordinal))
            return message.Contains("引用文件缺失") ? "MissingFile" : "OutsideVault";
        return null;
    }

    public static void Validate(ArchiveWarningPolicy policy)
    {
        if (policy == null || policy.BlockingCodes == null || policy.Revision < 0
            || policy.BlockingCodes.Length > Catalog.Length
            || policy.BlockingCodes.Distinct(StringComparer.Ordinal).Count() != policy.BlockingCodes.Length
            || policy.BlockingCodes.Any(code => !Catalog.Any(item => item.Code == code && item.Supported && !item.AlwaysBlocks)))
            throw new ArgumentException("存档警告设置包含无效、重复或尚不支持的项目，请刷新后重试。");
    }

    public static readonly ArchiveWarningDefinition[] Catalog = {
        Define("CyclicReference", "确认的循环引用", true, true),
        Define("DuplicateName", "文件名不唯一"),
        Define("MissingFile", "引用文件找不到", true),
        Define("ReferenceModified", "引用文件本地副本已修改"),
        Define("LocalVersionOverwritten", "本地版本被覆盖"),
        Define("BomDataPending", "BOM内部数据缺失或未更新"),
        Define("NoLocalCopy", "引用文件没有本地副本"),
        Define("OutsideVault", "引用未纳入库内或本次提交", true),
        Define("LargeDesignReview", "装配体保存于大型设计审阅模式"),
        Define("DetailingMode", "工程图保存于出详图模式"),
        Define("IndependentRevisionTable", "工程图使用独立修订表"),
        Define("PlatformCompatibility", "已作3DEXPERIENCE兼容性更新"),
        Define("ReferenceCheckedOutByOther", "引用被其他用户检出"),
        Define("ReferenceCheckedOutElsewhere", "引用在其他文件夹检出"),
        Define("ReferenceCheckedOutOtherComputer", "引用在其他电脑检出"),
        Define("NotRebuilt", "文件未重建"),
        Define("OpenInOtherApplication", "文件在其他应用中打开"),
        Define("ToolboxLocation", "Toolbox文件位于非Toolbox文件夹"),
        Define("UnknownFormat", "未知文件格式"),
        Define("MissingConfiguration", "引用配置不存在", true, false, "UPLM扩展"),
        Define("ConfigurationUnreadable", "配置目录暂时无法读取", true, false, "UPLM扩展"),
        Define("PropertyIndexPending", "非必填属性索引待补齐", true, false, "UPLM扩展"),
        Define("ReferenceTypeUnknown", "候选循环中的引用类型待核对", true, false, "UPLM扩展"),
        Define("DrawingOutOfSync", "工程图与模型尚未同步", false, false, "UPLM扩展")
    };

    private static ArchiveWarningDefinition Define(string code, string name, bool supported = false,
        bool alwaysBlocks = false, string source = "PDM官方") => new ArchiveWarningDefinition {
            Code = code, Name = name, Source = source, Supported = supported, AlwaysBlocks = alwaysBlocks };
}
