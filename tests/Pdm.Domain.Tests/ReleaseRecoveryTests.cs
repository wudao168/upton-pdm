using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class ReleaseRecoveryTests
{
    [Fact]
    public async Task UnfinishedConversionNotifiesDivisionAssistantsAndAllWorkflowPeopleOnceThenReportsRecovery()
    {
        using var f = await Fixture.CreateAsync();
        var company = (await f.Repository.GetOrganizationDirectoryAsync(default)).Organizations.First(item => item.IsActive).Id;
        var division = await f.Repository.SaveOrganizationUnitAsync(new(null, company, null, "PREVIEW-T1", "本事业部", OrganizationUnitKind.BusinessDivision, true, 0), default);
        var mechanical = await f.Repository.SaveOrganizationUnitAsync(new(null, company, division.Id, "PREVIEW-M", "机械组", OrganizationUnitKind.Department, true, 0), default);
        var assistants = await f.Repository.SaveOrganizationUnitAsync(new(null, company, division.Id, "PREVIEW-A", "技术助理组", OrganizationUnitKind.Department, true, 1), default);
        var other = await f.Repository.SaveOrganizationUnitAsync(new(null, company, null, "PREVIEW-T2", "其他事业部", OrganizationUnitKind.BusinessDivision, true, 1), default);
        var names = new[] { "submitter", "assigned-reviewer", "actual-reviewer", "assistant", "mixed-admin", "other-assistant", "foreign-reviewer", "inactive-reviewer", "system-admin" };
        foreach (var name in names)
        {
            var roles = name == "mixed-admin" ? new[] { "Administrator", "TechnicalAssistant" }
                : name.Contains("assistant") ? new[] { "TechnicalAssistant" }
                : name == "system-admin" ? new[] { "Administrator" } : new[] { "Engineer" };
            await f.Repository.CreateUserAsync(new(Guid.NewGuid(), name, name, "unused", UserRole.Engineer, name != "inactive-reviewer",
                RoleCode: roles[0], CompanyId: name == "foreign-reviewer" ? Guid.NewGuid() : company, RoleCodes: roles), default);
        }
        await f.Repository.SetOrganizationMembershipsAsync("submitter", [mechanical.Id], mechanical.Id, default);
        foreach (var name in new[] { "assistant", "mixed-admin" })
            await f.Repository.SetOrganizationMembershipsAsync(name, [assistants.Id], assistants.Id, default);
        await f.Repository.SetOrganizationMembershipsAsync("other-assistant", [other.Id], other.Id, default);
        var projects = (ConcurrentDictionary<Guid, Project>)typeof(InMemoryPdmRepository).GetField("projects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Repository)!;
        f.Project = projects[f.Project.Id] = f.Project with { OrganizationId = company, PrimaryProjectManager = "submitter" };
        ApprovalTask Task(string assigned, string? actual, int order) => new(Guid.NewGuid(), f.Package.Id, ApprovalStage.MechanicalEngineer,
            assigned, actual, ApprovalDecision.Approved, null, DateTimeOffset.UtcNow) { StepOrder = order };
        var package = f.Package with { PreviewState = ReleasePreviewState.Failed, PreviewAttempts = 5, PreviewError = "转换程序无法启动",
            ApprovalTasks = [Task("submitter", "submitter", 1), Task("assigned-reviewer", "actual-reviewer", 2),
                Task("foreign-reviewer", null, 3), Task("inactive-reviewer", null, 4), Task("system-admin", null, 5)] };
        ((ConcurrentDictionary<Guid, ReleasePackage>)typeof(InMemoryPdmRepository).GetField("packages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Repository)!)[package.Id] = package;
        await f.Service.ReconcileDeliveryAsync(package.Id, default);
        foreach (var name in new[] { "submitter", "assigned-reviewer", "actual-reviewer", "assistant", "mixed-admin" })
        {
            var notice = Assert.Single(await f.Repository.ListUserNotificationsAsync(name, 100, default));
            Assert.Equal(package.Id, notice.ReleasePackageId);
            Assert.Contains(package.Number, notice.Content);
            Assert.Contains("重试转图", notice.Content);
            Assert.Contains("自动重试已暂停", notice.Title);
            await f.Repository.MarkUserNotificationReadAsync(notice.Id, name, DateTimeOffset.UtcNow, default);
        }
        await f.Service.ReconcileDeliveryAsync(package.Id, default);
        Assert.Single(await f.Repository.ListUserNotificationsAsync("assistant", 100, default));
        foreach (var name in new[] { "other-assistant", "foreign-reviewer", "inactive-reviewer", "system-admin" })
            Assert.Empty(await f.Repository.ListUserNotificationsAsync(name, 100, default));
        Assert.Equal(0, f.Converter.Calls);
        package = await f.Repository.MarkReleasePreviewStateAsync(package.Id, ReleasePreviewState.Pending, null, 0, DateTimeOffset.UtcNow, default);
        Assert.True(await f.Service.ProcessAsync(package, default));
        var recovered = await f.Repository.ListUserNotificationsAsync("assistant", 100, default);
        Assert.Equal(2, recovered.Count);
        Assert.Contains(recovered, notice => notice.Title == "图纸转换已完成");
        await f.Service.ReconcileDeliveryAsync(package.Id, default);
        Assert.Equal(2, (await f.Repository.ListUserNotificationsAsync("assistant", 100, default)).Count);
    }

    [Fact]
    public async Task HistoricalBomOnlyConversionNotificationsAreHiddenWithoutLosingArchiveMessages()
    {
        using var fixture = await Fixture.CreateAsync();
        var reader = new UserAccount(Guid.NewGuid(), "preview-reader", "发布处理人", "unused", UserRole.Administrator, true,
            RoleCode: "developer", CompanyId: fixture.Project.OrganizationId);
        await fixture.Repository.CreateUserAsync(reader, default);
        var standard = fixture.Package with { Id = Guid.NewGuid(), Scope = ReleaseScope.StandardFormal };
        await fixture.Repository.CreateReleasePackageAsync(standard, default);
        UserNotification Notice(Guid id, string category, string key) => new(Guid.NewGuid(), reader.Username, category, "通知", "详情",
            fixture.Project.Id, id, key, DateTimeOffset.UtcNow, null);
        await fixture.Repository.CreateUserNotificationsAsync([
            Notice(standard.Id, "ReleasePreviewResult", "wrong-standard-conversion"),
            Notice(fixture.Package.Id, "ReleasePreviewResult", "nonstandard-conversion"),
            Notice(standard.Id, "ReleaseDeliveryResult", "standard-archive")], default);
        var messages = await fixture.Repository.ListUserNotificationsAsync(reader.Username, 100, default);
        Assert.DoesNotContain(messages, message => message.SourceKey == "wrong-standard-conversion");
        Assert.Contains(messages, message => message.SourceKey == "nonstandard-conversion");
        Assert.Contains(messages, message => message.SourceKey == "standard-archive");
    }

    [Theory]
    [InlineData(ReleaseScope.StandardFormal)]
    [InlineData(ReleaseScope.StandardSupplement)]
    [InlineData(ReleaseScope.StandardLongLead)]
    [InlineData(ReleaseScope.ElectricalFormal)]
    [InlineData(ReleaseScope.ElectricalSupplement)]
    [InlineData(ReleaseScope.ElectricalLongLead)]
    public async Task BomOnlyScopesNeverConvertEvenWithHistoricalSnapshotsAndPendingState(ReleaseScope scope)
    {
        using var fixture = await Fixture.CreateAsync();
        var package = fixture.Package with { Id = Guid.NewGuid(), Scope = scope, PreviewAttempts = 5, PreviewError = "历史转图异常" };
        await fixture.Repository.CreateReleasePackageAsync(package, default);
        Assert.Empty(await fixture.Repository.ListReleasePreviewSourcesAsync(package.Id, default));
        Assert.DoesNotContain(await fixture.Repository.ListReleasePackagesAwaitingPreviewAsync(5, 100, default), item => item.Id == package.Id);
        await fixture.Service.ReconcileDeliveryAsync(package.Id, default);
        var saved = (await fixture.Repository.FindReleasePackageAsync(package.Id, default))!;
        Assert.Equal(ReleasePreviewState.None, saved.PreviewState);
        Assert.Equal(0, saved.PreviewAttempts); Assert.Null(saved.PreviewError);
        Assert.True(await fixture.Service.ProcessAsync(saved, default));
        Assert.Equal(0, fixture.Converter.Calls);
    }

    [Fact]
    public async Task InspectionPreservesPausedAttemptLimitAndDoesNotPromiseUnscheduledRetries()
    {
        using var fixture = await Fixture.CreateAsync();
        await fixture.Repository.MarkReleasePreviewStateAsync(fixture.Package.Id, ReleasePreviewState.Pending, "原始失败原因", 5, DateTimeOffset.UtcNow, default);
        await fixture.Service.ReconcileDeliveryAsync(fixture.Package.Id, default);
        var saved = (await fixture.Repository.FindReleasePackageAsync(fixture.Package.Id, default))!;
        Assert.Equal(ReleasePreviewState.Failed, saved.PreviewState);
        Assert.Equal("原始失败原因", saved.PreviewError);
        Assert.Equal(0, await fixture.Service.ProcessPendingAsync(default));
        Assert.False(await fixture.Service.ProcessAsync(saved, default));
        Assert.Equal(0, fixture.Converter.Calls);
    }

    [Fact]
    public async Task UnavailableServerWaitsWithoutSpendingAttemptsThenAutomaticallyCompletes()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Converter.Failure = new PreviewServerUnavailableException("转图服务器离线，等待恢复。");
        for (var index = 0; index < 3; index++)
        {
            await fixture.Service.ProcessPendingAsync(default);
            var saved = (await fixture.Repository.FindReleasePackageAsync(fixture.Package.Id, default))!;
            Assert.Equal(ReleasePreviewState.WaitingForServer, saved.PreviewState);
            Assert.Equal(0, saved.PreviewAttempts);
        }
        fixture.Converter.Failure = null;
        await fixture.Service.ProcessPendingAsync(default);
        var completed = (await fixture.Repository.FindReleasePackageAsync(fixture.Package.Id, default))!;
        Assert.Equal(ReleasePreviewState.Succeeded, completed.PreviewState);
        Assert.Equal(1, completed.PreviewAttempts);
    }

    [Fact]
    public async Task ActualConversionFailuresStopAtFiveAndInspectionCannotResetTheLimit()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Converter.Failure = new PdmRuleException("图档转换失败，请核对源文件。");
        for (var index = 0; index < 7; index++) await fixture.Service.ProcessPendingAsync(default);
        Assert.Equal(5, fixture.Converter.Calls);
        var failed = (await fixture.Repository.FindReleasePackageAsync(fixture.Package.Id, default))!;
        Assert.Equal(ReleasePreviewState.Failed, failed.PreviewState);
        await fixture.Service.ReconcileDeliveryAsync(failed.Id, default);
        Assert.Equal(ReleasePreviewState.Failed, (await fixture.Repository.FindReleasePackageAsync(failed.Id, default))!.PreviewState);
        Assert.Equal(0, await fixture.Service.ProcessPendingAsync(default));
    }

    [Fact]
    public async Task ReturningFailedPublicationPreservesItsFilesAndClearsTheOldOutputForReediting()
    {
        using var fixture = await Fixture.CreateAsync();
        var failed = fixture.Package with { Id = Guid.NewGuid(), Number = "RP-FAILED-DRAFT", State = ReleasePackageState.PublishFailed,
            PublishedAt = null, PublishedPath = null };
        var output = Path.Combine(fixture.Project.ReleaseLocation, failed.Number);
        var staging = Path.Combine(fixture.Project.VaultLocation, ".release-staging", failed.Number);
        Directory.CreateDirectory(output);
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(Path.Combine(output, "original.txt"), "失败发布资料");
        await File.WriteAllTextAsync(Path.Combine(staging, "draft.txt"), "旧草稿");
        await new AtomicReleasePackagePublisher(TimeProvider.System).DiscardDraftAsync(failed, fixture.Project, default);
        Assert.False(Directory.Exists(output));
        Assert.False(Directory.Exists(staging));
        var backup = Assert.Single(Directory.GetDirectories(Path.Combine(fixture.Project.ReleaseLocation, ".withdrawn-releases")));
        Assert.Equal("失败发布资料", await File.ReadAllTextAsync(Path.Combine(backup, "original.txt")));
        Assert.True(Directory.Exists(fixture.Package.PublishedPath));
    }

    [Fact]
    public async Task QuantityOnlyPdfFillingDoesNotCallConverterAndKeepsOriginalPublicationBytes()
    {
        using var fixture = await Fixture.CreateAsync();
        Assert.True(await fixture.Service.ProcessAsync(fixture.Package, default));
        var originals = fixture.Versions.Values.Where(version => version.ReleasePackageId == fixture.Package.Id).ToArray();
        var bytes = originals.ToDictionary(version => version.DocumentId, version => File.ReadAllBytes(Path.Combine(fixture.Project.VaultLocation, version.Preview!.StorageRelativePath)));
        var count = fixture.Versions.Count;
        var path = Path.Combine(fixture.Project.ReleaseLocation, "RP-QUANTITY-FILL"); Directory.CreateDirectory(path);
        var package = fixture.Package with { Id = Guid.NewGuid(), Number = "RP-QUANTITY-FILL", Scope = ReleaseScope.NonStandardSupplement, PublishedPath = path,
            VersionBindings = originals.Select(version => new ReleaseVersionBinding(version.DocumentId, version.Id, version.Revision.Display, version.Sha256, true, "正式图纸.SLDDRW", false)).ToArray(),
            PdfTitleBlockSnapshot = new(["quantity"], originals.ToDictionary(version => version.DocumentId,
                version => new PdfDrawingTitleBlockSnapshot(version.Id, version.Sha256, new Dictionary<string, string> { ["quantity"] = "2" }, [])), true) };
        await fixture.Repository.CreateReleasePackageAsync(package, default);
        var stamp = new CountingReuseStamp();
        var service = new ReleasePreviewService(fixture.Repository, fixture.Converter, TimeProvider.System, pdfReleaseStamp: stamp);
        Assert.True(await service.ProcessAsync(package, default));
        Assert.Equal(1, fixture.Converter.Calls);
        Assert.Equal(originals.Length, stamp.Reissues);
        var publishing = package with { Id = Guid.NewGuid(), Number = "RP-PUBLISH-REUSE", State = ReleasePackageState.Publishing, PublishedPath = null };
        await fixture.Repository.CreateReleasePackageAsync(publishing, default);
        var publisher = new AtomicReleasePackagePublisher(TimeProvider.System, fixture.Converter, fixture.Repository, stamp);
        await publisher.PrepareAsync(publishing, fixture.Project, default);
        var publication = await publisher.PublishAsync(publishing, fixture.Project, await fixture.Repository.ListReleasePreviewSourcesAsync(publishing.Id, default), default);
        Assert.Null(publication.PreviewError);
        Assert.Equal(originals.Length, publication.Previews.Count);
        Assert.Equal(1, fixture.Converter.Calls);
        Assert.Equal(originals.Length * 2, stamp.Reissues);
        Assert.Equal(count, fixture.Versions.Count);
        foreach (var original in originals)
            Assert.Equal(bytes[original.DocumentId], await File.ReadAllBytesAsync(Path.Combine(fixture.Project.VaultLocation, original.Preview!.StorageRelativePath)));
        Assert.True(await service.ProcessAsync(package, default));
        Assert.Equal(1, fixture.Converter.Calls);
        Assert.Equal(originals.Length * 2, stamp.Reissues);
    }

    private sealed class CountingReuseStamp : IPdfReleaseStampProcessor
    {
        public int Reissues { get; private set; }
        public bool IsStamped(string path, Guid packageId, Guid sourceVersionId) => true;
        public Task ApplyAsync(ReleasePackage package, Project project, ReleasePreviewSource source, string pdfPath, CancellationToken ct)
            => throw new InvalidOperationException("数量增补不得走原始转图路径。");
        public async Task ReissueAsync(ReleasePackage package, Project project, ReleasePreviewSource source, string pdfPath, CancellationToken ct)
        {
            Reissues++;
            await File.AppendAllTextAsync(pdfPath, "|本次下发2", ct);
        }
    }

    [Fact]
    public async Task QuantityOnlyPublicationReusesFormalDrawingsAndUpdatesDeliveryWithoutNewVersions()
    {
        using var fixture = await Fixture.CreateAsync();
        Assert.True(await fixture.Service.ProcessAsync(fixture.Package, default));
        var originals = fixture.Versions.Values.Where(version => version.ReleasePackageId == fixture.Package.Id).ToArray();
        var count = fixture.Versions.Count;
        var path = Path.Combine(fixture.Project.ReleaseLocation, "RP-QUANTITY"); Directory.CreateDirectory(path);
        var package = fixture.Package with { Id = Guid.NewGuid(), Number = "RP-QUANTITY", Scope = ReleaseScope.NonStandardSupplement,
            PublishedAt = DateTimeOffset.UtcNow.AddSeconds(1), PublishedPath = path,
            VersionBindings = originals.Select(version => new ReleaseVersionBinding(version.DocumentId, version.Id, version.Revision.Display,
                version.Sha256, true, "已有正式图纸.SLDDRW", false, "A", "A")).ToArray() };
        await fixture.Repository.CreateReleasePackageAsync(package, default);
        Assert.True(await fixture.Service.ProcessAsync(package, default));
        Assert.Equal(1, fixture.Converter.Calls);
        Assert.Equal(count, fixture.Versions.Count);
        Assert.All(await fixture.Service.ListItemsAsync(package.Id, default), item => Assert.True(item.Succeeded));
        Assert.True(await fixture.Service.ProcessAsync(package, default));
        Assert.Equal(1, fixture.Converter.Calls); Assert.Equal(count, fixture.Versions.Count);
        var drawings = await new ProductionDrawingService(fixture.Repository).ListAsync("admin", UserRole.Administrator, false, null, default);
        Assert.All(drawings.Where(item => originals.Any(version => version.DocumentId == item.DocumentId)), item => Assert.Equal(package.Id, item.ReleasePackageId));
        var changed = await fixture.Repository.UpdatePublishedDrawingDeliveryAsync(package.Id, originals[0].DocumentId,
            new("Priority", new DateOnly(2026, 10, 20)), "admin", default);
        Assert.Equal("Priority", changed.DrawingDeliveryOverrides[originals[0].DocumentId].Priority);
        Assert.Equal(count, fixture.Versions.Count);
        await ReleaseDeliveryFiles.VerifyChecksumsAsync(path, default);
    }

    [Fact]
    public async Task MissingReusedPreviewCanBeRebuiltAgainstItsPinnedFormalSourceWithoutCreatingACadVersion()
    {
        using var fixture = await Fixture.CreateAsync();
        Assert.True(await fixture.Service.ProcessAsync(fixture.Package, default));
        var original = fixture.Versions.Values.First(version => version.ReleasePackageId == fixture.Package.Id);
        File.Delete(StorageLocationPolicy.ResolveUnder(fixture.Project.VaultLocation, original.Preview!.StorageRelativePath));
        var path = Path.Combine(fixture.Project.ReleaseLocation, "RP-REUSE-RECOVERY"); Directory.CreateDirectory(path);
        var package = fixture.Package with { Id = Guid.NewGuid(), Number = "RP-REUSE-RECOVERY", Scope = ReleaseScope.NonStandardSupplement, PublishedPath = path,
            VersionBindings = [new(original.DocumentId, original.Id, original.Revision.Display, original.Sha256, true, "复用图.SLDDRW", false)] };
        await fixture.Repository.CreateReleasePackageAsync(package, default);
        var count = fixture.Versions.Count;
        Assert.True(await fixture.Service.ProcessAsync(package, default));
        Assert.Equal(count, fixture.Versions.Count);
        Assert.Equal(original.Sha256, fixture.Versions[original.Id].Sha256);
        Assert.True(Assert.Single(await fixture.Service.ListItemsAsync(package.Id, default)).Succeeded);
    }

    [Fact]
    public async Task DeliveryFailureStaysPendingAndRetryCopiesExistingArtifactWithoutReconversion()
    {
        using var fixture = await Fixture.CreateAsync();
        var blockingPath = Path.Combine(fixture.Package.PublishedPath!, "previews");
        await File.WriteAllTextAsync(blockingPath, "目录暂时不可写");
        Assert.False(await fixture.Service.ProcessAsync(fixture.Package, default));
        var failed = await fixture.Repository.FindReleasePackageAsync(fixture.Package.Id, default);
        Assert.Equal(ReleasePreviewState.Pending, failed!.PreviewState);
        Assert.Contains("检查磁盘空间", failed.PreviewError);
        Assert.All(await fixture.Service.ListItemsAsync(fixture.Package.Id, default), item => Assert.False(item.Succeeded));
        File.Delete(blockingPath);
        Assert.True(await fixture.Service.ProcessAsync(failed, default));
        Assert.Equal(1, fixture.Converter.Calls);
        Assert.All(await fixture.Service.ListItemsAsync(fixture.Package.Id, default), item => Assert.True(item.Succeeded));
        await ReleaseDeliveryFiles.VerifyChecksumsAsync(fixture.Package.PublishedPath!, default);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Package.PublishedPath!, "manifest.json")));
        Assert.All(manifest.RootElement.GetProperty("files").EnumerateArray(), path => Assert.True(File.Exists(Path.Combine(fixture.Package.PublishedPath!, path.GetString()!))));
    }

    [Fact]
    public async Task StartupReconciliationRequeuesPreviouslySuccessfulButMissingDelivery()
    {
        using var fixture = await Fixture.CreateAsync();
        Assert.True(await fixture.Service.ProcessAsync(fixture.Package, default));
        var item = (await fixture.Service.ListItemsAsync(fixture.Package.Id, default))[0];
        var version = (await fixture.Repository.ListDocumentVersionsAsync(item.DocumentId, default)).Single();
        File.Delete(ReleaseDeliveryFiles.PreviewPath(fixture.Package.PublishedPath!, version.Preview!));
        await fixture.Service.ReconcileDeliveryAsync(fixture.Package.Id, default);
        var package = await fixture.Repository.FindReleasePackageAsync(fixture.Package.Id, default);
        Assert.Equal(ReleasePreviewState.Pending, package!.PreviewState);
        Assert.Equal(0, package.PreviewAttempts);
        Assert.True(await fixture.Service.ProcessAsync(package, default));
        Assert.Equal(1, fixture.Converter.Calls);
    }

    [Fact]
    public async Task MissingVaultFileIsNotTreatedAsCompletedAndCanBeReconverted()
    {
        using var fixture = await Fixture.CreateAsync();
        Assert.True(await fixture.Service.ProcessAsync(fixture.Package, default));
        var item = (await fixture.Service.ListItemsAsync(fixture.Package.Id, default))[0];
        File.Delete(Path.Combine(fixture.Project.VaultLocation, item.PreviewRelativePath));
        Assert.False((await fixture.Service.ListItemsAsync(fixture.Package.Id, default))[0].Succeeded);
        Assert.True(await fixture.Service.ProcessAsync(fixture.Package, default));
        Assert.Equal(2, fixture.Converter.Calls);
        Assert.Equal([item.DocumentId], fixture.Converter.LastIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IntactPreviewWithWrongSourceOrFormatIsReconverted(bool wrongFormat)
    {
        using var fixture = await Fixture.CreateAsync();
        Assert.True(await fixture.Service.ProcessAsync(fixture.Package, default));
        var item = (await fixture.Service.ListItemsAsync(fixture.Package.Id, default))[0];
        var version = fixture.Versions.Values.Single(value => value.DocumentId == item.DocumentId && value.ReleasePackageId == fixture.Package.Id);
        var original = version with { Id = Guid.NewGuid(), StorageRelativePath = "original-work.slddrw", Status = DocumentVersionStatus.Work, ReleasePackageId = null };
        fixture.Versions[original.Id] = original;
        fixture.Versions[version.Id] = version with { SourceVersionId = original.Id,
            Preview = wrongFormat ? version.Preview! with { Format = DocumentPreviewFormat.Step }
                : version.Preview! with { SourceSha256 = "WRONG-SOURCE" } };
        Assert.False((await fixture.Service.ListItemsAsync(fixture.Package.Id, default)).First(value => value.DocumentId == item.DocumentId).Succeeded);
        var result = await fixture.Service.RetryItemsAsync(fixture.Package.Id, [item.DocumentId], "admin", default);
        Assert.True(result.First(value => value.DocumentId == item.DocumentId).Succeeded);
        Assert.Equal(2, fixture.Converter.Calls);
        Assert.Equal([item.DocumentId], fixture.Converter.LastIds);
    }

    [Fact]
    public async Task ObsoletePreviewHashesAreNotRearchivedOrAddedToCurrentManifest()
    {
        using var fixture = await Fixture.CreateAsync();
        var files = new InMemoryProjectFileRepository(TimeProvider.System);
        var service = new ReleasePreviewService(fixture.Repository, fixture.Converter, TimeProvider.System,
            new ReleaseDeliveryArchiveService(fixture.Repository, files, TimeProvider.System));
        Assert.True(await service.ProcessAsync(fixture.Package, default));
        var item = (await service.ListItemsAsync(fixture.Package.Id, default))[0];
        var version = fixture.Versions.Values.Single(value => value.DocumentId == item.DocumentId && value.ReleasePackageId == fixture.Package.Id);
        var oldPreview = version.Preview!;
        var oldTarget = ReleaseDeliveryFiles.PreviewPath(fixture.Package.PublishedPath!, oldPreview);
        var bytes = System.Text.Encoding.UTF8.GetBytes("replacement-preview");
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var relative = Path.Combine(".release-previews", fixture.Package.Id.ToString("N"), hash, Path.GetFileName(oldPreview.StorageRelativePath));
        var path = Path.Combine(fixture.Project.VaultLocation, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);
        fixture.Versions[version.Id] = version with { Preview = oldPreview with { StorageRelativePath = relative, Sha256 = hash, FileLength = bytes.Length } };
        await File.WriteAllTextAsync(oldTarget, "corrupt historical preview");
        await service.RetryItemsAsync(fixture.Package.Id, [item.DocumentId], "admin", default);
        await ReleaseDeliveryFiles.VerifyChecksumsAsync(fixture.Package.PublishedPath!, default);
        var activeFiles = await ReleaseDeliveryFiles.ManifestFilesAsync(fixture.Package.PublishedPath!, default);
        Assert.DoesNotContain(oldTarget, activeFiles);
        Assert.True(File.Exists(oldTarget));
        Assert.DoesNotContain(Path.GetRelativePath(fixture.Package.PublishedPath!, oldTarget).Replace('\\', '/'),
            await File.ReadAllTextAsync(Path.Combine(fixture.Package.PublishedPath!, "checksums.sha256")));
        var folder = (await fixture.Repository.FindProjectFolderByKeyAsync(fixture.Project.Id, "mechanical.release", default))!;
        var archived = await files.ListAsync(folder.RootProjectId, folder.Id, false, default);
        var current = Assert.Single(archived, file => file.FileName == Path.GetFileName(relative));
        Assert.Equal(hash, current.CurrentVersion!.Sha256);
        Assert.Equal(2, (await files.ListVersionsAsync(current.Id, default)).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalReadbackFailureDoesNotLeaveManualRetryRunning(bool diagnosticFailure)
    {
        using var fixture = await Fixture.CreateAsync();
        var repository = DispatchProxy.Create<IPdmRepository, ReadbackFailureProxy>();
        var proxy = (ReadbackFailureProxy)repository;
        proxy.Inner = fixture.Repository;
        proxy.PublishedPath = fixture.Package.PublishedPath!;
        proxy.FailDiagnostic = diagnosticFailure;
        var service = new ReleasePreviewService(repository, fixture.Converter, TimeProvider.System);
        var ids = (await fixture.Repository.ListReleasePreviewSourcesAsync(fixture.Package.Id, default)).Select(item => item.DocumentId).ToArray();
        await Assert.ThrowsAsync<PdmRuleException>(() => service.RetryItemsAsync(fixture.Package.Id, ids, "admin", default));
        var package = (await fixture.Repository.FindReleasePackageAsync(fixture.Package.Id, default))!;
        Assert.Equal(ReleasePreviewState.Failed, package.PreviewState);
        Assert.All(await service.RetryItemsAsync(fixture.Package.Id, ids, "admin", default), item => Assert.True(item.Succeeded));
        Assert.Equal(1, fixture.Converter.Calls);
    }

    [Fact]
    public async Task ManualRetryCancellationRequeuesWithoutConsumingAttempt()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Converter.Block = true;
        using var cancellation = new CancellationTokenSource();
        var ids = (await fixture.Repository.ListReleasePreviewSourcesAsync(fixture.Package.Id, default)).Select(item => item.DocumentId).ToArray();
        var retry = fixture.Service.RetryItemsAsync(fixture.Package.Id, ids, "admin", cancellation.Token);
        await fixture.Converter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry);
        var package = await fixture.Repository.FindReleasePackageAsync(fixture.Package.Id, default);
        Assert.Equal(ReleasePreviewState.Pending, package!.PreviewState);
        Assert.Equal(0, package.PreviewAttempts);
    }

    [Fact]
    public async Task ManualAndBackgroundRetriesSharePackageLockAndDoNotReconvertSuccessfulFiles()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Converter.Block = true;
        var running = fixture.Service.ProcessAsync(fixture.Package, default);
        await fixture.Converter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var ids = (await fixture.Repository.ListReleasePreviewSourcesAsync(fixture.Package.Id, default)).Select(item => item.DocumentId).ToArray();
        var retry = fixture.Service.RetryItemsAsync(fixture.Package.Id, ids, "admin", default);
        Assert.False(retry.IsCompleted);
        fixture.Converter.Proceed.TrySetResult();
        Assert.True(await running);
        Assert.All(await retry, item => Assert.True(item.Succeeded));
        Assert.Equal(1, fixture.Converter.Calls);
    }

    [Fact]
    public async Task ArchiveFailureDoesNotChangeCompletedConversionAndCanRetryWithoutReconversion()
    {
        using var fixture = await Fixture.CreateAsync();
        var files = DispatchProxy.Create<IProjectFileRepository, FailingArchiveProxy>();
        var proxy = (FailingArchiveProxy)files;
        proxy.Inner = new InMemoryProjectFileRepository(TimeProvider.System);
        var service = new ReleasePreviewService(fixture.Repository, fixture.Converter, TimeProvider.System,
            new ReleaseDeliveryArchiveService(fixture.Repository, files, TimeProvider.System));
        Assert.True(await service.ProcessAsync(fixture.Package, default));
        Assert.Equal(ReleasePreviewState.Succeeded, (await fixture.Repository.FindReleasePackageAsync(fixture.Package.Id, default))!.PreviewState);
        Assert.True(await service.ProcessAsync(fixture.Package, default));
        Assert.Equal(1, fixture.Converter.Calls);
        Assert.Equal(2, proxy.Calls);
    }

    [Fact]
    public async Task ExistingPublicationResumesBeforeConversionEvenWhenStagingWasRemoved()
    {
        using var fixture = await Fixture.CreateAsync();
        var source = new ReleasePreviewSource(Guid.NewGuid(), Guid.NewGuid(), "PART-1", "PART-1.SLDPRT", DocumentKind.Part, "part.sldprt", 1, "SOURCE", "SOURCE");
        var package = fixture.Package with { Number = "RP-ATOMIC", State = ReleasePackageState.Publishing, Scope = ReleaseScope.NonStandardSupplement };
        var publisher = new AtomicReleasePackagePublisher(TimeProvider.System, fixture.Converter);
        await publisher.PrepareAsync(package, fixture.Project, default);
        var initial = await publisher.PublishAsync(package, fixture.Project, [source], default);
        Directory.Delete(Path.Combine(fixture.Project.VaultLocation, ".release-staging", package.Number), true);
        var resumed = await publisher.PublishAsync(package, fixture.Project, [source], default);
        Assert.Equal(initial.Previews[source.DocumentId], resumed.Previews[source.DocumentId]);
        Assert.Equal(1, fixture.Converter.Calls);
        await File.AppendAllTextAsync(Path.Combine(initial.PublishedPath, "nonstandard-parts-bom.xlsx"), "损坏");
        await Assert.ThrowsAsync<PdmConflictException>(() => publisher.PublishAsync(package, fixture.Project, [source], default));
        Assert.Equal(1, fixture.Converter.Calls);
    }

    public class FailingArchiveProxy : DispatchProxy
    {
        public required IProjectFileRepository Inner { get; set; }
        public int Calls { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method!.Name == nameof(IProjectFileRepository.ArchiveReleaseAsync) && ++Calls == 1)
                return Task.FromException<int>(new IOException("simulated disk failure"));
            return method.Invoke(Inner, arguments);
        }
    }

    public class ReadbackFailureProxy : DispatchProxy
    {
        public required IPdmRepository Inner { get; set; }
        public required string PublishedPath { get; set; }
        public bool FailDiagnostic { get; set; }
        private bool failed;
        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            if (method!.Name == nameof(IPdmRepository.AppendAuditAsync) && FailDiagnostic)
            {
                FailDiagnostic = false;
                return Task.FromException(new IOException("simulated diagnostic storage failure"));
            }
            if (method!.Name == nameof(IPdmRepository.ListReleasePreviewSourcesAsync) && !failed
                && File.Exists(Path.Combine(PublishedPath, "manifest.json")))
            {
                failed = true;
                return Task.FromException<IReadOnlyList<ReleasePreviewSource>>(new IOException("simulated final readback failure"));
            }
            return method.Invoke(Inner, arguments);
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pdm-release-recovery", Guid.NewGuid().ToString("N"));
        public InMemoryPdmRepository Repository { get; } = new(TimeProvider.System);
        public FileConverter Converter { get; } = new();
        public required Project Project { get; set; }
        public required ReleasePackage Package { get; set; }
        public ConcurrentDictionary<Guid, DocumentVersion> Versions => Field<DocumentVersion>(Repository, "versions");
        public ReleasePreviewService Service => new(Repository, Converter, TimeProvider.System);

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture { Project = null!, Package = null! };
            var projects = Field<Project>(fixture.Repository, "projects");
            var project = projects.Values.First();
            fixture.Project = projects[project.Id] = project with { VaultLocation = Path.Combine(fixture.Root, "vault"), ReleaseLocation = Path.Combine(fixture.Root, "release") };
            var publishedPath = Path.Combine(fixture.Project.ReleaseLocation, "RP-RECOVERY");
            Directory.CreateDirectory(publishedPath);
            fixture.Package = new ReleasePackage(Guid.NewGuid(), project.Id, "RP-RECOVERY", ReleasePackageState.Published, null, "", "", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, publishedPath)
                { Scope = ReleaseScope.NonStandardWithDrawing, PreviewState = ReleasePreviewState.Pending };
            await fixture.Repository.CreateReleasePackageAsync(fixture.Package, default);
            var versions = Field<DocumentVersion>(fixture.Repository, "versions");
            var tree = (await fixture.Repository.GetReferenceTreeAsync(project.Id, default))!;
            foreach (var drawing in Field<PdmDocument>(fixture.Repository, "documents").Values.Where(item => item.Kind == DocumentKind.Drawing))
            {
                var version = new DocumentVersion(Guid.NewGuid(), drawing.Id, RevisionLabel.Released('A'), DocumentVersionStatus.Released,
                    drawing.FileName, 1, "SOURCE", "admin", DateTimeOffset.UtcNow, "测试", new Dictionary<string, string?>(), tree, [], [], null, null, null, fixture.Package.Id);
                versions[version.Id] = version;
            }
            Assert.NotEmpty(await fixture.Repository.ListReleasePreviewSourcesAsync(fixture.Package.Id, default));
            return fixture;
        }

        private static ConcurrentDictionary<Guid, T> Field<T>(InMemoryPdmRepository repository, string name) =>
            (ConcurrentDictionary<Guid, T>)typeof(InMemoryPdmRepository).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(repository)!;
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private sealed class FileConverter : IServerPreviewConverter
    {
        public int Calls { get; private set; }
        public IReadOnlyList<Guid> LastIds { get; private set; } = [];
        public bool Block { get; set; }
        public Exception? Failure { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Proceed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyDictionary<Guid, DocumentPreviewArtifact>> GenerateAsync(ReleasePackage package, Project project,
            IReadOnlyList<ReleasePreviewSource> sources, string stagingDirectory, CancellationToken cancellationToken)
        {
            Calls++;
            if (Failure is not null) throw Failure;
            LastIds = sources.Select(source => source.DocumentId).ToArray();
            Entered.TrySetResult();
            if (Block) await Proceed.Task.WaitAsync(cancellationToken);
            var result = new Dictionary<Guid, DocumentPreviewArtifact>();
            foreach (var source in sources)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes("verified-preview-" + source.DocumentId);
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                var name = $"{source.DocumentId:N}_{source.DrawingNumber}.{(source.Kind == DocumentKind.Drawing ? "pdf" : "step")}";
                var relative = Path.Combine(".release-previews", package.Id.ToString("N"), hash, name);
                var path = Path.Combine(project.VaultLocation, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, bytes, cancellationToken);
                var staging = Path.Combine(stagingDirectory, "previews", hash, name);
                Directory.CreateDirectory(Path.GetDirectoryName(staging)!);
                await File.WriteAllBytesAsync(staging, bytes, cancellationToken);
                result[source.DocumentId] = new DocumentPreviewArtifact(source.Kind == DocumentKind.Drawing ? DocumentPreviewFormat.Pdf : DocumentPreviewFormat.Step,
                    relative, bytes.Length, hash, source.SourceSha256);
            }
            return result;
        }
    }
}
