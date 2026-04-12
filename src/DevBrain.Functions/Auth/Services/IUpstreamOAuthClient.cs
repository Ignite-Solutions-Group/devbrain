namespace DevBrain.Functions.Auth.Services;

/// <summary>
/// Thin abstraction over DevBrain's single upstream Entra app. Three operations:
/// <list type="bullet">
///   <item><see cref="BuildAuthorizeUri"/> — pure URL construction for the redirect at <c>/authorize</c>.</item>
///   <item><see cref="ExchangeCodeAsync"/> — POST to Entra's token endpoint at <c>/callback</c>.</item>
///   <item><see cref="RefreshTokenAsync"/> — POST to Entra's token endpoint at <c>/token</c> when a client refreshes.</item>
/// </list>
///
/// <para>
/// Kept as an interface (rather than a sealed class) so unit tests for the endpoints can substitute
/// a <c>FakeUpstreamOAuthClient</c> without standing up a real HttpClient stack. PKCE is NOT an
/// interface because it is pure; this is not pure (network I/O).
/// </para>
/// </summary>
public interface IUpstreamOAuthClient
{
    /// <summary>
    /// Builds the <c>/authorize</c> URL for the upstream Entra app. Includes DevBrain's own
    /// PKCE challenge (<paramref name="upstreamPkceChallenge"/>) and state, not the client's.
    /// See sprint §"PKCE double-dance" — the two hops use independent PKCE pairs.
    /// </summary>
    Uri BuildAuthorizeUri(string upstreamState, string upstreamPkceChallenge);

    /// <summary>
    /// Exchanges an authorization code returned by Entra at <c>/callback</c> for an access + refresh
    /// token pair. Sends DevBrain's own PKCE verifier (not the client's).
    /// </summary>
    Task<UpstreamTokenResponse> ExchangeCodeAsync(string code, string upstreamPkceVerifier);

    /// <summary>
    /// Refreshes the upstream access token. Called from <c>/token</c> when a client uses the
    /// <c>refresh_token</c> grant. The returned refresh token may or may not differ from the input
    /// depending on Entra's rotation policy — callers must always replace their stored value.
    /// </summary>
    Task<UpstreamTokenResponse> RefreshTokenAsync(string upstreamRefreshToken);
}

/// <summary>
/// Upstream Entra token response. Fields mirror the subset we care about from the Entra v2.0 token
/// response. <see cref="UserPrincipalName"/>/<see cref="ObjectId"/>/<see cref="TenantId"/> are
/// extracted from the <c>id_token</c> claims by the client so callers don't have to re-parse it.
/// </summary>
public sealed record UpstreamTokenResponse(
    string AccessToken,
    string RefreshToken,
    string IdToken,
    TimeSpan ExpiresIn,
    string UserPrincipalName,
    string ObjectId,
    string TenantId);
