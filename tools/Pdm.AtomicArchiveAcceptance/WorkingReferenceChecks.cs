using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

internal static class WorkingReferenceChecks
{
    internal static async Task RunAsync(MySqlPdmRepository repo, string workspace)
    {
        var session = Guid.NewGuid();
        var path = Path.Combine(workspace, "tmp", "working-reference-mysql", session.ToString("N"));
        var project = await repo.CreateProjectAsync(new("QA-WORK-" + session.ToString("N"), "子件独立升版验证", "qa", path, path), "qa", default);
        async Task<PdmDocument> Register(string name, DocumentKind kind) => await repo.RegisterDocumentAsync(
            new(project.Id, name, name, name + (kind == DocumentKind.Assembly ? ".SLDASM" : ".SLDPRT"), kind), "qa", default);
        DocumentReferenceNode Node(PdmDocument doc, params DocumentReferenceNode[] children) => new(Guid.NewGuid(), doc.Id,
            doc.DrawingNumber, doc.FileName, doc.Name, doc.Kind, "Default", 1, ReferenceNodeStatus.Normal, doc.Revision, null, children);
        async Task<DocumentCheckInResult> Save(PdmDocument doc, DocumentReferenceNode tree, char hash, Guid? expected, bool root = false)
        {
            await repo.CheckoutAsync(doc.Id, "qa", session, "QA", DateTimeOffset.UtcNow.AddHours(1), default);
            var commit = new DocumentVersionCommit(new(".versions/" + Guid.NewGuid().ToString("N"), 128, new string(hash, 64), DateTimeOffset.UtcNow),
                "工作结构验证", new Dictionary<string, string?>(),
                new(Guid.NewGuid(), project.Id, doc.Id, DateTimeOffset.UtcNow, "qa", tree, new string('F', 64)), [], [], IsProjectRoot: root)
                { RequireBaseVersion = true, ExpectedVersionId = expected };
            return await repo.CheckInVersionAsync(doc.Id, "qa", session, commit, default);
        }
        var child = await Register("CHILD", DocumentKind.Part);
        var child1 = await Save(child, Node(child), 'A', null); child = child1.Document;
        var parent = await Register("PARENT", DocumentKind.Assembly);
        var parent1 = await Save(parent, Node(parent, Node(child)), 'B', null, true); parent = parent1.Document;
        var frozen = (await repo.GetLatestReferenceSnapshotAsync(project.Id, default))!;
        var child2 = await Save(child, Node(child), 'C', child1.Version!.Id);
        var current = await repo.GetReferenceTreeAsync(project.Id, default);
        Require(current!.Children.Single().Revision!.Value.Display == "W2", "Current MySQL project root did not advance the child");
        Require((await repo.ListDocumentVersionsAsync(parent.Id, default)).Count == 1, "Child change created a parent version");
        Require((await repo.FindReferenceSnapshotAsync(frozen.SnapshotId, default))!.Root.Children.Single().Revision!.Value.Display == "W1", "Historical reference snapshot changed");
        var parentNoChange = await Save(parent, Node(parent, Node(child2.Document)), 'B', parent1.Version!.Id, true);
        Require(!parentNoChange.VersionCreated && (await repo.ListDocumentVersionsAsync(parent.Id, default)).Count == 1,
            "References-only parent submission created another file version");
        var quantityChange = Node(parent, Node(child2.Document) with { Quantity = 2 });
        await Save(parentNoChange.Document, quantityChange, 'B', parent1.Version.Id, true);
        Require((await repo.ListDocumentVersionsAsync(parent.Id, default)).Count == 2, "Actual direct component quantity edit was lost");
        Console.WriteLine("PASS MySQL child-only version updates current tree, preserves parent/historical versions, suppresses parent no-op and retains actual quantity changes");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
