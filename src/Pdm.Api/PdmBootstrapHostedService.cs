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
        foreach (var project in (await repository.ListProjectsAsync(cancellationToken)).Where(item => item.ParentProjectId is null))
        {
            await repository.EnsureProjectFolderTreeAsync(project.Id, cancellationToken);
        }
        if (await repository.CountUsersAsync(cancellationToken) > 0)
        {
            return;
        }

        var password = Environment.GetEnvironmentVariable("PDM_BOOTSTRAP_ADMIN_PASSWORD");
        if (string.IsNullOrWhiteSpace(password))
        {
            if (!environment.IsDevelopment())
            {
                logger.LogWarning("PLM has no users. Set PDM_BOOTSTRAP_ADMIN_PASSWORD before first production start.");
            }

            return;
        }

        var passwordService = scope.ServiceProvider.GetRequiredService<IPasswordService>();
        var primaryCompanyId = (await repository.GetProjectNumberingOptionsAsync(cancellationToken)).Organizations
            .Where(item => item.IsActive)
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => (Guid?)item.Id)
            .FirstOrDefault();
        if (primaryCompanyId is null)
        {
            logger.LogWarning("PLM has no active company. Create a company before bootstrapping the platform administrator.");
            return;
        }
        await repository.CreateUserAsync(new UserAccount(
            Guid.NewGuid(),
            "admin",
            "系统管理员",
            passwordService.Hash(password),
            UserRole.PlatformAdministrator,
            true,
            RoleCode: "platform_admin",
            CompanyId: primaryCompanyId), cancellationToken);
        logger.LogInformation("Created the initial PLM platform administrator account.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
