using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace DevBrain.Functions.Auth.Services;

/// <summary>
/// Options for <see cref="EntraOAuthClient"/>. Bound from configuration under the <c>OAuth</c> section.
/// All fields are required; the Program.cs startup path must fail-fast if any is missing.
///
/// <para>
/// Single-tenant is a load-bearing non-goal (see sprint). <see cref="TenantId"/> must be a GUID —
/// never <c>common</c> or <c>organizations</c>. The authority URL is baked as
/// <c>https://login.microsoftonline.com/{TenantId}/oauth2/v2.0</c> with no per-tenant routing.
/// </para>
/// </summary>
public sealed class EntraOAuthClientOptions
{
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>DevBrain's own <c>/callback</c> URL — always the upstream redirect, regardless of which DCR client initiated.</summary>
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>Space-separated Entra scopes. Must include <c>offline_access</c> for refresh tokens and <c>openid profile</c> for the id_token claims DevBrain rehydrates.</summary>
    public string Scope { get; set; } = "openid profile offline_access";
}

/// <summary>
/// Typed <see cref="HttpClient"/> wrapper for DevBrain's single upstream Entra app. Implements
/// the three operations in <see cref="IUpstreamOAuthClient"/> against the Entra v2.0 endpoints.
///
/// <para>
/// <b>ID token validation:</b> every <c>id_token</c> returned by Entra is fully validated before
/// any claim is read — signature against the tenant's published JWKS (via
/// <see cref="IConfigurationManager{T}"/> discovery), issuer, audience, and lifetime. A failure
/// throws <see cref="IdTokenValidationException"/> which the callback endpoint translates into a
/// local 400 with <c>invalid_grant</c>. Trust anchor is the OpenID Connect discovery document;
/// TLS alone is not enough.
/// </para>
/// </summary>
public sealed class EntraOAuthClient : IUpstreamOAuthClient
{
    private readonly HttpClient _httpClient;
    private readonly EntraOAuthClientOptions _options;
    private readonly IConfigurationManager<OpenIdConnectConfiguration> _openIdConfigManager;
    private readonly ILogger<EntraOAuthClient>? _logger;
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    /// <summary>
    /// Test-only convenience constructor that supplies a null logger. Production wiring goes
    /// through <c>AddHttpClient&lt;IUpstreamOAuthClient, EntraOAuthClient&gt;()</c>, which uses
    /// <see cref="ActivatorUtilities"/> to instantiate the client. <see cref="ActivatorUtilities"/>
    /// refuses to pick between two satisfiable constructors — so without the
    /// <see cref="ActivatorUtilitiesConstructorAttribute"/> on the 4-arg ctor below, DI would
    /// fail with "Multiple constructors accepting all given argument types have been found".
    /// </summary>
    public EntraOAuthClient(
        HttpClient httpClient,
        EntraOAuthClientOptions options,
        IConfigurationManager<OpenIdConnectConfiguration> openIdConfigManager)
        : this(httpClient, options, openIdConfigManager, logger: null)
    {
    }

    [ActivatorUtilitiesConstructor]
    public EntraOAuthClient(
        HttpClient httpClient,
        EntraOAuthClientOptions options,
        IConfigurationManager<OpenIdConnectConfiguration> openIdConfigManager,
        ILogger<EntraOAuthClient>? logger)
    {
        ValidateOptions(options);

        _httpClient = httpClient;
        _options = options;
        _openIdConfigManager = openIdConfigManager;
        _logger = logger;

        // Bake the authority as the HttpClient base. Single-tenant non-goal means no per-tenant routing.
        _httpClient.BaseAddress = new Uri($"https://login.microsoftonline.com/{options.TenantId}/oauth2/v2.0/");
        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public Uri BuildAuthorizeUri(string upstreamState, string upstreamPkceChallenge)
    {
        if (string.IsNullOrEmpty(upstreamState)) throw new ArgumentException("upstreamState is required.", nameof(upstreamState));
        if (string.IsNullOrEmpty(upstreamPkceChallenge)) throw new ArgumentException("upstreamPkceChallenge is required.", nameof(upstreamPkceChallenge));

        var builder = new UriBuilder(new Uri(_httpClient.BaseAddress!, "authorize"));
        var query = new List<KeyValuePair<string, string>>
        {
            new("client_id", _options.ClientId),
            new("response_type", "code"),
            new("redirect_uri", _options.RedirectUri),
            new("response_mode", "query"),
            new("scope", _options.Scope),
            new("state", upstreamState),
            new("code_challenge", upstreamPkceChallenge),
            new("code_challenge_method", "S256"),
        };
        builder.Query = BuildQueryString(query);
        return builder.Uri;
    }

    public async Task<UpstreamTokenResponse> ExchangeCodeAsync(string code, string upstreamPkceVerifier)
    {
        if (string.IsNullOrEmpty(code)) throw new ArgumentException("code is required.", nameof(code));
        if (string.IsNullOrEmpty(upstreamPkceVerifier)) throw new ArgumentException("upstreamPkceVerifier is required.", nameof(upstreamPkceVerifier));

        var form = new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _options.RedirectUri,
            ["code_verifier"] = upstreamPkceVerifier,
            ["scope"] = _options.Scope,
        };
        return await PostTokenAsync(form);
    }

    public async Task<UpstreamTokenResponse> RefreshTokenAsync(string upstreamRefreshToken)
    {
        if (string.IsNullOrEmpty(upstreamRefreshToken)) throw new ArgumentException("upstreamRefreshToken is required.", nameof(upstreamRefreshToken));

        var form = new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId,
            ["client_secret"] = _options.ClientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = upstreamRefreshToken,
            ["scope"] = _options.Scope,
        };
        return await PostTokenAsync(form);
    }

    private async Task<UpstreamTokenResponse> PostTokenAsync(Dictionary<string, string> form)
    {
        var grantType = form.TryGetValue("grant_type", out var gt) ? gt : "unknown";
        _logger?.LogInformation("EntraOAuthClient: POST /token upstream grantType={GrantType}", grantType);

        using var content = new FormUrlEncodedContent(form);
        using var response = await _httpClient.PostAsync("token", content);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            _logger?.LogError(
                "EntraOAuthClient: upstream /token returned non-success grantType={GrantType} status={StatusCode} body={Body}",
                grantType, (int)response.StatusCode, body);
            throw new UpstreamOAuthException(
                $"Upstream Entra token endpoint returned {(int)response.StatusCode}: {body}");
        }

        var payload = await response.Content.ReadFromJsonAsync<EntraTokenResponseDto>();
        if (payload is null)
        {
            _logger?.LogError("EntraOAuthClient: upstream /token returned an empty body grantType={GrantType}", grantType);
            throw new UpstreamOAuthException("Upstream Entra token endpoint returned an empty body.");
        }

        if (string.IsNullOrEmpty(payload.AccessToken))
        {
            _logger?.LogError("EntraOAuthClient: upstream /token missing access_token grantType={GrantType}", grantType);
            throw new UpstreamOAuthException("Upstream Entra token endpoint returned a response without an access_token.");
        }
        if (string.IsNullOrEmpty(payload.IdToken))
        {
            _logger?.LogError("EntraOAuthClient: upstream /token missing id_token grantType={GrantType} — openid scope likely missing", grantType);
            throw new UpstreamOAuthException("Upstream Entra token endpoint returned a response without an id_token — the `openid` scope may be missing.");
        }

        var (upn, oid, tid) = await ValidateAndExtractIdTokenClaimsAsync(payload.IdToken);

        _logger?.LogInformation(
            "EntraOAuthClient: upstream /token success grantType={GrantType} upn={Upn} tid={Tid} expiresInSeconds={ExpiresIn}",
            grantType, upn, tid, payload.ExpiresIn);

        return new UpstreamTokenResponse(
            AccessToken: payload.AccessToken,
            RefreshToken: payload.RefreshToken ?? string.Empty,
            IdToken: payload.IdToken,
            ExpiresIn: TimeSpan.FromSeconds(payload.ExpiresIn),
            UserPrincipalName: upn,
            ObjectId: oid,
            TenantId: tid);
    }

    /// <summary>
    /// Validates the id_token's signature, issuer, audience, and lifetime against the tenant's
    /// OpenID Connect discovery document, then extracts the UPN/OID/tid claims.
    ///
    /// <para>
    /// Throws <see cref="IdTokenValidationException"/> on any validation failure — the callback
    /// endpoint translates that into a local 400 with <c>invalid_grant</c> rather than a redirect,
    /// because an id_token that reached DevBrain but couldn't be validated is a security event
    /// (possible MitM, misconfigured tenant, or stale JWKS cache on our side).
    /// </para>
    /// </summary>
    private async Task<(string Upn, string Oid, string Tid)> ValidateAndExtractIdTokenClaimsAsync(string idToken)
    {
        OpenIdConnectConfiguration openIdConfig;
        try
        {
            openIdConfig = await _openIdConfigManager.GetConfigurationAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "EntraOAuthClient: failed to fetch OpenID Connect discovery configuration");
            throw new IdTokenValidationException("Failed to fetch OpenID Connect discovery configuration.", ex);
        }

        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = openIdConfig.Issuer,
            ValidateAudience = true,
            ValidAudience = _options.ClientId,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = openIdConfig.SigningKeys,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(5),
            RequireSignedTokens = true,
            RequireExpirationTime = true,
        };

        TokenValidationResult result;
        try
        {
            result = await _handler.ValidateTokenAsync(idToken, validationParameters);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "EntraOAuthClient: id_token validation threw");
            throw new IdTokenValidationException("id_token validation threw.", ex);
        }

        if (!result.IsValid)
        {
            _logger?.LogError(result.Exception,
                "EntraOAuthClient: id_token validation returned IsValid=false reason={Reason}",
                result.Exception?.GetType().Name ?? "unknown");
            throw new IdTokenValidationException(
                "id_token validation failed: " + (result.Exception?.Message ?? "unknown"),
                result.Exception ?? new InvalidOperationException("Unknown id_token validation failure."));
        }

        var jwt = (JsonWebToken)result.SecurityToken;

        // Entra uses `preferred_username` for UPN, `oid` for ObjectId, `tid` for TenantId.
        // Fall back to `upn` then `email` if `preferred_username` is absent (rare but possible
        // depending on token version).
        var upn = FindClaim(jwt, "preferred_username")
               ?? FindClaim(jwt, "upn")
               ?? FindClaim(jwt, "email")
               ?? string.Empty;
        var oid = FindClaim(jwt, "oid") ?? string.Empty;
        var tid = FindClaim(jwt, "tid") ?? string.Empty;

        return (upn, oid, tid);
    }

    private static string? FindClaim(JsonWebToken jwt, string type) =>
        jwt.Claims.FirstOrDefault(c => c.Type == type)?.Value;

    private static void ValidateOptions(EntraOAuthClientOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.TenantId))
            throw new ArgumentException("OAuth:EntraTenantId is required.", nameof(options));
        if (!Guid.TryParse(options.TenantId, out _))
            throw new ArgumentException($"OAuth:EntraTenantId must be a tenant GUID (got '{options.TenantId}'). Single-tenant only — 'common'/'organizations' are not supported.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.ClientId))
            throw new ArgumentException("OAuth:EntraClientId is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.ClientSecret))
            throw new ArgumentException("OAuth:EntraClientSecret is required.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.RedirectUri))
            throw new ArgumentException("OAuth:RedirectUri is required.", nameof(options));
    }

    private static string BuildQueryString(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        var parts = new List<string>();
        foreach (var pair in pairs)
        {
            parts.Add($"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}");
        }
        return string.Join('&', parts);
    }

    private sealed class EntraTokenResponseDto
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("id_token")] public string? IdToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("token_type")] public string? TokenType { get; set; }
    }
}

/// <summary>Thrown when an upstream Entra call fails in a way the caller should surface as an OAuth error to the client.</summary>
public sealed class UpstreamOAuthException : Exception
{
    public UpstreamOAuthException(string message) : base(message) { }
    public UpstreamOAuthException(string message, Exception innerException) : base(message, innerException) { }
}
