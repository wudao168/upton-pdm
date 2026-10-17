using Dapper;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

internal static class LargeReferenceSnapshotChecks
{
    public static async Task RunAsync(MySqlPdmRepository repository, string connectionString)
    {
        var database = new MySqlConnectionStringBuilder(connectionString).Database;
        Require(database.StartsWith("pdm_atomic_qa_", StringComparison.Ordinal) && database.Length == 46, "Unsafe fixture database");
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        var buffer = await connection.ExecuteScalarAsync<long>("SELECT @@session.sort_buffer_size");
        Console.WriteLine("Large-reference fixture MySQL sort buffer: " + buffer);
        var project = await repository.CreateProjectAsync(new("QA-LARGE-" + Guid.NewGuid().ToString("N"), "大装配引用排序回归", "qa", "vault", "release"), "qa", default);
        var session = Guid.NewGuid();
        var batch = Guid.NewGuid();
        var context = new ArchiveRegistrationContext(batch, session, "QA");
        async Task<PdmDocument> Reserve(string name, DocumentKind kind, Guid? model = null) =>
            await repository.ReserveArchiveDocumentAsync(new(project.Id, name, name, name + (kind == DocumentKind.Assembly ? ".SLDASM" : ".SLDDRW"), kind,
                RelatedModelDocumentId: model), "qa", context, default);
        var root = await Reserve("Z-ROOT", DocumentKind.Assembly);
        var other = await Reserve("A-ROOT", DocumentKind.Assembly);
        var drawings = new List<PdmDocument>();
        for (var i = 0; i < 6; i++) drawings.Add(await Reserve("DRAWING-" + i, DocumentKind.Drawing, root.Id));
        DocumentReferenceNode Node(PdmDocument document) => new(Guid.NewGuid(), document.Id, document.DrawingNumber, document.FileName, document.Name,
            document.Kind, "Default", 1, ReferenceNodeStatus.Normal, document.Revision, null, []);
        // Payload is deliberately larger than the production 256 KiB sort buffer. Embedded
        // instances retain distinct configurations without independent documents or versions.
        var embedded = Enumerable.Range(0, 600).Select(i => new DocumentReferenceNode(Guid.NewGuid(), null, "V" + i, "V" + i + ".SLDPRT",
            new string('x', 2048), DocumentKind.Part, "CFG-" + i % 3, 1, ReferenceNodeStatus.Virtual, null, null, [])
            { InstancePath = "Z-ROOT/V" + i }).ToArray();
        DocumentVersionCommit Commit(PdmDocument document, DocumentReferenceNode tree, bool isRoot = false) => new(
            new StoredFile(".versions/" + Guid.NewGuid().ToString("N"), 128, new string('C', 64), DateTimeOffset.UtcNow), "大快照回归",
            new Dictionary<string, string?>(), new(Guid.NewGuid(), project.Id, document.Id, DateTimeOffset.UtcNow, "qa", tree, new string('D', 64)), [], [], IsProjectRoot: isRoot)
            { RequireBaseVersion = true };
        var files = new List<ArchiveBatchFile> {
            new(root.Id, Commit(root, Node(root) with { Children = embedded }, true)),
            new(other.Id, Commit(other, Node(other), true))
        };
        files.AddRange(drawings.Select(document => new ArchiveBatchFile(document.Id, Commit(document, Node(document) with { Children = [Node(root)] }))));
        var invalid = files.ToArray();
        invalid[^1] = invalid[^1] with { Commit = invalid[^1].Commit with { ExpectedVersionId = Guid.NewGuid() } };
        try
        {
            await repository.CheckInBatchVersionsAsync(batch, "large", "qa", session, invalid, default);
            throw new Exception("Expected final drawing failure");
        }
        catch (PdmConflictException)
        {
            Require((await repository.ListDocumentsAsync(project.Id, default)).Count == 0, "Large-batch failure leaked documents");
            Require(await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM document_version WHERE document_id IN @Ids", new { Ids = files.Select(file => file.DocumentId).ToArray() }) == 0,
                "Large-batch failure leaked versions");
            Require(await repository.GetLatestReferenceSnapshotAsync(project.Id, default) is null, "Large-batch failure leaked structure");
            Require(await repository.FindArchiveBatchAsync(batch, default) is null, "Large-batch failure leaked receipt");
            Console.WriteLine("PASS MySQL large-reference final-drawing failure rolls back documents, versions, structure and receipt");
        }
        var receipt = await repository.CheckInBatchVersionsAsync(batch, "large", "qa", session, files, default);
        var replay = await repository.CheckInBatchVersionsAsync(batch, "large", "qa", session, files, default);
        Require(receipt.Results.Count == 8 && replay.Results.Select(result => result.Version!.Id).SequenceEqual(receipt.Results.Select(result => result.Version!.Id)),
            "Large-batch replay duplicated versions");
        var aggregate = (await repository.GetLatestReferenceSnapshotAsync(project.Id, default))!;
        Require(aggregate.Root.Children.Select(node => node.FileName).SequenceEqual(new[] { other.FileName, root.FileName }), "Project root ordering changed");
        var saved = aggregate.Root.Children.Single(node => node.DocumentId == root.Id);
        Require(saved.Children.Count == embedded.Length && saved.Children.All(node => node.Status == ReferenceNodeStatus.Virtual && node.DocumentId is null)
            && saved.Children.Select(node => node.Configuration).Distinct().Count() == 3, "Embedded instances or configurations lost");
        Require((await repository.ListDocumentRelationsAsync(project.Id, default)).Count == 6, "Six drawing relations incomplete");
        Console.WriteLine("PASS MySQL large-reference commit preserves six drawings, 600 embedded instances, three configurations and root ordering");
        var nextBatch = Guid.NewGuid();
        var unchanged = new List<ArchiveBatchFile>();
        foreach (var file in files)
        {
            await repository.CheckoutAsync(file.DocumentId, "qa", session, "QA", DateTimeOffset.UtcNow.AddHours(1), default);
            var previous = receipt.Results.Single(result => result.Document.Id == file.DocumentId).Version!;
            unchanged.Add(file with { Commit = file.Commit with { ExpectedVersionId = previous.Id, ReferenceSnapshot = file.Commit.ReferenceSnapshot with { Root = previous.ReferenceSnapshot } } });
        }
        var repeated = await repository.CheckInBatchVersionsAsync(nextBatch, "large-unchanged", "qa", session, unchanged, default);
        Require(repeated.Results.All(result => !result.VersionCreated), "Unchanged retry created new versions");
        foreach (var file in files) Require((await repository.ListDocumentVersionsAsync(file.DocumentId, default)).Count == 1, "Unexpected extra version");
        Console.WriteLine("PASS MySQL lost-response replay and unchanged new batch keep exactly one version per physical file");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
