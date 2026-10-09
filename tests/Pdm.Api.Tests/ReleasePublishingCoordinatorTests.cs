using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Upton.Pdm.Api;
using Upton.Pdm.Application;
using Upton.Pdm.Domain;

namespace Upton.Pdm.Api.Tests;

public sealed class ReleasePublishingCoordinatorTests
{
    [Fact]
    public async Task StartupRecoveryJoinsScheduledRunInsteadOfPublishingTwice()
    {
        var repository = DispatchProxy.Create<IPdmRepository, WaitingRepository>();
        var waiting = (WaitingRepository)(object)repository;
        var services = new ServiceCollection();
        services.AddSingleton(repository);
        services.AddSingleton(new PdmWorkflowService(repository, null!, null!, TimeProvider.System));
        await using var provider = services.BuildServiceProvider();
        var coordinator = new ReleasePublishingCoordinator(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ReleasePublishingCoordinator>.Instance, new Lifetime());

        Assert.True(coordinator.TryStart("Scheduled"));
        Assert.False(coordinator.TryStart("StartupRecovery"));
        Assert.False(coordinator.TryStart("Approval"));
        Assert.Equal(1, waiting.Reads);
        waiting.Completion.SetResult([]);
        Assert.Equal(1, waiting.Reads);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!coordinator.TryStart("NextRun")) await Task.Delay(10, timeout.Token);
        Assert.Equal(2, waiting.Reads);
    }

    public class WaitingRepository : DispatchProxy
    {
        public int Reads;
        public TaskCompletionSource<IReadOnlyList<Guid>> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IPdmRepository.GetSystemSettingsAsync))
                return Task.FromResult(new PdmSystemSettings("", ""));
            if (method!.Name != nameof(IPdmRepository.ListPublishingReleasePackageIdsAsync))
                throw new InvalidOperationException(method.Name);
            Interlocked.Increment(ref Reads);
            return Completion.Task;
        }
    }

    [Fact]
    public async Task ScheduledScanContinuesWhilePackageIsBusyAndDoesNotStartItTwice()
    {
        var repository = DispatchProxy.Create<IPdmRepository, BusyPackageRepository>();
        var busy = (BusyPackageRepository)(object)repository;
        var services = new ServiceCollection();
        services.AddSingleton(repository);
        services.AddSingleton(new PdmWorkflowService(repository, null!, null!, TimeProvider.System));
        await using var provider = services.BuildServiceProvider();
        var coordinator = new ReleasePublishingCoordinator(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ReleasePublishingCoordinator>.Instance, new Lifetime());
        Assert.True(coordinator.TryStart("Scheduled"));
        Assert.True(coordinator.TryStart("NextScheduledScan"));
        Assert.Equal(1, busy.FindReads);
        busy.Completion.SetResult(null);
        await Task.Delay(20);
    }

    public class BusyPackageRepository : DispatchProxy
    {
        public int FindReads;
        public Guid PackageId = Guid.NewGuid();
        public TaskCompletionSource<ReleasePackage?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IPdmRepository.ListPublishingReleasePackageIdsAsync))
                return Task.FromResult<IReadOnlyList<Guid>>([PackageId]);
            if (method.Name == nameof(IPdmRepository.FindReleasePackageAsync)) { Interlocked.Increment(ref FindReads); return Completion.Task; }
            throw new InvalidOperationException(method.Name);
        }
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
