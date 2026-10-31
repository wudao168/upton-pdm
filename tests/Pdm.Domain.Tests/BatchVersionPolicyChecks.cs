using System.Reflection;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Domain.Tests;

public sealed class BatchVersionPolicyChecks
{
    [Fact]
    public async Task PendingBatchIdentitiesHaveAccessibleEmptyVersionHistory()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("PENDING-VERIFY", "预留版本核对", "admin", "vault", "release"), "admin", default);
        var pending = await repository.ReserveArchiveDocumentAsync(new(project.Id, "NEW", "新图", "NEW.SLDPRT", DocumentKind.Part),
            "admin", new(Guid.NewGuid(), Guid.NewGuid(), "QA"), default);
        var workflow = new PdmWorkflowService(repository, null!, null!, TimeProvider.System);
        var row = Assert.Single(await workflow.VerifyDocumentVersionsAsync([new(pending.Id, null)], "admin", UserRole.Administrator, default));
        Assert.True(row.Accessible); Assert.Null(row.Latest); Assert.Null(row.Bound);
    }

    [Fact]
    public async Task ReusesProjectDecisionButChecksEveryDocumentAndEveryRequest()
    {
        var repository = new InMemoryPdmRepository(TimeProvider.System);
        var project = await repository.CreateProjectAsync(new("POLICY-1", "版本核对", "admin", "vault", "release"), "admin", default);
        var other = await repository.CreateProjectAsync(new("POLICY-2", "无权项目", "admin", "vault", "release"), "admin", default);
        var own = new List<PdmDocument>();
        for (var i = 0; i < 90; i++) own.Add(await repository.RegisterDocumentAsync(new(project.Id, "P-" + i, "零件", "P-" + i + ".SLDPRT", DocumentKind.Part), "admin", default));
        var denied = await repository.RegisterDocumentAsync(new(other.Id, "DENIED", "无权零件", "DENIED.SLDPRT", DocumentKind.Part), "admin", default);
        var proxy = DispatchProxy.Create<IPdmRepository, VersionPolicyRepository>();
        var counter = (VersionPolicyRepository)(object)proxy;
        counter.Target = repository; counter.DeniedProject = other.Id; counter.DeniedFile = own.Last().Id;
        var service = new PdmWorkflowService(proxy, null!, null!, TimeProvider.System);
        var requests = own.Select(doc => new DocumentVersionVerificationRequest(doc.Id, null)).Append(new(denied.Id, null)).ToArray();
        var rows = await service.VerifyDocumentVersionsAsync(requests, "admin", UserRole.Administrator, default);
        Assert.Equal(91, counter.FileChecks);
        Assert.Equal(2, counter.ProjectChecks);
        Assert.Equal(89, counter.Verified.Count);
        Assert.False(rows.Single(row => row.DocumentId == own.Last().Id).Accessible);
        Assert.False(rows.Single(row => row.DocumentId == denied.Id).Accessible);
        Assert.All(rows.Where(row => row.Accessible), row => { Assert.Null(row.Latest); Assert.Null(row.Bound); });
        // Permissions can change between requests. No persistent positive/negative ACL cache.
        counter.DeniedProject = project.Id; counter.DeniedFile = Guid.Empty;
        rows = await service.VerifyDocumentVersionsAsync(requests, "admin", UserRole.Administrator, default);
        Assert.Equal(182, counter.FileChecks);
        Assert.Equal(4, counter.ProjectChecks);
        Assert.All(rows.Where(row => own.Any(doc => doc.Id == row.DocumentId)), row => Assert.False(row.Accessible));
        Assert.True(rows.Single(row => row.DocumentId == denied.Id).Accessible);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.VerifyDocumentVersionsAsync(requests, "admin", UserRole.Administrator, cancellation.Token));
    }

    public class VersionPolicyRepository : DispatchProxy
    {
        public IPdmRepository Target = null!;
        public Guid DeniedProject, DeniedFile;
        public int ProjectChecks, FileChecks;
        public IReadOnlyList<DocumentVersionVerificationRequest> Verified = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IPdmRepository.HasDocumentAccessAsync))
            {
                FileChecks++;
                if ((Guid)args![0]! == DeniedFile) return Task.FromResult(false);
            }
            if (method.Name == nameof(IPdmRepository.HasProjectContentReadAccessAsync))
            {
                ProjectChecks++;
                if ((Guid)args![0]! == DeniedProject) return Task.FromResult(false);
            }
            if (method.Name == nameof(IPdmRepository.ListDocumentVersionVerificationsAsync))
            {
                Verified = (IReadOnlyList<DocumentVersionVerificationRequest>)args![0]!;
                return Task.FromResult<IReadOnlyList<DocumentVersionVerificationResult>>(Verified.Select(item => new DocumentVersionVerificationResult(item.DocumentId, null, null)).ToArray());
            }
            try { return method.Invoke(Target, args); }
            catch (TargetInvocationException error) when (error.InnerException != null) { throw error.InnerException; }
        }
    }
}
