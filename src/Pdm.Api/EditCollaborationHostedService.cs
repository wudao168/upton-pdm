using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

public sealed class EditCollaborationHostedService(IServiceScopeFactory scopes, ILogger<EditCollaborationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 服务恢复后先留出重连时间，不把维护停机立即当作客户端失联。
        var recovering = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(recovering ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(1), stoppingToken);
                using var scope = scopes.CreateScope();
                var released = await scope.ServiceProvider.GetRequiredService<PdmWorkflowService>().MaintainEditCollaborationAsync(stoppingToken);
                recovering = false;
                if (released > 0) logger.LogInformation("已自动回收长期离线图档 {Count} 项。", released);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { recovering = true; logger.LogWarning(error, "编辑交接维护未完成，恢复连接后保留5分钟缓冲再重试。"); }
        }
    }
}
