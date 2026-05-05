using DevBrain.Functions.Auth.Models;

namespace DevBrain.Functions.Auth.Services;

/// <summary>
/// Persistence for the DCR OAuth facade's five record kinds. Backed by a dedicated Cosmos
/// container (<c>oauth_state</c>, partition key <c>/key</c>, native TTL enabled) in production;
/// by an in-memory fake in unit tests.
///
/// Design notes:
/// <list type="bullet">
///   <item>Record keys are the state store's concern, not the caller's. Pass the primary
///         identifier (ClientId, UpstreamState, Code, Jti, RefreshToken) — the store constructs
///         the prefixed Cosmos key.</item>
///   <item>Expiry is checked defensively against an injected <see cref="TimeProvider"/> on every
///         read. Cosmos native TTL is best-effort and not trusted for security decisions.</item>
///   <item>Auth-code redemption and legacy refresh consumption are single-take operations.
///         Refresh rotation uses <see cref="RotateRefreshAsync"/> so concurrent client retries can
///         replay the winning rotation for a short grace window.</item>
/// </list>
/// </summary>
public interface IOAuthStateStore
{
    // ---------------- Registered clients (DCR output) ----------------

    Task SaveClientAsync(RegisteredClient client);

    Task<RegisteredClient?> GetClientAsync(string clientId);

    // ---------------- Pending authorization transactions ----------------

    Task SaveTransactionAsync(AuthTransaction transaction);

    Task<AuthTransaction?> GetTransactionAsync(string upstreamState);

    Task DeleteTransactionAsync(string upstreamState);

    // ---------------- DevBrain authorization codes ----------------

    Task SaveAuthCodeAsync(DevBrainAuthCode code);

    /// <summary>
    /// Atomically redeems an authorization code. Returns the record on success.
    /// Returns <c>null</c> if the code does not exist, has expired, or has already been redeemed.
    /// Concurrent callers are guaranteed: exactly one receives the record, all others receive <c>null</c>.
    /// </summary>
    Task<DevBrainAuthCode?> RedeemAuthCodeAsync(string code);

    // ---------------- Upstream token vault ----------------

    Task SaveUpstreamTokenAsync(UpstreamTokenRecord token);

    Task<UpstreamTokenRecord?> GetUpstreamTokenAsync(string jti);

    Task DeleteUpstreamTokenAsync(string jti);

    // ---------------- DevBrain refresh tokens (rotated on use) ----------------

    Task SaveRefreshAsync(DevBrainRefreshRecord refresh);

    /// <summary>
    /// Rotates a refresh token when it is still active and issued to <paramref name="clientId"/>.
    /// The old refresh token becomes a short-lived replay marker pointing to
    /// <paramref name="replacementRefreshToken"/>; calls during that window return the same
    /// replacement token. The referenced upstream vault record is defensively extended as part of
    /// the rotation/replay path.
    /// </summary>
    Task<RefreshRotationResult> RotateRefreshAsync(
        string refreshToken,
        string clientId,
        string replacementRefreshToken,
        TimeSpan replacementLifetime,
        TimeSpan replayLifetime,
        TimeSpan upstreamVaultLifetime);

    /// <summary>
    /// Atomically consumes a refresh token. Returns the stored record on success.
    /// Returns <c>null</c> if the token does not exist, has expired, or has already been rotated.
    /// Callers are expected to mint a new refresh and <see cref="SaveRefreshAsync"/> it as part of the same operation.
    /// Legacy primitive retained for low-level store tests; new token endpoint code should use
    /// <see cref="RotateRefreshAsync"/>.
    /// </summary>
    Task<DevBrainRefreshRecord?> ConsumeRefreshAsync(string refreshToken);
}
