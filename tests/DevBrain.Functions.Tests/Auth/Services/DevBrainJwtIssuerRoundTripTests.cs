using System.Text.Json;
using DevBrain.Functions.Auth.Services;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;

namespace DevBrain.Functions.Tests.Auth.Services;

/// <summary>
/// v1.6 post-deploy bug: production minter emitted a JWT with
/// <c>aud = "https://func-devbrain-ovp6emodfhlre.azurewebsites.net/runtime/webhooks/mcp"</c>,
/// and the production validator — configured with that exact string as <c>ValidAudience</c> —
/// rejected the token as "audience invalid". The existing
/// <c>Issue_Then_Validate_RoundTrips</c> test didn't catch it because it used a test-shaped
/// audience (<c>https://devbrain-a.example.com/runtime/webhooks/mcp</c>), not a real
/// azurewebsites.net URL.
///
/// <para>
/// These tests exercise the round-trip with the exact production URL shape AND inspect the
/// raw <c>aud</c> claim value in the JWT payload, so if <c>SecurityTokenDescriptor.Audience</c>
/// and <c>TokenValidationParameters.ValidAudience</c> are handling the claim differently under
/// the hood (e.g., one emits a string, the other expects an array), the assertion failure shows
/// exactly what the diff is.
/// </para>
/// </summary>
public sealed class DevBrainJwtIssuerRoundTripTests
{
    private const string TestTenantId = "11111111-1111-1111-1111-111111111111";
    private static readonly DateTimeOffset Epoch = new(2026, 4, 11, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Reproduction: mint with the exact shape of production audience, then validate on the
    /// same instance. If this fails, the production bug is reproduced in a unit test.
    /// </summary>
    [Fact]
    public async Task MintAndValidate_WithProductionShapedAudience_RoundTripsSuccessfully()
    {
        const string productionIssuer = "https://func-devbrain-ovp6emodfhlre.azurewebsites.net";
        const string productionAudience = "https://func-devbrain-ovp6emodfhlre.azurewebsites.net/runtime/webhooks/mcp";

        var clock = new FakeTimeProvider(Epoch);
        var issuer = new DevBrainJwtIssuer(
            new DevBrainJwtIssuerOptions
            {
                SigningSecret = DevBrainJwtIssuer.GenerateSigningSecret(),
                Issuer = productionIssuer,
                Audience = productionAudience,
                TenantId = TestTenantId,
            },
            clock);

        var (token, jti) = issuer.IssueWithJti("upstream-test", jti: "test-jti-123", TimeSpan.FromMinutes(10));

        var result = await issuer.ValidateAsync(token);

        Assert.True(
            result.IsValid,
            $"Round-trip failed: {result.Exception?.GetType().Name}: {result.Exception?.Message}");
    }

    /// <summary>
    /// Decodes the raw JWT payload to inspect exactly how the <c>aud</c> claim is being written.
    /// If this test shows <c>aud</c> as an array (<c>["x"]</c>) vs a string (<c>"x"</c>), we know
    /// the serializer behavior; the assertion message will spell out which one.
    /// </summary>
    [Fact]
    public void MintedToken_AudClaimWireFormat_IsExposedForInspection()
    {
        const string audience = "https://func-devbrain-ovp6emodfhlre.azurewebsites.net/runtime/webhooks/mcp";

        var clock = new FakeTimeProvider(Epoch);
        var issuer = new DevBrainJwtIssuer(
            new DevBrainJwtIssuerOptions
            {
                SigningSecret = DevBrainJwtIssuer.GenerateSigningSecret(),
                Issuer = "https://func-devbrain-ovp6emodfhlre.azurewebsites.net",
                Audience = audience,
                TenantId = TestTenantId,
            },
            clock);

        var (token, _) = issuer.IssueWithJti("upstream-test", "test-jti", TimeSpan.FromMinutes(10));

        // Decode the payload segment (middle of the three-dot JWT) without going through the
        // SDK — we want to see the bytes exactly as emitted.
        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);
        var payloadBytes = Base64UrlDecode(parts[1]);
        var payload = JsonDocument.Parse(payloadBytes);

        Assert.True(
            payload.RootElement.TryGetProperty("aud", out var audElement),
            $"Minted JWT payload has no 'aud' claim. Payload: {System.Text.Encoding.UTF8.GetString(payloadBytes)}");

        // The interesting bit: is it a string or an array?
        switch (audElement.ValueKind)
        {
            case JsonValueKind.String:
                Assert.Equal(audience, audElement.GetString());
                break;

            case JsonValueKind.Array:
                var items = audElement.EnumerateArray().Select(e => e.GetString()).ToList();
                Assert.Single(items);
                Assert.Equal(audience, items[0]);
                break;

            default:
                Assert.Fail(
                    $"Minted 'aud' claim has unexpected JSON kind {audElement.ValueKind}. " +
                    $"Expected String or Array. Raw payload: {System.Text.Encoding.UTF8.GetString(payloadBytes)}");
                break;
        }
    }

    /// <summary>
    /// Uses <see cref="JsonWebToken"/> (the SDK's reader) to verify that <c>token.Audiences</c>
    /// — the collection <see cref="TokenValidationParameters.ValidAudience"/> compares against
    /// during validation — contains the exact value the minter intended. If this collection
    /// is empty or differs from the descriptor's audience, the round-trip breaks at this layer.
    /// </summary>
    [Fact]
    public void MintedToken_SdkReader_ExposesAudienceViaAudiencesCollection()
    {
        const string audience = "https://func-devbrain-ovp6emodfhlre.azurewebsites.net/runtime/webhooks/mcp";

        var clock = new FakeTimeProvider(Epoch);
        var issuer = new DevBrainJwtIssuer(
            new DevBrainJwtIssuerOptions
            {
                SigningSecret = DevBrainJwtIssuer.GenerateSigningSecret(),
                Issuer = "https://func-devbrain-ovp6emodfhlre.azurewebsites.net",
                Audience = audience,
                TenantId = TestTenantId,
            },
            clock);

        var (token, _) = issuer.IssueWithJti("upstream-test", "test-jti", TimeSpan.FromMinutes(10));

        var reader = new JsonWebTokenHandler { MapInboundClaims = false };
        var jwt = reader.ReadJsonWebToken(token);

        var audiences = jwt.Audiences.ToList();

        Assert.Single(audiences);
        Assert.Equal(audience, audiences[0]);
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
}
