using Upton.Pdm.Application;

namespace Upton.Pdm.Api;

public sealed class QualityReviewReminderHostedService(IServiceScopeFactory scopes,
    ILogger<QualityReviewReminderHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var repository = scope.ServiceProvider.GetRequiredService<IPdmRepository>();
                    var service = scope.ServiceProvider.GetRequiredService<QualityReviewService>();
                    var directory = await repository.GetOrganizationDirectoryAsync(stoppingToken);
                    foreach (var company in directory.Organizations.Where(item => item.IsActive))
                    {
                        try
                        {
                            TenantContext.Set(new(Guid.Empty, company.Id, company.Id, "quality-review-reminder", "", false, new HashSet<string>()));
                            await service.NotifyPendingAsync(company.Id, stoppingToken);
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                        catch (Exception error) { logger.LogError(error, "质量改善待办更新失败：{CompanyId}", company.Id); }
                        finally { TenantContext.Clear(); }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception error) { logger.LogError(error, "质量改善待办扫描失败，稍后自动重试。"); }
                await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
