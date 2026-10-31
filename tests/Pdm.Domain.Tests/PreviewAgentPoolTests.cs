using System.Net;
using System.Text;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;
using Upton.Pdm.Application;

namespace Upton.Pdm.Domain.Tests;

public sealed class PreviewAgentPoolTests
{
    [Theory]
    [InlineData("busy")]
    [InlineData("offline")]
    public async Task ReleaseTasksReturnWaitingInsteadOfOccupyingTheBatchWhenNoServerIsAvailable(string state)
    {
        var url = $"http://{Guid.NewGuid():N}-{state}";
        using var client = new HttpClient(new HealthHandler());
        var settings = new PreviewConversionSettings { Servers = [new("转图服务器", url, "")] };
        await Assert.ThrowsAsync<PreviewServerUnavailableException>(() =>
            PreviewAgentPool.AcquireAsync(settings, "发布转图", client, default, waitForServer: false).WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task SkipsBusyServerAndDoesNotAssignTheSameServerTwice()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var servers = new[] { new PreviewAgentServer("忙碌", $"http://{prefix}-busy", "one"),
            new PreviewAgentServer("空闲", $"http://{prefix}-idle", "two") };
        using var client = new HttpClient(new HealthHandler());
        var settings = new PreviewConversionSettings { Servers = servers };
        using var lease = await PreviewAgentPool.AcquireAsync(settings, "first", client, default);
        Assert.Equal(servers[1].AgentUrl, lease.Settings.AgentUrl);
        Assert.Equal("two", lease.Settings.AgentToken);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PreviewAgentPool.AcquireAsync(settings, "second", client, cancellation.Token));
        lease.Dispose();
        using var next = await PreviewAgentPool.AcquireAsync(settings, "third", client, default);
        Assert.Equal(servers[1].AgentUrl, next.Settings.AgentUrl);
    }

    [Fact]
    public async Task OfflineServerFallsBackToAnotherServer()
    {
        var prefix = Guid.NewGuid().ToString("N");
        var settings = new PreviewConversionSettings { Servers = [new("离线", $"http://{prefix}-offline", ""), new("可用", $"http://{prefix}-idle", "")] };
        using var client = new HttpClient(new HealthHandler());
        using var lease = await PreviewAgentPool.AcquireAsync(settings, "job", client, default);
        Assert.EndsWith("-idle", lease.Settings.AgentUrl);
    }

    private sealed class HealthHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host.EndsWith("-offline", StringComparison.Ordinal)) throw new HttpRequestException("offline");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent($"{{\"workerExists\":true,\"busy\":{(request.RequestUri.Host.EndsWith("-busy", StringComparison.Ordinal) ? "true" : "false")}}}", Encoding.UTF8, "application/json")
            });
        }
    }
}
