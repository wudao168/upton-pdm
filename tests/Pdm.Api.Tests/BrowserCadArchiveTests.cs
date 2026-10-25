using System.IdentityModel.Tokens.Jwt;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Upton.Pdm.Api;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api.Tests;

public sealed class BrowserCadArchiveTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingDrawingKnownRelationIsListedAndDownloadedIncludingLegacyMissingRelationRows(bool legacyMissingRelation)
    {
        using var fixture = new Fixture(); await fixture.Setup();
        fixture.Reader.RootTransform = root => root.Kind == DocumentKind.Drawing ? root with { StructureIndexPending = true } : root;
        var files = new Dictionary<string, string>
        { ["ROOT.SLDASM"] = "root", ["MODEL.SLDPRT"] = "model", ["DIFFERENT-NAME.SLDDRW"] = "drawing with unresolved additional views" };
        var job = await fixture.Stage(new(fixture.Project.Id), files); await fixture.Process(job.Id, "ROOT.SLDASM");
        var documents = await fixture.Repository.ListDocumentsAsync(fixture.Project.Id, default);
        var model = documents.Single(document => document.Kind == DocumentKind.Part);
        var drawing = documents.Single(document => document.Kind == DocumentKind.Drawing);
        var root = documents.Single(document => document.Kind == DocumentKind.Assembly);
        var version = Assert.Single(await fixture.Repository.ListDocumentVersionsAsync(drawing.Id, default));
        Assert.True(version.ReferenceSnapshot.StructureIndexPending);
        var storedRelations = (IDictionary<(Guid DrawingId, Guid ModelId), DocumentModelDrawingRelation>)typeof(InMemoryPdmRepository)
            .GetField("documentRelations", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(fixture.Repository)!;
        if (legacyMissingRelation) Assert.True(storedRelations.Remove((drawing.Id, model.Id)));
        var relationCount = storedRelations.Count;
        var relations = (await fixture.Client.GetFromJsonAsync<DocumentModelDrawingRelation[]>($"/api/projects/{fixture.Project.Id}/document-relations"))!;
        Assert.Contains(relations, relation => relation.DrawingDocumentId == drawing.Id && relation.ModelDocumentId == model.Id);
        var response = await fixture.Client.PostAsJsonAsync($"/api/documents/{root.Id}/open-manifest", new { forEdit = false, releasedOnly = false, treeScopeOnly = true });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var manifest = (await response.Content.ReadFromJsonAsync<ControlledOpenManifest>())!;
        Assert.Equal(3, manifest.Files.Count);
        var retrieved = Assert.Single(manifest.Files, file => file.DocumentId == drawing.Id);
        Assert.Equal(version.Id, retrieved.VersionId);
        var download = await fixture.Client.GetAsync($"/api/documents/{drawing.Id}/versions/{retrieved.VersionId}/file?download=true");
        Assert.True(download.IsSuccessStatusCode);
        var bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.Equal(files[drawing.FileName], Encoding.UTF8.GetString(bytes));
        Assert.Equal(retrieved.Sha256, Convert.ToHexString(SHA256.HashData(bytes)));
        Assert.Equal(relationCount, storedRelations.Count);
        Assert.Equal(version, Assert.Single(await fixture.Repository.ListDocumentVersionsAsync(drawing.Id, default)));
    }

    [Fact]
    public async Task ProjectArchivePreservesStructureRelationsAndParentVersionsAndReplaysWithoutDuplicateVersions()
    {
        using var fixture = new Fixture(); await fixture.Setup();
        var target = new BrowserCadArchiveTarget(fixture.Project.Id);
        var files = new Dictionary<string, string> { ["ROOT.SLDASM"] = "root", ["PART.SLDPRT"] = "part", ["PART.SLDDRW"] = "drawing" };
        var first = await fixture.Stage(target, files); await fixture.Process(first.Id, "ROOT.SLDASM");
        var docs = await fixture.Repository.ListDocumentsAsync(fixture.Project.Id, default);
        Assert.Equal(3, docs.Count); Assert.All(docs, doc => Assert.Null(doc.CheckedOutBy));
        Assert.Equal(3, fixture.Queue.Read(first.Id)!.NewVersions);
        Assert.Single(await fixture.Repository.ListDocumentRelationsAsync(fixture.Project.Id, default));
        var savedDrawing = docs.Single(d => d.Kind == DocumentKind.Drawing);
        var drawingVersion = Assert.Single(await fixture.Repository.ListDocumentVersionsAsync(savedDrawing.Id, default));
        Assert.Equal(drawingVersion.Sha256, drawingVersion.Preview!.SourceSha256);
        Assert.Equal(DocumentPreviewFormat.Pdf, drawingVersion.Preview.Format);
        await fixture.Process(first.Id, "ROOT.SLDASM");
        foreach (var doc in docs) Assert.Single(await fixture.Repository.ListDocumentVersionsAsync(doc.Id, default));
        var repeated = await fixture.Stage(target, files); await fixture.Process(repeated.Id, "ROOT.SLDASM");
        Assert.Equal(0, fixture.Queue.Read(repeated.Id)!.NewVersions);
        var child = await fixture.Stage(target, new() { ["PART.SLDPRT"] = "part changed" }); await fixture.Process(child.Id, "PART.SLDPRT");
        var parent = docs.Single(d => d.Kind == DocumentKind.Assembly); var part = docs.Single(d => d.Kind == DocumentKind.Part);
        Assert.Single(await fixture.Repository.ListDocumentVersionsAsync(parent.Id, default));
        Assert.Equal(2, (await fixture.Repository.ListDocumentVersionsAsync(part.Id, default)).Count);
        var forest = (await fixture.Repository.GetLatestReferenceSnapshotAsync(fixture.Project.Id, default))!;
        Assert.Single(ProjectReferenceForest.Roots(forest));
        Assert.Equal("W2", forest.Root.Children.Single(n => n.DocumentId == part.Id).Revision!.Value.Display);
        var drawing = await fixture.Stage(target, new() { ["EXTRA.SLDDRW"] = "extra drawing" }); await fixture.Process(drawing.Id, "EXTRA.SLDDRW");
        Assert.Equal(1, fixture.Queue.Read(drawing.Id)!.NewVersions);
        Assert.Equal(2, (await fixture.Repository.ListDocumentVersionsAsync(part.Id, default)).Count);
        Assert.Equal(2, (await fixture.Repository.ListDocumentRelationsAsync(fixture.Project.Id, default)).Count);
        Assert.True(File.Exists(fixture.Queue.Under(first.Id, "input/PART.SLDPRT")));
    }

    [Fact]
    public async Task DisabledEntryRejectsDirectUploadAndQueuedCommitWithoutCreatingDocuments()
    {
        using var fixture = new Fixture(); await fixture.Setup();
        var target = new BrowserCadArchiveTarget(fixture.Project.Id);
        var staged = await fixture.Stage(target, new() { ["PART.SLDPRT"] = "part" });
        Assert.Equal(HttpStatusCode.OK, (await fixture.Client.PutAsJsonAsync("/api/browser-archives/settings", new { enabled = false })).StatusCode);
        var capability = (await fixture.Client.GetFromJsonAsync<BrowserCadArchiveCapability>($"/api/browser-archives/capabilities?projectId={fixture.Project.Id}"))!;
        Assert.False(capability.Enabled); Assert.False(capability.CanArchive);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Client.PostAsJsonAsync($"/api/browser-archives/{staged.Id}/start", new { rootPath = "PART.SLDPRT" })).StatusCode);
        using var upload = Fixture.Content(target, new() { ["PART.SLDPRT"] = "part" });
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Client.PostAsync($"/api/browser-archives/{Guid.NewGuid()}/files?projectId={fixture.Project.Id}", upload)).StatusCode);
        fixture.Tenant();
        await Assert.ThrowsAsync<PdmRuleException>(() => fixture.Service.ProcessAsync(fixture.Queue.Read(staged.Id)!, "admin", UserRole.Administrator, default));
        Assert.Empty(await fixture.Repository.ListDocumentsAsync(fixture.Project.Id, default));
        Assert.True(File.Exists(fixture.Queue.Under(staged.Id, "input/PART.SLDPRT")));
    }

    [Fact]
    public async Task ViewerAndNonOwnerCannotUseOrBypassArchiveAndOnlyCurrentAdministratorChangesSwitch()
    {
        using var fixture = new Fixture(); await fixture.Setup();
        var target = new BrowserCadArchiveTarget(fixture.Project.Id); var staged = await fixture.Stage(target, new() { ["PART.SLDPRT"] = "part" });
        await fixture.Repository.CreateUserAsync(new(Guid.NewGuid(), "viewer", "只读用户", "unused", UserRole.ProductionViewer, true), default);
        Fixture.Auth(fixture.Client, "viewer", "ProductionViewer");
        Assert.False((await fixture.Client.GetFromJsonAsync<BrowserCadArchiveCapability>($"/api/browser-archives/capabilities?projectId={fixture.Project.Id}"))!.CanArchive);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Client.GetAsync($"/api/browser-archives/{staged.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Client.PutAsJsonAsync("/api/browser-archives/settings", new { enabled = false })).StatusCode);
        using var content = Fixture.Content(target, new() { ["PART.SLDPRT"] = "part" });
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Client.PostAsync($"/api/browser-archives/{Guid.NewGuid()}/files?projectId={fixture.Project.Id}", content)).StatusCode);
        TenantContext.Set(new(Guid.NewGuid(), fixture.Company, fixture.Company, "viewer", "ProductionViewer", false, new HashSet<string>()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.SetEnabledAsync(false, "viewer", UserRole.Administrator, default));
    }

    [Fact]
    public async Task ZipTraversalIsRejectedBeforeAnyPathCanEscape()
    {
        using var fixture = new Fixture(); await fixture.Setup();
        using var bytes = new MemoryStream(); using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        { using var entry = new StreamWriter(zip.CreateEntry("../escaped.SLDPRT").Open()); entry.Write("unsafe"); }
        using var form = new MultipartFormDataContent(); form.Add(JsonContent.Create(new BrowserCadArchiveTarget(fixture.Project.Id)), "target");
        form.Add(new ByteArrayContent(bytes.ToArray()), "files", "test.zip");
        var response = await fixture.Client.PostAsync($"/api/browser-archives/{Guid.NewGuid()}/files?projectId={fixture.Project.Id}", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Contains("越界", await response.Content.ReadAsStringAsync());
        Assert.False(File.Exists(Path.Combine(fixture.Queue.Root, "escaped.SLDPRT")));
    }

    [Fact]
    public async Task IdenticalNamedCopiesFromDifferentDirectoriesStayReadyWithBothOriginalPathsAndSelectableRoot()
    {
        using var fixture = new Fixture(); await fixture.Setup();
        var files = new Dictionary<string, string> { ["first/PART.SLDPRT"] = "identical source", ["second/part.sldprt"] = "identical source" };
        var staged = await fixture.Stage(new(fixture.Project.Id), files);
        Assert.Equal("Ready", staged.State);
        Assert.Equal(files.Keys.OrderBy(path => path), staged.Files.Select(file => file.RelativePath).OrderBy(path => path));
        foreach (var file in files) Assert.Equal(file.Value, await File.ReadAllTextAsync(fixture.Queue.Under(staged.Id, "input/" + file.Key)));
        await fixture.Start(staged.Id, "second/part.sldprt");
        Assert.Equal("second/part.sldprt", fixture.Queue.Read(staged.Id)!.RootPath);
        Assert.Equal("Queued", fixture.Queue.Read(staged.Id)!.State);
        Assert.Empty(await fixture.Repository.ListDocumentsAsync(fixture.Project.Id, default));
    }

    [Fact]
    public async Task SameNameAndLengthWithDifferentShaIsRejectedAndOriginalUploadsAreRetained()
    {
        using var fixture = new Fixture(); await fixture.Setup();
        var id = Guid.NewGuid();
        var files = new Dictionary<string, string> { ["first/PART.SLDPRT"] = "source-one", ["second/PART.SLDPRT"] = "source-two" };
        Assert.Single(files.Values.Select(value => Encoding.UTF8.GetByteCount(value)).Distinct());
        using var form = Fixture.Content(new(fixture.Project.Id), files);
        var response = await fixture.Client.PostAsync($"/api/browser-archives/{id}/files?projectId={fixture.Project.Id}", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Contains("内容不同", await response.Content.ReadAsStringAsync());
        foreach (var file in files) Assert.Equal(file.Value, await File.ReadAllTextAsync(fixture.Queue.Under(id, "input/" + file.Key)));
        Assert.NotEqual("Ready", fixture.Queue.Read(id)!.State);
        Assert.Empty(await fixture.Repository.ListDocumentsAsync(fixture.Project.Id, default));
    }

    [Theory]
    [InlineData("SOURCE.stp")]
    [InlineData("SOURCE.step")]
    [InlineData("参数.txt")]
    [InlineData("设计表.xls")]
    [InlineData("设计表.xlsx")]
    public async Task NativeInventoryCompanionsAreRetrievedWithoutLocalInputsAndDoNotNeedPreviewsOrRepeatVersions(string companion)
    {
        using var fixture = new Fixture(true); await fixture.Setup(); fixture.Reader.CompanionNames = [companion];
        var files = new Dictionary<string, string> { ["ROOT.SLDASM"] = "root", ["PART.SLDPRT"] = "part",
            [companion] = "provided companion bytes", ["UNUSED.step"] = "not in native inventory" };
        var first = await fixture.Stage(new(fixture.Project.Id), files);
        Assert.DoesNotContain(first.Files, file => file.RelativePath == companion);
        Assert.Equal(2, fixture.Queue.Read(first.Id)!.CompanionFiles!.Count);
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Client.PostAsJsonAsync($"/api/browser-archives/{first.Id}/start", new { rootPath = companion })).StatusCode);
        await fixture.Process(first.Id, "ROOT.SLDASM");
        var documents = await fixture.Repository.ListDocumentsAsync(fixture.Project.Id, default); Assert.Equal(3, documents.Count);
        var root = documents.Single(doc => doc.Kind == DocumentKind.Assembly); var other = documents.Single(doc => doc.Kind == DocumentKind.Other);
        Assert.Equal(companion, other.FileName); Assert.DoesNotContain(documents, doc => doc.FileName == "UNUSED.step");
        var rootVersion = Assert.Single(await fixture.Repository.ListDocumentVersionsAsync(root.Id, default));
        Assert.True(rootVersion.ReferenceSnapshot.UsesNativeFileInventory);
        Assert.Contains(rootVersion.ReferenceSnapshot.Children, node => node.DocumentId == other.Id && node.Role == ReferenceRole.ExternalFileDependency);
        File.Delete(fixture.Queue.Under(first.Id, "input/" + companion));
        var response = await fixture.Client.PostAsJsonAsync($"/api/documents/{root.Id}/open-manifest", new { forEdit = false, releasedOnly = false, treeScopeOnly = true });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var manifest = (await response.Content.ReadFromJsonAsync<ControlledOpenManifest>())!;
        var retrieved = Assert.Single(manifest.Files, file => file.DocumentId == other.Id);
        var download = await fixture.Client.GetAsync($"/api/documents/{other.Id}/versions/{retrieved.VersionId}/file?download=true");
        Assert.True(download.IsSuccessStatusCode);
        var bytes = await download.Content.ReadAsByteArrayAsync(); Assert.Equal(files[companion], Encoding.UTF8.GetString(bytes));
        Assert.Equal(retrieved.Sha256, Convert.ToHexString(SHA256.HashData(bytes)));
        await fixture.Checkpoint(first.Id); await fixture.Resume(first.Id);
        Assert.False(fixture.Queue.Read(first.Id)!.PreviewsPending); Assert.False(fixture.Queue.Read(first.Id)!.PreviewRetryable);
        Assert.Null(Assert.Single(await fixture.Repository.ListDocumentVersionsAsync(other.Id, default)).Preview);
        var second = await fixture.Stage(new(fixture.Project.Id), files); await fixture.Process(second.Id, "ROOT.SLDASM");
        Assert.Equal(0, fixture.Queue.Read(second.Id)!.NewVersions);
        foreach (var document in documents) Assert.Single(await fixture.Repository.ListDocumentVersionsAsync(document.Id, default));
        fixture.Tenant();
        var snapshot = (await fixture.Repository.GetLatestReferenceSnapshotAsync(fixture.Project.Id, default))!;
        var generate = typeof(PdmWorkflowService).GetMethod("GenerateMechanicalBomFromSnapshotAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var bom = await (Task<BomGenerationResult>)generate.Invoke(fixture.Scope.ServiceProvider.GetRequiredService<PdmWorkflowService>(),
            [fixture.Project.Id, snapshot, "admin", CancellationToken.None, false, false])!;
        Assert.DoesNotContain(bom.StandardItems.Concat(bom.NonStandardItems).Concat(bom.UnclassifiedItems), item => item.SourceDocumentId == other.Id);
    }

    [Fact]
    public async Task VerifiedInventoryMigrationAddsOnlyCompanionAndOwningRootVersionAndRemovalKeepsIndependentFile()
    {
        using var fixture = new Fixture(); await fixture.Setup();
        var originals = new Dictionary<string, string> { ["ROOT.SLDASM"] = "root", ["P1.SLDPRT"] = "p1", ["P2.SLDPRT"] = "p2", ["P1.SLDDRW"] = "drawing" };
        var legacy = await fixture.Stage(new(fixture.Project.Id), originals); await fixture.Process(legacy.Id, "ROOT.SLDASM");
        var documents = await fixture.Repository.ListDocumentsAsync(fixture.Project.Id, default);
        var root = documents.Single(doc => doc.Kind == DocumentKind.Assembly);
        fixture.Reader.CompanionNames = [];
        var flagOnly = await fixture.Stage(new(fixture.Project.Id), originals); await fixture.Process(flagOnly.Id, "ROOT.SLDASM");
        Assert.Equal(0, fixture.Queue.Read(flagOnly.Id)!.NewVersions);
        fixture.Reader.CompanionNames = ["SOURCE.step"];
        var provided = new Dictionary<string, string>(originals) { ["SOURCE.step"] = "companion" };
        var migrated = await fixture.Stage(new(fixture.Project.Id), provided); await fixture.Process(migrated.Id, "ROOT.SLDASM");
        Assert.Equal(2, fixture.Queue.Read(migrated.Id)!.NewVersions);
        foreach (var document in documents)
            Assert.Equal(document.Id == root.Id ? 2 : 1, (await fixture.Repository.ListDocumentVersionsAsync(document.Id, default)).Count);
        var other = (await fixture.Repository.ListDocumentsAsync(fixture.Project.Id, default)).Single(doc => doc.Kind == DocumentKind.Other);
        var nativeIndexes = await fixture.Repository.ListNativeReferenceIndexesAsync(fixture.Project.Id, default);
        Assert.True(nativeIndexes.Any(index => index.DocumentId == root.Id
            && index.Children.Any(child => child.DocumentId == other.Id)),
            string.Join(" | ", nativeIndexes.Select(index => $"{index.DocumentId}/{index.Configuration}/{index.Revision?.Display}/{index.Children.Count}")));
        var opened = await fixture.Client.PostAsJsonAsync($"/api/documents/{root.Id}/open-manifest", new { treeScopeOnly = true });
        Assert.True(opened.IsSuccessStatusCode, await opened.Content.ReadAsStringAsync());
        var manifest = (await opened.Content.ReadFromJsonAsync<ControlledOpenManifest>())!;
        var source = Assert.Single(manifest.Files, file => file.DocumentId == other.Id);
        var sourceBytes = await fixture.Client.GetByteArrayAsync($"/api/documents/{other.Id}/versions/{source.VersionId}/file?download=true");
        Assert.Equal(source.Sha256, Convert.ToHexString(SHA256.HashData(sourceBytes)));
        var prefetched = await fixture.Stage(new(fixture.Project.Id), originals); await fixture.Process(prefetched.Id, "ROOT.SLDASM");
        Assert.Equal(0, fixture.Queue.Read(prefetched.Id)!.NewVersions);
        Assert.True(File.Exists(fixture.Queue.Under(prefetched.Id, "input/__plm__/SOURCE.step")));
        provided["SOURCE.step"] = "updated companion";
        var changed = await fixture.Stage(new(fixture.Project.Id), provided); await fixture.Process(changed.Id, "ROOT.SLDASM");
        Assert.Equal(2, fixture.Queue.Read(changed.Id)!.NewVersions);
        var changedRoot = (await fixture.Repository.ListDocumentVersionsAsync(root.Id, default))[0];
        Assert.Equal("W2", Assert.Single(changedRoot.ReferenceSnapshot.Children, node => node.DocumentId == other.Id).Revision?.Display);
        fixture.Reader.CompanionNames = [];
        var removed = await fixture.Stage(new(fixture.Project.Id), originals); await fixture.Process(removed.Id, "ROOT.SLDASM");
        Assert.Equal(1, fixture.Queue.Read(removed.Id)!.NewVersions);
        Assert.Equal(2, (await fixture.Repository.ListDocumentVersionsAsync(other.Id, default)).Count);
        var latest = (await fixture.Repository.ListDocumentVersionsAsync(root.Id, default))[0];
        Assert.DoesNotContain(latest.ReferenceSnapshot.Children, node => node.DocumentId == other.Id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CompanionWithoutVerifiedInventoryOrOwningReferenceIsRejectedBeforeRegistration(bool unverified)
    {
        using var fixture = new Fixture(); await fixture.Setup(); fixture.Reader.CompanionNames = ["SOURCE.step"];
        fixture.Reader.RootTransform = node => unverified
            ? node.Kind == DocumentKind.Other ? node with { UsesNativeFileInventory = false } : node
            : node.Kind == DocumentKind.Assembly ? node with { Children = node.Children.Where(child => child.Kind != DocumentKind.Other).ToArray() } : node;
        var staged = await fixture.Stage(new(fixture.Project.Id), new() { ["ROOT.SLDASM"] = "root", ["SOURCE.step"] = "companion" });
        await fixture.Start(staged.Id, "ROOT.SLDASM"); fixture.Tenant();
        var error = await Assert.ThrowsAsync<PdmRuleException>(() => fixture.Service.ProcessAsync(fixture.Queue.Read(staged.Id)!, "admin", UserRole.Administrator, default));
        Assert.Contains("原生存档清单", error.Message);
        Assert.Empty(await fixture.Repository.ListDocumentsAsync(fixture.Project.Id, default));
    }

    [Fact]
    public async Task StandardArchiveRequiresDepartmentRightsAndKeepsUnclassifiedLinesAndAvoidsNoOpVersions()
    {
        using var fixture = new Fixture(true); await fixture.Setup();
        using (var denied = Fixture.Content(new(), new() { ["PART.SLDPRT"] = "part" }))
            Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Client.PostAsync($"/api/browser-archives/{Guid.NewGuid()}/files", denied)).StatusCode);
        await fixture.Repository.CreateUserAsync(new(Guid.NewGuid(), "std", "标准化工程师", "unused", UserRole.ProcessReviewer, true), default);
        var department = await fixture.Repository.SaveOrganizationUnitAsync(new(null, fixture.Company, null, "STD-WEB", "标准化", OrganizationUnitKind.BusinessDivision, true, 1, false, true), default);
        await fixture.Repository.SetOrganizationMembershipsAsync("std", [department.Id], department.Id, default);
        Fixture.Auth(fixture.Client, "std", "ProcessReviewer");
        var files = new Dictionary<string, string> { ["PART.SLDPRT"] = "unclassified" };
        var staged = await fixture.Stage(new(DrawingType: 2), files); await fixture.Process(staged.Id, "PART.SLDPRT", "std", UserRole.ProcessReviewer);
        var standard = fixture.Scope.ServiceProvider.GetRequiredService<StandardStructureService>(); fixture.Tenant("std", UserRole.ProcessReviewer);
        var item = Assert.Single((await standard.ReadAsync("std", UserRole.ProcessReviewer, default)).Catalog.Structures);
        var revision = Assert.Single(item.Revisions); Assert.Equal(BomKind.Unclassified, Assert.Single(revision.Bom).Kind);
        var update = await fixture.Stage(new(StructureId: item.Id, ExpectedStructureRevisionId: revision.Id, DrawingType: 2), files);
        await fixture.Process(update.Id, "PART.SLDPRT", "std", UserRole.ProcessReviewer);
        fixture.Tenant("std", UserRole.ProcessReviewer);
        Assert.Single(Assert.Single((await standard.ReadAsync("std", UserRole.ProcessReviewer, default)).Catalog.Structures).Revisions);
        Assert.Equal(0, fixture.Queue.Read(update.Id)!.NewVersions);
        Assert.False(fixture.Reader.LastRequest!.ArchiveBeforePreviews);
        Assert.False(fixture.Queue.Read(update.Id)!.PreviewsPending);
    }

    [Fact]
    public async Task DeferredPreviewsAttachPartialResultsIdempotentlyToReceiptVersionInsteadOfNewLatest()
    {
        using var fixture = new Fixture(true); await fixture.Setup();
        var job = await fixture.Stage(new(fixture.Project.Id), new() { ["PART.SLDPRT"] = "part", ["PART.SLDDRW"] = "drawing" });
        await fixture.Process(job.Id, "PART.SLDPRT");
        Assert.True(fixture.Queue.Read(job.Id)!.PreviewsPending);
        Assert.Contains(fixture.Queue.Pending(), item => item.Id == job.Id);
        var documents = await fixture.Repository.ListDocumentsAsync(fixture.Project.Id, default);
        var part = documents.Single(d => d.Kind == DocumentKind.Part); var drawing = documents.Single(d => d.Kind == DocumentKind.Drawing);
        var partVersion = Assert.Single(await fixture.Repository.ListDocumentVersionsAsync(part.Id, default));
        var drawingVersion = Assert.Single(await fixture.Repository.ListDocumentVersionsAsync(drawing.Id, default));
        Assert.Null(partVersion.Preview); Assert.Null(drawingVersion.Preview);
        var nativeResponse = await fixture.Client.GetAsync($"/api/documents/{part.Id}/versions/{partVersion.Id}/file?download=true");
        Assert.True(nativeResponse.IsSuccessStatusCode); Assert.Equal("part", await nativeResponse.Content.ReadAsStringAsync());
        await fixture.Checkpoint(job.Id, ["PART.SLDDRW"], pending: true);
        await fixture.Resume(job.Id); await fixture.Resume(job.Id);
        Assert.NotNull((await fixture.Repository.FindDocumentVersionAsync(drawing.Id, drawingVersion.Id, default))!.Preview);
        Assert.Null((await fixture.Repository.FindDocumentVersionAsync(part.Id, partVersion.Id, default))!.Preview);
        fixture.Reader.TwoPhase = false;
        var changed = await fixture.Stage(new(fixture.Project.Id), new() { ["PART.SLDPRT"] = "new part bytes" });
        await fixture.Process(changed.Id, "PART.SLDPRT");
        var latest = (await fixture.Repository.ListDocumentVersionsAsync(part.Id, default))[0];
        Assert.NotEqual(partVersion.Id, latest.Id);
        await fixture.Checkpoint(job.Id); await fixture.Resume(job.Id); await fixture.Resume(job.Id);
        var saved = (await fixture.Repository.FindDocumentVersionAsync(part.Id, partVersion.Id, default))!;
        Assert.Equal(partVersion.Sha256, saved.Preview!.SourceSha256);
        Assert.Null((await fixture.Repository.FindDocumentVersionAsync(part.Id, latest.Id, default))!.Preview);
        Assert.Equal(2, (await fixture.Repository.ListDocumentVersionsAsync(part.Id, default)).Count);
        Assert.Single(await fixture.Repository.ListDocumentVersionsAsync(drawing.Id, default));
        var view = (await fixture.Client.GetFromJsonAsync<BrowserCadArchiveView>($"/api/browser-archives/{job.Id}"))!;
        Assert.Equal("Succeeded", view.State); Assert.False(view.PreviewsPending); Assert.False(view.PreviewRetryable);
        Assert.DoesNotContain(fixture.Queue.Pending(), item => item.Id == job.Id);
        Assert.True((await fixture.Client.GetAsync($"/api/documents/{drawing.Id}/versions/{drawingVersion.Id}/preview")).IsSuccessStatusCode);
    }

    [Fact]
    public async Task ExistingDocumentAcceptsPreviewWhenUploadedFileNameCaseDiffersWithoutRenaming()
    {
        using var fixture = new Fixture(); await fixture.Setup();
        var first = await fixture.Stage(new(fixture.Project.Id), new() { ["PART.SLDPRT"] = "old part" }); await fixture.Process(first.Id, "PART.SLDPRT");
        var document = Assert.Single(await fixture.Repository.ListDocumentsAsync(fixture.Project.Id, default));
        fixture.Reader.TwoPhase = true;
        var changed = await fixture.Stage(new(fixture.Project.Id), new() { ["part.sldprt"] = "changed part" }); await fixture.Process(changed.Id, "part.sldprt");
        var checkpoint = await fixture.Checkpoint(changed.Id); await fixture.Resume(changed.Id);
        Assert.False(fixture.Queue.Read(changed.Id)!.PreviewRetryable);
        var version = (await fixture.Repository.ListDocumentVersionsAsync(document.Id, default))[0];
        Assert.NotNull(version.Preview);
        await fixture.Queue.WriteAsync(changed.Id, "native-previews.json", JsonSerializer.Serialize(checkpoint with
            { Files = checkpoint.Files.Select(file => file with { FileName = file.FileName.ToUpperInvariant() }).ToArray() }, BrowserCadArchiveQueue.Json), default);
        await fixture.Resume(changed.Id);
        Assert.False(fixture.Queue.Read(changed.Id)!.PreviewRetryable);
        Assert.Equal("PART.SLDPRT", (await fixture.Repository.FindDocumentAsync(document.Id, default))!.FileName);
        Assert.Equal(2, (await fixture.Repository.ListDocumentVersionsAsync(document.Id, default)).Count);
        Assert.True((await fixture.Client.GetAsync($"/api/documents/{document.Id}/versions/{version.Id}/preview")).IsSuccessStatusCode);
    }

    [Theory]
    [InlineData("PART.SLDPRT", ReferenceNodeStatus.Suppressed, false)]
    [InlineData("PART.SLDPRT", ReferenceNodeStatus.Suppressed, true)]
    [InlineData("ROOT.SLDASM", ReferenceNodeStatus.Suppressed, false)]
    [InlineData("ROOT.SLDASM", ReferenceNodeStatus.Suppressed, true)]
    [InlineData("PART.SLDPRT", ReferenceNodeStatus.Hidden, false)]
    [InlineData("PART.SLDPRT", ReferenceNodeStatus.Hidden, true)]
    [InlineData("ROOT.SLDASM", ReferenceNodeStatus.Hidden, false)]
    [InlineData("ROOT.SLDASM", ReferenceNodeStatus.Hidden, true)]
    [InlineData("PART.SLDPRT", ReferenceNodeStatus.Lightweight, false)]
    [InlineData("PART.SLDPRT", ReferenceNodeStatus.Lightweight, true)]
    [InlineData("ROOT.SLDASM", ReferenceNodeStatus.Lightweight, false)]
    [InlineData("ROOT.SLDASM", ReferenceNodeStatus.Lightweight, true)]
    public async Task RepeatedSameBytesArchiveDoesNotUpgradeFilesWhenRepresentativeOccurrenceChanges(string rootName, ReferenceNodeStatus contextualStatus, bool contextualFirst)
    {
        using var fixture = new Fixture(); await fixture.Setup();
        var files = new Dictionary<string, string> { [rootName] = "unchanged native bytes" };
        if (rootName.EndsWith("SLDASM")) files["CHILD.SLDPRT"] = "unchanged child bytes";
        DocumentReferenceNode Context(DocumentReferenceNode node, ReferenceNodeStatus status, int quantity, string context) => node.FileName != rootName ? node
            : node with { NodeId = Guid.NewGuid(), InstancePath = "parent/" + context, Quantity = quantity,
                Configuration = node.Kind == DocumentKind.Part ? context : node.Configuration,
                Status = status, IsSuppressed = status == ReferenceNodeStatus.Suppressed };
        fixture.Reader.RootTransform = node => Context(node, contextualFirst ? contextualStatus : ReferenceNodeStatus.Normal, 3, "代表配置A");
        var first = await fixture.Stage(new(fixture.Project.Id), files); await fixture.Process(first.Id, rootName);
        fixture.Reader.RootTransform = node => Context(node, contextualFirst ? ReferenceNodeStatus.Normal : contextualStatus, 7, "代表配置B");
        var second = await fixture.Stage(new(fixture.Project.Id), files); await fixture.Process(second.Id, rootName);
        Assert.Equal(0, fixture.Queue.Read(second.Id)!.NewVersions);
        var documents = await fixture.Repository.ListDocumentsAsync(fixture.Project.Id, default);
        Assert.Equal(files.Count, documents.Count);
        foreach (var document in documents) Assert.Single(await fixture.Repository.ListDocumentVersionsAsync(document.Id, default));
    }

    [Theory]
    [InlineData("old-source")]
    [InlineData("native-sha")]
    [InlineData("preview-bytes")]
    [InlineData("preview-length")]
    [InlineData("preview-sha")]
    [InlineData("duplicate-key")]
    [InlineData("path-escape")]
    [InlineData("json")]
    public async Task InvalidPreviewCheckpointNeverChangesNativeSuccessOrAttachesUnverifiedBytes(string fault)
    {
        using var fixture = new Fixture(true); await fixture.Setup();
        var job = await fixture.Stage(new(fixture.Project.Id), new() { ["PART.SLDPRT"] = "part" }); await fixture.Process(job.Id, "PART.SLDPRT");
        var result = await fixture.Checkpoint(job.Id); var file = result.Files.Single();
        result = fault switch
        {
            "old-source" => result with { ArchiveSourceSha256 = new string('0', 64) },
            "native-sha" => result with { Files = [file with { Sha256 = new string('0', 64) }] },
            "preview-length" => result with { Files = [file with { PreviewLength = 0 }] },
            "preview-sha" => result with { Files = [file with { PreviewSha256 = new string('0', 64) }] },
            "duplicate-key" => result with { Files = [file, file] },
            "path-escape" => result with { Files = [file with { PreviewPath = "../../outside.step" }] },
            _ => result
        };
        if (fault == "preview-bytes") await File.AppendAllTextAsync(fixture.Queue.Under(job.Id, "work/" + file.PreviewPath), "corruption");
        await fixture.Queue.WriteAsync(job.Id, "native-previews.json", fault == "json" ? "{" : JsonSerializer.Serialize(result, BrowserCadArchiveQueue.Json), default);
        await fixture.Resume(job.Id);
        var state = fixture.Queue.Read(job.Id)!;
        Assert.Equal("Succeeded", state.State); Assert.Null(state.Error); Assert.True(state.PreviewRetryable); Assert.False(state.PreviewsPending);
        Assert.Contains(state.Warnings!, warning => warning.Contains("原生图档已存档"));
        var doc = Assert.Single(await fixture.Repository.ListDocumentsAsync(fixture.Project.Id, default));
        var version = Assert.Single(await fixture.Repository.ListDocumentVersionsAsync(doc.Id, default));
        Assert.Null(version.Preview); Assert.Equal("part", await File.ReadAllTextAsync(Path.Combine(fixture.Project.VaultLocation, version.StorageRelativePath)));
    }

    [Fact]
    public async Task PreviewRetryChecksOwnerAndPermissionAndNeverResubmitsNativeFiles()
    {
        using var fixture = new Fixture(true); await fixture.Setup();
        var job = await fixture.Stage(new(fixture.Project.Id), new() { ["PART.SLDPRT"] = "part" }); await fixture.Process(job.Id, "PART.SLDPRT");
        await fixture.Checkpoint(job.Id, [], success: false); await fixture.Resume(job.Id);
        var originalHash = await BrowserCadArchiveQueue.HashAsync(fixture.Queue.Under(job.Id, "native-result.json"), default);
        await fixture.Repository.CreateUserAsync(new(Guid.NewGuid(), "other", "其他管理员", "unused", UserRole.Administrator, true), default);
        Fixture.Auth(fixture.Client, "other", "Administrator");
        Assert.Equal(HttpStatusCode.Forbidden, (await fixture.Client.PostAsync($"/api/browser-archives/{job.Id}/previews/retry", null)).StatusCode);
        Fixture.Auth(fixture.Client, "admin", "Administrator");
        TenantContext.Set(new(Guid.NewGuid(), Guid.NewGuid(), fixture.Company, "admin", "Administrator", false, new HashSet<string>()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.RetryPreviewsAsync(job.Id, "admin", UserRole.Administrator, default));
        TenantContext.Clear();
        await fixture.Client.PutAsJsonAsync("/api/browser-archives/settings", new { enabled = false });
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Client.PostAsync($"/api/browser-archives/{job.Id}/previews/retry", null)).StatusCode);
        await fixture.Client.PutAsJsonAsync("/api/browser-archives/settings", new { enabled = true });
        var response = await fixture.Client.PostAsync($"/api/browser-archives/{job.Id}/previews/retry", null);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var view = (await response.Content.ReadFromJsonAsync<BrowserCadArchiveView>())!;
        Assert.Equal("Succeeded", view.State); Assert.True(view.PreviewsPending); Assert.False(view.PreviewRetryable);
        var retry = fixture.Queue.Under(job.Id, "native-preview-retry.json"); var retryText = await File.ReadAllTextAsync(retry);
        Assert.Contains(originalHash, retryText);
        await fixture.Resume(job.Id); Assert.True(fixture.Queue.Read(job.Id)!.PreviewsPending); // Ignore the previous failure until a new checkpoint arrives.
        Assert.True((await fixture.Client.PostAsync($"/api/browser-archives/{job.Id}/previews/retry", null)).IsSuccessStatusCode);
        Assert.Equal(retryText, await File.ReadAllTextAsync(retry));
        await fixture.Checkpoint(job.Id, [], pending: true); File.Delete(retry); await fixture.Resume(job.Id);
        await fixture.Checkpoint(job.Id); await fixture.Resume(job.Id);
        Assert.False(fixture.Queue.Read(job.Id)!.PreviewRetryable); Assert.False(fixture.Queue.Read(job.Id)!.PreviewsPending);
        Assert.Equal(originalHash, await BrowserCadArchiveQueue.HashAsync(fixture.Queue.Under(job.Id, "native-result.json"), default));
        var doc = Assert.Single(await fixture.Repository.ListDocumentsAsync(fixture.Project.Id, default));
        Assert.Single(await fixture.Repository.ListDocumentVersionsAsync(doc.Id, default));
        Assert.Equal(HttpStatusCode.BadRequest, (await fixture.Client.PostAsync($"/api/browser-archives/{job.Id}/previews/retry", null)).StatusCode);
    }

    [Fact]
    public async Task ExhaustedPreviewAttemptKeepsSuccessfulPdfAndOffersRetryOnlyForMissingResults()
    {
        using var fixture = new Fixture(true); await fixture.Setup();
        var job = await fixture.Stage(new(fixture.Project.Id), new() { ["PART.SLDPRT"] = "part", ["PART.SLDDRW"] = "drawing" });
        await fixture.Process(job.Id, "PART.SLDPRT");
        await fixture.Checkpoint(job.Id, ["PART.SLDDRW"], pending: true); await fixture.Resume(job.Id);
        // The worker reports native Success even when its final preview checkpoint has missing files.
        await fixture.Checkpoint(job.Id, ["PART.SLDDRW"]); await fixture.Resume(job.Id);
        Assert.True(fixture.Queue.Read(job.Id)!.PreviewRetryable); Assert.Equal("Succeeded", fixture.Queue.Read(job.Id)!.State);
        var documents = await fixture.Repository.ListDocumentsAsync(fixture.Project.Id, default);
        foreach (var doc in documents)
        {
            var version = Assert.Single(await fixture.Repository.ListDocumentVersionsAsync(doc.Id, default));
            Assert.Equal(doc.Kind == DocumentKind.Drawing, version.Preview != null);
        }
    }

    [Fact]
    public async Task CommittedReceiptRecoversAfterStateLossAndDisabledAccountCannotTurnItIntoFailure()
    {
        using var fixture = new Fixture(true); await fixture.Setup();
        var job = await fixture.Stage(new(fixture.Project.Id), new() { ["PART.SLDPRT"] = "part" }); await fixture.Process(job.Id, "PART.SLDPRT");
        var committed = fixture.Queue.Read(job.Id)!;
        await fixture.Queue.SaveAsync(committed with { State = "Committing", PreviewsPending = false, NewVersions = 0 }, default);
        await fixture.Resume(job.Id);
        Assert.Equal("Succeeded", fixture.Queue.Read(job.Id)!.State); Assert.True(fixture.Queue.Read(job.Id)!.PreviewsPending);
        await fixture.Queue.SaveAsync(committed with { State = "Committing", PreviewsPending = false, NewVersions = 0 }, default);
        await fixture.Repository.UpdateUserAsync("admin", "管理员", UserRole.Administrator, "Administrator", ["Administrator"], false, default);
        using var hosted = ActivatorUtilities.CreateInstance<BrowserCadArchiveHostedService>(fixture.Factory.Services);
        await hosted.StartAsync(default);
        for (var i = 0; i < 100 && fixture.Queue.Read(job.Id)!.State != "Succeeded"; i++) await Task.Delay(25);
        await hosted.StopAsync(default);
        var recovered = fixture.Queue.Read(job.Id)!;
        Assert.Equal("Succeeded", recovered.State); Assert.Null(recovered.Error); Assert.Equal(1, recovered.NewVersions);
        Assert.True(recovered.PreviewRetryable); Assert.False(recovered.PreviewsPending);
        var doc = Assert.Single(await fixture.Repository.ListDocumentsAsync(fixture.Project.Id, default));
        Assert.Single(await fixture.Repository.ListDocumentVersionsAsync(doc.Id, default));
    }

    [Fact]
    public async Task WorkPreviewStorageAcceptsOnlyVersionAndHashLayoutAndStillVerifiesActualBytes()
    {
        using var fixture = new Fixture(true); await fixture.Setup();
        var job = await fixture.Stage(new(fixture.Project.Id), new() { ["PART.SLDPRT"] = "part" }); await fixture.Process(job.Id, "PART.SLDPRT");
        await fixture.Checkpoint(job.Id); await fixture.Resume(job.Id);
        var doc = Assert.Single(await fixture.Repository.ListDocumentsAsync(fixture.Project.Id, default));
        var version = Assert.Single(await fixture.Repository.ListDocumentVersionsAsync(doc.Id, default)); var preview = version.Preview!;
        var storage = fixture.Scope.ServiceProvider.GetRequiredService<IFileStorage>();
        await storage.VerifyPreviewFileAsync(fixture.Project, preview, default);
        foreach (var path in new[] { $".work-previews/not-a-version/{preview.Sha256}.step", $".work-previews/{version.Id:N}/wrong.step",
            $".work-previews/{version.Id:N}/{preview.Sha256}.pdf", $".work-previews/{version.Id:N}/extra/{preview.Sha256}.step",
            $"other/{version.Id:N}/{preview.Sha256}.step" })
            await Assert.ThrowsAsync<PdmRuleException>(() => storage.VerifyPreviewFileAsync(fixture.Project, preview with { StorageRelativePath = path }, default));
        await Assert.ThrowsAsync<PdmConflictException>(() => storage.VerifyPreviewFileAsync(fixture.Project, preview with { FileLength = preview.FileLength + 1 }, default));
        await File.WriteAllTextAsync(Path.Combine(fixture.Project.VaultLocation, preview.StorageRelativePath), new string('x', checked((int)preview.FileLength)));
        await Assert.ThrowsAsync<PdmConflictException>(() => storage.VerifyPreviewFileAsync(fixture.Project, preview, default));
    }

    [Fact]
    public async Task PreviewCheckpointReaderAllowsAtomicReplacementAndReadsOneCompleteGeneration()
    {
        var path = Path.Combine(Path.GetTempPath(), "pdm-preview-share-" + Guid.NewGuid().ToString("N") + ".json");
        var replacement = path + ".next";
        const string previous = "{\"generation\":1,\"complete\":true}";
        const string next = "{\"generation\":2,\"complete\":true}";
        try
        {
            await File.WriteAllTextAsync(path, previous); await File.WriteAllTextAsync(replacement, next);
            var open = typeof(BrowserCadArchiveService).GetMethod("OpenPreviewCheckpoint", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            using (var stream = (FileStream)open.Invoke(null, [path])!)
            {
                Assert.Equal((byte)'{', stream.ReadByte());
                File.Replace(replacement, path, null);
                stream.Position = 0;
                using var reader = new StreamReader(stream);
                Assert.Equal(previous, await reader.ReadToEndAsync());
                Assert.Equal(next, await File.ReadAllTextAsync(path));
            }
            var read = typeof(BrowserCadArchiveService).GetMethod("ReadPreviewCheckpoint", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var bytes = await (Task<byte[]>)read.Invoke(null, [path, CancellationToken.None])!;
            Assert.Equal(next, Encoding.UTF8.GetString(bytes));
        }
        finally { File.Delete(path); File.Delete(replacement); }
    }

    [Theory]
    [InlineData("initial")]
    [InlineData("final")]
    [InlineData("failure-handler")]
    public async Task ExclusiveCheckpointLockDefersReadingWithoutLosingCommittedArchiveAndRecoversAfterRelease(string phase)
    {
        using var fixture = new Fixture(true); await fixture.Setup();
        var job = await fixture.Stage(new(fixture.Project.Id), new() { ["PART.SLDPRT"] = "part" }); await fixture.Process(job.Id, "PART.SLDPRT");
        await fixture.Checkpoint(job.Id);
        var path = fixture.Queue.Under(job.Id, "native-previews.json"); FileStream? exclusive = null;
        var proxy = System.Reflection.DispatchProxy.Create<IPdmRepository, PreviewReadLockProxy>();
        var hooks = (PreviewReadLockProxy)proxy; hooks.Repository = fixture.Repository;
        if (phase == "final") hooks.AfterAttach = () => exclusive ??= new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        else exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var service = ActivatorUtilities.CreateInstance<BrowserCadArchiveService>(fixture.Scope.ServiceProvider, proxy);
        try
        {
            fixture.Tenant();
            if (phase == "failure-handler")
                await service.RecordFailureAsync(fixture.Queue.Read(job.Id)!, new UnauthorizedAccessException("验证账号暂停。"), default).WaitAsync(TimeSpan.FromSeconds(3));
            else await service.ProcessAsync(fixture.Queue.Read(job.Id)!, "admin", UserRole.Administrator, default).WaitAsync(TimeSpan.FromSeconds(3));
            var state = fixture.Queue.Read(job.Id)!;
            Assert.Equal("Succeeded", state.State); Assert.Null(state.Error);
            Assert.Equal(phase != "failure-handler", state.PreviewsPending);
            Assert.Equal(phase == "failure-handler", state.PreviewRetryable);
            var document = Assert.Single(await fixture.Repository.ListDocumentsAsync(fixture.Project.Id, default));
            var version = Assert.Single(await fixture.Repository.ListDocumentVersionsAsync(document.Id, default));
            if (phase == "initial") Assert.Null(version.Preview);
        }
        finally { exclusive?.Dispose(); hooks.AfterAttach = null; TenantContext.Clear(); }
        await fixture.Resume(job.Id);
        var checkpoint = await fixture.Checkpoint(job.Id);
        await fixture.Queue.WriteAsync(job.Id, "native-previews.json", JsonSerializer.Serialize(checkpoint with { Warnings = ["锁释放后的后续清单。"] }, BrowserCadArchiveQueue.Json), default);
        await fixture.Resume(job.Id);
        var finished = fixture.Queue.Read(job.Id)!;
        Assert.Equal("Succeeded", finished.State); Assert.False(finished.PreviewsPending); Assert.False(finished.PreviewRetryable);
        Assert.Contains(finished.Warnings!, warning => warning.Contains("锁释放后的后续清单"));
        var doc = Assert.Single(await fixture.Repository.ListDocumentsAsync(fixture.Project.Id, default));
        Assert.NotNull(Assert.Single(await fixture.Repository.ListDocumentVersionsAsync(doc.Id, default)).Preview);
    }

    [Fact]
    public async Task MissingCheckpointDuringReplacementIsPendingButDirectoryAccessErrorIsNotSwallowed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pdm-preview-missing-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var read = typeof(BrowserCadArchiveService).GetMethod("ReadPreviewCheckpoint", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var missing = (Task<byte[]?>)read.Invoke(null, [Path.Combine(directory, "native-previews.json"), CancellationToken.None])!;
        Assert.Null(await missing.WaitAsync(TimeSpan.FromSeconds(3)));
        var inaccessible = (Task<byte[]?>)read.Invoke(null, [directory, CancellationToken.None])!;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inaccessible);
    }

    public class PreviewReadLockProxy : System.Reflection.DispatchProxy
    {
        public IPdmRepository Repository { get; set; } = null!;
        public Action? AfterAttach { get; set; }
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            var result = method!.Invoke(Repository, args);
            if (method.Name == nameof(IPdmRepository.AttachWorkVersionPreviewAsync)) AfterAttach?.Invoke();
            return result;
        }
    }

    [Fact]
    public async Task RewrittenIdenticalFinalCheckpointIsAcknowledgedAndDifferentCheckpointIsStillProcessed()
    {
        using var fixture = new Fixture(true); await fixture.Setup();
        var job = await fixture.Stage(new(fixture.Project.Id), new() { ["PART.SLDPRT"] = "part" }); await fixture.Process(job.Id, "PART.SLDPRT");
        var result = await fixture.Checkpoint(job.Id); await fixture.Resume(job.Id);
        var marker = fixture.Queue.Under(job.Id, "native-previews-attached.json");
        await fixture.Queue.WriteAsync(job.Id, "native-previews.json", JsonSerializer.Serialize(result, BrowserCadArchiveQueue.Json), default);
        File.SetLastWriteTimeUtc(marker, DateTime.UtcNow.AddMinutes(-1));
        Assert.Contains(fixture.Queue.Pending(), pending => pending.Id == job.Id);
        await fixture.Resume(job.Id);
        Assert.DoesNotContain(fixture.Queue.Pending(), pending => pending.Id == job.Id);
        await fixture.Queue.WriteAsync(job.Id, "native-previews.json", JsonSerializer.Serialize(result with { Warnings = ["后续预览核对已完成。"] }, BrowserCadArchiveQueue.Json), default);
        File.SetLastWriteTimeUtc(marker, DateTime.UtcNow.AddMinutes(-1));
        Assert.Contains(fixture.Queue.Pending(), pending => pending.Id == job.Id);
        await fixture.Resume(job.Id);
        Assert.Contains(fixture.Queue.Read(job.Id)!.Warnings!, warning => warning.Contains("后续预览核对已完成"));
        Assert.DoesNotContain(fixture.Queue.Pending(), pending => pending.Id == job.Id);
        var document = Assert.Single(await fixture.Repository.ListDocumentsAsync(fixture.Project.Id, default));
        Assert.Single(await fixture.Repository.ListDocumentVersionsAsync(document.Id, default));
    }

    [Fact]
    public async Task ProjectWaitingForWorkerDoesNotBlockEarlierPartialPreviewOrRewritePreparedInput()
    {
        using var fixture = new Fixture(true, fileReader: true); await fixture.Setup();
        var first = await fixture.Stage(new(fixture.Project.Id), new() { ["FIRST.SLDPRT"] = "first" });
        await fixture.Start(first.Id, "FIRST.SLDPRT"); await fixture.Resume(first.Id).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("Processing", fixture.Queue.Read(first.Id)!.State);
        await fixture.Reader.ReadAsync(fixture.Queue.DirectoryFor(first.Id),
            new(fixture.Queue.Under(first.Id, "input"), "FIRST.SLDPRT", true, true), default);
        await fixture.Resume(first.Id);
        var second = await fixture.Stage(new(fixture.Project.Id), new() { ["SECOND.SLDPRT"] = "second" });
        await fixture.Start(second.Id, "SECOND.SLDPRT"); await fixture.Resume(second.Id).WaitAsync(TimeSpan.FromSeconds(3));
        var requestPath = fixture.Queue.Under(second.Id, "native-request.json");
        File.SetLastWriteTimeUtc(requestPath, DateTime.UtcNow.AddMinutes(-40));
        var requestTime = File.GetLastWriteTimeUtc(requestPath); var request = await File.ReadAllTextAsync(requestPath);
        var prefetched = fixture.Queue.Under(second.Id, "input/__plm__/FIRST.SLDPRT");
        Assert.True(File.Exists(prefetched)); await File.WriteAllTextAsync(prefetched, "worker input must stay unchanged");
        await fixture.Checkpoint(first.Id, pending: true);
        var doc = Assert.Single(await fixture.Repository.ListDocumentsAsync(fixture.Project.Id, default));
        var version = Assert.Single(await fixture.Repository.ListDocumentVersionsAsync(doc.Id, default));
        using var hosted = ActivatorUtilities.CreateInstance<BrowserCadArchiveHostedService>(fixture.Factory.Services);
        await hosted.StartAsync(default);
        for (var i = 0; i < 200 && (await fixture.Repository.FindDocumentVersionAsync(doc.Id, version.Id, default))!.Preview == null; i++)
            await Task.Delay(25);
        await hosted.StopAsync(default);
        Assert.NotNull((await fixture.Repository.FindDocumentVersionAsync(doc.Id, version.Id, default))!.Preview);
        await fixture.Resume(second.Id).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("Processing", fixture.Queue.Read(second.Id)!.State); Assert.Null(fixture.Queue.Read(second.Id)!.Error);
        Assert.Equal(request, await File.ReadAllTextAsync(requestPath)); Assert.Equal(requestTime, File.GetLastWriteTimeUtc(requestPath));
        Assert.Equal("worker input must stay unchanged", await File.ReadAllTextAsync(prefetched));
        Assert.Single(await fixture.Repository.ListDocumentsAsync(fixture.Project.Id, default));
        Assert.Single(await fixture.Repository.ListDocumentVersionsAsync(doc.Id, default));
    }

    [Fact]
    public async Task FileReaderPollsProjectsAndStandardsWithoutChangingTheirPayloadProtocol()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pdm-reader-poll-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var reader = new FileBrowserCadReader(); var request = new BrowserCadNativeRequest(directory, "PART.SLDPRT", true, true);
        var pending = await Assert.ThrowsAnyAsync<Exception>(() => reader.ReadAsync(directory, request, default).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("BrowserCadReadPendingException", pending.GetType().Name);
        var requestPath = Path.Combine(directory, "native-request.json");
        File.SetLastWriteTimeUtc(requestPath, DateTime.UtcNow.AddHours(-1)); var requestTime = File.GetLastWriteTimeUtc(requestPath);
        var repeated = await Assert.ThrowsAnyAsync<Exception>(() => reader.ReadAsync(directory, request, default).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("BrowserCadReadPendingException", repeated.GetType().Name); Assert.Equal(requestTime, File.GetLastWriteTimeUtc(requestPath));
        var standardDirectory = Path.Combine(directory, "standard"); Directory.CreateDirectory(standardDirectory);
        var standardPending = await Assert.ThrowsAnyAsync<Exception>(() => reader.ReadAsync(standardDirectory,
            request with { ArchiveBeforePreviews = false }, default).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("BrowserCadReadPendingException", standardPending.GetType().Name);
        var standardRequest = JsonSerializer.Deserialize<BrowserCadNativeRequest>(await File.ReadAllTextAsync(Path.Combine(standardDirectory, "native-request.json")), BrowserCadArchiveQueue.Json)!;
        Assert.False(standardRequest.ArchiveBeforePreviews); Assert.True(standardRequest.Previews);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task FileReaderReturnsCompletedNativeSuccessOrFailureWithoutRepublishingRequest(bool success, bool archiveBeforePreviews)
    {
        var directory = Path.Combine(Path.GetTempPath(), "pdm-reader-result-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var expected = new BrowserCadNativeResult(success, Guid.NewGuid(), [], Error: success ? null : "原生文件无法打开。");
        await File.WriteAllTextAsync(Path.Combine(directory, "native-result.json"), JsonSerializer.Serialize(expected, BrowserCadArchiveQueue.Json));
        var actual = await new FileBrowserCadReader().ReadAsync(directory, new(directory, "PART.SLDPRT", true, archiveBeforePreviews), default);
        Assert.Equal(success, actual.Success); Assert.Equal(expected.RootKey, actual.RootKey); Assert.Equal(expected.Error, actual.Error);
        Assert.False(File.Exists(Path.Combine(directory, "native-request.json")));
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "pdm-browser-archive-test", Guid.NewGuid().ToString("N"));
        private readonly PdmApiFactory basic = new();
        internal readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Factory;
        internal readonly IServiceScope Scope;
        internal readonly HttpClient Client;
        internal IPdmRepository Repository => Scope.ServiceProvider.GetRequiredService<IPdmRepository>();
        internal BrowserCadArchiveService Service => Scope.ServiceProvider.GetRequiredService<BrowserCadArchiveService>();
        internal BrowserCadArchiveQueue Queue => Scope.ServiceProvider.GetRequiredService<BrowserCadArchiveQueue>();
        internal Project Project = null!;
        internal Guid Company = Guid.Parse("70000000-0000-0000-0000-000000000001");
        internal readonly FakeReader Reader;
        internal Fixture(bool twoPhase = false, bool fileReader = false)
        {
            Reader = new FakeReader { TwoPhase = twoPhase };
            Factory = basic.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                foreach (var hosted in services.Where(s => s.ImplementationType == typeof(BrowserCadArchiveHostedService)).ToArray()) services.Remove(hosted);
                services.RemoveAll<IBrowserCadReader>(); services.AddSingleton<IBrowserCadReader>(fileReader ? new FileBrowserCadReader() : Reader);
                services.RemoveAll<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>();
                services.AddSingleton<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
                services.AddSingleton<IOptions<PdmStorageOptions>>(Options.Create(new PdmStorageOptions { UploadTempRoot = Path.Combine(Root, "uploads"), StandardStructureRoot = Path.Combine(Root, "standard"), ChunkSizeBytes = 4 }));
            }));
            Client = Factory.CreateClient(); Scope = Factory.Services.CreateScope(); Auth(Client, "admin", "Administrator");
        }
        internal async Task Setup()
        {
            if (await Repository.FindUserAsync("admin", default) == null) await Repository.CreateUserAsync(new(Guid.NewGuid(), "admin", "管理员", "unused", UserRole.Administrator, true), default);
            Tenant(); Project = await Repository.CreateProjectAsync(new("WEB-" + Guid.NewGuid().ToString("N"), "网页存档验证", "admin", Path.Combine(Root, "vault"), Path.Combine(Root, "release")), "admin", default);
            TenantContext.Clear();
        }
        internal void Tenant(string actor = "admin", UserRole role = UserRole.Administrator) => TenantContext.Set(new(Guid.NewGuid(), Company, Company, actor, role.ToString(), false, new HashSet<string>()));
        internal static void Auth(HttpClient client, string user, string role)
        {
            var jwt = new JwtSecurityToken("upton-pdm", "upton-pdm-clients", [new(ClaimTypes.Name, user), new(ClaimTypes.Role, role)], expires: DateTime.UtcNow.AddMinutes(10),
                signingCredentials: new(new SymmetricSecurityKey(Encoding.UTF8.GetBytes("development-only-pdm-signing-key-2026")), SecurityAlgorithms.HmacSha256));
            client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
        }
        internal static MultipartFormDataContent Content(BrowserCadArchiveTarget target, Dictionary<string, string> files)
        {
            var form = new MultipartFormDataContent(); form.Add(JsonContent.Create(target), "target");
            foreach (var file in files) form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(file.Value)), "files", file.Key);
            return form;
        }
        internal async Task<BrowserCadArchiveView> Stage(BrowserCadArchiveTarget target, Dictionary<string, string> files)
        {
            TenantContext.Clear(); using var form = Content(target, files);
            var result = await Client.PostAsync($"/api/browser-archives/{Guid.NewGuid()}/files{(target.ProjectId is Guid project ? "?projectId=" + project : "")}", form);
            Assert.True(result.IsSuccessStatusCode, await result.Content.ReadAsStringAsync());
            return (await result.Content.ReadFromJsonAsync<BrowserCadArchiveView>())!;
        }
        internal async Task Process(Guid id, string root, string actor = "admin", UserRole role = UserRole.Administrator)
        {
            TenantContext.Clear(); var started = await Client.PostAsJsonAsync($"/api/browser-archives/{id}/start", new { rootPath = root });
            Assert.True(started.IsSuccessStatusCode, await started.Content.ReadAsStringAsync()); Tenant(actor, role);
            await Service.ProcessAsync(Queue.Read(id)!, actor, role, default); TenantContext.Clear();
            Assert.Equal("Succeeded", Queue.Read(id)!.State);
        }
        internal async Task Start(Guid id, string root)
        {
            TenantContext.Clear();
            var result = await Client.PostAsJsonAsync($"/api/browser-archives/{id}/start", new { rootPath = root });
            Assert.True(result.IsSuccessStatusCode, await result.Content.ReadAsStringAsync());
        }
        internal async Task Resume(Guid id)
        {
            Tenant(); await Service.ProcessAsync(Queue.Read(id)!, "admin", UserRole.Administrator, default); TenantContext.Clear();
        }
        internal async Task<BrowserCadNativeResult> Checkpoint(Guid id, string[]? names = null, bool pending = false, bool success = true)
        {
            var archive = Queue.Under(id, "native-result.json");
            var core = JsonSerializer.Deserialize<BrowserCadNativeResult>(await File.ReadAllTextAsync(archive), BrowserCadArchiveQueue.Json)!;
            var files = new List<BrowserCadNativeFile>();
            foreach (var file in core.Files)
            {
                if (file.Root.Kind == DocumentKind.Other) { files.Add(file); continue; }
                if (names != null && !names.Contains(file.FileName)) { files.Add(file); continue; }
                var extension = file.Root.Kind == DocumentKind.Drawing ? ".pdf" : ".step";
                var relative = "_previews/" + file.Key.ToString("N") + extension; var output = Queue.Under(id, "work/" + relative);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await File.WriteAllTextAsync(output, extension == ".pdf" ? "%PDF-1.7 isolated preview" : "ISO-10303-21; isolated preview");
                files.Add(file with { PreviewPath = relative, PreviewLength = new FileInfo(output).Length,
                    PreviewSha256 = await BrowserCadArchiveQueue.HashAsync(output, default) });
            }
            var result = core with { Success = success, Files = files, PreviewsPending = pending,
                ArchiveSourceSha256 = await BrowserCadArchiveQueue.HashAsync(archive, default) };
            await Queue.WriteAsync(id, "native-previews.json", JsonSerializer.Serialize(result, BrowserCadArchiveQueue.Json), default);
            return result;
        }
        public void Dispose() { TenantContext.Clear(); Scope.Dispose(); Client.Dispose(); Factory.Dispose(); basic.Dispose(); }
    }

    private sealed class FakeReader : IBrowserCadReader
    {
        internal bool TwoPhase;
        internal BrowserCadNativeRequest? LastRequest;
        internal Func<DocumentReferenceNode, DocumentReferenceNode>? RootTransform;
        internal string[]? CompanionNames;
        public async Task<BrowserCadNativeResult> ReadAsync(string directory, BrowserCadNativeRequest request, CancellationToken ct)
        {
            LastRequest = request;
            var input = Directory.GetFiles(request.Directory, "*", SearchOption.AllDirectories).Where(p => new[] { ".sldasm", ".sldprt", ".slddrw" }.Contains(Path.GetExtension(p).ToLowerInvariant())
                || CompanionNames?.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase) == true).ToArray();
            var paths = input.ToDictionary(p => Path.GetFileName(p)!, StringComparer.OrdinalIgnoreCase); var rootName = Path.GetFileName(request.RootPath);
            Guid Key(string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes(name)).Take(16).ToArray());
            DocumentKind Kind(string name) => name.EndsWith("SLDASM", StringComparison.OrdinalIgnoreCase) ? DocumentKind.Assembly
                : name.EndsWith("SLDPRT", StringComparison.OrdinalIgnoreCase) ? DocumentKind.Part
                : name.EndsWith("SLDDRW", StringComparison.OrdinalIgnoreCase) ? DocumentKind.Drawing : DocumentKind.Other;
            DocumentReferenceNode Node(string name) => new(Key("node-" + name), Key(name), name, name, Path.GetFileNameWithoutExtension(name), Kind(name), "Default", 1, ReferenceNodeStatus.Normal, RevisionLabel.InitialWork(), null, [])
                { TreeScopeOnly = CompanionNames == null, UsesNativeFileInventory = CompanionNames != null,
                    Role = Kind(name) == DocumentKind.Other ? ReferenceRole.ExternalFileDependency : ReferenceRole.Component };
            var partNames = paths.Keys.Where(n => Kind(n) == DocumentKind.Part).ToArray();
            var used = paths.Keys.Where(n => n == rootName || Kind(n) is DocumentKind.Part or DocumentKind.Drawing or DocumentKind.Other).ToArray();
            var result = new List<BrowserCadNativeFile>();
            foreach (var name in used)
            {
                var source = paths[name]; var relative = Path.GetRelativePath(request.Directory, source).Replace('\\', '/'); var output = Path.Combine(directory, "work", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.Copy(source, output, true); var text = await File.ReadAllTextAsync(source, ct);
                var sha = await BrowserCadArchiveQueue.HashAsync(output, ct); var node = Node(name);
                if (Kind(name) == DocumentKind.Assembly) node = node with { Children = partNames.Select(p => Node(p) with { InstancePath = name + "/" + p }).ToArray() };
                if (Kind(name) == DocumentKind.Drawing) node = node with { Children = partNames.Take(1).Select(Node).ToArray() };
                if (name == rootName && Kind(name) != DocumentKind.Other)
                    node = node with { Children = node.Children.Concat(paths.Keys.Where(n => Kind(n) == DocumentKind.Other).Select(Node)).ToArray() };
                var properties = new Dictionary<string, string?> { ["SourceFileSha256"] = sha, ["物料名称"] = node.DisplayName, ["物料编码"] = node.DisplayName, ["单位"] = "件", ["材质"] = "钢" };
                if (text != "unclassified") properties["物料分类"] = "非标件";
                string? preview = null;
                if (Kind(name) == DocumentKind.Drawing)
                {
                    preview = "_previews/" + Key(name).ToString("N") + ".pdf";
                    var previewFile = Path.Combine(directory, "work", preview); Directory.CreateDirectory(Path.GetDirectoryName(previewFile)!);
                    await File.WriteAllTextAsync(previewFile, "%PDF-1.7 isolated work preview", ct);
                }
                result.Add(new(Key(name), relative, name, new FileInfo(output).Length, sha, RootTransform?.Invoke(node) ?? node, properties, preview));
            }
            if (TwoPhase && request.ArchiveBeforePreviews)
            {
                var frozen = result.Select(file =>
                {
                    var relative = "_archive/" + file.FileName; var copy = Path.Combine(directory, "work", relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(copy)!); File.Copy(Path.Combine(directory, "work", file.RelativePath), copy, true);
                    return file with { RelativePath = relative, InputRelativePath = file.RelativePath, PreviewPath = null };
                }).ToArray();
                var core = new BrowserCadNativeResult(true, Key(rootName), frozen, PreviewsPending: true);
                await File.WriteAllTextAsync(Path.Combine(directory, "native-result.json"), JsonSerializer.Serialize(core, BrowserCadArchiveQueue.Json), ct);
                return core;
            }
            return new(true, Key(rootName), result);
        }
    }
}
