using System.Security.Cryptography;
using DevBrain.Functions.Auth.Crypto;
using DevBrain.Functions.Auth.Models;
using DevBrain.Functions.Auth.Services;
using Microsoft.Extensions.Logging;

namespace DevBrain.Functions.Auth.DcrFacade;

/// <summary>
/// Service layer for <c>POST /token</c>. Handles both the <c>authorization_code</c> and
/// <c>refresh_token</c> grant types. Atomicity of code redemption and refresh rotation lives in
/// <see cref="IOAuthStateStore.RedeemAuthCodeAsync"/> / <see cref="IOAuthStateStore.RotateRefreshAsync"/>
/// — this handler is responsible for the validation around those two atomic pivots.
///
/// <para>Acceptance gates covered here:</para>
/// <list type="bullet">
///   <item><b>#2 PKCE downgrade</b> — verifier mismatch returns <c>invalid_grant</c>.</item>
///   <item><b>#3 Code replay</b> — second redemption returns <c>invalid_grant</c> via the atomic store.</item>
///   <item><b>#5 Refresh rotation</b> — every refresh grant rotates; the old token becomes a short-lived replay marker.</item>
/// </list>
/// </summary>
public sealed class TokenHandler
{
    // Refresh tokens: 30 days to match the sprint spec. Rotated on every use.
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(30);

    // Keep the upstream vault alive for the full local refresh window. CallbackHandler creates the
    // initial vault record; the refresh path slides it forward on every successful rotation/replay.
    private static readonly TimeSpan UpstreamVaultTtl = TimeSpan.FromDays(30);

    private readonly IOAuthStateStore _store;
    private readonly DevBrainJwtIssuer _jwtIssuer;
    private readonly TimeProvider _timeProvider;
    private readonly TokenHandlerOptions _options;
    private readonly ILogger<TokenHandler>? _logger;

    public TokenHandler(IOAuthStateStore store, DevBrainJwtIssuer jwtIssuer, TimeProvider timeProvider)
        : this(store, jwtIssuer, timeProvider, TokenHandlerOptions.Default, logger: null)
    {
    }

    public TokenHandler(
        IOAuthStateStore store,
        DevBrainJwtIssuer jwtIssuer,
        TimeProvider timeProvider,
        ILogger<TokenHandler>? logger)
        : this(store, jwtIssuer, timeProvider, TokenHandlerOptions.Default, logger)
    {
    }

    public TokenHandler(
        IOAuthStateStore store,
        DevBrainJwtIssuer jwtIssuer,
        TimeProvider timeProvider,
        TokenHandlerOptions options,
        ILogger<TokenHandler>? logger)
    {
        options.Validate();
        _store = store;
        _jwtIssuer = jwtIssuer;
        _timeProvider = timeProvider;
        _options = options;
        _logger = logger;
    }

    public Task<TokenResult> HandleAsync(TokenRequest request)
    {
        _logger?.LogInformation(
            "TokenHandler: request received grantType={GrantType} clientId={ClientId} hasCode={HasCode} hasRefreshToken={HasRefreshToken}",
            request.GrantType, request.ClientId, !string.IsNullOrEmpty(request.Code), !string.IsNullOrEmpty(request.RefreshToken));

        return request.GrantType switch
        {
            "authorization_code" => HandleAuthorizationCodeAsync(request),
            "refresh_token" => HandleRefreshAsync(request),
            _ => LogAndReturnUnsupported(request.GrantType),
        };
    }

    private Task<TokenResult> LogAndReturnUnsupported(string grantType)
    {
        _logger?.LogWarning("TokenHandler: rejected — unsupported grant_type={GrantType}", grantType);
        return Task.FromResult(TokenResult.Error("unsupported_grant_type", $"grant_type '{grantType}' is not supported."));
    }

    private async Task<TokenResult> HandleAuthorizationCodeAsync(TokenRequest request)
    {
        if (string.IsNullOrEmpty(request.Code))
        {
            _logger?.LogWarning("TokenHandler/authcode: rejected — code required");
            return TokenResult.Error("invalid_request", "code is required for grant_type=authorization_code.");
        }
        if (string.IsNullOrEmpty(request.CodeVerifier))
        {
            _logger?.LogWarning("TokenHandler/authcode: rejected — code_verifier required");
            return TokenResult.Error("invalid_request", "code_verifier is required (PKCE is mandatory).");
        }
        if (string.IsNullOrEmpty(request.ClientId))
        {
            _logger?.LogWarning("TokenHandler/authcode: rejected — client_id required");
            return TokenResult.Error("invalid_request", "client_id is required.");
        }

        // Atomic redeem — see FakeOAuthStateStore / CosmosOAuthStateStore. Single-take semantics
        // guarantee that a second /token call with the same code returns null here.
        var code = await _store.RedeemAuthCodeAsync(request.Code);
        if (code is null)
        {
            _logger?.LogWarning("TokenHandler/authcode: rejected — code invalid, expired, or already redeemed");
            return TokenResult.Error("invalid_grant", "Authorization code is invalid, expired, or already redeemed.");
        }

        if (!string.Equals(code.ClientId, request.ClientId, StringComparison.Ordinal))
        {
            _logger?.LogWarning(
                "TokenHandler/authcode: rejected — client binding mismatch codeClientId={CodeClientId} requestClientId={RequestClientId}",
                code.ClientId, request.ClientId);
            return TokenResult.Error("invalid_grant", "Authorization code was issued to a different client.");
        }

        if (!string.IsNullOrEmpty(request.RedirectUri)
            && !string.Equals(request.RedirectUri, code.ClientRedirectUri, StringComparison.Ordinal))
        {
            _logger?.LogWarning("TokenHandler/authcode: rejected — redirect_uri mismatch");
            return TokenResult.Error("invalid_grant", "redirect_uri does not match the value used at /authorize.");
        }

        if (!Pkce.VerifyChallenge(request.CodeVerifier, code.ClientCodeChallenge))
        {
            _logger?.LogWarning("TokenHandler/authcode: rejected — PKCE verifier does not match stored challenge");
            return TokenResult.Error("invalid_grant", "code_verifier does not match the code_challenge sent at /authorize.");
        }

        var upstreamJti = code.UpstreamJti;
        var (jwt, _) = IssueJwtForUpstream(upstreamJti);
        var refresh = await MintAndStoreRefreshAsync(code.ClientId, upstreamJti);

        _logger?.LogInformation(
            "TokenHandler/authcode: issued access+refresh clientId={ClientId} upstreamJti={Jti}",
            code.ClientId, upstreamJti);

        return TokenResult.Success(new TokenResponse(
            AccessToken: jwt,
            TokenType: "Bearer",
            ExpiresIn: (int)_options.AccessTokenLifetime.TotalSeconds,
            RefreshToken: refresh,
            Scope: "documents.readwrite"));
    }

    private async Task<TokenResult> HandleRefreshAsync(TokenRequest request)
    {
        if (string.IsNullOrEmpty(request.RefreshToken))
        {
            _logger?.LogWarning("TokenHandler/refresh: rejected — refresh_token required");
            return TokenResult.Error("invalid_request", "refresh_token is required for grant_type=refresh_token.");
        }
        if (string.IsNullOrEmpty(request.ClientId))
        {
            _logger?.LogWarning("TokenHandler/refresh: rejected — client_id required");
            return TokenResult.Error("invalid_request", "client_id is required.");
        }

        var replacementRefresh = GenerateOpaqueToken();
        var rotation = await _store.RotateRefreshAsync(
            request.RefreshToken,
            request.ClientId,
            replacementRefresh,
            RefreshTokenLifetime,
            _options.RefreshReplayLifetime,
            UpstreamVaultTtl);

        if (!rotation.Succeeded)
        {
            _logger?.LogWarning(
                "TokenHandler/refresh: rejected reason={Reason} clientId={ClientId} refreshTokenFingerprint={RefreshTokenFingerprint}",
                rotation.LogCode, request.ClientId, FingerprintToken(request.RefreshToken));
            return TokenResult.Error("invalid_grant", "refresh_token is invalid, expired, already rotated outside the replay window, or bound to a different client.");
        }

        var upstreamJti = rotation.UpstreamJti!;
        var (jwt, _) = IssueJwtForUpstream(upstreamJti);

        _logger?.LogInformation(
            "TokenHandler/refresh: {RotationKind} refresh clientId={ClientId} upstreamJti={Jti} refreshTokenFingerprint={RefreshTokenFingerprint} returnedRefreshTokenFingerprint={ReturnedRefreshTokenFingerprint}",
            rotation.IsReplay ? "replayed" : "rotated",
            request.ClientId,
            upstreamJti,
            FingerprintToken(request.RefreshToken),
            FingerprintToken(rotation.RefreshToken!));

        return TokenResult.Success(new TokenResponse(
            AccessToken: jwt,
            TokenType: "Bearer",
            ExpiresIn: (int)_options.AccessTokenLifetime.TotalSeconds,
            RefreshToken: rotation.RefreshToken!,
            Scope: "documents.readwrite"));
    }

    /// <summary>
    /// Mints a DevBrain JWT whose <c>jti</c> is the provided upstream JTI. The subject is synthetic
    /// (<c>upstream-{jti}</c>) — the real user identity is carried in the <see cref="UpstreamTokenRecord"/>
    /// at <c>upstream:{jti}</c> and rehydrated by the middleware on tool-call time.
    /// </summary>
    private (string Token, string Jti) IssueJwtForUpstream(string upstreamJti)
    {
        // NOTE: DevBrainJwtIssuer.Issue generates its own JTI internally, and that JTI is what we
        // want to use as the key into upstream:{jti}. The reason we pass `upstreamJti` separately
        // into this method is that /callback already committed to a JTI when it created the
        // upstream vault record; /token has to issue a JWT whose JTI matches that.
        //
        // But DevBrainJwtIssuer.Issue doesn't accept a pre-chosen JTI. This is the one place we
        // need to side-step it and craft the token directly — or change the issuer to allow an
        // override. The cleanest fix is the override route.
        return _jwtIssuer.IssueWithJti(subject: $"upstream-{upstreamJti}", jti: upstreamJti, lifetime: _options.AccessTokenLifetime);
    }

    private async Task<string> MintAndStoreRefreshAsync(string clientId, string upstreamJti)
    {
        var token = GenerateOpaqueToken();
        var now = _timeProvider.GetUtcNow();
        await _store.SaveRefreshAsync(new DevBrainRefreshRecord
        {
            RefreshToken = token,
            ClientId = clientId,
            UpstreamJti = upstreamJti,
            CreatedAt = now,
            ExpiresAt = now + RefreshTokenLifetime,
            Ttl = (int)RefreshTokenLifetime.TotalSeconds,
        });
        return token;
    }

    private static string GenerateOpaqueToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string FingerprintToken(string token)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token), hash);
        return Convert.ToHexString(hash[..6]).ToLowerInvariant();
    }
}

public sealed record TokenHandlerOptions(TimeSpan AccessTokenLifetime, TimeSpan RefreshReplayLifetime)
{
    public static TokenHandlerOptions Default { get; } = new(
        AccessTokenLifetime: TimeSpan.FromMinutes(10),
        RefreshReplayLifetime: TimeSpan.FromMinutes(5));

    public void Validate()
    {
        ValidateLifetime(nameof(AccessTokenLifetime), AccessTokenLifetime);
        ValidateLifetime(nameof(RefreshReplayLifetime), RefreshReplayLifetime);
    }

    private static void ValidateLifetime(string name, TimeSpan lifetime)
    {
        if (lifetime < TimeSpan.FromMinutes(1) || lifetime > TimeSpan.FromDays(1))
        {
            throw new InvalidOperationException(
                $"TokenHandlerOptions.{name} must be between 1 minute and 24 hours.");
        }
    }
}

public sealed record TokenRequest(
    string GrantType,
    string? ClientId,
    string? Code,
    string? CodeVerifier,
    string? RedirectUri,
    string? RefreshToken);

public sealed record TokenResult(bool IsSuccess, TokenResponse? Response, string? ErrorCode, string? ErrorDescription)
{
    public static TokenResult Success(TokenResponse response) => new(true, response, null, null);
    public static TokenResult Error(string code, string description) => new(false, null, code, description);
}

public sealed record TokenResponse(
    string AccessToken,
    string TokenType,
    int ExpiresIn,
    string RefreshToken,
    string Scope);
