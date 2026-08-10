using System.Globalization;
using System.Text.Json;
using System.Threading.RateLimiting;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using DevBrain.Core.Auth.DcrFacade;
using DevBrain.Core.Auth.Middleware;
using DevBrain.Core.Auth.Services;
using DevBrain.Core.Services;
using DevBrain.Server.Auth;
using DevBrain.Server.Authentication;
using DevBrain.Server.Tools;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Azure.Cosmos;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;

EnsureConfig(configuration, "CosmosDb:AccountEndpoint");
EnsureConfig(configuration, "OAuth:BaseUrl");
EnsureConfig(configuration, "OAuth:JwtSigningSecret");
EnsureConfig(configuration, "OAuth:EntraTenantId");
EnsureConfig(configuration, "OAuth:EntraClientId");
EnsureConfig(configuration, "OAuth:EntraClientSecret");
EnsureConfig(configuration, "DataProtection:BlobUri");
EnsureConfig(configuration, "DataProtection:KeyVaultKeyUri");

var maxRequestBodySize = ReadPositiveInt(configuration, "Server:MaxRequestBodySizeBytes", 4 * 1024 * 1024);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maxRequestBodySize);

var applicationInsightsConnectionString = configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
if (!string.IsNullOrWhiteSpace(applicationInsightsConnectionString))
{
    builder.Services
        .AddOpenTelemetry()
        .UseAzureMonitor(options => options.ConnectionString = applicationInsightsConnectionString);
}

builder.Services.AddProblemDetails();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddSingleton(sp =>
{
    var endpoint = sp.GetRequiredService<IConfiguration>()["CosmosDb:AccountEndpoint"]!;
    return new CosmosClient(endpoint, (TokenCredential)new DefaultAzureCredential(), new CosmosClientOptions
    {
        UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions(),
    });
});

builder.Services.AddSingleton<IDocumentStore, CosmosDocumentStore>();
builder.Services.AddSingleton<IDocumentEditService, DocumentEditService>();
builder.Services.AddSingleton<ITagEditService, TagEditService>();

builder.Services
    .AddDataProtection()
    .SetApplicationName("DevBrain.v2")
    .PersistKeysToAzureBlobStorage(new Uri(configuration["DataProtection:BlobUri"]!), new DefaultAzureCredential())
    .ProtectKeysWithAzureKeyVault(new Uri(configuration["DataProtection:KeyVaultKeyUri"]!), new DefaultAzureCredential());

builder.Services.AddSingleton<IUpstreamTokenProtector, DataProtectionUpstreamTokenProtector>();
builder.Services.AddSingleton<IOAuthStateStore, CosmosOAuthStateStore>();

var tokenHandlerOptions = new TokenHandlerOptions(
    AccessTokenLifetime: TimeSpan.FromMinutes(ReadWholeMinutes(
        configuration,
        "OAuth:AccessTokenLifetimeMinutes",
        (int)TokenHandlerOptions.Default.AccessTokenLifetime.TotalMinutes)),
    RefreshReplayLifetime: TimeSpan.FromMinutes(ReadWholeMinutes(
        configuration,
        "OAuth:RefreshReplayLifetimeMinutes",
        (int)TokenHandlerOptions.Default.RefreshReplayLifetime.TotalMinutes)));
tokenHandlerOptions.Validate();
builder.Services.AddSingleton(tokenHandlerOptions);

builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var baseUrl = config["OAuth:BaseUrl"]!.TrimEnd('/');
    return new DevBrainJwtIssuer(
        new DevBrainJwtIssuerOptions
        {
            SigningSecret = config["OAuth:JwtSigningSecret"]!,
            Issuer = baseUrl,
            Audience = $"{baseUrl}/mcp",
            TenantId = config["OAuth:EntraTenantId"]!,
        },
        sp.GetRequiredService<TimeProvider>());
});

builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    return new EntraOAuthClientOptions
    {
        TenantId = config["OAuth:EntraTenantId"]!,
        ClientId = config["OAuth:EntraClientId"]!,
        ClientSecret = config["OAuth:EntraClientSecret"]!,
        RedirectUri = $"{config["OAuth:BaseUrl"]!.TrimEnd('/')}/callback",
        Scope = config["OAuth:EntraScope"] ?? "openid profile offline_access",
    };
});
builder.Services.AddHttpClient<IUpstreamOAuthClient, EntraOAuthClient>();
builder.Services.AddSingleton<IConfigurationManager<OpenIdConnectConfiguration>>(sp =>
{
    var tenantId = sp.GetRequiredService<IConfiguration>()["OAuth:EntraTenantId"]!;
    return new ConfigurationManager<OpenIdConnectConfiguration>(
        $"https://login.microsoftonline.com/{tenantId}/v2.0/.well-known/openid-configuration",
        new OpenIdConnectConfigurationRetriever(),
        new HttpDocumentRetriever { RequireHttps = true });
});

builder.Services.AddSingleton<RegistrationHandler>();
builder.Services.AddSingleton<AuthorizationHandler>();
builder.Services.AddSingleton<TokenHandler>();
builder.Services.AddSingleton<CallbackHandler>();
builder.Services.AddSingleton(sp => new JwtAuthenticatorOptions
{
    ExpectedTenantId = sp.GetRequiredService<IConfiguration>()["OAuth:EntraTenantId"]!,
});
builder.Services.AddSingleton<JwtAuthenticator>();

builder.Services
    .AddAuthentication(DevBrainAuthenticationDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, DevBrainAuthenticationHandler>(
        DevBrainAuthenticationDefaults.Scheme,
        _ => { });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(
        DevBrainAuthenticationDefaults.UserPolicy,
        policy => policy
            .AddAuthenticationSchemes(DevBrainAuthenticationDefaults.Scheme)
            .RequireAuthenticatedUser()
            .RequireRole(DevBrainAuthenticationDefaults.UserPolicy));
});

var rateLimitPermitCount = ReadPositiveInt(configuration, "RateLimit:PermitLimit", 120);
var rateLimitWindowSeconds = ReadPositiveInt(configuration, "RateLimit:WindowSeconds", 60);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("public", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.User.FindFirst("oid")?.Value
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = rateLimitPermitCount,
            Window = TimeSpan.FromSeconds(rateLimitWindowSeconds),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));
});

var allowedOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
if (allowedOrigins.Length > 0)
{
    builder.Services.AddCors(options => options.AddPolicy(
        "ConfiguredOrigins",
        policy => policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));
}

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "DevBrain",
            Title = "DevBrain",
            Version = "2.0.0",
            Description = "Persistent developer knowledge shared across MCP clients.",
        };
        options.ServerInstructions =
            "Use colon-separated document keys. Use preview/apply for safe exact-text edits and metadata/compare before retrieving or rewriting large documents.";
    })
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<ServerDocumentTools>();

var app = builder.Build();

app.UseExceptionHandler();
if (allowedOrigins.Length > 0)
{
    app.UseCors();
}
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }))
    .AllowAnonymous();
app.MapDevBrainOAuth("public");

var mcpEndpoint = app.MapMcp("/mcp")
    .RequireAuthorization(DevBrainAuthenticationDefaults.UserPolicy)
    .RequireRateLimiting("public");
if (allowedOrigins.Length > 0)
{
    mcpEndpoint.RequireCors("ConfiguredOrigins");
}

app.Logger.LogInformation(
    "DevBrain v2 configured MCP stateless=true accessTokenLifetimeMinutes={AccessTokenLifetimeMinutes} refreshReplayLifetimeMinutes={RefreshReplayLifetimeMinutes}",
    (int)tokenHandlerOptions.AccessTokenLifetime.TotalMinutes,
    (int)tokenHandlerOptions.RefreshReplayLifetime.TotalMinutes);

app.Run();

static void EnsureConfig(IConfiguration config, string key)
{
    if (string.IsNullOrWhiteSpace(config[key]))
    {
        throw new InvalidOperationException($"Required configuration value '{key}' is missing or empty.");
    }
}

static int ReadWholeMinutes(IConfiguration config, string key, int defaultValue)
{
    var value = config[key];
    if (string.IsNullOrWhiteSpace(value))
    {
        return defaultValue;
    }

    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
    {
        throw new InvalidOperationException($"Configuration value '{key}' must be a whole number of minutes.");
    }

    return minutes;
}

static int ReadPositiveInt(IConfiguration config, string key, int defaultValue)
{
    var value = config[key];
    if (string.IsNullOrWhiteSpace(value))
    {
        return defaultValue;
    }

    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
    {
        throw new InvalidOperationException($"Configuration value '{key}' must be a positive whole number.");
    }

    return parsed;
}

public partial class Program;
