using DevBrain.Functions.Auth.Services;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;

namespace DevBrain.Functions.Tests.Auth.Services;

/// <summary>
/// Unit tests for <see cref="DevBrainJwtIssuer"/>. Covers round-trip issue/validate, expiry,
/// tampering, and — most importantly — the CVE-2025-69196 audience guard (acceptance gate #1)
/// and cross-host rejection (gate #7). These gates also re-appear at the middleware level in
/// <c>McpJwtValidationMiddlewareTests</c>; testing them here proves the issuer enforces them even
/// outside the request pipeline.
/// </summary>
public sealed class DevBrainJwtIssuerTests
{
    private const string IssuerA = "https://devbrain-a.example.com";
    private const string AudienceA = "https://devbrain-a.example.com/runtime/webhooks/mcp";
    private const string IssuerB = "https://devbrain-b.example.com";
    private const string AudienceB = "https://devbrain-b.example.com/runtime/webhooks/mcp";
    private const string TestTenantId = "11111111-1111-1111-1111-111111111111";

    private static readonly DateTimeOffset Epoch = new(2026, 4, 11, 0, 0, 0, TimeSpan.Zero);

    private static DevBrainJwtIssuer CreateIssuer(
        FakeTimeProvider clock,
        string issuer = IssuerA,
        string audience = AudienceA,
        string? signingSecret = null,
        string tenantId = TestTenantId)
    {
        var options = new DevBrainJwtIssuerOptions
        {
            SigningSecret = signingSecret ?? DevBrainJwtIssuer.GenerateSigningSecret(),
            Issuer = issuer,
            Audience = audience,
            TenantId = tenantId,
        };
        return new DevBrainJwtIssuer(options, clock);
    }

    [Fact]
    public async Task Issue_Then_Validate_RoundTrips()
    {
        var clock = new FakeTimeProvider(Epoch);
        var issuer = CreateIssuer(clock);

        var (token, jti) = issuer.Issue("derek@ignitesolutions.group", TimeSpan.FromMinutes(10));

        var result = await issuer.ValidateAsync(token);

        Assert.True(result.IsValid);
        var jwt = (JsonWebToken)result.SecurityToken;
        Assert.Equal(jti, jwt.GetClaim("jti").Value);
        Assert.Equal("derek@ignitesolutions.group", jwt.GetClaim("sub").Value);
        Assert.Equal(AudienceA, jwt.Audiences.Single());
        Assert.Equal(IssuerA, jwt.Issuer);
    }

    [Fact]
    public async Task Validate_ExpiredToken_Fails()
    {
        var clock = new FakeTimeProvider(Epoch);
        var issuer = CreateIssuer(clock);

        var (token, _) = issuer.Issue("user", TimeSpan.FromMinutes(5));

        // Advance well past the token lifetime + clock skew.
        clock.Advance(TimeSpan.FromMinutes(10));

        var result = await issuer.ValidateAsync(token);
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Validate_TamperedPayload_Fails()
    {
        var clock = new FakeTimeProvider(Epoch);
        var issuer = CreateIssuer(clock);

        var (token, _) = issuer.Issue("derek", TimeSpan.FromMinutes(10));

        // Decode the payload segment, swap the subject claim (which the original signature covers),
        // re-encode, and leave the signature unchanged. Validation must reject because the HMAC now
        // disagrees with the mutated payload.
        var parts = token.Split('.');
        var payloadBytes = Base64UrlDecode(parts[1]);
        var payloadJson = System.Text.Encoding.UTF8.GetString(payloadBytes);
        var tamperedJson = payloadJson.Replace("\"sub\":\"derek\"", "\"sub\":\"attacker\"");
        Assert.NotEqual(payloadJson, tamperedJson); // guard: replacement actually happened
        parts[1] = Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(tamperedJson));
        var tampered = string.Join('.', parts);

        var result = await issuer.ValidateAsync(tampered);
        Assert.False(result.IsValid);
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Acceptance gate #1 — CVE-2025-69196 audience guard, issuer-level. A token whose audience is
    /// the DevBrain base URL (instead of the webhook URL) must be rejected. Test constructs one issuer
    /// with a base-URL audience and validates its token against a second issuer configured with the
    /// correct webhook audience; the second issuer rejects it.
    /// </summary>
    [Fact]
    public async Task Validate_BaseUrlAudienceInsteadOfWebhook_Rejected()
    {
        var clock = new FakeTimeProvider(Epoch);
        var secret = DevBrainJwtIssuer.GenerateSigningSecret();

        // "Bad" issuer: mints tokens with audience = base URL. This is the exact FastMCP bug.
        var badIssuer = new DevBrainJwtIssuer(
            new DevBrainJwtIssuerOptions { SigningSecret = secret, Issuer = IssuerA, Audience = IssuerA, TenantId = TestTenantId },
            clock);
        var (badToken, _) = badIssuer.Issue("user", TimeSpan.FromMinutes(10));

        // Correct issuer: audience = webhook URL. Shares signing material (same tenant, same Key Vault secret).
        var goodIssuer = new DevBrainJwtIssuer(
            new DevBrainJwtIssuerOptions { SigningSecret = secret, Issuer = IssuerA, Audience = AudienceA, TenantId = TestTenantId },
            clock);

        var result = await goodIssuer.ValidateAsync(badToken);
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Validate_CompletelyWrongAudience_Rejected()
    {
        var clock = new FakeTimeProvider(Epoch);
        var secret = DevBrainJwtIssuer.GenerateSigningSecret();

        var foreignIssuer = new DevBrainJwtIssuer(
            new DevBrainJwtIssuerOptions { SigningSecret = secret, Issuer = IssuerA, Audience = "https://unrelated.example.com", TenantId = TestTenantId },
            clock);
        var (foreignToken, _) = foreignIssuer.Issue("user", TimeSpan.FromMinutes(10));

        var goodIssuer = CreateIssuer(clock, signingSecret: secret);

        var result = await goodIssuer.ValidateAsync(foreignToken);
        Assert.False(result.IsValid);
    }

    /// <summary>
    /// Acceptance gate #7 — cross-host rejection. A JWT issued by one DevBrain instance (configured
    /// with host A) must be rejected by a different DevBrain instance (configured with host B) even
    /// when both share signing material. Prevents the same bug class as CVE-2025-69196 from the
    /// validator side.
    /// </summary>
    [Fact]
    public async Task Validate_TokenIssuedForHostA_RejectedByHostB()
    {
        var clock = new FakeTimeProvider(Epoch);
        var secret = DevBrainJwtIssuer.GenerateSigningSecret();

        var hostA = new DevBrainJwtIssuer(
            new DevBrainJwtIssuerOptions { SigningSecret = secret, Issuer = IssuerA, Audience = AudienceA, TenantId = TestTenantId },
            clock);
        var hostB = new DevBrainJwtIssuer(
            new DevBrainJwtIssuerOptions { SigningSecret = secret, Issuer = IssuerB, Audience = AudienceB, TenantId = TestTenantId },
            clock);

        var (hostAToken, _) = hostA.Issue("user", TimeSpan.FromMinutes(10));

        var resultAtB = await hostB.ValidateAsync(hostAToken);
        Assert.False(resultAtB.IsValid);
    }

    [Fact]
    public void Constructor_MissingSigningSecret_Throws()
    {
        var clock = new FakeTimeProvider(Epoch);
        var options = new DevBrainJwtIssuerOptions { SigningSecret = "", Issuer = IssuerA, Audience = AudienceA, TenantId = TestTenantId };

        Assert.Throws<InvalidOperationException>(() => new DevBrainJwtIssuer(options, clock));
    }

    [Fact]
    public void Constructor_ShortSigningSecret_Throws()
    {
        var clock = new FakeTimeProvider(Epoch);
        var shortSecret = Convert.ToBase64String(new byte[16]); // only 16 bytes, minimum is 32
        var options = new DevBrainJwtIssuerOptions { SigningSecret = shortSecret, Issuer = IssuerA, Audience = AudienceA, TenantId = TestTenantId };

        var ex = Assert.Throws<InvalidOperationException>(() => new DevBrainJwtIssuer(options, clock));
        Assert.Contains("at least 32 bytes", ex.Message);
    }

    [Fact]
    public void Constructor_MalformedBase64_Throws()
    {
        var clock = new FakeTimeProvider(Epoch);
        var options = new DevBrainJwtIssuerOptions
        {
            SigningSecret = "not-valid-base64!!!",
            Issuer = IssuerA,
            Audience = AudienceA,
            TenantId = TestTenantId,
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new DevBrainJwtIssuer(options, clock));
        Assert.Contains("base64", ex.Message);
    }

    [Fact]
    public void Constructor_NonGuidTenantId_Throws()
    {
        var clock = new FakeTimeProvider(Epoch);
        var options = new DevBrainJwtIssuerOptions
        {
            SigningSecret = DevBrainJwtIssuer.GenerateSigningSecret(),
            Issuer = IssuerA,
            Audience = AudienceA,
            TenantId = "common",
        };

        var ex = Assert.Throws<InvalidOperationException>(() => new DevBrainJwtIssuer(options, clock));
        Assert.Contains("GUID", ex.Message);
    }

    [Fact]
    public async Task IssuedToken_CarriesTenantIdClaim()
    {
        var clock = new FakeTimeProvider(Epoch);
        var issuer = CreateIssuer(clock);

        var (token, _) = issuer.Issue("user", TimeSpan.FromMinutes(10));

        var validation = await issuer.ValidateAsync(token);
        Assert.True(validation.IsValid);
        var jwt = (JsonWebToken)validation.SecurityToken;
        Assert.Equal(TestTenantId, jwt.GetClaim("tid").Value);
    }

    [Fact]
    public void Issue_GeneratesUniqueJtis()
    {
        var clock = new FakeTimeProvider(Epoch);
        var issuer = CreateIssuer(clock);

        var (_, jti1) = issuer.Issue("user", TimeSpan.FromMinutes(10));
        var (_, jti2) = issuer.Issue("user", TimeSpan.FromMinutes(10));

        Assert.NotEqual(jti1, jti2);
    }
}
