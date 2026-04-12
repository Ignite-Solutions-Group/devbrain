using System.Security.Claims;
using DevBrain.Functions.Auth.Services;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;

namespace DevBrain.Functions.Auth.Middleware;

/// <summary>
/// Configuration for <see cref="JwtAuthenticator"/>. The <see cref="ExpectedTenantId"/> is the
/// load-bearing single-tenant non-goal — every incoming JWT's <c>tid</c> claim must match this
/// exact value or the token is rejected before any Cosmos lookup.
/// </summary>
public sealed class JwtAuthenticatorOptions
{
    /// <summary>The single Entra tenant GUID this DevBrain instance trusts. Must be a GUID — never <c>common</c>/<c>organizations</c>.</summary>
    public string ExpectedTenantId { get; set; } = string.Empty;
}

/// <summary>
/// Pure authentication logic for the MCP webhook gate. Takes a raw <c>Authorization</c> header
/// value and returns either a rehydrated <see cref="ClaimsPrincipal"/> or a reason for rejection.
///
/// <para>
/// Held separate from <see cref="McpJwtValidationMiddleware"/> so the acceptance gates can be
/// covered by unit tests without constructing a <see cref="Microsoft.Azure.Functions.Worker.FunctionContext"/>.
/// The middleware is a thin HTTP-to-authenticator adapter.
/// </para>
///
/// <para>Acceptance gates proven at this layer:</para>
/// <list type="bullet">
///   <item><b>#1</b> CVE-2025-69196 audience guard (via <see cref="DevBrainJwtIssuer"/>).</item>
///   <item><b>#6</b> Per-user identity end-to-end — the rehydrated principal's <c>preferred_username</c> equals the Entra UPN from <c>upstream:{jti}</c>.</item>
///   <item><b>#8</b> Cross-tenant rejection — a token whose <c>tid</c> doesn't match <see cref="JwtAuthenticatorOptions.ExpectedTenantId"/> is rejected with <b>zero</b> calls to <see cref="IOAuthStateStore"/>.</item>
/// </list>
/// </summary>
public sealed class JwtAuthenticator
{
    private readonly DevBrainJwtIssuer _jwtIssuer;
    private readonly IOAuthStateStore _store;
    private readonly string _expectedTenantId;
    private readonly ILogger<JwtAuthenticator>? _logger;

    public JwtAuthenticator(DevBrainJwtIssuer jwtIssuer, IOAuthStateStore store, JwtAuthenticatorOptions options)
        : this(jwtIssuer, store, options, logger: null)
    {
    }

    public JwtAuthenticator(
        DevBrainJwtIssuer jwtIssuer,
        IOAuthStateStore store,
        JwtAuthenticatorOptions options,
        ILogger<JwtAuthenticator>? logger)
    {
        if (string.IsNullOrWhiteSpace(options.ExpectedTenantId))
        {
            throw new InvalidOperationException("JwtAuthenticatorOptions.ExpectedTenantId is required.");
        }
        if (!Guid.TryParse(options.ExpectedTenantId, out _))
        {
            throw new InvalidOperationException(
                $"JwtAuthenticatorOptions.ExpectedTenantId must be a tenant GUID (got '{options.ExpectedTenantId}'). " +
                "Single-tenant only — 'common'/'organizations' are not supported.");
        }

        _jwtIssuer = jwtIssuer;
        _store = store;
        _expectedTenantId = options.ExpectedTenantId;
        _logger = logger;
    }

    public async Task<AuthenticationResult> AuthenticateAsync(string? authorizationHeader)
    {
        if (string.IsNullOrEmpty(authorizationHeader))
        {
            _logger?.LogWarning("JwtAuthenticator: missing Authorization header");
            return AuthenticationResult.Fail("missing_authorization", "Authorization header is required.");
        }

        // RFC 6750 §2.1 — "Bearer " prefix is required (case-insensitive per RFC 2617 §1.2, but most clients use capital B).
        const string bearerPrefix = "Bearer ";
        if (!authorizationHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogWarning("JwtAuthenticator: Authorization header does not start with Bearer");
            return AuthenticationResult.Fail("invalid_authorization", "Authorization header must start with 'Bearer '.");
        }
        var token = authorizationHeader[bearerPrefix.Length..].Trim();
        if (string.IsNullOrEmpty(token))
        {
            _logger?.LogWarning("JwtAuthenticator: empty bearer token");
            return AuthenticationResult.Fail("invalid_authorization", "Bearer token is empty.");
        }

        // Signature + aud + iss + lifetime validation. DevBrainJwtIssuer bakes in the webhook-URL
        // audience, so this is where gate #1 (CVE-2025-69196) lands at the middleware layer.
        var validation = await _jwtIssuer.ValidateAsync(token);
        if (!validation.IsValid)
        {
            _logger?.LogWarning(validation.Exception,
                "JwtAuthenticator: JWT failed validation reason={Reason}",
                validation.Exception?.GetType().Name ?? "unknown");
            return AuthenticationResult.Fail("invalid_token", "JWT failed validation: " + (validation.Exception?.Message ?? "unknown"));
        }

        var jwt = (JsonWebToken)validation.SecurityToken;

        // Gate #8: cross-tenant rejection — check the tid claim on the JWT BEFORE any Cosmos lookup.
        // DevBrainJwtIssuer bakes the configured tenant into every issued JWT's `tid` claim (see
        // DevBrainJwtIssuerOptions.TenantId), so a token whose tid doesn't match this middleware's
        // expected tenant is rejected without ever touching IOAuthStateStore. Tests assert via
        // FakeOAuthStateStore.ReadCallCount that zero reads occurred on this path.
        var tokenTid = FindClaim(jwt, "tid");
        if (string.IsNullOrEmpty(tokenTid))
        {
            _logger?.LogWarning("JwtAuthenticator: JWT missing tid claim");
            return AuthenticationResult.Fail("invalid_token", "JWT is missing the tid claim.");
        }
        if (!string.Equals(tokenTid, _expectedTenantId, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogWarning(
                "JwtAuthenticator: tid mismatch tokenTid={TokenTid} expectedTid={ExpectedTid}",
                tokenTid, _expectedTenantId);
            return AuthenticationResult.Fail("invalid_token", "Token tenant does not match configured tenant.");
        }

        var jti = FindClaim(jwt, "jti");
        if (string.IsNullOrEmpty(jti))
        {
            _logger?.LogWarning("JwtAuthenticator: JWT missing jti claim");
            return AuthenticationResult.Fail("invalid_token", "JWT is missing the jti claim.");
        }

        // Look up the upstream vault entry to rehydrate the user's real identity (UPN/OID). The
        // token is already known to be for our tenant by this point.
        var upstreamRecord = await _store.GetUpstreamTokenAsync(jti);
        if (upstreamRecord is null)
        {
            _logger?.LogWarning("JwtAuthenticator: no upstream session jti={Jti} (expired or revoked)", jti);
            return AuthenticationResult.Fail("invalid_token", "No upstream session for this token (expired or revoked).");
        }

        // Rehydrate the ClaimsPrincipal. DocumentTools.GetCallerIdentity reads preferred_username
        // first, then oid, so both must be present.
        var identity = new ClaimsIdentity(authenticationType: "DevBrainOAuth");
        identity.AddClaim(new Claim("preferred_username", upstreamRecord.UserPrincipalName));
        identity.AddClaim(new Claim("oid", upstreamRecord.ObjectId));
        identity.AddClaim(new Claim("tid", upstreamRecord.TenantId));
        identity.AddClaim(new Claim("jti", jti));
        var principal = new ClaimsPrincipal(identity);

        _logger?.LogInformation(
            "JwtAuthenticator: accepted jti={Jti} upn={Upn}",
            jti, upstreamRecord.UserPrincipalName);

        return AuthenticationResult.Success(principal);
    }

    private static string? FindClaim(JsonWebToken jwt, string type) =>
        jwt.Claims.FirstOrDefault(c => c.Type == type)?.Value;
}

public sealed record AuthenticationResult(bool IsAuthenticated, ClaimsPrincipal? Principal, string? ErrorCode, string? ErrorDescription)
{
    public static AuthenticationResult Success(ClaimsPrincipal principal) => new(true, principal, null, null);
    public static AuthenticationResult Fail(string code, string description) => new(false, null, code, description);
}
