using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DevBrain.Functions.Tests.TestHelpers;

/// <summary>
/// Test helper that mints signed id_tokens for <c>EntraOAuthClient</c> tests. The returned
/// <see cref="SigningKey"/> is the public half of the keypair — callers put it into the
/// fake <c>OpenIdConnectConfiguration.SigningKeys</c> so validation succeeds.
///
/// <para>
/// The client validates id_tokens against the JWKS from the discovery endpoint, so test tokens
/// must be properly signed with an RSA keypair. No more unsigned-JWT path — gate #10 is
/// specifically about the validator rejecting unsigned/wrong-key/wrong-issuer/wrong-audience/
/// expired tokens.
/// </para>
/// </summary>
public static class TestJwtFactory
{
    /// <summary>
    /// Mints a fresh RSA 2048 keypair and returns both halves:
    /// <list type="bullet">
    ///   <item><see cref="IdTokenKeyPair.SigningKey"/> — used as <c>SigningCredentials</c> when minting tokens (private key).</item>
    ///   <item><see cref="IdTokenKeyPair.VerificationKey"/> — the public-only half, matching what JWKS would return, for the fake config manager's <c>SigningKeys</c> collection.</item>
    /// </list>
    /// </summary>
    public static IdTokenKeyPair CreateKeyPair()
    {
        using var rsa = RSA.Create(2048);
        var privateParameters = rsa.ExportParameters(includePrivateParameters: true);
        var keyId = Guid.NewGuid().ToString("N");

        var signingKey = new RsaSecurityKey(privateParameters) { KeyId = keyId };

        var publicParameters = new RSAParameters
        {
            Modulus = privateParameters.Modulus,
            Exponent = privateParameters.Exponent,
        };
        var verificationKey = new RsaSecurityKey(publicParameters) { KeyId = keyId };

        return new IdTokenKeyPair(signingKey, verificationKey);
    }

    /// <summary>
    /// Builds a signed id_token with the given claims. The token's header includes the signing
    /// key's <c>KeyId</c>, matching Entra's real id_token header shape so the validator's
    /// <c>IssuerSigningKeys</c> lookup by kid works.
    /// </summary>
    public static string CreateSignedIdToken(
        RsaSecurityKey signingKey,
        IDictionary<string, object> claims,
        string issuer,
        string audience,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? expires = null)
    {
        var handler = new JsonWebTokenHandler { MapInboundClaims = false };
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            NotBefore = (notBefore ?? DateTimeOffset.UtcNow).UtcDateTime,
            Expires = (expires ?? DateTimeOffset.UtcNow.AddHours(1)).UtcDateTime,
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256),
            Claims = new Dictionary<string, object>(claims),
        };
        return handler.CreateToken(descriptor);
    }
}

/// <summary>Paired RSA keys for minting (private) and validating (public) test id_tokens.</summary>
public sealed record IdTokenKeyPair(RsaSecurityKey SigningKey, RsaSecurityKey VerificationKey);
