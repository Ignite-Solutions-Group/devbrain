using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DevBrain.Functions.Auth.Services;

/// <summary>
/// Options for <see cref="DevBrainJwtIssuer"/>. Read from configuration under the <c>OAuth</c> section.
/// All fields are required; <see cref="DevBrainJwtIssuer"/> fail-fasts in its constructor if any is missing or malformed.
/// </summary>
public sealed class DevBrainJwtIssuerOptions
{
    /// <summary>Base64-encoded HMAC-SHA256 signing key. Must decode to at least 32 bytes. In production, a Key Vault reference to the <c>jwt-signing-secret</c> secret.</summary>
    public string SigningSecret { get; set; } = string.Empty;

    /// <summary>JWT <c>iss</c> claim. Typically the DevBrain base URL (e.g., <c>https://devbrain.example.com</c>).</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>
    /// JWT <c>aud</c> claim. <b>Must</b> be the MCP webhook URL (<c>{base_url}/runtime/webhooks/mcp</c>), not the base URL.
    /// This is the CVE-2025-69196 guard — a bad <c>aud</c> here allows token reuse across servers.
    /// </summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// The Entra tenant GUID this DevBrain instance trusts. Baked into every issued JWT as the
    /// <c>tid</c> claim so the middleware can enforce the single-tenant non-goal without a Cosmos lookup
    /// (acceptance gate #8). Single-tenant is a permanent design decision — never <c>common</c>/<c>organizations</c>.
    /// </summary>
    public string TenantId { get; set; } = string.Empty;
}

/// <summary>
/// Issues and validates DevBrain's own HS256 JWTs. The client-facing OAuth flow mints these at
/// <c>/token</c>; <see cref="Middleware.McpJwtValidationMiddleware"/> validates them on every tool call.
///
/// <para>Design:</para>
/// <list type="bullet">
///   <item><b>HS256</b> with a Key Vault–backed 32-byte secret. Simple, small, one KV secret to provision.</item>
///   <item><see cref="DevBrainJwtIssuerOptions.Audience"/> is baked at construction. Every token issued has this audience;
///         every token validated is rejected unless its audience is exactly this one. This prevents a token minted for
///         one Function host from being replayed against another.</item>
///   <item>The CVE-2025-69196 audience guard lives here: the audience <b>must</b> be the MCP webhook URL, not the base URL.
///         Acceptance gate #1 asserts this by rejecting JWTs with base-url-only audiences via the middleware.</item>
///   <item><see cref="TimeProvider"/> is injected so expiry tests don't sleep.</item>
/// </list>
/// </summary>
public sealed class DevBrainJwtIssuer
{
    private readonly SigningCredentials _signingCredentials;
    private readonly TokenValidationParameters _validationParameters;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly string _tenantId;
    private readonly TimeProvider _timeProvider;
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    public DevBrainJwtIssuer(DevBrainJwtIssuerOptions options, TimeProvider timeProvider)
    {
        if (string.IsNullOrWhiteSpace(options.SigningSecret))
        {
            throw new InvalidOperationException("OAuth:JwtSigningSecret is required.");
        }
        if (string.IsNullOrWhiteSpace(options.Issuer))
        {
            throw new InvalidOperationException("OAuth:JwtIssuer is required.");
        }
        if (string.IsNullOrWhiteSpace(options.Audience))
        {
            throw new InvalidOperationException("OAuth:JwtAudience is required.");
        }
        if (string.IsNullOrWhiteSpace(options.TenantId))
        {
            throw new InvalidOperationException("OAuth:JwtTenantId is required (single-tenant non-goal).");
        }
        if (!Guid.TryParse(options.TenantId, out _))
        {
            throw new InvalidOperationException(
                $"OAuth:JwtTenantId must be a tenant GUID (got '{options.TenantId}'). " +
                "Single-tenant only — 'common'/'organizations' are not supported.");
        }

        byte[] keyBytes;
        try
        {
            keyBytes = Convert.FromBase64String(options.SigningSecret);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("OAuth:JwtSigningSecret must be a valid base64 string.", ex);
        }
        if (keyBytes.Length < 32)
        {
            throw new InvalidOperationException(
                $"OAuth:JwtSigningSecret must decode to at least 32 bytes (got {keyBytes.Length}). " +
                "Use `openssl rand -base64 32` or equivalent.");
        }

        var key = new SymmetricSecurityKey(keyBytes) { KeyId = "devbrain-jwt-v1" };
        _signingCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        _issuer = options.Issuer;
        _audience = options.Audience;
        _tenantId = options.TenantId;
        _timeProvider = timeProvider;

        _validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _issuer,

            // CVE-2025-69196 guard: a JWT with the wrong audience — including the base URL instead
            // of the webhook URL — is rejected here before any handler sees the token.
            ValidateAudience = true,
            ValidAudience = _audience,

            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,

            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            LifetimeValidator = (notBefore, expires, _, parameters) =>
            {
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                var skew = parameters.ClockSkew;
                if (notBefore.HasValue && notBefore.Value > now + skew) return false;
                if (expires.HasValue && expires.Value + skew < now) return false;
                return true;
            },

            RequireSignedTokens = true,
            RequireExpirationTime = true,
        };
    }

    /// <summary>
    /// Issues a new DevBrain JWT with an auto-generated JTI. Returns the token and the JTI so the
    /// caller can link the JWT to its upstream token vault record (<c>upstream:{jti}</c>).
    /// </summary>
    public (string Token, string Jti) Issue(string subject, TimeSpan lifetime) =>
        IssueWithJti(subject, NewJti(), lifetime);

    /// <summary>
    /// Issues a DevBrain JWT using a caller-supplied JTI. Used at <c>/token</c> redemption time,
    /// where the <c>/callback</c> handler has already committed to a JTI when it created the
    /// upstream vault record — the token issued here must match that JTI so the middleware's
    /// <c>jti → upstream:{jti}</c> lookup succeeds.
    /// </summary>
    public (string Token, string Jti) IssueWithJti(string subject, string jti, TimeSpan lifetime)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new ArgumentException("Subject is required.", nameof(subject));
        }
        if (string.IsNullOrWhiteSpace(jti))
        {
            throw new ArgumentException("JTI is required.", nameof(jti));
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _issuer,
            Audience = _audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now + lifetime,
            SigningCredentials = _signingCredentials,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subject,
                ["jti"] = jti,
                // Bake in the tenant ID so the middleware can enforce the single-tenant non-goal
                // before any state store lookup. See acceptance gate #8.
                ["tid"] = _tenantId,
            },
        };

        var token = _handler.CreateToken(descriptor);
        return (token, jti);
    }

    /// <summary>
    /// Validates a DevBrain JWT. Wraps <see cref="JsonWebTokenHandler.ValidateTokenAsync(string, TokenValidationParameters)"/>
    /// with this issuer's baked-in validation parameters. Callers inspect <c>IsValid</c>, then
    /// read <c>Claims["jti"]</c> and <c>Claims["sub"]</c> on success.
    /// </summary>
    public Task<TokenValidationResult> ValidateAsync(string token) =>
        _handler.ValidateTokenAsync(token, _validationParameters);

    /// <summary>
    /// Generates a random JTI suitable for the JWT <c>jti</c> claim. 16 random bytes, base64url-encoded.
    /// Collision probability at our scale is trivially low.
    /// </summary>
    public static string NewJti()
    {
        var bytes = new byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Helper for tests/deploy scripts: generates a fresh base64-encoded 32-byte HMAC secret.
    /// Equivalent to <c>openssl rand -base64 32</c>. Use to populate <c>OAuth:JwtSigningSecret</c>.
    /// </summary>
    public static string GenerateSigningSecret()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }
}
