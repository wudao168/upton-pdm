using Upton.Pdm.Application;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class EditCollaborationTests
{
    private readonly Clock clock = new();
    private readonly InMemoryPdmRepository repository;
    private readonly PdmWorkflowService workflow;
    private readonly Guid session = Guid.NewGuid();
    public EditCollaborationTests()
    {
        repository = new(clock);
        workflow = new(repository, null!, null!, clock);
    }

    private async Task<(Project Project, PdmDocument Document)> Editing(string name = "协作零件")
    {
        var project = Assert.Single(await repository.ListProjectsAsync(default));
        var document = await repository.RegisterDocumentAsync(new(project.Id, name, name, name + ".SLDPRT", DocumentKind.Part), "admin", default);
        document = await repository.CheckoutAsync(document.Id, "admin", session, "设计电脑A", clock.GetUtcNow().AddMinutes(5), default);
        return (project, document);
    }

    private static DocumentVersionCommit Commit(Project project, PdmDocument document, char hash = 'A') => ArchiveBatchTests.Commit(project, document, ArchiveBatchTests.Node(document), hash);

    [Fact]
    public async Task ContinueEditing_KeepsOwnershipAndRejectsOldCloseGeneration()
    {
        var (project, document) = await Editing();
        var commit = Commit(project, document) with { KeepEditing = true };
        var first = await repository.CheckInVersionAsync(document.Id, "admin", session, commit, default);
        Assert.True(first.VersionCreated);
        Assert.Equal("admin", first.Document.CheckedOutBy);
        Assert.Equal(session, first.Document.CheckoutSessionId);
        Assert.True(first.Document.CheckedOutAt > document.CheckedOutAt);
        await Assert.ThrowsAsync<PdmConflictException>(() => repository.CloseCheckoutAsync(document.Id, "admin", session, document.CheckedOutAt!.Value, first.Version!.Sha256, false, default));
        var repeated = await repository.CheckInVersionAsync(document.Id, "admin", session, commit with { ExpectedVersionId = first.Version!.Id }, default);
        Assert.False(repeated.VersionCreated);
        Assert.Equal(session, repeated.Document.CheckoutSessionId);
        Assert.Single(await repository.ListDocumentVersionsAsync(document.Id, default));
        await Assert.ThrowsAsync<PdmConflictException>(() => repository.CheckoutAsync(document.Id, "collaborator", Guid.NewGuid(), "电脑B", clock.GetUtcNow().AddMinutes(5), default));
    }

    [Fact]
    public async Task Handoff_ReservesTheCommittedVersionForRecipientAndExpiresAfterFiveMinutes()
    {
        var (project, document) = await Editing();
        var saved = await repository.CheckInVersionAsync(document.Id, "admin", session, Commit(project, document) with { HandoffTo = "helper" }, default);
        Assert.Null(saved.Document.CheckedOutBy);
        var handoff = Assert.Single(await repository.ListEditHandoffsAsync(default));
        Assert.Equal(saved.Version!.Id, handoff.VersionId);
        await Assert.ThrowsAsync<PdmConflictException>(() => repository.CheckoutAsync(document.Id, "another", Guid.NewGuid(), "电脑C", clock.GetUtcNow().AddMinutes(5), default));
        var accepted = await repository.CheckoutAsync(document.Id, "helper", Guid.NewGuid(), "电脑B", clock.GetUtcNow().AddMinutes(5), default);
        Assert.Equal("helper", accepted.CheckedOutBy);
        Assert.Empty(await repository.ListEditHandoffsAsync(default));

        var second = await Editing("超时预留");
        await repository.CheckInVersionAsync(second.Document.Id, "admin", session, Commit(second.Project, second.Document) with { HandoffTo = "helper" }, default);
        clock.Advance(TimeSpan.FromMinutes(5));
        var others = await repository.CheckoutAsync(second.Document.Id, "another", Guid.NewGuid(), "电脑C", clock.GetUtcNow().AddMinutes(5), default);
        Assert.Equal("another", others.CheckedOutBy);
    }

    [Fact]
    public async Task FailedBatchDoesNotLeaveHalfHandoffOrVersions()
    {
        var first = await Editing("A");
        var second = await Editing("B");
        var files = new[] {
            new ArchiveBatchFile(first.Document.Id, Commit(first.Project, first.Document) with { HandoffTo = "helper" }),
            new ArchiveBatchFile(second.Document.Id, Commit(second.Project, second.Document, 'B') with { ExpectedVersionId = Guid.NewGuid(), HandoffTo = "helper" })
        };
        await Assert.ThrowsAsync<PdmConflictException>(() => repository.CheckInBatchVersionsAsync(Guid.NewGuid(), "rollback", "admin", session, files, default));
        Assert.Empty(await repository.ListEditHandoffsAsync(default));
        Assert.Empty(await repository.ListDocumentVersionsAsync(first.Document.Id, default));
        Assert.Equal(session, (await repository.FindDocumentAsync(first.Document.Id, default))!.CheckoutSessionId);
    }

    [Fact]
    public async Task ContinueEditingBatchReplayDoesNotCreateVersionOrChangeGenerationAgain()
    {
        var item = await Editing();
        var batch = Guid.NewGuid();
        ArchiveBatchFile[] files = [new(item.Document.Id, Commit(item.Project, item.Document) with { KeepEditing = true })];
        var first = await repository.CheckInBatchVersionsAsync(batch, "continue", "admin", session, files, default);
        clock.Advance(TimeSpan.FromMinutes(1));
        var replay = await repository.CheckInBatchVersionsAsync(batch, "continue", "admin", session, files, default);
        Assert.Equal(first.Results.Single().Document.CheckedOutAt, replay.Results.Single().Document.CheckedOutAt);
        Assert.Single(await repository.ListDocumentVersionsAsync(item.Document.Id, default));
    }

    [Fact]
    public async Task HandoffCannotGrantEditingRightsOrBeCombinedWithContinueEditing()
    {
        var item = await Editing();
        await Assert.ThrowsAsync<PdmRuleException>(() => workflow.ValidateEditDispositionAsync(item.Document.Id, "admin", true, "helper", null, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => workflow.ValidateEditDispositionAsync(item.Document.Id, "admin", false, "admin", null, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => workflow.ValidateEditDispositionAsync(item.Document.Id, "admin", false, "missing", null, default));
        await repository.CreateUserAsync(new(Guid.NewGuid(), "disabled", "停用协助人", "unused", UserRole.Administrator, false), default);
        await Assert.ThrowsAsync<PdmRuleException>(() => workflow.ValidateEditDispositionAsync(item.Document.Id, "admin", false, "disabled", null, default));
        await Assert.ThrowsAsync<PdmRuleException>(() => workflow.ValidateEditDispositionAsync(item.Document.Id, "admin", true, null, Guid.NewGuid(), default));
    }

    [Fact]
    public async Task HandoffUsesRecipientsPermissionsAndRestoresInitiatorsContext()
    {
        var item = await Editing();
        await repository.CreateUserAsync(new(Guid.NewGuid(), "viewer", "只读协助人", "unused", UserRole.ProductionViewer, true), default);
        await repository.CreateUserAsync(new(Guid.NewGuid(), "helper-admin", "有权限协助人", "unused", UserRole.Administrator, true), default);
        var company = item.Project.OrganizationId ?? Guid.Empty;
        var initiator = new CurrentTenant(Guid.NewGuid(), company, company, "admin", "developer", true, new HashSet<string>(), ["developer"]);
        TenantContext.Set(initiator);
        try
        {
            await Assert.ThrowsAsync<PdmRuleException>(() => workflow.ValidateEditDispositionAsync(item.Document.Id, "admin", false, "viewer", null, default));
            Assert.Equal(initiator, TenantContext.Current);
            await workflow.ValidateEditDispositionAsync(item.Document.Id, "admin", false, "helper-admin", null, default);
            Assert.Equal(initiator, TenantContext.Current);
        }
        finally { TenantContext.Clear(); }
    }

    [Fact]
    public async Task ContinueEditingSingleRequestReplayReturnsCommittedVersion()
    {
        var item = await Editing();
        var commit = Commit(item.Project, item.Document) with { KeepEditing = true };
        var saved = await repository.CheckInVersionAsync(item.Document.Id, "admin", session, commit, default);
        var replay = await workflow.CheckInAsync(item.Document.Id, "admin", UserRole.Administrator, session, commit.File, commit.ChangeNote, commit.Properties,
            commit.ReferenceSnapshot, false, false, default, requireBaseVersion: true, keepEditing: true);
        Assert.Equal(saved.Version!.Id, replay.Version!.Id);
        Assert.Equal(saved.Document.CheckedOutAt, replay.Document.CheckedOutAt);
        Assert.Single(await repository.ListDocumentVersionsAsync(item.Document.Id, default));
    }

    [Fact]
    public async Task EmergencyTakeoverIsAuthorizedAndFencesOldHeartbeatAndArchive()
    {
        var item = await Editing();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => workflow.EmergencyReleaseEditLockAsync(item.Document.Id, session, item.Document.CheckedOutAt!.Value, "engineer", UserRole.Engineer, "协助", default));
        await Assert.ThrowsAsync<PdmRuleException>(() => workflow.EmergencyReleaseEditLockAsync(item.Document.Id, session, item.Document.CheckedOutAt!.Value, "admin", UserRole.Administrator, "", default));
        var released = await workflow.EmergencyReleaseEditLockAsync(item.Document.Id, session, item.Document.CheckedOutAt!.Value, "admin", UserRole.Administrator, "负责人确认交接", default);
        Assert.Null(released.CheckedOutBy);
        Assert.Empty(await repository.HeartbeatCheckoutSessionAsync(session, "admin", "设计电脑A", [item.Document.Id], clock.GetUtcNow().AddMinutes(5), default));
        await Assert.ThrowsAsync<PdmConflictException>(() => repository.CheckInVersionAsync(item.Document.Id, "admin", session, Commit(item.Project, item.Document), default));
        var next = await repository.CheckoutAsync(item.Document.Id, "helper", Guid.NewGuid(), "电脑B", clock.GetUtcNow().AddMinutes(5), default);
        await Assert.ThrowsAsync<PdmConflictException>(() => repository.ReclaimCheckoutAsync(item.Document.Id, session, item.Document.CheckedOutAt.Value, null, clock.GetUtcNow(), default));
        Assert.Equal(next.CheckoutSessionId, (await repository.FindDocumentAsync(item.Document.Id, default))!.CheckoutSessionId);
    }

    [Fact]
    public async Task OrdinaryOfflineTakeoverWaitsThirtyMinutes()
    {
        var item = await Editing();
        clock.Advance(TimeSpan.FromMinutes(29));
        await Assert.ThrowsAsync<PdmRuleException>(() => workflow.ForceReleaseEditLockAsync(item.Document.Id, "admin", UserRole.Administrator, "离线交接", default));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null((await workflow.ForceReleaseEditLockAsync(item.Document.Id, "admin", UserRole.Administrator, "离线交接", default)).CheckoutSessionId);
    }

    [Fact]
    public async Task AutomaticOfflineReleaseWaitsFortyEightHoursAndHonorsDisableSwitch()
    {
        var first = await Editing();
        clock.Advance(TimeSpan.FromHours(47));
        Assert.Equal(0, await workflow.MaintainEditCollaborationAsync(default));
        clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        Assert.Equal(1, await workflow.MaintainEditCollaborationAsync(default));
        Assert.Null((await repository.FindDocumentAsync(first.Document.Id, default))!.CheckoutSessionId);
        var audit = await repository.ListAuditAsync("admin", UserRole.Administrator, 100, default);
        Assert.Contains(audit, entry => entry.Action == "document.checkout.auto-offline-release");
        Assert.Equal(0, await workflow.MaintainEditCollaborationAsync(default));

        var second = await Editing("关闭自动回收");
        await repository.UpdateSystemSettingsAsync((await repository.GetSystemSettingsAsync(default)) with { CheckoutAutoReleaseEnabled = false }, default);
        clock.Advance(TimeSpan.FromHours(72));
        Assert.Equal(0, await workflow.MaintainEditCollaborationAsync(default));
        Assert.Equal(session, (await repository.FindDocumentAsync(second.Document.Id, default))!.CheckoutSessionId);
    }

    [Fact]
    public async Task RestoredHeartbeatPreventsOfflineReclaimEvenForLongHeldDocument()
    {
        var item = await Editing();
        clock.Advance(TimeSpan.FromHours(72));
        var cutoff = clock.GetUtcNow().AddHours(-48);
        await repository.HeartbeatCheckoutSessionAsync(session, "admin", "设计电脑A", [item.Document.Id], clock.GetUtcNow().AddMinutes(5), default);
        await Assert.ThrowsAsync<PdmConflictException>(() => repository.ReclaimCheckoutAsync(item.Document.Id, session, item.Document.CheckedOutAt!.Value, cutoff, clock.GetUtcNow(), default));
        Assert.Equal(0, await workflow.MaintainEditCollaborationAsync(default));
    }

    [Fact]
    public async Task PendingUploadBlocksEmergencyAndAutomaticReclaim()
    {
        var item = await Editing();
        await repository.RegisterArchiveUploadAsync(new(Guid.NewGuid(), item.Project.Id, item.Document.FileName, 128, 128, new string('A', 64), 0, clock.GetUtcNow().AddHours(100), "admin")
            { ArchiveContext = new(Guid.NewGuid(), item.Document.Id, session), TargetRelativePath = "pending/upload" }, default);
        await Assert.ThrowsAsync<PdmConflictException>(() => workflow.EmergencyReleaseEditLockAsync(item.Document.Id, session, item.Document.CheckedOutAt!.Value, "admin", UserRole.Administrator, "紧急", default));
        clock.Advance(TimeSpan.FromHours(49));
        Assert.Equal(0, await workflow.MaintainEditCollaborationAsync(default));
        Assert.Equal(session, (await repository.FindDocumentAsync(item.Document.Id, default))!.CheckoutSessionId);
    }

    [Fact]
    public async Task IdleReleaseRequiresMatchingBaseAndDoesNotRunWhenDisabled()
    {
        var item = await Editing();
        var saved = await repository.CheckInVersionAsync(item.Document.Id, "admin", session, Commit(item.Project, item.Document) with { KeepEditing = true }, default);
        var version = saved.Version!;
        await Assert.ThrowsAsync<PdmConflictException>(() => workflow.AutoReleaseUnchangedEditAsync(item.Document.Id, session, saved.Document.CheckedOutAt!.Value, Guid.NewGuid(), version.Sha256, "admin", UserRole.Administrator, default));
        var settings = await repository.GetSystemSettingsAsync(default);
        await repository.UpdateSystemSettingsAsync(settings with { CheckoutAutoReleaseEnabled = false }, default);
        await Assert.ThrowsAsync<PdmRuleException>(() => workflow.AutoReleaseUnchangedEditAsync(item.Document.Id, session, saved.Document.CheckedOutAt!.Value, version.Id, version.Sha256, "admin", UserRole.Administrator, default));
        await repository.UpdateSystemSettingsAsync(settings, default);
        Assert.Null((await workflow.AutoReleaseUnchangedEditAsync(item.Document.Id, session, saved.Document.CheckedOutAt!.Value, version.Id, version.Sha256, "admin", UserRole.Administrator, default)).CheckoutSessionId);
    }

    [Fact]
    public async Task IdleReleaseRejectsAnotherOwnerAndConcurrentArchiveUpload()
    {
        var item = await Editing();
        var saved = await repository.CheckInVersionAsync(item.Document.Id, "admin", session, Commit(item.Project, item.Document) with { KeepEditing = true }, default);
        await Assert.ThrowsAsync<PdmConflictException>(() => workflow.AutoReleaseUnchangedEditAsync(item.Document.Id, session, saved.Document.CheckedOutAt!.Value, saved.Version!.Id, saved.Version.Sha256, "helper", UserRole.Administrator, default));
        await repository.RegisterArchiveUploadAsync(new(Guid.NewGuid(), item.Project.Id, item.Document.FileName, 128, 128, new string('B', 64), 0, clock.GetUtcNow().AddHours(1), "admin")
            { ArchiveContext = new(Guid.NewGuid(), item.Document.Id, session), TargetRelativePath = "pending/new-upload" }, default);
        await Assert.ThrowsAsync<PdmConflictException>(() => workflow.AutoReleaseUnchangedEditAsync(item.Document.Id, session, saved.Document.CheckedOutAt!.Value, saved.Version!.Id, saved.Version.Sha256, "admin", UserRole.Administrator, default));
        Assert.Equal(session, (await repository.FindDocumentAsync(item.Document.Id, default))!.CheckoutSessionId);
    }

    [Fact]
    public async Task MultipleWaitersKeepFirstDeadlineAcrossCheckpointsAndDoNotLeakIntoNextEdit()
    {
        var item = await Editing();
        await workflow.RequestEditLockReleaseAsync(item.Document.Id, "helper", UserRole.Administrator, "协助设计", default);
        var first = (await repository.FindDocumentAsync(item.Document.Id, default))!;
        clock.Advance(TimeSpan.FromMinutes(4));
        await workflow.RequestEditLockReleaseAsync(item.Document.Id, "another", UserRole.Administrator, "协助检查", default);
        var second = (await repository.FindDocumentAsync(item.Document.Id, default))!;
        Assert.Equal(first.CheckoutReleaseRequestedAt, second.CheckoutReleaseRequestedAt);
        Assert.Equal("helper", second.CheckoutReleaseRequestedBy);
        clock.Advance(TimeSpan.FromMinutes(2));
        await workflow.MaintainEditCollaborationAsync(default);
        Assert.Single(await repository.ListUserNotificationsAsync("admin", 100, default), n => n.Category == "EditLockReminder");
        var saved = await repository.CheckInVersionAsync(item.Document.Id, "admin", session, Commit(item.Project, second) with { KeepEditing = true }, default);
        await workflow.MaintainEditCollaborationAsync(default);
        Assert.Single(await repository.ListUserNotificationsAsync("admin", 100, default), n => n.Category == "EditLockReminder");
        await workflow.CloseEditAsync(item.Document.Id, "admin", UserRole.Administrator, session, saved.Document.CheckedOutAt!.Value, saved.Version!.Sha256, false, default);
        foreach (var name in new[] { "helper", "another" })
            Assert.Single(await repository.ListUserNotificationsAsync(name, 100, default), n => n.Category == "EditLockAvailable");

        clock.Advance(TimeSpan.FromMinutes(1));
        var next = await repository.CheckoutAsync(item.Document.Id, "admin", session, "设计电脑A", clock.GetUtcNow().AddMinutes(5), default);
        await workflow.RequestEditLockReleaseAsync(item.Document.Id, "third", UserRole.Administrator, "下一轮协助", default);
        await workflow.CloseEditAsync(item.Document.Id, "admin", UserRole.Administrator, session, next.CheckedOutAt!.Value, saved.Version.Sha256, false, default);
        Assert.Single(await repository.ListUserNotificationsAsync("helper", 100, default), n => n.Category == "EditLockAvailable");
        Assert.Single(await repository.ListUserNotificationsAsync("third", 100, default), n => n.Category == "EditLockAvailable");
    }

    [Fact]
    public async Task ExpiredHandoffNotifiesWaitingCollaboratorsOnce()
    {
        var item = await Editing();
        await workflow.RequestEditLockReleaseAsync(item.Document.Id, "waiting", UserRole.Administrator, "协助", default);
        await repository.CheckInVersionAsync(item.Document.Id, "admin", session, Commit(item.Project, item.Document) with { HandoffTo = "helper" }, default);
        clock.Advance(TimeSpan.FromMinutes(5));
        await workflow.MaintainEditCollaborationAsync(default);
        await workflow.MaintainEditCollaborationAsync(default);
        Assert.Empty(await repository.ListEditHandoffsAsync(default));
        foreach (var name in new[] { "admin", "helper", "waiting" })
            Assert.Single(await repository.ListUserNotificationsAsync(name, 100, default), n => n.Title == "图档交接预留已到期");
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan time) => now += time;
    }
}
