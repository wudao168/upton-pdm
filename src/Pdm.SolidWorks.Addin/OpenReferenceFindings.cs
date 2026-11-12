using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace Upton.Pdm.SolidWorks;

internal sealed class OpenReferenceIssueDto
{
    public string InstancePath { get; set; }
    public string FileName { get; set; }
    public string OriginalPath { get; set; }
    public string Reason { get; set; }
    public string Key => (InstancePath ?? "") + "|" + (OriginalPath ?? "") + "|" + FileName;
    public override string ToString() => "引用：" + FileName + "\r\n位置：" + (InstancePath ?? "未记录")
        + "\r\n原路径：" + (string.IsNullOrWhiteSpace(OriginalPath) ? "存档快照未记录" : OriginalPath) + "\r\n原因：" + Reason;
}

internal static class OpenReferenceFindings
{
    public static List<OpenReferenceIssueDto> Merge(IEnumerable<OpenReferenceIssueDto> issues) =>
        (issues ?? Enumerable.Empty<OpenReferenceIssueDto>()).Where(issue => issue != null)
        .GroupBy(issue => issue.Key, StringComparer.OrdinalIgnoreCase).Select(group => group.Last()).ToList();

    private static string RecordPath(string rootPath, Guid versionId) => rootPath + "." + versionId.ToString("N") + ".uplm-findings.json";

    public static void Save(string rootPath, Guid versionId, IEnumerable<OpenReferenceIssueDto> issues)
    {
        var path = RecordPath(rootPath, versionId);
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(Merge(issues)));
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static List<OpenReferenceIssueDto> Read(string rootPath, Guid versionId) =>
        File.Exists(RecordPath(rootPath, versionId))
            ? new JavaScriptSerializer().Deserialize<List<OpenReferenceIssueDto>>(File.ReadAllText(RecordPath(rootPath, versionId)))
            : new List<OpenReferenceIssueDto>();
}
