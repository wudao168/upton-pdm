using System.Text.Json;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ArchiveReferenceWarningTests
{
    [Fact]
    public async Task WarningsSurviveVersionStorageAndReplayWithoutCreatingGeometryChanges()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("WARN-" + Guid.NewGuid().ToString("N"), "引用提示", "admin", "vault", "release"), "admin", default);
        var session = Guid.NewGuid();
        var part = await ArchiveBatchTests.Register(repository, project, "PART", DocumentKind.Part, session);
        var original = ArchiveBatchTests.Node(part);
        var warned = original with { ReferenceWarnings = [@"来源：Z:\旧目录\SOURCE.STEP；未找到源文件；原有关联保留"] };
        Assert.True(ArchiveSafety.SameOwnDesignReferences(original, warned));
        Assert.Empty(ReferenceTreeDiff.Compare(original, warned));
        Assert.False(warned.HasBlockingIssue);
        var batch = Guid.NewGuid();
        var commit = ArchiveBatchTests.Commit(project, part, warned, 'A');
        var first = await repository.CheckInBatchVersionsAsync(batch, "warnings", "admin", session, [new(part.Id, commit)], default);
        await repository.CheckInBatchVersionsAsync(batch, "warnings", "admin", session, [new(part.Id, commit)], default);
        var version = Assert.Single(await repository.ListDocumentVersionsAsync(part.Id, default));
        Assert.True(Assert.Single(first.Results).VersionCreated);
        Assert.Equal(warned.ReferenceWarnings, version.ReferenceSnapshot.ReferenceWarnings);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var reloaded = JsonSerializer.Deserialize<DocumentReferenceNode>(JsonSerializer.Serialize(version.ReferenceSnapshot, options), options)!;
        Assert.Equal(warned.ReferenceWarnings, reloaded.ReferenceWarnings);
        Assert.Empty(reloaded.Children);
        Assert.DoesNotContain("referenceWarnings", JsonSerializer.Serialize(original, options));
        Assert.Null(JsonSerializer.Deserialize<DocumentReferenceNode>(JsonSerializer.Serialize(original, options), options)!.ReferenceWarnings);
        await repository.CheckoutAsync(part.Id, "admin", session, "QA", DateTimeOffset.UtcNow.AddMinutes(15), null, default);
        var unchanged = await repository.CheckInBatchVersionsAsync(Guid.NewGuid(), "unchanged", "admin", session,
            [new(part.Id, commit with { ExpectedVersionId = version.Id })], default);
        Assert.False(Assert.Single(unchanged.Results).VersionCreated);
        Assert.Equal(warned.ReferenceWarnings, Assert.Single(await repository.ListDocumentVersionsAsync(part.Id, default)).ReferenceSnapshot.ReferenceWarnings);
    }
}
