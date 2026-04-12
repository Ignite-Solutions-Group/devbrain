using System.Net;
using System.Text.Json.Serialization;
using DevBrain.Functions.Auth.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;

namespace DevBrain.Functions.Auth.Services;

/// <summary>
/// Cosmos-backed <see cref="IOAuthStateStore"/>. Uses the dedicated <c>oauth_state</c> container
/// (partition key <c>/key</c>) with native TTL enabled.
///
/// Atomic operations (<see cref="RedeemAuthCodeAsync"/>, <see cref="ConsumeRefreshAsync"/>) use the
/// read-then-ETag-conditional-delete pattern: one reader wins the delete, all concurrent readers
/// that lose get a PreconditionFailed and return null. Same optimistic-concurrency idea as
/// <c>CosmosDocumentStore.AppendAsync</c>.
///
/// Expiry is checked defensively against an injected <see cref="TimeProvider"/> on every read —
/// Cosmos native TTL is best-effort and must not be trusted for security decisions.
/// </summary>
public sealed class CosmosOAuthStateStore : IOAuthStateStore
{
    private readonly Container _container;
    private readonly TimeProvider _timeProvider;
    private readonly IUpstreamTokenProtector _protector;

    public CosmosOAuthStateStore(
        CosmosClient cosmosClient,
        IConfiguration configuration,
        TimeProvider timeProvider,
        IUpstreamTokenProtector protector)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "devbrain";
        var containerName = configuration["CosmosDb:OAuthContainerName"] ?? "oauth_state";
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
                    $"CosmosOAuthStateStore.TryDeleteAsync: unexpected CosmosException {ex.StatusCode} for key '{key}': {ex.Message}");
            }
        }
    }

    // ---------------- Key construction ----------------
    //
    // Kept as private static methods rather than a public constants class so callers never
    // construct raw Cosmos keys — all access goes through the typed interface methods.

    private static string ClientKey(string clientId) => $"client:{clientId}";
    private static string TransactionKey(string upstreamState) => $"txn:{upstreamState}";
    private static string AuthCodeKey(string code) => $"code:{code}";
    private static string UpstreamKey(string jti) => $"upstream:{jti}";
    private static string RefreshKey(string refreshToken) => $"refresh:{refreshToken}";

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

        [JsonPropertyName("createdAt")]
        public DateTimeOffset CreatedAt { get; set; }

        [JsonPropertyName("expiresAt")]
        public DateTimeOffset ExpiresAt { get; set; }

        [JsonPropertyName("ttl")]
        public int Ttl { get; set; }
    }
}
