using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Upton.Pdm.Domain;

namespace Upton.Pdm.SolidWorks;

internal sealed class OpenReferenceIssueDto
{
    public string InstancePath { get; set; }
    public string FileName { get; set; }
    public string OriginalPath { get; set; }
    public string Reason { get; set; }
    public bool IsToolbox { get; set; }
    public Guid? OwnerDocumentId { get; set; }
    public string Key => (InstancePath ?? "") + "|" + (OriginalPath ?? "") + "|" + FileName;
    public override string ToString() => "引用：" + FileName + "\r\n位置：" + (InstancePath ?? "未记录")
        + "\r\n原路径：" + (string.IsNullOrWhiteSpace(OriginalPath) ? "存档快照未记录" : OriginalPath) + "\r\n原因：" + Reason;
}

internal static class OpenReferenceFindings
{
    internal static bool IsToolboxDiagnostic(OpenReferenceIssueDto issue) => issue != null && (issue.IsToolbox
        || issue.Reason?.StartsWith("SolidWorks已临时加载本机Toolbox文件", StringComparison.Ordinal) == true
        || issue.Reason?.StartsWith("Toolbox原文件缺失", StringComparison.Ordinal) == true);

    internal static bool IsArchivedToolboxReference(IEnumerable<OpenReferenceIssueDto> issues, string path, Guid ownerId) =>
        issues?.Any(issue => issue?.IsToolbox == true && issue.OwnerDocumentId == ownerId
            && string.Equals(issue.OriginalPath, path, StringComparison.OrdinalIgnoreCase)) == true;

    // Old advisories record both the original path and the Toolbox file SolidWorks loaded.
    // Keep the occurrence and its owning document together when matching either name.
    internal static IEnumerable<(string Instance, string Original, string Loaded)> ArchivedToolboxOccurrences(
        IEnumerable<OpenReferenceIssueDto> issues, Guid ownerId)
    {
        foreach (var issue in issues ?? Enumerable.Empty<OpenReferenceIssueDto>())
        {
            if (!IsToolboxDiagnostic(issue) || issue.OwnerDocumentId != ownerId
                || !ArchiveWarningPolicy.TryReadToolboxReferenceWarning(issue.Reason, out var original, out var instance)
                || string.IsNullOrWhiteSpace(instance)) continue;
            const string sourceLabel = "；实际来源 ";
            var sourceStart = issue.Reason.IndexOf(sourceLabel, StringComparison.Ordinal);
            string loaded = null;
            if (sourceStart >= 0)
            {
                sourceStart += sourceLabel.Length;
                var end = issue.Reason.IndexOf('；', sourceStart);
                var source = end < 0 ? issue.Reason.Substring(sourceStart) : issue.Reason.Substring(sourceStart, end - sourceStart);
                if (Path.IsPathRooted(source) && string.Equals(Path.GetExtension(source), ".SLDPRT", StringComparison.OrdinalIgnoreCase))
                    loaded = source;
            }
            yield return (OccurrenceName(instance), original, loaded);
        }
    }

    internal static string OccurrenceName(string instance) =>
        instance?.Substring(instance.LastIndexOf('/') + 1);

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
