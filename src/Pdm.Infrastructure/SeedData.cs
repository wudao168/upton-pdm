using Upton.Pdm.Domain;

namespace Upton.Pdm.Infrastructure;

internal static class SeedData
{
    internal static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    internal static readonly Guid RootDocumentId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    internal static readonly Guid SnapshotId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    internal static readonly Guid ReleasePackageId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    internal static Project Project() => new(
        ProjectId,
        "PRJ-2026-018",
        "自动装配线",
        "王工",
        @"D:\PDM\Vault\PRJ-2026-018",
        @"D:\PDM\Release\PRJ-2026-018",
        true);

    internal static IReadOnlyList<PdmDocument> Documents(DateTimeOffset now) =>
    [
        new(RootDocumentId, ProjectId, "A01-000", "总装配", "A01-000.SLDASM", DocumentKind.Assembly, DocumentLifecycleState.Work, RevisionLabel.Parse("W3"), "王工", now),
        new(Guid.Parse("22222222-2222-2222-2222-222222222223"), ProjectId, "A01-100", "机架组件", "A01-100.SLDASM", DocumentKind.Assembly, DocumentLifecycleState.Released, RevisionLabel.Released('A'), null, now),
        new(Guid.Parse("22222222-2222-2222-2222-222222222224"), ProjectId, "A01-101", "底板", "A01-101.SLDPRT", DocumentKind.Part, DocumentLifecycleState.Released, RevisionLabel.Released('A'), null, now),
        new(Guid.Parse("22222222-2222-2222-2222-222222222225"), ProjectId, "A01-102", "立柱", "A01-102.SLDPRT", DocumentKind.Part, DocumentLifecycleState.Released, RevisionLabel.Released('A'), null, now),
        new(Guid.Parse("22222222-2222-2222-2222-222222222226"), ProjectId, "A01-100", "机架工程图", "A01-100.SLDDRW", DocumentKind.Drawing, DocumentLifecycleState.Released, RevisionLabel.Released('A'), null, now),
        new(Guid.Parse("22222222-2222-2222-2222-222222222227"), ProjectId, "A01-200", "夹具组件", "A01-200.SLDASM", DocumentKind.Assembly, DocumentLifecycleState.Work, RevisionLabel.Parse("W2"), null, now),
        new(Guid.Parse("22222222-2222-2222-2222-222222222228"), ProjectId, "A01-300", "电气柜组件", "A01-300.SLDASM", DocumentKind.Assembly, DocumentLifecycleState.Work, RevisionLabel.Parse("W1"), null, now)
    ];

    internal static DocumentReferenceNode Tree(IReadOnlyDictionary<Guid, PdmDocument> documents)
    {
        var root = documents[RootDocumentId];
        DocumentReferenceNode Node(Guid id, string path, int quantity, ReferenceNodeStatus status, params DocumentReferenceNode[] children)
        {
            var document = documents[id];
            return new DocumentReferenceNode(
                Guid.NewGuid(),
                id,
                path,
                document.FileName,
                document.Name,
                document.Kind,
                "默认",
                quantity,
                status,
                document.Revision,
                document.CheckedOutBy,
                children);
        }

        var frame = Node(
            Guid.Parse("22222222-2222-2222-2222-222222222223"),
            "A01-000/A01-100-1",
            1,
            ReferenceNodeStatus.Normal,
            Node(Guid.Parse("22222222-2222-2222-2222-222222222224"), "A01-000/A01-100-1/A01-101-1", 2, ReferenceNodeStatus.Normal),
            Node(Guid.Parse("22222222-2222-2222-2222-222222222225"), "A01-000/A01-100-1/A01-102-1", 4, ReferenceNodeStatus.Normal),
            Node(Guid.Parse("22222222-2222-2222-2222-222222222226"), "A01-000/A01-100-1/A01-100-DRAWING", 1, ReferenceNodeStatus.Normal));

        var missing = new DocumentReferenceNode(
            Guid.NewGuid(),
            null,
            "A01-000/A01-401-1",
            "A01-401.SLDPRT",
            "传感器支架",
            DocumentKind.Part,
            "默认",
            2,
            ReferenceNodeStatus.Missing,
            null,
            null,
            []);

        return new DocumentReferenceNode(
            Guid.NewGuid(),
            RootDocumentId,
            "A01-000",
            root.FileName,
            root.Name,
            root.Kind,
            "装配体A",
            1,
            ReferenceNodeStatus.Normal,
            root.Revision,
            root.CheckedOutBy,
            [
                frame,
                Node(Guid.Parse("22222222-2222-2222-2222-222222222227"), "A01-000/A01-200-1", 2, ReferenceNodeStatus.Lightweight),
                Node(Guid.Parse("22222222-2222-2222-2222-222222222228"), "A01-000/A01-300-1", 1, ReferenceNodeStatus.Normal),
                missing
            ]);
    }

    internal static IReadOnlyList<BomItem> Bom() =>
    [
        new(Guid.NewGuid(), ProjectId, BomKind.Standard, 1, "A01-100", "机架组件", 1, "011", null, "标准组件", "A", true),
        new(Guid.NewGuid(), ProjectId, BomKind.NonStandard, 1, "A01-200", "夹具组件", 2, "011", "Q235B", null, "W2", true),
        new(Guid.NewGuid(), ProjectId, BomKind.NonStandard, 2, "A01-301", "定位块", 4, "件", "45#", null, "A", true),
        new(Guid.NewGuid(), ProjectId, BomKind.Electrical, 1, "EL-001", "光电传感器", 4, "件", null, "M18 PNP", "A", true),
        new(Guid.NewGuid(), ProjectId, BomKind.Electrical, 2, "EL-002", "伺服驱动器", 2, "件", null, "750W", "A", false)
    ];

    internal static ReleasePackage ReleasePackage(DateTimeOffset now) => new(
        ReleasePackageId,
        ProjectId,
        "RP-2026-018-003",
        ReleasePackageState.Approval,
        SnapshotId,
        "M-A",
        "E-A",
        [
            new ApprovalTask(Guid.Parse("55555555-5555-5555-5555-555555555555"), ReleasePackageId, ApprovalStage.ProcessReview, "李工", "李工", ApprovalDecision.Approved, "工艺可行", now.AddHours(-2)),
            new ApprovalTask(Guid.Parse("66666666-6666-6666-6666-666666666666"), ReleasePackageId, ApprovalStage.Approval, "赵经理", null, null, null, null)
        ],
        now.AddDays(-1),
        null,
        null);
}
