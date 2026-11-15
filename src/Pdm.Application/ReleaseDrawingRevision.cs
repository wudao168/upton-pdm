using System.Security.Cryptography;
using System.Text;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Application;

public static class ReleaseDrawingRevision
{
    public static ApprovalTask? CurrentTask(ReleasePackage package) =>
        package.State is ReleasePackageState.ProcessReview or ReleasePackageState.Approval
            ? package.ApprovalTasks.OrderBy(task => task.StepOrder).FirstOrDefault(task => task.Decision is null)
            : null;

    public static ReleasePackage Replace(ReleasePackage package, ApprovalTask task, ReleaseDrawingReviewItem item,
        DocumentVersion previous, DocumentVersion version, string? designerName)
    {
        var review = package.DrawingReview!;
        var bindings = package.VersionBindings ?? throw new PdmConflictException("发布包缺少冻结图纸绑定。");
        var binding = bindings.SingleOrDefault(value => value.DocumentId == item.DocumentId && value.VersionId == previous.Id && value.Sha256 == previous.Sha256)
            ?? throw new PdmConflictException("发布包的原图纸版本已变化，请刷新后重试。");
        if (version.Id == previous.Id || version.DocumentId != item.DocumentId || version.Status != DocumentVersionStatus.Work
            || version.ReferenceSnapshot.HasPendingStructureIndex || version.ReferenceSnapshot.HasBlockingIssue
            || !ArchiveSafety.SameOwnDesignReferences(previous.ReferenceSnapshot, version.ReferenceSnapshot))
            throw new PdmRuleException("修改图纸必须完整存档并保持原发布包的模型及引用版本。涉及BOM或模型变更时，请核对后重新发起发布。");

        var replacement = item with
        {
            VersionId = version.Id,
            Sha256 = version.Sha256,
            Required = true,
            Status = "Pending",
            Preview = null,
            Token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{item.Token}:{version.Id:N}:{version.Sha256}")))
        };
        var newBinding = binding with
        {
            VersionId = version.Id,
            Sha256 = version.Sha256,
            Revision = version.Revision.Display,
            RequiresPromotion = binding.Publish,
            TargetRevision = binding.Publish ? version.Revision.Release().Display : ""
        };
        if (newBinding.Publish)
            BomDrawingRevision.ValidateDrawingProperties(version, newBinding.TargetRevision, item.FileName);
        // A partial signature covers all drawings in this node. The revised drawing must
        // be confirmed again, while the remaining drawings keep their original tokens.
        var group = task.Participants;
        if (group is not null)
        {
            group = group with
            {
                Signatures = [],
                Departments = group.Departments.Select(branch => branch with { Signatures = [] }).ToArray()
            };
        }
        return package with
        {
            VersionBindings = bindings.Select(value => value.DocumentId == item.DocumentId ? newBinding : value).ToArray(),
            DrawingReview = review with
            {
                Revision = review.Revision + 1,
                Items = review.Items.Select(value => value.DocumentId == item.DocumentId ? replacement : value).ToArray()
            },
            ApprovalTasks = package.ApprovalTasks.Select(value => value.Id == task.Id ? task with { Participants = group } : value).ToArray(),
            PdfTitleBlockSnapshot = ReplaceTitleBlock(package.PdfTitleBlockSnapshot, version, ReleasePreviewPolicy.DeliversFrozenDrawings(package)
                ? version.Revision.Display : version.Revision.Release().Display, designerName)
        };
    }

    private static PdfTitleBlockSnapshot? ReplaceTitleBlock(PdfTitleBlockSnapshot? snapshot, DocumentVersion version,
        string deliveryRevision, string? designerName)
    {
        if (snapshot is null || !snapshot.Drawings.TryGetValue(version.DocumentId, out var drawing)) return snapshot;
        var values = new Dictionary<string, string>(drawing.Values);
        var name = string.IsNullOrWhiteSpace(designerName) || string.Equals(designerName, version.CreatedBy, StringComparison.OrdinalIgnoreCase)
            ? "姓名待补充" : designerName.Trim();
        foreach (var field in new[] { "designer", "drafter" })
            if (values.ContainsKey(field)) values[field] = name;
        if (values.ContainsKey("designedOn")) values["designedOn"] = version.CreatedAt.ToOffset(TimeSpan.FromHours(8))
            .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (values.ContainsKey("revision")) values["revision"] = deliveryRevision;
        var drawings = new Dictionary<Guid, PdfDrawingTitleBlockSnapshot>(snapshot.Drawings)
        {
            [version.DocumentId] = drawing with { SourceVersionId = version.Id, SourceSha256 = version.Sha256, Values = values }
        };
        return snapshot with { Drawings = drawings };
    }
}
