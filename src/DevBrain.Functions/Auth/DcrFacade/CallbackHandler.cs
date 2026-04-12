using System.Security.Cryptography;
using DevBrain.Functions.Auth.Models;
using DevBrain.Functions.Auth.Services;
using Microsoft.Extensions.Logging;

namespace DevBrain.Functions.Auth.DcrFacade;

/// <summary>
/// Service layer for <c>GET /callback</c>. This is the integration choke point — the only endpoint
/// that exercises <see cref="IOAuthStateStore"/>, <see cref="IUpstreamOAuthClient"/>, and
/// <see cref="DevBrainJwtIssuer"/> in concert. By the time this is written, all three services
/// already have their own passing tests.
///
/// <para>Flow (happy path):</para>
/// <list type="number">
///   <item>Look up the stored transaction by the <c>state</c> parameter. Missing/expired → 400.</item>
///   <item>Exchange the Entra code for upstream tokens (access + refresh + id_token) using DevBrain's own PKCE verifier.</item>
///   <item>Pre-commit a JTI. Store the upstream tokens (ciphertext + UPN/OID/tid claims) at <c>upstream:{jti}</c>.</item>
///   <item>Store a new DevBrain auth code at <c>code:{code}</c> referencing that JTI and the client's PKCE challenge.</item>
///   <item>Delete the transaction (one-shot).</item>
///   <item>Return a redirect to the client's <c>redirect_uri</c> with <c>?code={devbrainCode}&amp;state={clientState}</c>.</item>
/// </list>
///
/// <para>Acceptance gate #4 lives here: expired transactions are rejected before any upstream call.</para>
/// </summary>
public sealed class CallbackHandler
{
    private static readonly TimeSpan AuthCodeTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long the upstream token vault record persists. Long enough to survive multiple refresh
    /// cycles — the DevBrain refresh token is rotated every time it's used, and each rotation
    /// touches the upstream vault entry's expiry. 30 days matches the refresh token lifetime.
    /// </summary>
    private static readonly TimeSpan UpstreamVaultTtl = TimeSpan.FromDays(30);

    private readonly IOAuthStateStore _store;
    private readonly IUpstreamOAuthClient _upstream;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CallbackHandler>? _logger;

    public CallbackHandler(IOAuthStateStore store, IUpstreamOAuthClient upstream, TimeProvider timeProvider)
        : this(store, upstream, timeProvider, logger: null)
    {
    }

    public CallbackHandler(
        IOAuthStateStore store,
        IUpstreamOAuthClient upstream,
        TimeProvider timeProvider,
        ILogger<CallbackHandler>? logger)
    {
        _store = store;
        _upstream = upstream;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<CallbackResult> HandleAsync(CallbackRequest request)
    {
        _logger?.LogInformation(
            "CallbackHandler: request received hasCode={HasCode} hasState={HasState} hasError={HasError}",
            !string.IsNullOrEmpty(request.Code), !string.IsNullOrEmpty(request.State), !string.IsNullOrEmpty(request.Error));

        // Upstream error forwarding: if Entra rejected the auth (user denied consent, etc.), the
        // redirect carries ?error=... instead of ?code=.... We still need the transaction to know
        // which client to forward the error to.
        if (!string.IsNullOrEmpty(request.Error))
        {
            _logger?.LogWarning(
                "CallbackHandler: upstream returned error={UpstreamError} description={UpstreamErrorDescription}",
                request.Error, request.ErrorDescription);
            var errorTxn = await _store.GetTransactionAsync(request.State ?? string.Empty);
            if (errorTxn is null)
            {
                _logger?.LogWarning("CallbackHandler: upstream error with unknown state — no transaction to forward to");
                return CallbackResult.LocalError("invalid_state", "Unknown or expired state.");
            }
            await _store.DeleteTransactionAsync(errorTxn.UpstreamState);
            return CallbackResult.RedirectToClient(BuildClientErrorRedirect(errorTxn, request.Error, request.ErrorDescription));
        }

        if (string.IsNullOrEmpty(request.Code))
        {
            _logger?.LogWarning("CallbackHandler: rejected — code missing");
            return CallbackResult.LocalError("invalid_request", "code is required.");
        }
        if (string.IsNullOrEmpty(request.State))
        {
            _logger?.LogWarning("CallbackHandler: rejected — state missing");
            return CallbackResult.LocalError("invalid_request", "state is required.");
        }

        // Gate #4: expired transaction rejection. The state store's expiry check uses the injected
        // TimeProvider, so tests can advance time past 600s without sleeping.
        var transaction = await _store.GetTransactionAsync(request.State);
        if (transaction is null)
        {
            _logger?.LogWarning(
                "CallbackHandler: rejected — transaction not found or expired state={State}",
                request.State);
            return CallbackResult.LocalError("invalid_state", "Unknown or expired transaction state.");
        }

        // Exchange upstream — DevBrain sends its own PKCE verifier, never the client's.
        UpstreamTokenResponse upstreamTokens;
        try
        {
            upstreamTokens = await _upstream.ExchangeCodeAsync(request.Code, transaction.UpstreamPkceVerifier);
        }
        catch (IdTokenValidationException ex)
        {
            // Security event: an id_token reached DevBrain but failed JWKS/issuer/audience/lifetime
            // validation. Do NOT redirect back to the client (that would paper over the failure in
            // their error-handling UI). Return a local 400 with invalid_grant so the failure
            // surfaces in logs and error reporting. The transaction is still consumed so an
            // attacker can't replay the same state.
            _logger?.LogError(ex,
                "CallbackHandler: id_token validation failed clientId={ClientId} upstreamState={UpstreamState}",
                transaction.ClientId, transaction.UpstreamState);
            await _store.DeleteTransactionAsync(transaction.UpstreamState);
            return CallbackResult.LocalError("invalid_grant", $"id_token validation failed: {ex.Message}");
        }
        catch (UpstreamOAuthException ex)
        {
            // Forward upstream transport/grant failure to the client with a generic error. We don't
            // leak the upstream error body — it could reveal tenant internals.
            _logger?.LogError(ex,
                "CallbackHandler: upstream token exchange failed clientId={ClientId} upstreamState={UpstreamState}",
                transaction.ClientId, transaction.UpstreamState);
            await _store.DeleteTransactionAsync(transaction.UpstreamState);
            return CallbackResult.RedirectToClient(BuildClientErrorRedirect(
                transaction,
                "server_error",
                $"Upstream token exchange failed: {ex.Message}"));
        }

        // Pre-commit a JTI. This JTI is the key into the upstream vault AND the `jti` claim of the
        // DevBrain JWT that /token will eventually issue. /token uses DevBrainJwtIssuer.IssueWithJti
        // with this exact value so the middleware can find the vault record on tool-call time.
        var jti = DevBrainJwtIssuer.NewJti();
        var now = _timeProvider.GetUtcNow();

        // The state store wraps the envelope through IUpstreamTokenProtector (ASP.NET Core Data
        // Protection, purpose string DevBrain.OAuth.UpstreamToken) on save — callers hand it the
        // plaintext shape and only ever see the plaintext shape. See sprint gate #9.
        await _store.SaveUpstreamTokenAsync(new UpstreamTokenRecord
        {
            Jti = jti,
            Envelope = new UpstreamTokenEnvelope(
                AccessToken: upstreamTokens.AccessToken,
                RefreshToken: upstreamTokens.RefreshToken,
                ExpiresAtUnixSeconds: (now + upstreamTokens.ExpiresIn).ToUnixTimeSeconds()),
            UserPrincipalName = upstreamTokens.UserPrincipalName,
            ObjectId = upstreamTokens.ObjectId,
            TenantId = upstreamTokens.TenantId,
            CreatedAt = now,
            ExpiresAt = now + UpstreamVaultTtl,
            Ttl = (int)UpstreamVaultTtl.TotalSeconds,
        });

        // Mint the DevBrain authorization code and point it at the upstream JTI + the client's
        // original PKCE challenge. /token will redeem this code atomically and verify PKCE.
        var devbrainCode = GenerateOpaqueToken();
        await _store.SaveAuthCodeAsync(new DevBrainAuthCode
        {
            Code = devbrainCode,
            ClientId = transaction.ClientId,
            ClientRedirectUri = transaction.ClientRedirectUri,
            ClientCodeChallenge = transaction.ClientCodeChallenge,
            ClientCodeChallengeMethod = transaction.ClientCodeChallengeMethod,
            UpstreamJti = jti,
            CreatedAt = now,
            ExpiresAt = now + AuthCodeTtl,
            Ttl = (int)AuthCodeTtl.TotalSeconds,
        });

        await _store.DeleteTransactionAsync(transaction.UpstreamState);

        _logger?.LogInformation(
            "CallbackHandler: success clientId={ClientId} upstreamJti={Jti} upn={Upn} — minted devbrain auth code, redirecting to client",
            transaction.ClientId, jti, upstreamTokens.UserPrincipalName);

        var redirect = BuildClientSuccessRedirect(transaction, devbrainCode);
        return CallbackResult.RedirectToClient(redirect);
    }

    private static Uri BuildClientSuccessRedirect(AuthTransaction transaction, string devbrainCode)
    {
        var builder = new UriBuilder(transaction.ClientRedirectUri);
        var pairs = new List<string>
        {
            $"code={Uri.EscapeDataString(devbrainCode)}",
        };
        if (!string.IsNullOrEmpty(transaction.ClientState))
        {
            pairs.Add($"state={Uri.EscapeDataString(transaction.ClientState)}");
        }
        builder.Query = AppendToQuery(builder.Query, pairs);
        return builder.Uri;
    }

    private static Uri BuildClientErrorRedirect(AuthTransaction transaction, string error, string? errorDescription)
    {
        var builder = new UriBuilder(transaction.ClientRedirectUri);
        var pairs = new List<string>
        {
            $"error={Uri.EscapeDataString(error)}",
        };
        if (!string.IsNullOrEmpty(errorDescription))
        {
            pairs.Add($"error_description={Uri.EscapeDataString(errorDescription)}");
        }
        if (!string.IsNullOrEmpty(transaction.ClientState))
        {
            pairs.Add($"state={Uri.EscapeDataString(transaction.ClientState)}");
        }
        builder.Query = AppendToQuery(builder.Query, pairs);
        return builder.Uri;
    }

    private static string AppendToQuery(string existingQuery, IEnumerable<string> pairs)
    {
        var trimmed = existingQuery.TrimStart('?');
        return string.IsNullOrEmpty(trimmed)
            ? string.Join('&', pairs)
            : $"{trimmed}&{string.Join('&', pairs)}";
    }

    private static string GenerateOpaqueToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

public sealed record CallbackRequest(
    string? Code,
    string? State,
    string? Error,
    string? ErrorDescription);

public sealed record CallbackResult(
    CallbackResultKind Kind,
    Uri? RedirectTo,
    string? ErrorCode,
    string? ErrorDescription)
{
    public static CallbackResult RedirectToClient(Uri uri) => new(CallbackResultKind.Redirect, uri, null, null);
    public static CallbackResult LocalError(string code, string description) => new(CallbackResultKind.LocalError, null, code, description);
}

public enum CallbackResultKind
{
    Redirect,
    LocalError,
}

