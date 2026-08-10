using System.Net;
using System.Net.Http.Json;
using System.ComponentModel;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json.Nodes;
using DevBrain.Core.Auth.Models;
using DevBrain.Core.Auth.Services;
using DevBrain.Functions.Tools;
using DevBrain.Server.Authentication;
using DevBrain.Server.Tools;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Server;

namespace DevBrain.Server.Tests;

public sealed class ServerEndpointTests : IClassFixture<DevBrainWebApplicationFactory>
{
    private readonly HttpClient _client;
    private readonly DevBrainWebApplicationFactory _factory;

    public ServerEndpointTests(DevBrainWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
    }

    [Fact]
    public void ServerPublishesAllTwelveDocumentToolContracts()
    {
        var toolNames = typeof(ServerDocumentTools)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(name => name is not null)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "AppendDocument",
                "ApplyEditDocument",
                "CompareDocument",
                "DeleteDocument",
                "EditTags",
                "GetDocument",
                "GetDocumentMetadata",
                "ListDocuments",
                "PreviewEditDocument",
                "SearchDocuments",
                "UpsertDocument",
                "UpsertDocumentChunked",
            },
            toolNames);
    }

    [Fact]
    public void ServerToolContractsMatchFunctionsCompatibilityHost()
    {
        var functionsContracts = ReadFunctionsContracts();
        var serverContracts = ReadServerContracts();

        Assert.Equal(functionsContracts.Length, serverContracts.Length);
        for (var index = 0; index < functionsContracts.Length; index++)
        {
            Assert.Equal(functionsContracts[index].Name, serverContracts[index].Name);
            Assert.Equal(functionsContracts[index].Description, serverContracts[index].Description);
            Assert.Equal(functionsContracts[index].Parameters, serverContracts[index].Parameters);
        }
    }

    [Fact]
    public async Task UserPolicyRequiresDevBrainUserRole()
    {
        var authorization = _factory.Services.GetRequiredService<IAuthorizationService>();
        var withoutRole = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("oid", "user-1")],
            DevBrainAuthenticationDefaults.Scheme));
        var withRole = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("oid", "user-1"), new Claim(ClaimTypes.Role, DevBrainAuthenticationDefaults.UserPolicy)],
            DevBrainAuthenticationDefaults.Scheme));

        Assert.False((await authorization.AuthorizeAsync(
            withoutRole,
            resource: null,
            DevBrainAuthenticationDefaults.UserPolicy)).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(
            withRole,
            resource: null,
            DevBrainAuthenticationDefaults.UserPolicy)).Succeeded);
    }

    [Fact]
    public async Task Healthz_IsAnonymousAndHealthy()
    {
        using var response = await _client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("healthy", body!["status"]);
    }

    [Fact]
    public async Task McpWithoutBearerToken_ReturnsProtectedResourceChallenge()
    {
        using var response = await _client.PostAsync("/mcp", JsonContent.Create(new { }));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(
            "Bearer resource_metadata=\"https://devbrain.example.com/.well-known/oauth-protected-resource\"",
            response.Headers.WwwAuthenticate.Single().ToString());
    }

    [Fact]
    public async Task ProtectedResourceMetadata_AdvertisesStatelessMcpResource()
    {
        using var response = await _client.GetAsync("/.well-known/oauth-protected-resource");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("https://devbrain.example.com/mcp", body!["resource"].ToString());
    }

    [Fact]
    public async Task AuthenticatedDiscover_AdvertisesPinnedProtocolRevision()
    {
        const string jti = "server-initialize-test";
        var store = _factory.Services.GetRequiredService<ServerTestOAuthStateStore>();
        store.UpstreamTokens[jti] = new UpstreamTokenRecord
        {
            Jti = jti,
            UserPrincipalName = "user@example.com",
            ObjectId = "00000000-0000-0000-0000-000000000001",
            TenantId = "11111111-1111-1111-1111-111111111111",
            Roles = [DevBrainAuthenticationDefaults.UserPolicy],
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        };
        var issuer = _factory.Services.GetRequiredService<DevBrainJwtIssuer>();
        var (token, _) = issuer.IssueWithJti("server-test", jti, TimeSpan.FromMinutes(5));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "server/discover",
                @params = new
                {
                    _meta = new Dictionary<string, object>
                    {
                        ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                        ["io.modelcontextprotocol/clientCapabilities"] = new { },
                        ["io.modelcontextprotocol/clientInfo"] = new { name = "DevBrain.Server.Tests", version = "1.0.0" },
                    },
                },
            }),
        };
        request.Headers.Authorization = new("Bearer", token);
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Method", "server/discover");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseText = await response.Content.ReadAsStringAsync();
        var jsonText = response.Content.Headers.ContentType?.MediaType == "text/event-stream"
            ? responseText.Split('\n', StringSplitOptions.TrimEntries)
                .Single(line => line.StartsWith("data: ", StringComparison.Ordinal))[6..]
            : responseText;
        var body = JsonNode.Parse(jsonText)!.AsObject();
        Assert.True(body["result"] is not null, responseText);
        Assert.Contains(
            "2026-07-28",
            body!["result"]!["supportedVersions"]!.AsArray().Select(node => node!.GetValue<string>()));
    }

    private static ToolContract[] ReadFunctionsContracts() =>
        typeof(DocumentTools)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method =>
            {
                var trigger = method.GetParameters()
                    .SelectMany(parameter => parameter.GetCustomAttributesData())
                    .SingleOrDefault(attribute => attribute.AttributeType.Name == "McpToolTriggerAttribute");
                if (trigger is null)
                {
                    return null;
                }

                var parameters = method.GetParameters()
                    .Select(parameter => (Parameter: parameter, Attribute: parameter.GetCustomAttributesData()
                        .SingleOrDefault(attribute => attribute.AttributeType.Name == "McpToolPropertyAttribute")))
                    .Where(item => item.Attribute is not null)
                    .Select(item => new ToolParameterContract(
                        Name: (string)item.Attribute!.ConstructorArguments[0].Value!,
                        Description: (string)item.Attribute.ConstructorArguments[1].Value!,
                        Type: item.Parameter.ParameterType,
                        Required: (bool)item.Attribute.ConstructorArguments[2].Value!))
                    .ToArray();

                return new ToolContract(
                    Name: (string)trigger.ConstructorArguments[0].Value!,
                    Description: (string)trigger.ConstructorArguments[1].Value!,
                    Parameters: parameters);
            })
            .Where(contract => contract is not null)
            .Cast<ToolContract>()
            .OrderBy(contract => contract.Name, StringComparer.Ordinal)
            .ToArray();

    private static ToolContract[] ReadServerContracts()
    {
        var nullability = new NullabilityInfoContext();
        return typeof(ServerDocumentTools)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => (Method: method, Tool: method.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(item => item.Tool is not null)
            .Select(item => new ToolContract(
                Name: item.Tool!.Name!,
                Description: item.Method.GetCustomAttribute<DescriptionAttribute>()!.Description,
                Parameters: item.Method.GetParameters()
                    .Select(parameter => new ToolParameterContract(
                        Name: parameter.Name!,
                        Description: parameter.GetCustomAttribute<DescriptionAttribute>()!.Description,
                        Type: parameter.ParameterType,
                        Required: parameter.ParameterType.IsValueType
                            ? Nullable.GetUnderlyingType(parameter.ParameterType) is null
                            : nullability.Create(parameter).ReadState == NullabilityState.NotNull))
                    .ToArray()))
            .OrderBy(contract => contract.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private sealed record ToolContract(
        string Name,
        string Description,
        ToolParameterContract[] Parameters);

    private sealed record ToolParameterContract(
        string Name,
        string Description,
        Type Type,
        bool Required);
}

public sealed class DevBrainWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("AllowedHosts", "localhost");
        builder.UseSetting("CosmosDb:AccountEndpoint", "https://localhost:8081");
        builder.UseSetting("OAuth:BaseUrl", "https://devbrain.example.com");
        builder.UseSetting("OAuth:JwtSigningSecret", "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=");
        builder.UseSetting("OAuth:EntraTenantId", "11111111-1111-1111-1111-111111111111");
        builder.UseSetting("OAuth:EntraClientId", "test-client-id");
        builder.UseSetting("OAuth:EntraClientSecret", "test-client-secret");
        builder.UseSetting("DataProtection:BlobUri", "https://example.blob.core.windows.net/dataprotection-v2/keys.xml");
        builder.UseSetting("DataProtection:KeyVaultKeyUri", "https://example.vault.azure.net/keys/data-protection-key");
        builder.ConfigureServices(services =>
        {
            services.PostConfigure<KeyManagementOptions>(options =>
            {
                options.XmlRepository = new InMemoryXmlRepository();
                options.XmlEncryptor = null;
            });
            services.RemoveAll<IOAuthStateStore>();
            services.AddSingleton<ServerTestOAuthStateStore>();
            services.AddSingleton<IOAuthStateStore>(sp => sp.GetRequiredService<ServerTestOAuthStateStore>());
        });
    }

    private sealed class InMemoryXmlRepository : IXmlRepository
    {
        private readonly List<System.Xml.Linq.XElement> _elements = [];

        public IReadOnlyCollection<System.Xml.Linq.XElement> GetAllElements() =>
            _elements.Select(element => new System.Xml.Linq.XElement(element)).ToArray();

        public void StoreElement(System.Xml.Linq.XElement element, string friendlyName) =>
            _elements.Add(new System.Xml.Linq.XElement(element));
    }
}

public sealed class ServerTestOAuthStateStore : IOAuthStateStore
{
    public Dictionary<string, UpstreamTokenRecord> UpstreamTokens { get; } = new(StringComparer.Ordinal);

    public Task<UpstreamTokenRecord?> GetUpstreamTokenAsync(string jti) =>
        Task.FromResult(UpstreamTokens.GetValueOrDefault(jti));

    public Task SaveUpstreamTokenAsync(UpstreamTokenRecord token)
    {
        UpstreamTokens[token.Jti] = token;
        return Task.CompletedTask;
    }

    public Task DeleteUpstreamTokenAsync(string jti)
    {
        UpstreamTokens.Remove(jti);
        return Task.CompletedTask;
    }

    public Task SaveClientAsync(RegisteredClient client) => throw new NotSupportedException();
    public Task<RegisteredClient?> GetClientAsync(string clientId) => throw new NotSupportedException();
    public Task SaveTransactionAsync(AuthTransaction transaction) => throw new NotSupportedException();
    public Task<AuthTransaction?> GetTransactionAsync(string upstreamState) => throw new NotSupportedException();
    public Task DeleteTransactionAsync(string upstreamState) => throw new NotSupportedException();
    public Task SaveAuthCodeAsync(DevBrainAuthCode code) => throw new NotSupportedException();
    public Task<DevBrainAuthCode?> RedeemAuthCodeAsync(string code) => throw new NotSupportedException();
    public Task SaveRefreshAsync(DevBrainRefreshRecord refresh) => throw new NotSupportedException();
    public Task<RefreshRotationResult> RotateRefreshAsync(
        string refreshToken,
        string clientId,
        string replacementRefreshToken,
        TimeSpan replacementLifetime,
        TimeSpan replayLifetime,
        TimeSpan upstreamVaultLifetime,
        string? resource = null) => throw new NotSupportedException();
    public Task<DevBrainRefreshRecord?> ConsumeRefreshAsync(string refreshToken) => throw new NotSupportedException();
}
