using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class PendingArchiveHeartbeatTests
{
    [Fact]
    public async Task PendingRegistrationRetainsItsSessionUntilCanceledWithoutCreatingVersions()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("QA-PENDING-HB", "临时登记心跳", "admin", "vault", "release"), "admin", default);
        var session = Guid.NewGuid();
        var batch = Guid.NewGuid();
        var pending = await repository.ReserveArchiveDocumentAsync(new(project.Id, "PART", "零件", "PART.SLDPRT", DocumentKind.Part), "admin", new(batch, session, "QA"), default);
        var workflow = new PdmWorkflowService(repository, null!, null!, TimeProvider.System);
        var heartbeat = await workflow.HeartbeatEditSessionAsync(session, "admin", UserRole.Administrator, "QA", [pending.Id], default);
        Assert.Contains(pending.Id, heartbeat.ActiveDocumentIds);
        Assert.Empty(heartbeat.LostDocumentIds);
        Assert.Empty(await repository.HeartbeatCheckoutSessionAsync(Guid.NewGuid(), "admin", "QA", [pending.Id], DateTimeOffset.UtcNow.AddHours(1), default));
        Assert.Empty(await repository.HeartbeatCheckoutSessionAsync(session, "other", "QA", [pending.Id], DateTimeOffset.UtcNow.AddHours(1), default));
        Assert.Empty(await repository.ListDocumentsAsync(project.Id, default));
        Assert.Empty(await repository.ListDocumentVersionsAsync(pending.Id, default));
        await repository.CancelArchiveRegistrationAsync(batch, "admin", session, default);
        heartbeat = await workflow.HeartbeatEditSessionAsync(session, "admin", UserRole.Administrator, "QA", [pending.Id], default);
        Assert.Contains(pending.Id, heartbeat.LostDocumentIds);
        Assert.Empty(await repository.ListDocumentsAsync(project.Id, default));
    }
    [Fact]
    public async Task ExpiredReservationsAreNotRenewedByHeartbeatAndCleanupIsBounded()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var repository = new InMemoryPdmRepository(clock);
        var project = await repository.CreateProjectAsync(new("QA-HB-EXPIRE", "过期暂存", "admin", "vault", "release"), "admin", default);
        var session = Guid.NewGuid();
        var documents = new List<PdmDocument>();
        for (var i = 0; i < 3; i++)
            documents.Add(await repository.ReserveArchiveDocumentAsync(new(project.Id, "PART" + i, "零件", "PART" + i + ".SLDPRT", DocumentKind.Part), "admin", new(Guid.NewGuid(), session, "QA"), default));
        clock.Now = clock.Now.AddHours(25);
        Assert.Empty(await repository.HeartbeatCheckoutSessionAsync(session, "admin", "QA", documents.Select(d => d.Id).ToArray(), clock.Now.AddHours(1), default));
        await repository.ClaimExpiredArchiveUploadsAsync(1, default);
        var remaining = 0;
        foreach (var document in documents)
            if (await repository.FindPendingArchiveDocumentAsync(document.Id, default) is not null) remaining++;
        Assert.Equal(2, remaining);
        Assert.Empty(await repository.ListDocumentsAsync(project.Id, default));
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
