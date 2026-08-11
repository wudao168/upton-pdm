using Upton.Pdm.Application;
using Upton.Pdm.Domain;
using Upton.Pdm.Infrastructure;

namespace Upton.Pdm.Api;

public sealed class PdmBootstrapHostedService(
    IServiceProvider serviceProvider,
    IHostEnvironment environment,
    ILogger<PdmBootstrapHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = serviceProvider.CreateAsyncScope();
        var migrationRunner = scope.ServiceProvider.GetRequiredService<MySqlMigrationRunner>();
        await migrationRunner.RunAsync(cancellationToken);

        var repository = scope.ServiceProvider.GetRequiredService<IPdmRepository>();
        if (await repository.CountUsersAsync(cancellationToken) > 0)
        {
            return;
        }

        var password = Environment.GetEnvironmentVariable("PDM_BOOTSTRAP_ADMIN_PASSWORD");
        if (string.IsNullOrWhiteSpace(password))
        {
            if (!environment.IsDevelopment())
            {
                logger.LogWarning("PDM has no users. Set PDM_BOOTSTRAP_ADMIN_PASSWORD before first production start.");
            }

            return;
        }

        var passwordService = scope.ServiceProvider.GetRequiredService<IPasswordService>();
        await repository.CreateUserAsync(new UserAccount(
            Guid.NewGuid(),
            "admin",
            "系统管理员",
            passwordService.Hash(password),
            UserRole.Administrator,
            true), cancellationToken);
        logger.LogInformation("Created the initial PDM administrator account.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
