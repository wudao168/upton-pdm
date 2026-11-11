using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api;

public sealed class BrowserCadArchiveHostedService(IServiceScopeFactory scopes, BrowserCadArchiveQueue queue,
    ILogger<BrowserCadArchiveHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
            foreach (var job in queue.Pending())
            {
                if (!await queue.Gate(job.Id).WaitAsync(0, stoppingToken)) continue;
                try
                {
                    using var scope = scopes.CreateScope(); var repository = scope.ServiceProvider.GetRequiredService<IPdmRepository>();
                    var account = await repository.FindUserAsync(job.Owner, stoppingToken);
                    if (account is null || !account.IsActive) throw new UnauthorizedAccessException("发起账号已停用，上传文件已保留。");
                    var company = await scope.ServiceProvider.GetRequiredService<CompanySessionService>().ResolveAsync(account, job.CompanyId.ToString(), stoppingToken);
                    var permissions = await repository.GetUserPermissionsAsync(account.Username, account.Role, stoppingToken);
                    TenantContext.Set(new(account.Id, company.ActiveCompanyId, company.PrimaryCompanyId, account.Username,
                        account.EffectiveRoleCode, company.CrossCompanyView, permissions, account.EffectiveRoleCodes));
                    await scope.ServiceProvider.GetRequiredService<BrowserCadArchiveService>().ProcessAsync(job, account.Username, account.Role, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception error)
                {
                    logger.LogError(error, "网页存档任务 {JobId} 未完成，已保留上传文件", job.Id);
                    try
                    {
                        using var scope = scopes.CreateScope();
                        await scope.ServiceProvider.GetRequiredService<BrowserCadArchiveService>().RecordFailureAsync(job, error, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                    catch (Exception persistenceError)
                    {
                        logger.LogError(persistenceError, "网页存档任务 {JobId} 的失败状态暂时无法保存，将在下次轮询重试；API 服务继续运行", job.Id);
                    }
                }
                finally { TenantContext.Clear(); queue.Gate(job.Id).Release(); }
            }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception pollingError)
            {
                logger.LogError(pollingError, "网页存档队列暂时无法读取，将在下次轮询重试；API 服务继续运行");
            }
            await Task.Delay(1500, stoppingToken);
        }
    }
}
