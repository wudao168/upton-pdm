using System.Text;
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
builder.Host.UseWindowsService(options => options.ServiceName = "UPTON PDM API");
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
builder.Services.AddSingleton<IOptions<AuthenticationOptions>>(Options.Create(authenticationOptions));

if (string.Equals(databaseOptions.Provider, "MySql", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddScoped<IPdmRepository, MySqlPdmRepository>();
}
else
{
    builder.Services.AddSingleton<IPdmRepository, InMemoryPdmRepository>();
}

builder.Services.AddScoped<MySqlMigrationRunner>();
builder.Services.AddSingleton<IPasswordService, Pbkdf2PasswordService>();
builder.Services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();
builder.Services.AddScoped<IFileStorage, LocalFileStorage>();
builder.Services.AddSingleton<IReleasePackagePublisher, AtomicReleasePackagePublisher>();
builder.Services.AddScoped<PdmWorkflowService>();
builder.Services.AddHostedService<PdmBootstrapHostedService>();

builder.Services.AddCors(options => options.AddPolicy("PdmClients", policy => policy
    .WithOrigins("http://127.0.0.1:5173", "http://localhost:5173", "https://appassets.pdm.local")
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
    });
builder.Services.AddAuthorization();
builder.Services.AddProblemDetails();

var app = builder.Build();
app.UseExceptionHandler(exceptionHandler => exceptionHandler.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var status = exception switch
    {
        PdmNotFoundException => StatusCodes.Status404NotFound,
        PdmConflictException => StatusCodes.Status409Conflict,
        PdmRuleException => StatusCodes.Status400BadRequest,
        UnauthorizedAccessException => StatusCodes.Status401Unauthorized,
        _ => StatusCodes.Status500InternalServerError
    };
    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new ProblemDetails
    {
        Status = status,
        Title = status == 500 ? "PDM服务发生内部错误" : exception?.Message,
        Detail = status == 500 && !app.Environment.IsDevelopment() ? null : exception?.Message
    });
}));
app.UseCors("PdmClients");
app.UseAuthentication();
app.UseAuthorization();
app.MapPdmEndpoints();
try
{
    app.Run();
}
catch (IOException exception)
{
    app.Logger.LogCritical(exception, "PDM API启动失败，请确认5080端口是否已由UptonPdmApi服务占用。");
    Environment.ExitCode = 1;
}

public partial class Program;
