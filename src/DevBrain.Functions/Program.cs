using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using DevBrain.Functions.Auth.DcrFacade;
using DevBrain.Functions.Auth.Middleware;
using DevBrain.Functions.Auth.Services;
using DevBrain.Functions.Services;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

var builder = FunctionsApplication.CreateBuilder(args);

// ---------------- Application Insights (v1.6 post-deploy logging work) ----------------
//
// The isolated worker does NOT wire AI telemetry by default, even when
// APPLICATIONINSIGHTS_CONNECTION_STRING is in app settings. Without these two calls the worker's
// ILogger writes go to stdout (which Flex Consumption doesn't surface) and the Functions host
// sees nothing. See:
//   https://learn.microsoft.com/azure/azure-functions/dotnet-isolated-process-guide#application-insights
//
// AddApplicationInsightsTelemetryWorkerService() stands up the TelemetryClient and the default
// logger provider. ConfigureFunctionsApplicationInsights() adds the Functions-specific filter
// that attaches function-execution activity as AI request telemetry.
builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// The AI SDK installs a LoggerFilterRule that silently drops anything below Warning for its
// own provider, regardless of the appsettings/host.json config. This is a well-known gotcha —
// if we don't remove it, DevBrain's Information/Debug logs never reach AI. See:
//   https://learn.microsoft.com/azure/azure-monitor/app/worker-service#logging
builder.Logging.Services.Configure<LoggerFilterOptions>(options =>
{
    var defaultRule = options.Rules.FirstOrDefault(r =>
        r.ProviderName == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");
    if (defaultRule is not null)
    {
        options.Rules.Remove(defaultRule);
    }
});

// Startup fail-fast: every OAuth config value is required. Missing values would surface as 500s
// at first traffic; prefer a loud refusal to start. Single-tenant tenant GUID enforcement lives
// inside DevBrainJwtIssuer and EntraOAuthClient — those throw if TenantId isn't a GUID.
var startupConfig = builder.Configuration;
EnsureConfig(startupConfig, "CosmosDb:AccountEndpoint");
EnsureConfig(startupConfig, "OAuth:BaseUrl");
EnsureConfig(startupConfig, "OAuth:JwtSigningSecret");
EnsureConfig(startupConfig, "OAuth:EntraTenantId");
EnsureConfig(startupConfig, "OAuth:EntraClientId");
EnsureConfig(startupConfig, "OAuth:EntraClientSecret");
EnsureConfig(startupConfig, "DataProtection:BlobUri");
EnsureConfig(startupConfig, "DataProtection:KeyVaultKeyUri");

builder.Services.AddSingleton(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var endpoint = configuration["CosmosDb:AccountEndpoint"]!;
    return new CosmosClient(endpoint, (TokenCredential)new DefaultAzureCredential(), new CosmosClientOptions
    {
        UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions()
    });
});

builder.Services.AddSingleton<IDocumentStore, CosmosDocumentStore>();
builder.Services.AddSingleton<IDocumentEditService, DocumentEditService>();

// ---------------- OAuth DCR facade (v1.6) ----------------

builder.Services.AddSingleton(TimeProvider.System);

// Data Protection — key ring persisted to the existing storage account, protected by a key in
// the existing Key Vault. The Function MI already has Storage Blob Data Owner on the storage
// account and Key Vault Crypto User on the vault (both from v1.6 step 7 infra), so no new role
// assignments are required beyond what the Bicep already provisions.
//
// Used exclusively by the upstream token protector (DevBrain.OAuth.UpstreamToken purpose). JWT
// signing intentionally does NOT go through Data Protection — it uses a plain Key Vault HMAC
// secret (OAuth:JwtSigningSecret), because Data Protection's API is Protect/Unprotect and is not
// appropriate for HMAC signing key derivation. See sprint §"Scope decisions".
builder.Services
    .AddDataProtection()
    .SetApplicationName("DevBrain")
    .PersistKeysToAzureBlobStorage(new Uri(builder.Configuration["DataProtection:BlobUri"]!), new DefaultAzureCredential())
    .ProtectKeysWithAzureKeyVault(new Uri(builder.Configuration["DataProtection:KeyVaultKeyUri"]!), new DefaultAzureCredential());

builder.Services.AddSingleton<IUpstreamTokenProtector, DataProtectionUpstreamTokenProtector>();
builder.Services.AddSingleton<IOAuthStateStore, CosmosOAuthStateStore>();

// JWT issuer — signing secret + issuer + audience + tenant baked at construction.
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var baseUrl = config["OAuth:BaseUrl"]!.TrimEnd('/');
    var options = new DevBrainJwtIssuerOptions
    {
        SigningSecret = config["OAuth:JwtSigningSecret"]!,
        Issuer = baseUrl,
        // CVE-2025-69196 guard: audience MUST be the webhook URL, not the base URL.
        // The MCP extension's built-in webhook auth is disabled via
        // host.json extensions.mcp.system.webhookAuthorizationLevel = "anonymous" —
        // our worker middleware (McpJwtValidationMiddleware) is the sole gate.
        Audience = $"{baseUrl}/runtime/webhooks/mcp",
        TenantId = config["OAuth:EntraTenantId"]!,
    };
    return new DevBrainJwtIssuer(options, sp.GetRequiredService<TimeProvider>());
});

// Upstream Entra client — typed HttpClient.
builder.Services.AddHttpClient<IUpstreamOAuthClient, EntraOAuthClient>().ConfigureHttpClient((sp, _) => { /* HttpClient config set inside the client */ });
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

// OpenID Connect discovery for id_token signature validation. ConfigurationManager defaults:
// 24-hour auto-refresh, 5-minute last-known-good retention, 30-second minimum refresh interval.
// Caching lives inside the manager; we treat it as a singleton so all EntraOAuthClient instances
// share the same JWKS cache.
builder.Services.AddSingleton<IConfigurationManager<OpenIdConnectConfiguration>>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var tenantId = config["OAuth:EntraTenantId"]!;
    var metadataAddress = $"https://login.microsoftonline.com/{tenantId}/v2.0/.well-known/openid-configuration";
    return new ConfigurationManager<OpenIdConnectConfiguration>(
        metadataAddress,
        new OpenIdConnectConfigurationRetriever(),
        new HttpDocumentRetriever { RequireHttps = true });
});

// DCR facade handlers.
builder.Services.AddSingleton<RegistrationHandler>();
builder.Services.AddSingleton<AuthorizationHandler>();
builder.Services.AddSingleton<TokenHandler>();
builder.Services.AddSingleton<CallbackHandler>();

// JWT authenticator + middleware. The middleware is registered on MCP tool triggers only — the
// DCR facade HTTP endpoints are anonymous so clients can register/authorize/token without
// presenting a credential they don't yet have.
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    return new JwtAuthenticatorOptions { ExpectedTenantId = config["OAuth:EntraTenantId"]! };
});
builder.Services.AddSingleton<JwtAuthenticator>();

// Diagnostic pass-through middleware — runs unconditionally for every invocation, logs function
// name + binding types + outcome. This is how we surface the actual runtime binding-type string
// emitted by the MCP extension (v1.6 post-deploy investigation). Registered FIRST so it sees
// every invocation before any conditional middleware runs.
builder.UseMiddleware<InvocationDiagnosticMiddleware>();

builder.UseWhen<McpJwtValidationMiddleware>(ctx =>
    ctx.FunctionDefinition.InputBindings.Values.Any(b => b.Type == "mcpToolTrigger"));

builder.Build().Run();

static void EnsureConfig(IConfiguration config, string key)
{
    if (string.IsNullOrWhiteSpace(config[key]))
    {
        throw new InvalidOperationException($"Required configuration value '{key}' is missing or empty. " +
            "Set it via app settings, environment variable (double-underscore form), or local.settings.json.");
    }
}
