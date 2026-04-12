using DevBrain.Functions.Auth.Middleware;
using DevBrain.Functions.Auth.Models;
using DevBrain.Functions.Auth.Services;
using DevBrain.Functions.Tests.Auth.Services;
using Microsoft.Extensions.Time.Testing;

namespace DevBrain.Functions.Tests.Auth.Middleware;

/// <summary>
/// Unit tests for <see cref="JwtAuthenticator"/>. These are the final three acceptance gates:
/// <list type="bullet">
///   <item><b>#1</b> CVE-2025-69196 audience guard at the middleware layer (token with base-url aud rejected).</item>
///   <item><b>#6</b> Per-user identity end-to-end — the rehydrated ClaimsPrincipal has the real Entra UPN.</item>
///   <item><b>#8</b> Cross-tenant rejection — wrong tid rejected with ZERO calls to IOAuthStateStore.</item>
/// </list>
/// </summary>
public sealed class JwtAuthenticatorTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 4, 11, 0, 0, 0, TimeSpan.Zero);
    private const string Issuer = "https://devbrain.example.com";
    private const string Audience = "https://devbrain.example.com/runtime/webhooks/mcp";
    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";

    private sealed record Harness(
        JwtAuthenticator Authenticator,
        DevBrainJwtIssuer Issuer,
        FakeOAuthStateStore Store,
        FakeTimeProvider Clock);

    private static Harness Create(string tenantId = TenantA)
    {
        var clock = new FakeTimeProvider(Epoch);
        var store = new FakeOAuthStateStore(clock);
        var jwtIssuer = new DevBrainJwtIssuer(
            new DevBrainJwtIssuerOptions
            {
                SigningSecret = DevBrainJwtIssuer.GenerateSigningSecret(),
                Issuer = Issuer,
                Audience = Audience,
                TenantId = tenantId,
            },
            clock);
        var authenticator = new JwtAuthenticator(
            jwtIssuer,
            store,
            new JwtAuthenticatorOptions { ExpectedTenantId = tenantId });
        return new Harness(authenticator, jwtIssuer, store, clock);
    }

    private static async Task<string> SeedUpstreamTokenAndIssueJwtAsync(Harness h, string upn = "derek@ignitesolutions.group", string tenantId = TenantA)
    {
        var jti = DevBrainJwtIssuer.NewJti();
        await h.Store.SaveUpstreamTokenAsync(new UpstreamTokenRecord
        {
            Jti = jti,
            Envelope = new UpstreamTokenEnvelope("at", "rt", 0),
            UserPrincipalName = upn,
            ObjectId = "00000000-0000-0000-0000-000000000001",
            TenantId = tenantId,
            CreatedAt = Epoch,
            ExpiresAt = Epoch.AddHours(1),
        });

        // Issue a JWT bound to this JTI. The issuer bakes the configured tid into the token.
        var (token, _) = h.Issuer.IssueWithJti($"upstream-{jti}", jti, TimeSpan.FromMinutes(10));
        return token;
    }

    /// <summary>Gate #6: per-user identity end-to-end — the rehydrated principal matches the Entra UPN from the vault.</summary>
    [Fact]
    public async Task ValidToken_RehydratesClaimsPrincipalWithUpn()
    {
        var h = Create();
        var token = await SeedUpstreamTokenAndIssueJwtAsync(h, "derek@ignitesolutions.group");

        var result = await h.Authenticator.AuthenticateAsync("Bearer " + token);

        Assert.True(result.IsAuthenticated);
        Assert.NotNull(result.Principal);
        var principal = result.Principal;
        Assert.Equal("derek@ignitesolutions.group", principal.FindFirst("preferred_username")!.Value);
        Assert.Equal("00000000-0000-0000-0000-000000000001", principal.FindFirst("oid")!.Value);
        Assert.Equal(TenantA, principal.FindFirst("tid")!.Value);
    }

    /// <summary>Gate #8: cross-tenant rejection with zero state store reads.</summary>
    [Fact]
    public async Task WrongTenant_Rejected_WithZeroStoreReads()
    {
        var clock = new FakeTimeProvider(Epoch);
        var store = new FakeOAuthStateStore(clock);
        var sharedSecret = DevBrainJwtIssuer.GenerateSigningSecret();

        // "Rogue" issuer minting TenantB tokens, but using the same signing secret — simulates a
        // hypothetical attacker who somehow knows the DevBrain signing key. Proves the tenant check
        // is independent of the signing check.
        var rogueIssuer = new DevBrainJwtIssuer(
            new DevBrainJwtIssuerOptions
            {
                SigningSecret = sharedSecret,
                Issuer = Issuer,
                Audience = Audience,
                TenantId = TenantB,
            },
            clock);

        var middlewareIssuer = new DevBrainJwtIssuer(
            new DevBrainJwtIssuerOptions
            {
                SigningSecret = sharedSecret,
                Issuer = Issuer,
                Audience = Audience,
                TenantId = TenantA,
            },
            clock);
        var authenticator = new JwtAuthenticator(
            middlewareIssuer,
            store,
            new JwtAuthenticatorOptions { ExpectedTenantId = TenantA });

        // Seed a vault entry so the test can prove rejection is tenant-based, not jti-lookup-based.
        var jti = DevBrainJwtIssuer.NewJti();
        await store.SaveUpstreamTokenAsync(new UpstreamTokenRecord
        {
            Jti = jti,
            UserPrincipalName = "attacker@other.tenant",
            TenantId = TenantB,
            ExpiresAt = Epoch.AddHours(1),
        });

        var (rogueToken, _) = rogueIssuer.IssueWithJti($"upstream-{jti}", jti, TimeSpan.FromMinutes(10));

        var readsBefore = store.ReadCallCount;
        var result = await authenticator.AuthenticateAsync("Bearer " + rogueToken);

        Assert.False(result.IsAuthenticated);
        Assert.Equal("invalid_token", result.ErrorCode);
        // ZERO additional store reads — the tid check short-circuited before the jti lookup.
        Assert.Equal(readsBefore, store.ReadCallCount);
    }

    /// <summary>Gate #1 revisited at middleware layer: token with base-URL audience is rejected here too.</summary>
    [Fact]
    public async Task TokenWithBaseUrlAudience_RejectedAtMiddleware()
    {
        var clock = new FakeTimeProvider(Epoch);
        var store = new FakeOAuthStateStore(clock);
        var sharedSecret = DevBrainJwtIssuer.GenerateSigningSecret();

        var badIssuer = new DevBrainJwtIssuer(
            new DevBrainJwtIssuerOptions
            {
                SigningSecret = sharedSecret,
                Issuer = Issuer,
                Audience = Issuer, // base URL instead of webhook URL — the CVE shape
                TenantId = TenantA,
            },
            clock);

        var middlewareIssuer = new DevBrainJwtIssuer(
            new DevBrainJwtIssuerOptions
            {
                SigningSecret = sharedSecret,
                Issuer = Issuer,
                Audience = Audience,
                TenantId = TenantA,
            },
            clock);
        var authenticator = new JwtAuthenticator(
            middlewareIssuer,
            store,
            new JwtAuthenticatorOptions { ExpectedTenantId = TenantA });

        var jti = DevBrainJwtIssuer.NewJti();
        await store.SaveUpstreamTokenAsync(new UpstreamTokenRecord
        {
            Jti = jti,
            UserPrincipalName = "derek@ignitesolutions.group",
            TenantId = TenantA,
            ExpiresAt = Epoch.AddHours(1),
        });

        var (badToken, _) = badIssuer.IssueWithJti($"upstream-{jti}", jti, TimeSpan.FromMinutes(10));

        var result = await authenticator.AuthenticateAsync("Bearer " + badToken);
        Assert.False(result.IsAuthenticated);
        Assert.Equal("invalid_token", result.ErrorCode);
    }

    [Fact]
    public async Task NoBearerPrefix_Rejected()
    {
        var h = Create();

        var result = await h.Authenticator.AuthenticateAsync("BasicAuth xxx");

        Assert.False(result.IsAuthenticated);
        Assert.Equal("invalid_authorization", result.ErrorCode);
    }

    [Fact]
    public async Task MissingHeader_Rejected()
    {
        var h = Create();

        var result = await h.Authenticator.AuthenticateAsync(null);

        Assert.False(result.IsAuthenticated);
        Assert.Equal("missing_authorization", result.ErrorCode);
    }

    [Fact]
    public async Task EmptyBearer_Rejected()
    {
        var h = Create();

        var result = await h.Authenticator.AuthenticateAsync("Bearer    ");

        Assert.False(result.IsAuthenticated);
        Assert.Equal("invalid_authorization", result.ErrorCode);
    }

    [Fact]
    public async Task ExpiredJwt_Rejected()
    {
        var h = Create();
        var token = await SeedUpstreamTokenAndIssueJwtAsync(h);

        h.Clock.Advance(TimeSpan.FromMinutes(15)); // token lifetime was 10 min + clock skew 30s

        var result = await h.Authenticator.AuthenticateAsync("Bearer " + token);
        Assert.False(result.IsAuthenticated);
        Assert.Equal("invalid_token", result.ErrorCode);
    }

    [Fact]
    public async Task JtiNotInStore_Rejected()
    {
        var h = Create();

        // Issue a token bound to a JTI that has no corresponding upstream record.
        var orphanJti = DevBrainJwtIssuer.NewJti();
        var (token, _) = h.Issuer.IssueWithJti($"upstream-{orphanJti}", orphanJti, TimeSpan.FromMinutes(10));

        var result = await h.Authenticator.AuthenticateAsync("Bearer " + token);
        Assert.False(result.IsAuthenticated);
        Assert.Equal("invalid_token", result.ErrorCode);
    }

    [Fact]
    public void Constructor_NonGuidExpectedTenantId_Throws()
    {
        var clock = new FakeTimeProvider(Epoch);
        var store = new FakeOAuthStateStore(clock);
        var issuer = new DevBrainJwtIssuer(
            new DevBrainJwtIssuerOptions
            {
                SigningSecret = DevBrainJwtIssuer.GenerateSigningSecret(),
                Issuer = Issuer,
                Audience = Audience,
                TenantId = TenantA,
            },
            clock);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new JwtAuthenticator(issuer, store, new JwtAuthenticatorOptions { ExpectedTenantId = "common" }));
        Assert.Contains("GUID", ex.Message);
    }
}
