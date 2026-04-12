using System.Net;
using System.Text;
using System.Text.Json;
using DevBrain.Functions.Auth.Services;
using DevBrain.Functions.Tests.TestHelpers;

namespace DevBrain.Functions.Tests.Auth.Services;

/// <summary>
/// Unit tests for <see cref="EntraOAuthClient"/>. Uses <see cref="FakeHttpMessageHandler"/> to capture
/// outbound requests and <see cref="FakeOpenIdConfigurationManager"/> to supply a known signing key
/// that the client's id_token validator will accept. Acceptance gate #10 (id_token JWKS validation)
/// has its own file — <see cref="EntraOAuthClientIdTokenValidationTests"/> — so this file only
/// covers the happy-path shape tests.
/// </summary>
public sealed class EntraOAuthClientTests
{
    private const string TenantGuid = "11111111-1111-1111-1111-111111111111";
    private const string ClientId = "upstream-client-id";
    private static readonly string Issuer = $"https://login.microsoftonline.com/{TenantGuid}/v2.0";

    private static EntraOAuthClientOptions ValidOptions() => new()
    {
        TenantId = TenantGuid,
        ClientId = ClientId,
        ClientSecret = "upstream-client-secret",
        RedirectUri = "https://devbrain.example.com/callback",
        Scope = "openid profile offline_access documents.readwrite",
    };

    /// <summary>Harness that wires a valid signing key through the fake config manager so id_token validation passes.</summary>
    private sealed record Harness(
        EntraOAuthClient Client,
        FakeHttpMessageHandler Handler,
        HttpClient HttpClient,
        FakeOpenIdConfigurationManager ConfigManager,
        IdTokenKeyPair Keys);

    private static Harness CreateHarness(Func<HttpRequestMessage, HttpResponseMessage>? responder = null)
    {
        var handler = new FakeHttpMessageHandler(responder ?? (_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var http = new HttpClient(handler);
        var configManager = FakeOpenIdConfigurationManager.ForTenant(TenantGuid);
        var keys = TestJwtFactory.CreateKeyPair();
        configManager.AddSigningKey(keys.VerificationKey);
        var client = new EntraOAuthClient(http, ValidOptions(), configManager);
        return new Harness(client, handler, http, configManager, keys);
    }

    private static string SampleSignedIdToken(Harness h) =>
        TestJwtFactory.CreateSignedIdToken(
            h.Keys.SigningKey,
            claims: new Dictionary<string, object>
            {
                ["preferred_username"] = "derek@ignitesolutions.group",
                ["oid"] = "00000000-0000-0000-0000-000000000001",
                ["tid"] = TenantGuid,
            },
            issuer: Issuer,
            audience: ClientId);

    [Fact]
    public void Constructor_NonGuidTenantId_Throws()
    {
        var options = ValidOptions();
        options.TenantId = "common";

        using var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler);
        var configManager = FakeOpenIdConfigurationManager.ForTenant(TenantGuid);

        var ex = Assert.Throws<ArgumentException>(() => new EntraOAuthClient(http, options, configManager));
        Assert.Contains("GUID", ex.Message);
        Assert.Contains("Single-tenant", ex.Message);
    }

    [Fact]
    public void Constructor_ValidOptions_BakesAuthorityBaseAddress()
    {
        var h = CreateHarness();

        Assert.Equal($"https://login.microsoftonline.com/{TenantGuid}/oauth2/v2.0/", h.HttpClient.BaseAddress!.ToString());
    }

    [Fact]
    public void BuildAuthorizeUri_IncludesDevBrainsChallengeNotTheClients()
    {
        var h = CreateHarness();

        var uri = h.Client.BuildAuthorizeUri("upstream-state-xyz", "upstream-challenge-abc");

        Assert.Equal("login.microsoftonline.com", uri.Host);
        Assert.Contains($"/{TenantGuid}/oauth2/v2.0/authorize", uri.AbsolutePath);

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal(ClientId, query["client_id"]);
        Assert.Equal("https://devbrain.example.com/callback", query["redirect_uri"]);
        Assert.Equal("upstream-state-xyz", query["state"]);
        Assert.Equal("upstream-challenge-abc", query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("openid profile offline_access documents.readwrite", query["scope"]);
    }

    [Fact]
    public async Task ExchangeCodeAsync_PostsFormWithExpectedFields_AndValidatesIdToken()
    {
        // Two-phase harness: create keys + config FIRST, then configure the handler to return a
        // token response whose id_token is signed by those keys.
        var keys = TestJwtFactory.CreateKeyPair();
        var configManager = FakeOpenIdConfigurationManager.ForTenant(TenantGuid);
        configManager.AddSigningKey(keys.VerificationKey);

        var idToken = TestJwtFactory.CreateSignedIdToken(
            keys.SigningKey,
            claims: new Dictionary<string, object>
            {
                ["preferred_username"] = "derek@ignitesolutions.group",
                ["oid"] = "00000000-0000-0000-0000-000000000001",
                ["tid"] = TenantGuid,
            },
            issuer: Issuer,
            audience: ClientId);

        var tokenJson = JsonSerializer.Serialize(new
        {
            access_token = "at",
            refresh_token = "rt",
            id_token = idToken,
            expires_in = 3600,
        });

        using var handler = FakeHttpMessageHandler.ReturningJson(tokenJson);
        using var http = new HttpClient(handler);
        var client = new EntraOAuthClient(http, ValidOptions(), configManager);

        var result = await client.ExchangeCodeAsync("entra-code-xyz", "upstream-verifier-abc");

        Assert.Equal("at", result.AccessToken);
        Assert.Equal("rt", result.RefreshToken);
        Assert.Equal("derek@ignitesolutions.group", result.UserPrincipalName);
        Assert.Equal("00000000-0000-0000-0000-000000000001", result.ObjectId);
        Assert.Equal(TenantGuid, result.TenantId);
        Assert.Equal(TimeSpan.FromSeconds(3600), result.ExpiresIn);

        var request = Assert.Single(handler.ReceivedRequests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.EndsWith("/token", request.RequestUri!.AbsolutePath);
        Assert.NotNull(request.Body);
        var form = System.Web.HttpUtility.ParseQueryString(request.Body);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("entra-code-xyz", form["code"]);
        Assert.Equal("upstream-verifier-abc", form["code_verifier"]);
        Assert.Equal(ClientId, form["client_id"]);
        Assert.Equal("upstream-client-secret", form["client_secret"]);
        Assert.Equal("https://devbrain.example.com/callback", form["redirect_uri"]);

        // The id_token validator actually fetched the discovery config.
        Assert.Equal(1, configManager.FetchCalls);
    }

    [Fact]
    public async Task RefreshTokenAsync_UsesRefreshGrant()
    {
        var h = CreateHarness(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                access_token = "new-at",
                refresh_token = "new-rt",
                id_token = SampleSignedIdToken(CreateHarness()),
                expires_in = 3600,
            }), Encoding.UTF8, "application/json"),
        });

        // The sample token above was signed by a DIFFERENT harness's keys — so it would fail
        // validation. Re-do the harness properly: mint the id_token inside the handler closure
        // using the SAME harness's keys.
        var keys = TestJwtFactory.CreateKeyPair();
        var configManager = FakeOpenIdConfigurationManager.ForTenant(TenantGuid);
        configManager.AddSigningKey(keys.VerificationKey);

        var idToken = TestJwtFactory.CreateSignedIdToken(
            keys.SigningKey,
            claims: new Dictionary<string, object>
            {
                ["preferred_username"] = "derek@ignitesolutions.group",
                ["oid"] = "00000000-0000-0000-0000-000000000001",
                ["tid"] = TenantGuid,
            },
            issuer: Issuer,
            audience: ClientId);

        var tokenJson = JsonSerializer.Serialize(new
        {
            access_token = "new-at",
            refresh_token = "new-rt",
            id_token = idToken,
            expires_in = 3600,
        });

        using var handler = FakeHttpMessageHandler.ReturningJson(tokenJson);
        using var http = new HttpClient(handler);
        var client = new EntraOAuthClient(http, ValidOptions(), configManager);

        var result = await client.RefreshTokenAsync("old-rt");
        Assert.Equal("new-at", result.AccessToken);
        Assert.Equal("new-rt", result.RefreshToken);

        var request = Assert.Single(handler.ReceivedRequests);
        Assert.NotNull(request.Body);
        var form = System.Web.HttpUtility.ParseQueryString(request.Body);
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("old-rt", form["refresh_token"]);
        Assert.Null(form["redirect_uri"]);
    }

    [Fact]
    public async Task ExchangeCodeAsync_UpstreamError_ThrowsUpstreamOAuthException()
    {
        using var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"invalid_grant","error_description":"bad code"}""", Encoding.UTF8, "application/json"),
        });
        using var http = new HttpClient(handler);
        var configManager = FakeOpenIdConfigurationManager.ForTenant(TenantGuid);
        var client = new EntraOAuthClient(http, ValidOptions(), configManager);

        var ex = await Assert.ThrowsAsync<UpstreamOAuthException>(
            () => client.ExchangeCodeAsync("bad-code", "verifier"));
        Assert.Contains("400", ex.Message);
        Assert.Contains("invalid_grant", ex.Message);
    }

    [Fact]
    public async Task ExchangeCodeAsync_MissingIdToken_ThrowsUpstreamOAuthException()
    {
        var tokenJson = JsonSerializer.Serialize(new
        {
            access_token = "at",
            refresh_token = "rt",
            expires_in = 3600,
        });

        using var handler = FakeHttpMessageHandler.ReturningJson(tokenJson);
        using var http = new HttpClient(handler);
        var configManager = FakeOpenIdConfigurationManager.ForTenant(TenantGuid);
        var client = new EntraOAuthClient(http, ValidOptions(), configManager);

        var ex = await Assert.ThrowsAsync<UpstreamOAuthException>(
            () => client.ExchangeCodeAsync("code", "verifier"));
        Assert.Contains("id_token", ex.Message);
    }
}
