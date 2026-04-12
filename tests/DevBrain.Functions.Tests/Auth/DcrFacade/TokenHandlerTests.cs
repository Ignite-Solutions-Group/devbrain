using DevBrain.Functions.Auth.Crypto;
using DevBrain.Functions.Auth.DcrFacade;
using DevBrain.Functions.Auth.Models;
using DevBrain.Functions.Auth.Services;
using DevBrain.Functions.Tests.Auth.Services;
using Microsoft.Extensions.Time.Testing;

namespace DevBrain.Functions.Tests.Auth.DcrFacade;

/// <summary>
/// Unit tests for <see cref="TokenHandler"/>. Directly verifies acceptance gates #2 (PKCE downgrade),
/// #3 (code replay), and #5 (refresh rotation).
/// </summary>
public sealed class TokenHandlerTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 4, 11, 0, 0, 0, TimeSpan.Zero);
    private const string ClientId = "test-client";
    private const string ClientRedirect = "https://localhost:8000/callback";
    private const string Issuer = "https://devbrain.example.com";
    private const string Audience = "https://devbrain.example.com/runtime/webhooks/mcp";
    private const string TestTenantId = "11111111-1111-1111-1111-111111111111";

    private sealed record Harness(
        TokenHandler Handler,
        FakeOAuthStateStore Store,
        DevBrainJwtIssuer JwtIssuer,
        FakeTimeProvider Clock);

    private static Harness Create()
    {
        var clock = new FakeTimeProvider(Epoch);
        var store = new FakeOAuthStateStore(clock);
        var jwtIssuer = new DevBrainJwtIssuer(
            new DevBrainJwtIssuerOptions
            {
                SigningSecret = DevBrainJwtIssuer.GenerateSigningSecret(),
                Issuer = Issuer,
                Audience = Audience,
                TenantId = TestTenantId,
            },
            clock);
        var handler = new TokenHandler(store, jwtIssuer, clock);
        return new Harness(handler, store, jwtIssuer, clock);
    }

    /// <summary>Seeds an auth code + upstream vault entry, mirroring what /callback would have done.</summary>
    private static async Task<(string code, string verifier, string upstreamJti)> SeedAuthCodeAsync(Harness h)
    {
        var (verifier, challenge) = Pkce.GenerateChallengePair();
        var code = Guid.NewGuid().ToString("N");
        var jti = DevBrainJwtIssuer.NewJti();

        await h.Store.SaveAuthCodeAsync(new DevBrainAuthCode
        {
            Code = code,
            ClientId = ClientId,
            ClientRedirectUri = ClientRedirect,
            ClientCodeChallenge = challenge,
            ClientCodeChallengeMethod = "S256",
            UpstreamJti = jti,
            CreatedAt = Epoch,
            ExpiresAt = Epoch.AddMinutes(5),
        });

        await h.Store.SaveUpstreamTokenAsync(new UpstreamTokenRecord
        {
            Jti = jti,
            Envelope = new UpstreamTokenEnvelope("at", "rt", 0),
            UserPrincipalName = "derek@ignitesolutions.group",
            ObjectId = "00000000-0000-0000-0000-000000000001",
            TenantId = "tenant-guid",
            CreatedAt = Epoch,
            ExpiresAt = Epoch.AddHours(1),
        });

        return (code, verifier, jti);
    }

    [Fact]
    public async Task AuthorizationCode_ValidRequest_ReturnsJwtAndRefresh()
    {
        var h = Create();
        var (code, verifier, _) = await SeedAuthCodeAsync(h);

        var result = await h.Handler.HandleAsync(new TokenRequest(
            GrantType: "authorization_code",
            ClientId: ClientId,
            Code: code,
            CodeVerifier: verifier,
            RedirectUri: ClientRedirect,
            RefreshToken: null));

        Assert.True(result.IsSuccess);
        Assert.NotEmpty(result.Response!.AccessToken);
        Assert.NotEmpty(result.Response.RefreshToken);
        Assert.Equal("Bearer", result.Response.TokenType);
        Assert.Equal(600, result.Response.ExpiresIn);

        // The issued JWT must validate against the same issuer (round-trip via signing material).
        var validation = await h.JwtIssuer.ValidateAsync(result.Response.AccessToken);
        Assert.True(validation.IsValid);
    }

    /// <summary>Gate #3: authorization code replay rejection.</summary>
    [Fact]
    public async Task AuthorizationCode_ReplayRejected()
    {
        var h = Create();
        var (code, verifier, _) = await SeedAuthCodeAsync(h);

        var first = await h.Handler.HandleAsync(new TokenRequest(
            "authorization_code", ClientId, code, verifier, ClientRedirect, null));
        var second = await h.Handler.HandleAsync(new TokenRequest(
            "authorization_code", ClientId, code, verifier, ClientRedirect, null));

        Assert.True(first.IsSuccess);
        Assert.False(second.IsSuccess);
        Assert.Equal("invalid_grant", second.ErrorCode);
    }

    /// <summary>Gate #2: PKCE downgrade rejection.</summary>
    [Theory]
    [InlineData("WrongVerifierEntirelyXXXXXXXXXXXXXXXXXXXXXXX")]  // mismatched, length ok
    [InlineData("")]                                               // empty
    [InlineData("tooshort")]                                       // under 43-char minimum
    public async Task AuthorizationCode_BadVerifier_Rejected(string badVerifier)
    {
        var h = Create();
        var (code, _, _) = await SeedAuthCodeAsync(h);

        var result = await h.Handler.HandleAsync(new TokenRequest(
            "authorization_code", ClientId, code, badVerifier, ClientRedirect, null));

        Assert.False(result.IsSuccess);
        Assert.True(result.ErrorCode is "invalid_grant" or "invalid_request",
            $"Expected invalid_grant or invalid_request, got {result.ErrorCode}.");
    }

    [Fact]
    public async Task AuthorizationCode_VerifierLongerThanAllowed_Rejected()
    {
        var h = Create();
        var (code, _, _) = await SeedAuthCodeAsync(h);

        var longVerifier = new string('a', 129); // RFC 7636 max is 128
        var result = await h.Handler.HandleAsync(new TokenRequest(
            "authorization_code", ClientId, code, longVerifier, ClientRedirect, null));

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_grant", result.ErrorCode);
    }

    [Fact]
    public async Task AuthorizationCode_WrongClientId_Rejected()
    {
        var h = Create();
        var (code, verifier, _) = await SeedAuthCodeAsync(h);

        var result = await h.Handler.HandleAsync(new TokenRequest(
            "authorization_code", "different-client", code, verifier, ClientRedirect, null));

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_grant", result.ErrorCode);
    }

    [Fact]
    public async Task AuthorizationCode_RedirectUriMismatch_Rejected()
    {
        var h = Create();
        var (code, verifier, _) = await SeedAuthCodeAsync(h);

        var result = await h.Handler.HandleAsync(new TokenRequest(
            "authorization_code", ClientId, code, verifier, "https://evil.example/cb", null));

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_grant", result.ErrorCode);
    }

    [Fact]
    public async Task AuthorizationCode_UnknownCode_Rejected()
    {
        var h = Create();
        var (_, verifier, _) = await SeedAuthCodeAsync(h);

        var result = await h.Handler.HandleAsync(new TokenRequest(
            "authorization_code", ClientId, "never-issued", verifier, ClientRedirect, null));

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_grant", result.ErrorCode);
    }

    [Fact]
    public async Task AuthorizationCode_Expired_Rejected()
    {
        var h = Create();
        var (code, verifier, _) = await SeedAuthCodeAsync(h);

        h.Clock.Advance(TimeSpan.FromMinutes(6)); // codes expire after 5 minutes

        var result = await h.Handler.HandleAsync(new TokenRequest(
            "authorization_code", ClientId, code, verifier, ClientRedirect, null));

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_grant", result.ErrorCode);
    }

    /// <summary>Gate #5: refresh token rotation — happy path and rotation invariant.</summary>
    [Fact]
    public async Task RefreshToken_RotatesOldAndIssuesNew()
    {
        var h = Create();
        var (code, verifier, _) = await SeedAuthCodeAsync(h);

        // First, exchange the code to get an initial refresh token.
        var initial = await h.Handler.HandleAsync(new TokenRequest(
            "authorization_code", ClientId, code, verifier, ClientRedirect, null));
        var firstRefresh = initial.Response!.RefreshToken;

        // Use the refresh token to rotate.
        var refreshed = await h.Handler.HandleAsync(new TokenRequest(
            GrantType: "refresh_token",
            ClientId: ClientId,
            Code: null,
            CodeVerifier: null,
            RedirectUri: null,
            RefreshToken: firstRefresh));

        Assert.True(refreshed.IsSuccess);
        Assert.NotEqual(firstRefresh, refreshed.Response!.RefreshToken);
        Assert.NotEmpty(refreshed.Response.AccessToken);

        // Gate #5 invariant: the old refresh must now be unusable.
        var replayed = await h.Handler.HandleAsync(new TokenRequest(
            "refresh_token", ClientId, null, null, null, firstRefresh));
        Assert.False(replayed.IsSuccess);
        Assert.Equal("invalid_grant", replayed.ErrorCode);

        // New refresh works.
        var third = await h.Handler.HandleAsync(new TokenRequest(
            "refresh_token", ClientId, null, null, null, refreshed.Response.RefreshToken));
        Assert.True(third.IsSuccess);
    }

    [Fact]
    public async Task RefreshToken_WrongClient_Rejected()
    {
        var h = Create();
        var (code, verifier, _) = await SeedAuthCodeAsync(h);

        var initial = await h.Handler.HandleAsync(new TokenRequest(
            "authorization_code", ClientId, code, verifier, ClientRedirect, null));

        var attacker = await h.Handler.HandleAsync(new TokenRequest(
            "refresh_token", "different-client", null, null, null, initial.Response!.RefreshToken));

        Assert.False(attacker.IsSuccess);
        Assert.Equal("invalid_grant", attacker.ErrorCode);
    }

    [Fact]
    public async Task UnsupportedGrantType_Rejected()
    {
        var h = Create();

        var result = await h.Handler.HandleAsync(new TokenRequest(
            GrantType: "client_credentials",
            ClientId: ClientId,
            Code: null,
            CodeVerifier: null,
            RedirectUri: null,
            RefreshToken: null));

        Assert.False(result.IsSuccess);
        Assert.Equal("unsupported_grant_type", result.ErrorCode);
    }

    [Fact]
    public async Task AccessToken_JtiMatchesUpstream()
    {
        var h = Create();
        var (code, verifier, upstreamJti) = await SeedAuthCodeAsync(h);

        var result = await h.Handler.HandleAsync(new TokenRequest(
            "authorization_code", ClientId, code, verifier, ClientRedirect, null));

        var validation = await h.JwtIssuer.ValidateAsync(result.Response!.AccessToken);
        Assert.True(validation.IsValid);

        var jwt = (Microsoft.IdentityModel.JsonWebTokens.JsonWebToken)validation.SecurityToken;
        Assert.Equal(upstreamJti, jwt.GetClaim("jti").Value);
    }
}
