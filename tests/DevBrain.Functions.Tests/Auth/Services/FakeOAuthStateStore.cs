using DevBrain.Functions.Auth.Models;
using DevBrain.Functions.Auth.Services;
using DevBrain.Functions.Tests.TestHelpers;

namespace DevBrain.Functions.Tests.Auth.Services;

/// <summary>
/// In-memory <see cref="IOAuthStateStore"/> for unit tests. Semantics match
/// <see cref="CosmosOAuthStateStore"/>:
/// <list type="bullet">
///   <item>Defensive expiry check on every read using the injected <see cref="TimeProvider"/> (typically a <c>FakeTimeProvider</c>).</item>
///   <item><see cref="RedeemAuthCodeAsync"/> and <see cref="ConsumeRefreshAsync"/> are single-take under a lock —
///         concurrent callers get the record at most once.</item>
/// </list>
///
/// <para>
/// Exposes <see cref="ReadCallCount"/> for tests that need to assert zero-reads (gate #8:
/// cross-tenant token rejection must short-circuit before any state lookup).
/// </para>
/// </summary>
public sealed class FakeOAuthStateStore : IOAuthStateStore
{
    private readonly TimeProvider _timeProvider;
    private readonly IUpstreamTokenProtector _protector;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, RegisteredClient> _clients = new();
    private readonly Dictionary<string, AuthTransaction> _transactions = new();
    private readonly Dictionary<string, DevBrainAuthCode> _codes = new();
    private readonly Dictionary<string, UpstreamDto> _upstreams = new();
    private readonly Dictionary<string, DevBrainRefreshRecord> _refreshes = new();

    /// <summary>
    /// Default constructor wires a <see cref="FakeUpstreamTokenProtector"/> so existing tests that
    /// don't care about the encryption path keep working with zero changes. Tests that want to
    /// assert the protector is invoked should pass an explicit protector (typically another
    /// <see cref="FakeUpstreamTokenProtector"/> whose call counts they read).
    /// </summary>
    public FakeOAuthStateStore(TimeProvider timeProvider)
        : this(timeProvider, new FakeUpstreamTokenProtector())
    {
    }

    public FakeOAuthStateStore(TimeProvider timeProvider, IUpstreamTokenProtector protector)
    {
        _timeProvider = timeProvider;
        _protector = protector;
    }

    /// <summary>
    /// Count of calls made to any read method (Get*, Redeem*, Consume*). Tests assert zero-reads
    /// to verify early-rejection paths (e.g., bad <c>aud</c> or <c>tid</c>) short-circuit before the state store.
    /// </summary>
    public int ReadCallCount { get; private set; }

    // ---------------- Clients ----------------

    public Task SaveClientAsync(RegisteredClient client)
    {
        lock (_lock)
        {
            _clients[client.ClientId] = Clone(client);
        }
        return Task.CompletedTask;
    }

    public Task<RegisteredClient?> GetClientAsync(string clientId)
    {
        lock (_lock)
        {
            ReadCallCount++;
            if (!_clients.TryGetValue(clientId, out var record) || IsExpired(record.ExpiresAt))
            {
                return Task.FromResult<RegisteredClient?>(null);
            }
            return Task.FromResult<RegisteredClient?>(Clone(record));
        }
    }

    // ---------------- Transactions ----------------

    public Task SaveTransactionAsync(AuthTransaction transaction)
    {
        lock (_lock)
        {
            _transactions[transaction.UpstreamState] = Clone(transaction);
        }
        return Task.CompletedTask;
    }

    public Task<AuthTransaction?> GetTransactionAsync(string upstreamState)
    {
        lock (_lock)
        {
            ReadCallCount++;
            if (!_transactions.TryGetValue(upstreamState, out var record) || IsExpired(record.ExpiresAt))
            {
                return Task.FromResult<AuthTransaction?>(null);
            }
            return Task.FromResult<AuthTransaction?>(Clone(record));
        }
    }

    public Task DeleteTransactionAsync(string upstreamState)
    {
        lock (_lock)
        {
            _transactions.Remove(upstreamState);
        }
        return Task.CompletedTask;
    }

    // ---------------- Auth codes ----------------

    public Task SaveAuthCodeAsync(DevBrainAuthCode code)
    {
        lock (_lock)
        {
            _codes[code.Code] = Clone(code);
        }
        return Task.CompletedTask;
    }

    public Task<DevBrainAuthCode?> RedeemAuthCodeAsync(string code)
    {
        lock (_lock)
        {
            ReadCallCount++;
            if (!_codes.TryGetValue(code, out var record))
            {
                return Task.FromResult<DevBrainAuthCode?>(null);
            }
            // Remove unconditionally: whether the record is live or expired, the slot must be free
            // so a subsequent redeem call returns null. A live record is returned once; an expired
            // record is returned as null.
            _codes.Remove(code);
            if (IsExpired(record.ExpiresAt))
            {
                return Task.FromResult<DevBrainAuthCode?>(null);
            }
            return Task.FromResult<DevBrainAuthCode?>(Clone(record));
        }
    }

    // ---------------- Upstream tokens ----------------
    //
    // Mirrors CosmosOAuthStateStore: every save protects the envelope through
    // IUpstreamTokenProtector, every read unprotects. The internal state holds an opaque
    // UpstreamDto with ciphertext bytes — never the plaintext envelope.

    public Task SaveUpstreamTokenAsync(UpstreamTokenRecord token)
    {
        lock (_lock)
        {
            _upstreams[token.Jti] = new UpstreamDto
            {
                Jti = token.Jti,
                EncryptedPayload = _protector.Protect(token.Envelope),
                UserPrincipalName = token.UserPrincipalName,
                ObjectId = token.ObjectId,
                TenantId = token.TenantId,
                CreatedAt = token.CreatedAt,
                ExpiresAt = token.ExpiresAt,
                Ttl = token.Ttl,
            };
        }
        return Task.CompletedTask;
    }

    public Task<UpstreamTokenRecord?> GetUpstreamTokenAsync(string jti)
    {
        lock (_lock)
        {
            ReadCallCount++;
            if (!_upstreams.TryGetValue(jti, out var dto) || IsExpired(dto.ExpiresAt))
            {
                return Task.FromResult<UpstreamTokenRecord?>(null);
            }

            return Task.FromResult<UpstreamTokenRecord?>(new UpstreamTokenRecord
            {
                Jti = dto.Jti,
                Envelope = _protector.Unprotect(dto.EncryptedPayload),
                UserPrincipalName = dto.UserPrincipalName,
                ObjectId = dto.ObjectId,
                TenantId = dto.TenantId,
                CreatedAt = dto.CreatedAt,
                ExpiresAt = dto.ExpiresAt,
                Ttl = dto.Ttl,
            });
        }
    }

    public Task DeleteUpstreamTokenAsync(string jti)
    {
        lock (_lock)
        {
            _upstreams.Remove(jti);
        }
        return Task.CompletedTask;
    }

    /// <summary>Internal ciphertext-bearing DTO mirroring <c>CosmosOAuthStateStore.UpstreamCosmosDto</c>.</summary>
    private sealed class UpstreamDto
    {
        public string Jti { get; set; } = string.Empty;
        public byte[] EncryptedPayload { get; set; } = [];
        public string UserPrincipalName { get; set; } = string.Empty;
        public string ObjectId { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public int Ttl { get; set; }
    }

    // ---------------- Refresh tokens ----------------

    public Task SaveRefreshAsync(DevBrainRefreshRecord refresh)
    {
        lock (_lock)
        {
            _refreshes[refresh.RefreshToken] = Clone(refresh);
        }
        return Task.CompletedTask;
    }

    public Task<DevBrainRefreshRecord?> ConsumeRefreshAsync(string refreshToken)
    {
        lock (_lock)
        {
            ReadCallCount++;
            if (!_refreshes.TryGetValue(refreshToken, out var record))
            {
                return Task.FromResult<DevBrainRefreshRecord?>(null);
            }
            _refreshes.Remove(refreshToken);
            if (IsExpired(record.ExpiresAt))
            {
                return Task.FromResult<DevBrainRefreshRecord?>(null);
            }
            return Task.FromResult<DevBrainRefreshRecord?>(Clone(record));
        }
    }

    // ---------------- Helpers ----------------

    private bool IsExpired(DateTimeOffset expiresAt) => expiresAt <= _timeProvider.GetUtcNow();

    // Cheap clone so the caller can't mutate the stored copy. Uses the same JSON round-trip as
    // Cosmos does on the wire — ensures test expectations match production serialization surface.
    private static T Clone<T>(T record) where T : class
    {
        var json = System.Text.Json.JsonSerializer.Serialize(record);
        return System.Text.Json.JsonSerializer.Deserialize<T>(json)!;
    }
}
