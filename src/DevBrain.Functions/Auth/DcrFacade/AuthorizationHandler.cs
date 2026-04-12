using DevBrain.Functions.Auth.Crypto;
using DevBrain.Functions.Auth.Models;
using DevBrain.Functions.Auth.Services;
using Microsoft.Extensions.Logging;

namespace DevBrain.Functions.Auth.DcrFacade;

/// <summary>
/// Service layer for <c>GET /authorize</c>. Validates the client, generates the upstream-side
/// PKCE pair, persists an <see cref="AuthTransaction"/>, and returns the URL to redirect the
/// user's browser to (Entra's own <c>/authorize</c>).
///
/// <para>
/// PKCE double-dance: the client's <c>code_challenge</c> is stored as-is and validated at <c>/token</c>;
/// the upstream verifier/challenge pair is freshly generated here and never sent to the client.
/// See sprint §"PKCE double-dance" — the two hops are independent.
/// </para>
/// </summary>
public sealed class AuthorizationHandler
{
    private static readonly TimeSpan TransactionTtl = TimeSpan.FromSeconds(600);

    private readonly IOAuthStateStore _store;
    private readonly IUpstreamOAuthClient _upstream;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AuthorizationHandler>? _logger;

    public AuthorizationHandler(IOAuthStateStore store, IUpstreamOAuthClient upstream, TimeProvider timeProvider)
        : this(store, upstream, timeProvider, logger: null)
    {
    }

    public AuthorizationHandler(
        IOAuthStateStore store,
        IUpstreamOAuthClient upstream,
        TimeProvider timeProvider,
        ILogger<AuthorizationHandler>? logger)
    {
        _store = store;
        _upstream = upstream;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<AuthorizationResult> HandleAsync(AuthorizationRequest request)
    {
        _logger?.LogInformation(
            "AuthorizationHandler: request received clientId={ClientId} responseType={ResponseType} redirectUri={RedirectUri} hasState={HasState} codeChallengeMethod={CodeChallengeMethod}",
            request.ClientId, request.ResponseType, request.RedirectUri, !string.IsNullOrEmpty(request.State), request.CodeChallengeMethod);

        // RFC 6749 §4.1.1: response_type, client_id, redirect_uri are the structural inputs.
        // RFC 7636 §4.3: code_challenge + code_challenge_method are PKCE.
        if (string.IsNullOrEmpty(request.ClientId))
        {
            _logger?.LogWarning("AuthorizationHandler: rejected — missing client_id");
            return AuthorizationResult.Error("invalid_request", "client_id is required.");
        }
        if (request.ResponseType != "code")
        {
            _logger?.LogWarning("AuthorizationHandler: rejected — unsupported response_type={ResponseType}", request.ResponseType);
            return AuthorizationResult.Error("unsupported_response_type", "Only response_type=code is supported.");
        }
        if (string.IsNullOrEmpty(request.RedirectUri))
        {
            _logger?.LogWarning("AuthorizationHandler: rejected — missing redirect_uri");
            return AuthorizationResult.Error("invalid_request", "redirect_uri is required.");
        }
        if (string.IsNullOrEmpty(request.CodeChallenge))
        {
            _logger?.LogWarning("AuthorizationHandler: rejected — missing code_challenge (PKCE mandatory)");
            return AuthorizationResult.Error("invalid_request", "code_challenge is required (PKCE is mandatory).");
        }
        if (request.CodeChallengeMethod != "S256")
        {
            _logger?.LogWarning("AuthorizationHandler: rejected — code_challenge_method={Method} (only S256)", request.CodeChallengeMethod);
            return AuthorizationResult.Error("invalid_request", "code_challenge_method must be S256.");
        }

        var client = await _store.GetClientAsync(request.ClientId);
        if (client is null)
        {
            _logger?.LogWarning("AuthorizationHandler: rejected — unknown or expired clientId={ClientId}", request.ClientId);
            return AuthorizationResult.Error("invalid_client", "Unknown or expired client_id.");
        }

        // redirect_uri must be one of the pre-registered values, exact match. This is the
        // canonical mitigation against open-redirect via a stolen client_id.
        if (!client.RedirectUris.Any(u => string.Equals(u, request.RedirectUri, StringComparison.Ordinal)))
        {
            _logger?.LogWarning(
                "AuthorizationHandler: rejected — redirect_uri {RedirectUri} not registered for clientId={ClientId}",
                request.RedirectUri, request.ClientId);
            return AuthorizationResult.Error("invalid_redirect_uri", "redirect_uri is not registered for this client_id.");
        }

        // Generate DevBrain's own upstream PKCE pair and a random state. Both are independent
        // of whatever the client provided.
        var (upstreamVerifier, upstreamChallenge) = Pkce.GenerateChallengePair();
        var upstreamState = Pkce.GenerateChallengePair().Verifier; // Reuse the verifier generator as a random-string source.

        var now = _timeProvider.GetUtcNow();
        var transaction = new AuthTransaction
        {
            ClientId = request.ClientId,
            ClientRedirectUri = request.RedirectUri,
            ClientState = request.State,
            ClientCodeChallenge = request.CodeChallenge,
            ClientCodeChallengeMethod = request.CodeChallengeMethod,
            UpstreamState = upstreamState,
            UpstreamPkceVerifier = upstreamVerifier,
            CreatedAt = now,
            ExpiresAt = now + TransactionTtl,
            Ttl = (int)TransactionTtl.TotalSeconds,
        };
        await _store.SaveTransactionAsync(transaction);

        var upstreamAuthorizeUri = _upstream.BuildAuthorizeUri(upstreamState, upstreamChallenge);

        _logger?.LogInformation(
            "AuthorizationHandler: transaction persisted clientId={ClientId} upstreamState={UpstreamState} redirecting to Entra",
            request.ClientId, upstreamState);

        return AuthorizationResult.Success(upstreamAuthorizeUri);
    }
}

/// <summary>Query-string inputs to <c>/authorize</c>, flattened into a record. The endpoint class extracts these from <see cref="Microsoft.Azure.Functions.Worker.Http.HttpRequestData.Url"/>.</summary>
public sealed record AuthorizationRequest(
    string ClientId,
    string ResponseType,
    string RedirectUri,
    string? State,
    string CodeChallenge,
    string CodeChallengeMethod);

/// <summary>
/// Either "redirect the user here" (success) or "show this error" (failure). RFC 6749 §4.1.2.1 says
/// some errors should redirect to the client's redirect_uri with <c>?error=</c>, but DevBrain returns
/// a 400 for structural errors (missing client_id, unknown client_id, unregistered redirect_uri)
/// because those are the cases where redirecting could be weaponized.
/// </summary>
public sealed record AuthorizationResult(bool IsSuccess, Uri? RedirectTo, string? ErrorCode, string? ErrorDescription)
{
    public static AuthorizationResult Success(Uri redirectTo) => new(true, redirectTo, null, null);
    public static AuthorizationResult Error(string code, string description) => new(false, null, code, description);
}
