using System.Text.Json;
using System.Text.Json.Serialization;
using DevBrain.Core.Auth.DcrFacade;
using DevBrain.Functions.Tests.Auth.Services;
using DevBrain.Functions.Tests.TestHelpers;
using Microsoft.Extensions.Time.Testing;

namespace DevBrain.Functions.Tests.Auth.DcrFacade;

/// <summary>
/// Unit tests for <see cref="RegistrationHandler"/>. The HTTP adapter (<c>RegisterEndpoint</c>) is a
/// thin wrapper over this class and doesn't need its own unit tests — it just parses JSON and shapes
/// the response.
/// </summary>
public sealed class RegistrationHandlerTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 4, 11, 0, 0, 0, TimeSpan.Zero);

    private static (RegistrationHandler handler, FakeOAuthStateStore store, FakeTimeProvider clock) Create(
        RecordingLogger<RegistrationHandler>? logger = null)
    {
        var clock = new FakeTimeProvider(Epoch);
        var store = new FakeOAuthStateStore(clock);
        var handler = new RegistrationHandler(store, clock, logger);
        return (handler, store, clock);
    }

    [Fact]
    public async Task ValidRequest_ReturnsClientIdAndPersists()
    {
        var (handler, store, _) = Create();

        var result = await handler.HandleAsync(new RegistrationRequest(
            RedirectUris: ["https://localhost:8000/callback"],
            ClientName: "Claude Code CLI"));

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Response);
        Assert.NotEmpty(result.Response.ClientId);
        Assert.Equal("Claude Code CLI", result.Response.ClientName);
        Assert.Equal(["https://localhost:8000/callback"], result.Response.RedirectUris);
        Assert.Equal("web", result.Response.ApplicationType);
        Assert.Equal("none", result.Response.TokenEndpointAuthMethod);
        Assert.Equal(Epoch.ToUnixTimeSeconds(), result.Response.ClientIdIssuedAt);

        var stored = await store.GetClientAsync(result.Response.ClientId);
        Assert.NotNull(stored);
        Assert.Equal("Claude Code CLI", stored.ClientName);
        Assert.Equal("web", stored.ApplicationType);
    }

    [Fact]
    public async Task MissingRedirectUris_ReturnsError()
    {
        var (handler, _, _) = Create();

        var result = await handler.HandleAsync(new RegistrationRequest(RedirectUris: null, ClientName: "X"));

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_redirect_uri", result.ErrorCode);
    }

    [Fact]
    public async Task EmptyRedirectUris_ReturnsError()
    {
        var (handler, _, _) = Create();

        var result = await handler.HandleAsync(new RegistrationRequest(RedirectUris: [], ClientName: "X"));

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_redirect_uri", result.ErrorCode);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("mailto:foo@bar.com")]
    [InlineData("ftp://example.com/")]
    [InlineData("javascript:alert(1)")]
    public async Task InvalidRedirectUriScheme_ReturnsError(string uri)
    {
        var (handler, _, _) = Create();

        var result = await handler.HandleAsync(new RegistrationRequest(RedirectUris: [uri], ClientName: null));

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_redirect_uri", result.ErrorCode);
    }

    [Fact]
    public async Task Diagnostics_DoNotRenderRawRegistrationValues()
    {
        var logger = new RecordingLogger<RegistrationHandler>();
        var (handler, _, _) = Create(logger);
        const string maliciousName = "Client\r\nFORGED-REGISTRATION-LOG";

        var result = await handler.HandleAsync(new RegistrationRequest(
            ["javascript:alert(1)\r\nFORGED-REDIRECT-LOG"],
            maliciousName));

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_redirect_uri", result.ErrorCode);
        Assert.NotEmpty(logger.Messages);
        Assert.All(logger.Messages, message =>
        {
            Assert.DoesNotContain("FORGED", message, StringComparison.Ordinal);
            Assert.DoesNotContain('\r', message);
            Assert.DoesNotContain('\n', message);
        });
    }

    [Fact]
    public async Task NativeApplicationType_IsPersisted()
    {
        var (handler, store, _) = Create();

        var result = await handler.HandleAsync(new RegistrationRequest(
            ["http://127.0.0.1:43123/callback"],
            "Desktop Client",
            ApplicationType: "native"));

        Assert.True(result.IsSuccess);
        Assert.Equal("native", result.Response!.ApplicationType);
        Assert.Equal("native", (await store.GetClientAsync(result.Response.ClientId))!.ApplicationType);
    }

    [Fact]
    public async Task NonLoopbackHttpRedirect_IsRejected()
    {
        var (handler, _, _) = Create();

        var result = await handler.HandleAsync(new RegistrationRequest(
            ["http://example.com/callback"],
            "Unsafe Client",
            ApplicationType: "native"));

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_redirect_uri", result.ErrorCode);
    }

    [Fact]
    public async Task UnknownApplicationType_IsRejected()
    {
        var (handler, _, _) = Create();

        var result = await handler.HandleAsync(new RegistrationRequest(
            ["https://example.com/callback"],
            "Client",
            ApplicationType: "service"));

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_client_metadata", result.ErrorCode);
    }

    [Fact]
    public async Task SubsequentCalls_ReturnDistinctClientIds()
    {
        var (handler, _, _) = Create();

        var r1 = await handler.HandleAsync(new RegistrationRequest(["https://a.example/cb"], null));
        var r2 = await handler.HandleAsync(new RegistrationRequest(["https://b.example/cb"], null));

        Assert.NotEqual(r1.Response!.ClientId, r2.Response!.ClientId);
    }

    [Fact]
    public void RegistrationRequest_DeserializesRfc7591SnakeCaseJson()
    {
        // Regression: v1.6 post-deploy bug where Claude Desktop's spec-compliant DCR body
        // (redirect_uris, client_name) silently deserialized to nulls because the endpoint's
        // JsonSerializerDefaults.Web options applied camelCase naming and the RegistrationRequest
        // record had no [JsonPropertyName] attributes. The handler then rejected every real
        // client with "invalid_redirect_uri". Guard against regression by exercising the same
        // JsonOptions the RegisterEndpoint uses.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        // Real-shaped body from RFC 7591 §2 — exactly what spec-compliant DCR clients send.
        const string rfc7591Body = """
        {
            "redirect_uris": ["https://localhost:8000/callback", "http://localhost:8000/oauth/callback"],
            "client_name": "Claude Desktop",
            "application_type": "native",
            "token_endpoint_auth_method": "none",
            "grant_types": ["authorization_code", "refresh_token"],
            "response_types": ["code"]
        }
        """;

        var request = JsonSerializer.Deserialize<RegistrationRequest>(rfc7591Body, options);

        Assert.NotNull(request);
        Assert.Equal("Claude Desktop", request.ClientName);
        Assert.Equal("native", request.ApplicationType);
        Assert.NotNull(request.RedirectUris);
        Assert.Equal(2, request.RedirectUris.Length);
        Assert.Equal("https://localhost:8000/callback", request.RedirectUris[0]);
        Assert.Equal("http://localhost:8000/oauth/callback", request.RedirectUris[1]);
    }

    [Fact]
    public void RegistrationRequest_IgnoresUnknownRfc7591Fields()
    {
        // Defensive: RFC 7591 defines many optional client metadata fields we don't care about
        // (logo_uri, jwks_uri, software_id, etc.). System.Text.Json ignores unknown properties by
        // default, but this test pins that behavior so a future serializer-option change doesn't
        // silently flip it on and start rejecting real-world DCR bodies.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        const string bodyWithExtras = """
        {
            "redirect_uris": ["https://localhost:8000/callback"],
            "client_name": "Test Client",
            "logo_uri": "https://example.com/logo.png",
            "jwks_uri": "https://example.com/.well-known/jwks.json",
            "software_id": "4NRB1-0XZABZI9E6-5SM3R",
            "software_version": "1.0.0"
        }
        """;

        var request = JsonSerializer.Deserialize<RegistrationRequest>(bodyWithExtras, options);

        Assert.NotNull(request);
        Assert.Equal("Test Client", request.ClientName);
        Assert.Single(request.RedirectUris!);
    }

    [Fact]
    public async Task StoredClient_HasNinetyDayTtl()
    {
        var (handler, store, clock) = Create();

        var result = await handler.HandleAsync(new RegistrationRequest(["https://a.example/cb"], null));

        // 89 days later: still valid
        clock.Advance(TimeSpan.FromDays(89));
        Assert.NotNull(await store.GetClientAsync(result.Response!.ClientId));

        // 91 days later: expired
        clock.Advance(TimeSpan.FromDays(2));
        Assert.Null(await store.GetClientAsync(result.Response.ClientId));
    }
}
