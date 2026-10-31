using System.Text.Json;
using Dapper;
using MySqlConnector;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

internal static class DeferredReferenceImmutableChecks
{
    public static async Task RunAsync(MySqlPdmRepository repository, string connectionString)
    {
        var db = new MySqlConnectionStringBuilder(connectionString).Database;
        Require(db.StartsWith("pdm_atomic_qa_", StringComparison.Ordinal) && db.Length == 46, "Unsafe fixture database");
        await using var connection = new MySqlConnection(connectionString); await connection.OpenAsync();
        foreach (var unknown in new[] { false, true })
        {
            var project = await repository.CreateProjectAsync(new("QA-DEFER-" + Guid.NewGuid().ToString("N"), "延后引用不可变版本验证", "qa", "vault", "release"), "qa", default);
            var session = Guid.NewGuid();
            async Task<PdmDocument> Register(string name, DocumentKind kind)
            {
                var doc = await repository.RegisterDocumentAsync(new(project.Id, name, name, name + (kind == DocumentKind.Assembly ? ".SLDASM" : ".SLDPRT"), kind), "qa", default);
                return await repository.CheckoutAsync(doc.Id, "qa", session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
            }
            var parent = await Register("PARENT", DocumentKind.Assembly);
            var part = await Register("PART", DocumentKind.Part);
            DocumentReferenceNode PartTree() => Node(part) with { Children = [Node(parent) with {
                Role = ReferenceRole.ExternalFileDependency, IsContextReference = !unknown,
                ReferenceTypeUnverified = unknown, Children = [] }] };
            ArchiveBatchFile[] Files(char hash, Guid? partBase = null, Guid? parentBase = null) => [
                new(parent.Id, Commit(project, parent, Node(parent) with { Children = [Node(part)] }, hash, true) with { ExpectedVersionId = parentBase }),
                new(part.Id, Commit(project, part, PartTree(), hash) with { ExpectedVersionId = partBase })];
            var initialBatch = Guid.NewGuid(); var initialFiles = Files('A');
            var initial = await repository.CheckInBatchVersionsAsync(initialBatch, "initial", "qa", session, initialFiles, default);
            Require(initial.Results.All(r => r.VersionCreated), "Initial versions missing");
            VerifyBindings(initial, parent.Id, part.Id, unknown);
            var oldVersions = initial.Results.ToDictionary(r => r.Document.Id, r => r.Version!);
            var oldJson = await connection.QueryAsync<(Guid Id, string Json)>("SELECT id,reference_snapshot_json FROM document_version WHERE document_id IN @Ids", new { Ids = oldVersions.Keys.ToArray() });
            var replay = await repository.CheckInBatchVersionsAsync(initialBatch, "initial", "qa", session, initialFiles, default);
            Require(replay.Results.Select(r => r.Version!.Id).SequenceEqual(initial.Results.Select(r => r.Version!.Id)), "Replay created versions");
            Console.WriteLine("PASS MySQL deferred " + unknown + ": initial reciprocal links and idempotent receipt");

            async Task Checkout()
            {
                parent = await repository.CheckoutAsync(parent.Id, "qa", session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
                part = await repository.CheckoutAsync(part.Id, "qa", session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
            }
            await Checkout();
            var unchanged = await repository.CheckInBatchVersionsAsync(Guid.NewGuid(), "unchanged", "qa", session,
                Files('A', oldVersions[part.Id].Id, oldVersions[parent.Id].Id), default);
            Require(unchanged.Results.All(r => !r.VersionCreated), "Unchanged files created versions");
            Console.WriteLine("PASS MySQL deferred " + unknown + ": unchanged files keep versions");

            await Checkout();
            var failedBatch = Guid.NewGuid(); var rejected = false;
            try { await repository.CheckInBatchVersionsAsync(failedBatch, "rejected", "qa", session, Files('B', oldVersions[part.Id].Id, Guid.NewGuid()), default); }
            catch (PdmConflictException) { rejected = true; }
            Require(rejected, "Invalid parent base was accepted");
            Require(await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM archive_batch_receipt WHERE batch_id=@Id", new { Id = failedBatch }) == 0, "Failed receipt leaked");
            foreach (var id in oldVersions.Keys)
            {
                Require((await repository.ListDocumentVersionsAsync(id, default)).Count == 1, "Failed batch leaked a version");
                Require((await repository.FindDocumentAsync(id, default))!.CheckoutSessionId == session, "Failed batch released edit session");
            }
            Console.WriteLine("PASS MySQL deferred " + unknown + ": late validation failure rolls back versions, receipt and edit state");

            var updated = await repository.CheckInBatchVersionsAsync(Guid.NewGuid(), "updated", "qa", session,
                Files('B', oldVersions[part.Id].Id, oldVersions[parent.Id].Id), default);
            VerifyBindings(updated, parent.Id, part.Id, unknown);
            foreach (var old in oldJson)
                Require(await connection.ExecuteScalarAsync<string>("SELECT reference_snapshot_json FROM document_version WHERE id=@Id", new { old.Id }) == old.Json, "Old version was changed");
            var blocked = false;
            try { await connection.ExecuteAsync("UPDATE document_version SET reference_snapshot_json='{}' WHERE id=@Id", new { Id = oldVersions[part.Id].Id }); }
            catch (MySqlException error) when (error.Message.Contains("immutable", StringComparison.OrdinalIgnoreCase)) { blocked = true; }
            Require(blocked, "Immutable trigger was weakened");
            Console.WriteLine("PASS MySQL deferred " + unknown + ": updated references bind once; old versions and immutable trigger preserved");
        }
    }

    private static void VerifyBindings(ArchiveBatchReceipt receipt, Guid parent, Guid part, bool unknown)
    {
        var a = receipt.Results.Single(r => r.Document.Id == parent); var b = receipt.Results.Single(r => r.Document.Id == part);
        Require(a.Version!.ReferenceSnapshot.Children.Single().Revision == b.Document.Revision, "Parent reference is stale");
        var back = b.Version!.ReferenceSnapshot.Children.Single();
        Require(back.Revision == a.Document.Revision && back.ReferenceTypeUnverified == unknown && back.IsContextReference == !unknown, "Back reference changed type or version");
    }
    private static DocumentReferenceNode Node(PdmDocument doc) => new(Guid.NewGuid(), doc.Id, doc.DrawingNumber, doc.FileName, doc.Name,
        doc.Kind, "Default", 1, ReferenceNodeStatus.Normal, doc.Revision, "qa", []);
    private static DocumentVersionCommit Commit(Project project, PdmDocument doc, DocumentReferenceNode tree, char hash, bool root = false) => new(
        new StoredFile(".versions/" + Guid.NewGuid().ToString("N"), 128, new string(hash, 64), DateTimeOffset.UtcNow), "整批引用验证",
        new Dictionary<string, string?>(), new CadReferenceSnapshot(Guid.NewGuid(), project.Id, doc.Id, DateTimeOffset.UtcNow, "qa", tree, new string('F', 64)), [], [], IsProjectRoot: root)
        { RequireBaseVersion = true };
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
