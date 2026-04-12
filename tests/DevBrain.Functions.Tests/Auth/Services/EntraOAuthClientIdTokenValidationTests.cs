using System.Net;
using System.Text.Json;
using DevBrain.Functions.Auth.Services;
using DevBrain.Functions.Tests.TestHelpers;

namespace DevBrain.Functions.Tests.Auth.Services;

/// <summary>
/// Acceptance gate #10 — <see cref="EntraOAuthClient"/> id_token JWKS validation.
///
/// <para>
/// One happy-path test (proving the validator accepts a properly-signed id_token with the right
/// issuer/audience/lifetime) and four rejection tests — one per failure mode the plan enumerates:
/// wrong signing key, wrong issuer, wrong audience, expired. All five must surface as the typed
/// <see cref="IdTokenValidationException"/> (or return success for the happy path).
/// </para>
/// </summary>
public sealed class EntraOAuthClientIdTokenValidationTests
{
    private const string TenantGuid = "11111111-1111-1111-1111-111111111111";
    private const string ClientId = "upstream-client-id";
    private static readonly string ExpectedIssuer = $"https://login.microsoftonline.com/{TenantGuid}/v2.0";

    private static EntraOAuthClientOptions ValidOptions() => new()
    {
        TenantId = TenantGuid,
        ClientId = ClientId,
        ClientSecret = "upstream-client-secret",
        RedirectUri = "https://devbrain.example.com/callback",
        Scope = "openid profile offline_access documents.readwrite",
    };

    /// <summary>
    /// Builds a client whose configured verification key is <paramref name="verificationKey"/>.
    /// The handler returns a token response carrying <paramref name="idToken"/> as the id_token.
    /// </summary>
    private static EntraOAuthClient CreateClientExpectingIdToken(
        Microsoft.IdentityModel.Tokens.RsaSecurityKey verificationKey,
        string idToken)
    {
        var tokenJson = JsonSerializer.Serialize(new
        {
            access_token = "at",
            refresh_token = "rt",
            id_token = idToken,
            expires_in = 3600,
        });

        var handler = FakeHttpMessageHandler.ReturningJson(tokenJson);
        var http = new HttpClient(handler);
        var configManager = FakeOpenIdConfigurationManager.ForTenant(TenantGuid);
        configManager.AddSigningKey(verificationKey);
        return new EntraOAuthClient(http, ValidOptions(), configManager);
    }

    private static Dictionary<string, object> StandardClaims() => new()
    {
        ["preferred_username"] = "derek@ignitesolutions.group",
        ["oid"] = "00000000-0000-0000-0000-000000000001",
        ["tid"] = TenantGuid,
    };

    /// <summary>Gate #10 / happy path — properly signed, issued, audienced, and unexpired token passes.</summary>
    [Fact]
    public async Task ValidIdToken_Accepted()
    {
        var keys = TestJwtFactory.CreateKeyPair();
        var idToken = TestJwtFactory.CreateSignedIdToken(
            keys.SigningKey,
            claims: StandardClaims(),
            issuer: ExpectedIssuer,
            audience: ClientId);

        var client = CreateClientExpectingIdToken(keys.VerificationKey, idToken);

        var result = await client.ExchangeCodeAsync("code", "verifier");

        Assert.Equal("derek@ignitesolutions.group", result.UserPrincipalName);
        Assert.Equal(TenantGuid, result.TenantId);
    }

    /// <summary>Gate #10 / wrong signing key — validator's key ring doesn't match the token's signature.</summary>
    [Fact]
    public async Task IdToken_SignedByDifferentKey_Rejected()
    {
        var realKeys = TestJwtFactory.CreateKeyPair();
        var rogueKeys = TestJwtFactory.CreateKeyPair();

        // Token is signed by the rogue key, but the validator only has the real key's public half.
        var idToken = TestJwtFactory.CreateSignedIdToken(
            rogueKeys.SigningKey,
            claims: StandardClaims(),
            issuer: ExpectedIssuer,
            audience: ClientId);

        var client = CreateClientExpectingIdToken(realKeys.VerificationKey, idToken);

        await Assert.ThrowsAsync<IdTokenValidationException>(
            () => client.ExchangeCodeAsync("code", "verifier"));
    }

    /// <summary>Gate #10 / wrong issuer — token signed by the expected key but with the wrong <c>iss</c>.</summary>
    [Fact]
    public async Task IdToken_WrongIssuer_Rejected()
    {
        var keys = TestJwtFactory.CreateKeyPair();
        var idToken = TestJwtFactory.CreateSignedIdToken(
            keys.SigningKey,
            claims: StandardClaims(),
            issuer: "https://login.microsoftonline.com/99999999-9999-9999-9999-999999999999/v2.0",
            audience: ClientId);

        var client = CreateClientExpectingIdToken(keys.VerificationKey, idToken);

        await Assert.ThrowsAsync<IdTokenValidationException>(
            () => client.ExchangeCodeAsync("code", "verifier"));
    }

    /// <summary>Gate #10 / wrong audience — token's <c>aud</c> claim doesn't match the configured upstream client_id.</summary>
    [Fact]
    public async Task IdToken_WrongAudience_Rejected()
    {
        var keys = TestJwtFactory.CreateKeyPair();
        var idToken = TestJwtFactory.CreateSignedIdToken(
            keys.SigningKey,
            claims: StandardClaims(),
            issuer: ExpectedIssuer,
            audience: "some-other-app-client-id");

        var client = CreateClientExpectingIdToken(keys.VerificationKey, idToken);

        await Assert.ThrowsAsync<IdTokenValidationException>(
            () => client.ExchangeCodeAsync("code", "verifier"));
    }

    /// <summary>Gate #10 / expired — token's <c>exp</c> is in the past (past the 5-minute clock skew).</summary>
    [Fact]
    public async Task IdToken_Expired_Rejected()
    {
        var keys = TestJwtFactory.CreateKeyPair();
        // Far enough in the past to clear the 5-minute clock skew allowance.
        var idToken = TestJwtFactory.CreateSignedIdToken(
            keys.SigningKey,
            claims: StandardClaims(),
            issuer: ExpectedIssuer,
            audience: ClientId,
            notBefore: DateTimeOffset.UtcNow.AddHours(-2),
            expires: DateTimeOffset.UtcNow.AddHours(-1));

        var client = CreateClientExpectingIdToken(keys.VerificationKey, idToken);

        await Assert.ThrowsAsync<IdTokenValidationException>(
            () => client.ExchangeCodeAsync("code", "verifier"));
    }

    /// <summary>Regression: the typed exception is thrown, not a generic one.</summary>
    [Fact]
    public async Task IdToken_RejectionIsTypedException()
    {
        var keys = TestJwtFactory.CreateKeyPair();
        var rogueKeys = TestJwtFactory.CreateKeyPair();

        var idToken = TestJwtFactory.CreateSignedIdToken(
            rogueKeys.SigningKey,
            claims: StandardClaims(),
            issuer: ExpectedIssuer,
            audience: ClientId);

        var client = CreateClientExpectingIdToken(keys.VerificationKey, idToken);

        var ex = await Assert.ThrowsAsync<IdTokenValidationException>(
            () => client.ExchangeCodeAsync("code", "verifier"));
        Assert.Contains("id_token", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Discovery fetch failure surfaces as <see cref="IdTokenValidationException"/>, not a raw exception.</summary>
    [Fact]
    public async Task DiscoveryFetchFailure_Rejected()
    {
        var tokenJson = JsonSerializer.Serialize(new
        {
            access_token = "at",
            refresh_token = "rt",
            id_token = "any.token.here", // won't get validated because discovery fetch throws first
            expires_in = 3600,
        });

        using var handler = FakeHttpMessageHandler.ReturningJson(tokenJson);
        using var http = new HttpClient(handler);
        var configManager = FakeOpenIdConfigurationManager.ForTenant(TenantGuid);
        configManager.ThrowOnFetch = new HttpRequestException("network down");

        var client = new EntraOAuthClient(http, ValidOptions(), configManager);

        await Assert.ThrowsAsync<IdTokenValidationException>(
            () => client.ExchangeCodeAsync("code", "verifier"));
    }
}
