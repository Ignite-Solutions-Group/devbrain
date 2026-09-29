using System.Net;
using System.Text.Json.Serialization;
using DevBrain.Core.Auth.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;

namespace DevBrain.Core.Auth.Services;

/// <summary>
/// Cosmos-backed <see cref="IOAuthStateStore"/>. Uses the dedicated <c>oauth_state</c> container
/// (partition key <c>/key</c>) with native TTL enabled.
///
/// Atomic operations use ETag checks. Auth-code redemption keeps the read-then-conditional-delete
/// pattern; refresh rotation conditionally replaces the old token with a short-lived replay marker
/// so parallel client retries can observe the winning replacement token.
///
/// Expiry is checked defensively against an injected <see cref="TimeProvider"/> on every read —
/// Cosmos native TTL is best-effort and must not be trusted for security decisions.
/// </summary>
public sealed class CosmosOAuthStateStore : IOAuthStateStore
{
    private readonly Container _container;
    private readonly TimeProvider _timeProvider;
    private readonly IUpstreamTokenProtector _protector;
    private readonly string _keyPrefix;

    public CosmosOAuthStateStore(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        TimeProvider timeProvider,
        IUpstreamTokenProtector protector)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "devbrain";
        var containerName = configuration["CosmosDb:OAuthContainerName"] ?? "oauth_state";
        _keyPrefix = configuration["CosmosDb:OAuthKeyPrefix"] ?? string.Empty;
        _container = cosmosClient.GetContainer(databaseName, containerName);
        _timeProvider = timeProvider;
        _protector = protector;
    }

    // ---------------- Registered clients ----------------

    public Task SaveClientAsync(RegisteredClient client)
    {
        var key = ClientKey(client.ClientId);
        client.Id = key;
        client.Key = key;
        return UpsertAsync(client, key);
    }

    public Task<RegisteredClient?> GetClientAsync(string clientId) =>
        ReadWithExpiryAsync<RegisteredClient>(ClientKey(clientId), r => r.ExpiresAt);

    // ---------------- Pending authorization transactions ----------------

    public Task SaveTransactionAsync(AuthTransaction transaction)
    {
        var key = TransactionKey(transaction.UpstreamState);
        transaction.Id = key;
        transaction.Key = key;
        return UpsertAsync(transaction, key);
    }

    public Task<AuthTransaction?> GetTransactionAsync(string upstreamState) =>
        ReadWithExpiryAsync<AuthTransaction>(TransactionKey(upstreamState), r => r.ExpiresAt);

    public Task DeleteTransactionAsync(string upstreamState) =>
        DeleteAsync<AuthTransaction>(TransactionKey(upstreamState));

    // ---------------- DevBrain authorization codes ----------------

    public Task SaveAuthCodeAsync(DevBrainAuthCode code)
    {
        var key = AuthCodeKey(code.Code);
        code.Id = key;
        code.Key = key;
        return UpsertAsync(code, key);
    }

    public async Task<DevBrainAuthCode?> RedeemAuthCodeAsync(string code)
    {
        var key = AuthCodeKey(code);
        var partition = new PartitionKey(key);

        DevBrainAuthCode record;
        string etag;
        try
        {
            var response = await _container.ReadItemAsync<DevBrainAuthCode>(key, partition);
            record = response.Resource;
            etag = response.ETag;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        // Defensive expiry check — Cosmos TTL is best-effort, do not trust for security decisions.
        if (record.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            // Best-effort cleanup of the expired record. Redemption semantics don't depend on the
            // delete succeeding — a second reader will hit this branch and return null the same way.
            await TryDeleteAsync<DevBrainAuthCode>(key, partition, etag);
            return null;
        }

        try
        {
            await _container.DeleteItemAsync<DevBrainAuthCode>(
                key,
                partition,
                new ItemRequestOptions { IfMatchEtag = etag });
            return record;
        }
        catch (CosmosException ex)
            when (ex.StatusCode == HttpStatusCode.PreconditionFailed
               || ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Lost the race to a concurrent redeemer.
            return null;
        }
    }

    // ---------------- Upstream token vault ----------------
    //
    // Upstream records are the only kind that go through IUpstreamTokenProtector. Every save
    // protects the plaintext envelope; every read unprotects it. The public API (IOAuthStateStore)
    // takes and returns UpstreamTokenRecord with a plaintext Envelope property — the Cosmos DTO
    // (UpstreamCosmosDto) with opaque ciphertext bytes is private to this file.

    public async Task SaveUpstreamTokenAsync(UpstreamTokenRecord token)
    {
        var key = UpstreamKey(token.Jti);
        token.Id = key;
        token.Key = key;

        var dto = new UpstreamCosmosDto
        {
            Id = key,
            Key = key,
            Jti = token.Jti,
            EncryptedPayload = _protector.Protect(token.Envelope),
            UserPrincipalName = token.UserPrincipalName,
            ObjectId = token.ObjectId,
            TenantId = token.TenantId,
            Roles = token.Roles.ToArray(),
            CreatedAt = token.CreatedAt,
            ExpiresAt = token.ExpiresAt,
            Ttl = token.Ttl,
        };

        await _container.UpsertItemAsync(dto, new PartitionKey(key));
    }

    public async Task<UpstreamTokenRecord?> GetUpstreamTokenAsync(string jti)
    {
        var key = UpstreamKey(jti);
        try
        {
            var response = await _container.ReadItemAsync<UpstreamCosmosDto>(key, new PartitionKey(key));
            var dto = response.Resource;
            if (dto.ExpiresAt <= _timeProvider.GetUtcNow())
            {
                return null;
            }

            return new UpstreamTokenRecord
            {
                Id = dto.Id,
                Key = dto.Key,
                Jti = dto.Jti,
                Envelope = _protector.Unprotect(dto.EncryptedPayload),
                UserPrincipalName = dto.UserPrincipalName,
                ObjectId = dto.ObjectId,
                TenantId = dto.TenantId,
                Roles = dto.Roles,
                CreatedAt = dto.CreatedAt,
                ExpiresAt = dto.ExpiresAt,
                Ttl = dto.Ttl,
            };
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task DeleteUpstreamTokenAsync(string jti) =>
        DeleteAsync<UpstreamCosmosDto>(UpstreamKey(jti));

    // ---------------- DevBrain refresh tokens (rotated on use) ----------------

    public Task SaveRefreshAsync(DevBrainRefreshRecord refresh)
    {
        var key = RefreshKey(refresh.RefreshToken);
        refresh.Id = key;
        refresh.Key = key;
        return UpsertAsync(refresh, key);
    }

    public async Task<RefreshRotationResult> RotateRefreshAsync(
        string refreshToken,
        string clientId,
        string replacementRefreshToken,
        TimeSpan replacementLifetime,
        TimeSpan replayLifetime,
        TimeSpan upstreamVaultLifetime,
        string? resource = null)
    {
        var key = RefreshKey(refreshToken);
        var partition = new PartitionKey(key);
        var now = _timeProvider.GetUtcNow();

        DevBrainRefreshRecord record;
        string etag;
        try
        {
            var response = await _container.ReadItemAsync<DevBrainRefreshRecord>(key, partition);
            record = response.Resource;
            etag = response.ETag;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return RefreshRotationResult.Rejected(RefreshRotationOutcome.Missing);
        }

        if (record.ExpiresAt <= now)
        {
            await TryDeleteAsync<DevBrainRefreshRecord>(key, partition, etag);
            return RefreshRotationResult.Rejected(
                record.IsReplayMarker
                    ? RefreshRotationOutcome.ReplayWindowExpired
                    : RefreshRotationOutcome.Expired);
        }

        if (!string.Equals(record.ClientId, clientId, StringComparison.Ordinal))
        {
            return RefreshRotationResult.Rejected(RefreshRotationOutcome.WrongClient);
        }
        if (!string.IsNullOrEmpty(resource)
            && !string.IsNullOrEmpty(record.Resource)
            && !string.Equals(record.Resource, resource, StringComparison.Ordinal))
        {
            return RefreshRotationResult.Rejected(RefreshRotationOutcome.WrongResource);
        }

        if (record.IsReplayMarker)
        {
            if (string.IsNullOrEmpty(record.RotatedToRefreshToken))
            {
                return RefreshRotationResult.Rejected(RefreshRotationOutcome.ReplayMarkerMissingReplacement);
            }

            if (!await TouchUpstreamTokenAsync(record.UpstreamJti, upstreamVaultLifetime))
            {
                return RefreshRotationResult.Rejected(RefreshRotationOutcome.UpstreamMissingOrExpired);
            }

            return RefreshRotationResult.Replayed(record.UpstreamJti, record.RotatedToRefreshToken);
        }

        if (!await TouchUpstreamTokenAsync(record.UpstreamJti, upstreamVaultLifetime))
        {
            return RefreshRotationResult.Rejected(RefreshRotationOutcome.UpstreamMissingOrExpired);
        }

        var replacement = new DevBrainRefreshRecord
        {
            RefreshToken = replacementRefreshToken,
            ClientId = record.ClientId,
            UpstreamJti = record.UpstreamJti,
            Resource = record.Resource,
            CreatedAt = now,
            ExpiresAt = now + replacementLifetime,
            Ttl = (int)replacementLifetime.TotalSeconds,
        };
        await SaveRefreshAsync(replacement);

        var marker = new DevBrainRefreshRecord
        {
            Id = key,
            Key = key,
            RefreshToken = refreshToken,
            ClientId = record.ClientId,
            UpstreamJti = record.UpstreamJti,
            Resource = record.Resource,
            CreatedAt = record.CreatedAt,
            ExpiresAt = now + replayLifetime,
            RotatedAt = now,
            RotatedToRefreshToken = replacementRefreshToken,
            Ttl = (int)replayLifetime.TotalSeconds,
        };

        try
        {
            await _container.ReplaceItemAsync(
                marker,
                key,
                partition,
                new ItemRequestOptions { IfMatchEtag = etag });

            return RefreshRotationResult.Rotated(record.UpstreamJti, replacementRefreshToken);
        }
        catch (CosmosException ex)
            when (ex.StatusCode == HttpStatusCode.PreconditionFailed
               || ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Another request won the rotation. Re-read once and, if it left a replay marker,
            // return that winning replacement instead of surfacing a spurious invalid_grant.
            await DeleteAsync<DevBrainRefreshRecord>(RefreshKey(replacementRefreshToken));
            var replay = await ReadRefreshReplayAsync(refreshToken, clientId, resource, upstreamVaultLifetime);
            return replay.Succeeded
                ? replay
                : RefreshRotationResult.Rejected(RefreshRotationOutcome.ConcurrentReplayUnavailable);
        }
    }

    public async Task<DevBrainRefreshRecord?> ConsumeRefreshAsync(string refreshToken)
    {
        var key = RefreshKey(refreshToken);
        var partition = new PartitionKey(key);

        DevBrainRefreshRecord record;
        string etag;
        try
        {
            var response = await _container.ReadItemAsync<DevBrainRefreshRecord>(key, partition);
            record = response.Resource;
            etag = response.ETag;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (record.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            await TryDeleteAsync<DevBrainRefreshRecord>(key, partition, etag);
            return null;
        }

        try
        {
            await _container.DeleteItemAsync<DevBrainRefreshRecord>(
                key,
                partition,
                new ItemRequestOptions { IfMatchEtag = etag });
            return record;
        }
        catch (CosmosException ex)
            when (ex.StatusCode == HttpStatusCode.PreconditionFailed
               || ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // ---------------- Internal helpers ----------------

    private async Task<RefreshRotationResult> ReadRefreshReplayAsync(
        string refreshToken,
        string clientId,
        string? resource,
        TimeSpan upstreamVaultLifetime)
    {
        var key = RefreshKey(refreshToken);
        try
        {
            var response = await _container.ReadItemAsync<DevBrainRefreshRecord>(key, new PartitionKey(key));
            var record = response.Resource;
            if (record.ExpiresAt <= _timeProvider.GetUtcNow())
            {
                return RefreshRotationResult.Rejected(
                    record.IsReplayMarker
                        ? RefreshRotationOutcome.ReplayWindowExpired
                        : RefreshRotationOutcome.Expired);
            }

            if (!string.Equals(record.ClientId, clientId, StringComparison.Ordinal))
            {
                return RefreshRotationResult.Rejected(RefreshRotationOutcome.WrongClient);
            }
            if (!string.IsNullOrEmpty(resource)
                && !string.IsNullOrEmpty(record.Resource)
                && !string.Equals(record.Resource, resource, StringComparison.Ordinal))
            {
                return RefreshRotationResult.Rejected(RefreshRotationOutcome.WrongResource);
            }

            if (!record.IsReplayMarker)
            {
                return RefreshRotationResult.Rejected(RefreshRotationOutcome.ConcurrentReplayUnavailable);
            }

            if (string.IsNullOrEmpty(record.RotatedToRefreshToken))
            {
                return RefreshRotationResult.Rejected(RefreshRotationOutcome.ReplayMarkerMissingReplacement);
            }

            if (!await TouchUpstreamTokenAsync(record.UpstreamJti, upstreamVaultLifetime))
            {
                return RefreshRotationResult.Rejected(RefreshRotationOutcome.UpstreamMissingOrExpired);
            }

            return RefreshRotationResult.Replayed(record.UpstreamJti, record.RotatedToRefreshToken);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return RefreshRotationResult.Rejected(RefreshRotationOutcome.Missing);
        }
    }

    private async Task<bool> TouchUpstreamTokenAsync(string jti, TimeSpan lifetime)
    {
        var key = UpstreamKey(jti);
        var partition = new PartitionKey(key);
        try
        {
            var response = await _container.ReadItemAsync<UpstreamCosmosDto>(key, partition);
            var dto = response.Resource;
            var now = _timeProvider.GetUtcNow();
            if (dto.ExpiresAt <= now)
            {
                await TryDeleteAsync<UpstreamCosmosDto>(key, partition, response.ETag);
                return false;
            }

            dto.ExpiresAt = now + lifetime;
            dto.Ttl = (int)lifetime.TotalSeconds;
            await _container.ReplaceItemAsync(
                dto,
                key,
                partition,
                new ItemRequestOptions { IfMatchEtag = response.ETag });
            return true;
        }
        catch (CosmosException ex)
            when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
        catch (CosmosException ex)
            when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            // A concurrent refresh already slid the expiry. Treat that as success if the vault
            // entry is still present and live, otherwise parallel refreshes can fail spuriously.
            return await UpstreamTokenStillLiveAsync(jti);
        }
    }

    private async Task<bool> UpstreamTokenStillLiveAsync(string jti)
    {
        var key = UpstreamKey(jti);
        try
        {
            var response = await _container.ReadItemAsync<UpstreamCosmosDto>(key, new PartitionKey(key));
            return response.Resource.ExpiresAt > _timeProvider.GetUtcNow();
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private async Task UpsertAsync<T>(T item, string key)
    {
        await _container.UpsertItemAsync(item, new PartitionKey(key));
    }

    private async Task<T?> ReadWithExpiryAsync<T>(string key, Func<T, DateTimeOffset> getExpiresAt)
        where T : class
    {
        try
        {
            var response = await _container.ReadItemAsync<T>(key, new PartitionKey(key));
            var record = response.Resource;
            if (getExpiresAt(record) <= _timeProvider.GetUtcNow())
            {
                return null;
            }
            return record;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task DeleteAsync<T>(string key)
    {
        try
        {
            await _container.DeleteItemAsync<T>(key, new PartitionKey(key));
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Idempotent delete — missing record is success.
        }
    }

    private async Task TryDeleteAsync<T>(string key, PartitionKey partition, string etag)
    {
        try
        {
            await _container.DeleteItemAsync<T>(
                key,
                partition,
                new ItemRequestOptions { IfMatchEtag = etag });
        }
        catch (CosmosException ex)
        {
            // Best-effort cleanup: NotFound/PreconditionFailed are expected in concurrent-delete
            // races (another caller got there first) and must not propagate. Anything else is
            // surprising and worth logging, but still must not throw — this is a cleanup path
            // invoked from expired-record handling and we don't want to mask the caller's result.
            // No ILogger injected at the state-store layer by design (keeps it a pure service);
            // higher-level middleware will observe the record eventually expiring via Cosmos TTL.
            if (ex.StatusCode != System.Net.HttpStatusCode.NotFound
                && ex.StatusCode != System.Net.HttpStatusCode.PreconditionFailed)
            {
                System.Diagnostics.Trace.TraceWarning(
                    $"CosmosOAuthStateStore.TryDeleteAsync: unexpected CosmosException status={ex.StatusCode}");
            }
        }
    }

    // ---------------- Key construction ----------------
    //
    // Kept as private static methods rather than a public constants class so callers never
    // construct raw Cosmos keys — all access goes through the typed interface methods.

    private string ClientKey(string clientId) => ComposeKey(_keyPrefix, "client", clientId);
    private string TransactionKey(string upstreamState) => ComposeKey(_keyPrefix, "txn", upstreamState);
    private string AuthCodeKey(string code) => ComposeKey(_keyPrefix, "code", code);
    private string UpstreamKey(string jti) => ComposeKey(_keyPrefix, "upstream", jti);
    private string RefreshKey(string refreshToken) => ComposeKey(_keyPrefix, "refresh", refreshToken);

    internal static string ComposeKey(string prefix, string recordKind, string identifier) =>
        $"{prefix}{recordKind}:{identifier}";

    /// <summary>
    /// Cosmos wire shape for upstream token vault entries. The <c>encryptedPayload</c> field holds
    /// <see cref="IUpstreamTokenProtector"/>-wrapped bytes (base64-encoded by
    /// <c>System.Text.Json</c>). Private to this file — callers see the plaintext
    /// <see cref="UpstreamTokenRecord"/> shape.
    /// </summary>
    private sealed class UpstreamCosmosDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("jti")]
        public string Jti { get; set; } = string.Empty;

        [JsonPropertyName("encryptedPayload")]
        public byte[] EncryptedPayload { get; set; } = [];

        [JsonPropertyName("userPrincipalName")]
        public string UserPrincipalName { get; set; } = string.Empty;

        [JsonPropertyName("objectId")]
        public string ObjectId { get; set; } = string.Empty;

        [JsonPropertyName("tenantId")]
        public string TenantId { get; set; } = string.Empty;

        [JsonPropertyName("roles")]
        public string[] Roles { get; set; } = [];

        [JsonPropertyName("createdAt")]
        public DateTimeOffset CreatedAt { get; set; }

        [JsonPropertyName("expiresAt")]
        public DateTimeOffset ExpiresAt { get; set; }

        [JsonPropertyName("ttl")]
        public int Ttl { get; set; }
    }
}
