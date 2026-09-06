using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MySqlConnector;
using Upton.Pdm.Api;
using Upton.Pdm.Application;
using Upton.Pdm.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
var httpUrlOverride = Environment.GetEnvironmentVariable("PDM_HTTP_URL");
if (!string.IsNullOrWhiteSpace(httpUrlOverride))
{
    builder.Configuration["Kestrel:Endpoints:Http:Url"] = httpUrlOverride;
}
builder.Host.UseWindowsService(options => options.ServiceName = "UPLM API");
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
var timeProvider = TimeProvider.System;
builder.Services.AddSingleton(timeProvider);

var databaseOptions = new PdmDatabaseOptions();
builder.Configuration.GetSection(PdmDatabaseOptions.SectionName).Bind(databaseOptions);
if (string.Equals(databaseOptions.Provider, "MySql", StringComparison.OrdinalIgnoreCase))
{
    var baseConnectionString = builder.Configuration.GetConnectionString("Pdm")
        ?? throw new InvalidOperationException("ConnectionStrings:Pdm未配置。 ");
    var connectionBuilder = new MySqlConnectionStringBuilder(baseConnectionString);
    var databaseNameOverride = Environment.GetEnvironmentVariable("PDM_DATABASE_NAME");
    if (!string.IsNullOrWhiteSpace(databaseNameOverride))
    {
        if (databaseNameOverride.Any(character => !char.IsLetterOrDigit(character) && character != '_'))
        {
            throw new InvalidOperationException("PDM_DATABASE_NAME只能包含字母、数字和下划线。");
        }
        connectionBuilder.Database = databaseNameOverride;
    }
    var databasePassword = Environment.GetEnvironmentVariable("PDM_DB_PASSWORD");
    if (!string.IsNullOrWhiteSpace(databasePassword))
    {
        connectionBuilder.Password = databasePassword;
    }

    if (!builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(connectionBuilder.Password))
    {
        throw new InvalidOperationException("生产环境必须设置PDM_DB_PASSWORD。 ");
    }

    databaseOptions.ConnectionString = connectionBuilder.ConnectionString;
}

var storageOptions = new PdmStorageOptions();
builder.Configuration.GetSection(PdmStorageOptions.SectionName).Bind(storageOptions);
if (!Path.IsPathFullyQualified(storageOptions.UploadTempRoot))
{
    storageOptions.UploadTempRoot = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, storageOptions.UploadTempRoot));
}
if (!Path.IsPathFullyQualified(storageOptions.ProgramTemplateRoot))
{
    storageOptions.ProgramTemplateRoot = Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, storageOptions.ProgramTemplateRoot));
}

var previewWorkerOptions = new PdmPreviewWorkerOptions();
builder.Configuration.GetSection(PdmPreviewWorkerOptions.SectionName).Bind(previewWorkerOptions);
var previewWorkerPathOverride = Environment.GetEnvironmentVariable("PDM_PREVIEW_WORKER_PATH");
if (!string.IsNullOrWhiteSpace(previewWorkerPathOverride)) previewWorkerOptions.WorkerPath = previewWorkerPathOverride;

var authenticationOptions = new AuthenticationOptions();
builder.Configuration.GetSection(AuthenticationOptions.SectionName).Bind(authenticationOptions);
authenticationOptions.SigningKey = Environment.GetEnvironmentVariable("PDM_JWT_SIGNING_KEY") ?? string.Empty;
if (authenticationOptions.SigningKey.Length < 32)
{
    if (!builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException("生产环境必须设置至少32字符的PDM_JWT_SIGNING_KEY。 ");
    }

    authenticationOptions.SigningKey = "development-only-pdm-signing-key-2026";
}

builder.Services.AddSingleton<IOptions<PdmDatabaseOptions>>(Options.Create(databaseOptions));
builder.Services.AddSingleton<IOptions<PdmStorageOptions>>(Options.Create(storageOptions));
builder.Services.AddSingleton<IOptions<PdmPreviewWorkerOptions>>(Options.Create(previewWorkerOptions));
builder.Services.AddSingleton<IOptions<AuthenticationOptions>>(Options.Create(authenticationOptions));

var dataProtection = builder.Services.AddDataProtection().SetApplicationName("Upton.Pdm.CrmIntegration");
if (!builder.Environment.IsDevelopment())
{
    var keyDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "secrets", "data-protection-keys"));
    Directory.CreateDirectory(keyDirectory);
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
    if (OperatingSystem.IsWindows()) dataProtection.ProtectKeysWithDpapi(protectToLocalMachine: true);
}

if (string.Equals(databaseOptions.Provider, "MySql", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddScoped<IPdmRepository, MySqlPdmRepository>();
    builder.Services.AddScoped<IProjectFileRepository, MySqlProjectFileRepository>();
    builder.Services.AddScoped<IMaterialRepository, MySqlMaterialRepository>();
    builder.Services.AddScoped<IU9InventoryRepository, MySqlU9InventoryRepository>();
    builder.Services.AddScoped<IStandardLibraryRepository, MySqlStandardLibraryRepository>();
    builder.Services.AddScoped<IMaterialRelationRepository, MySqlMaterialRelationRepository>();
    builder.Services.AddScoped<IProgramTemplateRepository, MySqlProgramTemplateRepository>();
}
else
{
    builder.Services.AddSingleton<IPdmRepository, InMemoryPdmRepository>();
    builder.Services.AddSingleton<IProjectFileRepository, InMemoryProjectFileRepository>();
    builder.Services.AddSingleton<IMaterialRepository, InMemoryMaterialRepository>();
    builder.Services.AddSingleton<IU9InventoryRepository, InMemoryU9InventoryRepository>();
    builder.Services.AddSingleton<IStandardLibraryRepository, InMemoryStandardLibraryRepository>();
    builder.Services.AddSingleton<IMaterialRelationRepository, InMemoryMaterialRelationRepository>();
    builder.Services.AddSingleton<IProgramTemplateRepository, InMemoryProgramTemplateRepository>();
}

builder.Services.AddScoped<MySqlMigrationRunner>();
builder.Services.AddSingleton<IPasswordService, Pbkdf2PasswordService>();
builder.Services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();
builder.Services.AddSingleton<IPersistentSessionTokenService, PersistentSessionTokenService>();
builder.Services.AddScoped<IFileStorage, LocalFileStorage>();
builder.Services.AddScoped<IMaterialAttachmentStorage, LocalMaterialAttachmentStorage>();
builder.Services.AddScoped<IProgramTemplateStorage, LocalProgramTemplateStorage>();
builder.Services.AddScoped<IProjectFileStorage, LocalProjectFileStorage>();
builder.Services.AddSingleton<IServerPreviewConverter, SolidWorksServerPreviewConverter>();
builder.Services.AddSingleton<IReleasePackagePublisher, AtomicReleasePackagePublisher>();
builder.Services.AddSingleton<ICrmCredentialProtector, DataProtectionCrmCredentialProtector>();
builder.Services.AddSingleton<IU9SecretProtector, DataProtectionU9SecretProtector>();
builder.Services.AddHttpClient<ICrmCustomerClient, CrmCustomerClient>(client => client.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient<IU9OpenApiClient, U9OpenApiClient>(client => client.Timeout = TimeSpan.FromSeconds(20))
    .RemoveAllLoggers();
builder.Services.AddHttpClient<IU9InventoryClient, U9OpenApiClient>(client => client.Timeout = TimeSpan.FromMinutes(2))
    .RemoveAllLoggers();
builder.Services.AddHttpClient<IU9BomQueryClient, U9OpenApiClient>(client => client.Timeout = TimeSpan.FromSeconds(20))
    .RemoveAllLoggers();
builder.Services.AddScoped<PdmWorkflowService>();
builder.Services.AddScoped<ReleaseItemCommentService>();
builder.Services.AddScoped<CrmCustomerIntegrationService>();
builder.Services.AddScoped<MaterialService>();
builder.Services.AddScoped<MaterialAttachmentService>();
builder.Services.AddScoped<StandardLibraryService>();
builder.Services.AddScoped<MaterialRelationService>();
builder.Services.AddScoped<IMaterialRelationReleaseGuard>(provider => provider.GetRequiredService<MaterialRelationService>());
builder.Services.AddScoped<ProgramTemplateService>();
builder.Services.AddScoped<ProjectFileService>();
builder.Services.AddScoped<BomHeaderService>();
builder.Services.AddScoped<U9MaterialIntegrationService>();
builder.Services.AddScoped<U9MaterialFullSyncService>();
builder.Services.AddSingleton<U9MaterialFullSyncCoordinator>();
builder.Services.AddScoped<U9InventoryService>();
builder.Services.AddSingleton<U9InventorySyncCoordinator>();
builder.Services.AddScoped<U9BomQueryService>();
builder.Services.AddScoped<U9BomWriteService>();
builder.Services.AddScoped<ProjectBomU9SyncService>();
builder.Services.AddScoped<ApprovalU9AutomationService>();
builder.Services.AddScoped<MaterialCodeSynchronizationService>();
builder.Services.AddScoped<MaterialSyncBatchService>();
builder.Services.AddHostedService<PdmBootstrapHostedService>();
builder.Services.AddHostedService<CrmCustomerSyncHostedService>();
builder.Services.AddHostedService<MaterialU9SyncHostedService>();
builder.Services.AddHostedService<U9InventorySyncHostedService>();
builder.Services.AddHostedService<MaterialU9SyncBatchHostedService>();
builder.Services.AddHostedService<ProjectFileRecycleCleanupService>();

builder.Services.AddCors(options => options.AddPolicy("PdmClients", policy => policy
    .WithOrigins("http://127.0.0.1:5173", "http://localhost:5173", "http://127.0.0.1:5175", "http://localhost:5175", "https://appassets.pdm.local")
    .AllowAnyHeader()
    .AllowAnyMethod()));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = authenticationOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = authenticationOptions.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authenticationOptions.SigningKey)),
            NameClaimType = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name",
            RoleClaimType = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
            ClockSkew = TimeSpan.FromMinutes(1)
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var versionText = context.Principal?.FindFirst("token_version")?.Value;
                if (versionText is null) return;

                var username = context.Principal?.Identity?.Name;
                var repository = context.HttpContext.RequestServices.GetRequiredService<IPdmRepository>();
                var account = string.IsNullOrWhiteSpace(username)
                    ? null
                    : await repository.FindUserAsync(username, context.HttpContext.RequestAborted);
                if (account is null || !account.IsActive || !long.TryParse(versionText, out var version) || version != account.TokenVersion)
                {
                    context.Fail("登录状态已失效，请重新登录。");
                }
            }
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddScoped<CompanySessionService>();
builder.Services.AddProblemDetails();

var app = builder.Build();
var deployedWebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
app.UseExceptionHandler(exceptionHandler => exceptionHandler.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var status = exception switch
    {
        PdmNotFoundException => StatusCodes.Status404NotFound,
        PdmConflictException => StatusCodes.Status409Conflict,
        PdmRuleException => StatusCodes.Status400BadRequest,
        UnauthorizedAccessException => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status500InternalServerError
    };
    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new ProblemDetails
    {
        Status = status,
        Title = status == 500 ? "PLM服务发生内部错误" : exception?.Message,
        Detail = status == 500 && !app.Environment.IsDevelopment() ? null : exception?.Message
    });
}));
if (Directory.Exists(deployedWebRoot))
{
    var deployedFiles = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(deployedWebRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = deployedFiles });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = deployedFiles });
}
app.UseCors("PdmClients");
app.UseAuthentication();
app.UseMiddleware<CompanyContextMiddleware>();
app.UseAuthorization();
app.MapPdmEndpoints();
app.MapPdmMaterialEndpoints();
app.MapPdmInventoryEndpoints();
app.MapStandardLibraryEndpoints();
app.MapMaterialRelationEndpoints();
app.MapPdmBomHeaderEndpoints();
app.MapProgramTemplateEndpoints();
app.MapProjectFileEndpoints();
app.MapU9BomEndpoints();
if (Directory.Exists(deployedWebRoot))
{
    app.MapGet("/{**path}", async context =>
    {
        if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/health"))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var relativePath = (context.Request.Path.Value ?? string.Empty).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var requestedFile = Path.GetFullPath(Path.Combine(deployedWebRoot, relativePath));
        var deployedRootPrefix = Path.GetFullPath(deployedWebRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (requestedFile.StartsWith(deployedRootPrefix, StringComparison.OrdinalIgnoreCase) && File.Exists(requestedFile))
        {
            var contentTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
            context.Response.ContentType = contentTypes.TryGetContentType(requestedFile, out var contentType)
                ? contentType
                : "application/octet-stream";
            await context.Response.SendFileAsync(requestedFile);
            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(Path.Combine(deployedWebRoot, "index.html"));
    });
}
try
{
    app.Run();
}
catch (IOException exception)
{
    app.Logger.LogCritical(exception, "PLM API启动失败，请确认5080端口是否已由UptonPdmApi服务占用。");
    Environment.ExitCode = 1;
}

public partial class Program;
