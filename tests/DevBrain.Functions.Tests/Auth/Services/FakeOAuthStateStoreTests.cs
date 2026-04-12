using DevBrain.Functions.Auth.Models;
using DevBrain.Functions.Auth.Services;
using DevBrain.Functions.Tests.TestHelpers;
using Microsoft.Extensions.Time.Testing;

namespace DevBrain.Functions.Tests.Auth.Services;

/// <summary>
/// Covers the semantics that <see cref="FakeOAuthStateStore"/> is expected to share with
/// <see cref="CosmosOAuthStateStore"/>: round-trip, defensive expiry check, single-take redemption,
/// and refresh rotation. <see cref="CosmosOAuthStateStore"/> itself is trusted rather than unit-tested
/// (matches the existing <c>CosmosDocumentStore</c> policy — no integration tests in this project).
/// </summary>
public sealed class FakeOAuthStateStoreTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 4, 11, 0, 0, 0, TimeSpan.Zero);

    private static (FakeOAuthStateStore store, FakeTimeProvider clock) CreateStore()
    {
        var clock = new FakeTimeProvider(Epoch);
        var store = new FakeOAuthStateStore(clock);
        return (store, clock);
    }

    [Fact]
    public async Task RegisteredClient_RoundTrip_ReturnsEquivalentRecord()
    {
        var (store, _) = CreateStore();
        var client = new RegisteredClient
        {
            ClientId = "abc-123",
            ClientName = "Claude Code CLI",
            RedirectUris = ["http://localhost:8000/callback"],
            CreatedAt = Epoch,
            ExpiresAt = Epoch.AddDays(90),
            Ttl = 90 * 24 * 3600,
        };

        await store.SaveClientAsync(client);
        var result = await store.GetClientAsync("abc-123");

        Assert.NotNull(result);
        Assert.Equal("abc-123", result.ClientId);
        Assert.Equal("Claude Code CLI", result.ClientName);
        Assert.Equal(["http://localhost:8000/callback"], result.RedirectUris);
    }

    [Fact]
    public async Task RegisteredClient_PastExpiresAt_ReturnsNull()
    {
        var (store, clock) = CreateStore();
        await store.SaveClientAsync(new RegisteredClient
        {
            ClientId = "abc-123",
            ExpiresAt = Epoch.AddMinutes(10),
        });

        clock.Advance(TimeSpan.FromMinutes(11));

        var result = await store.GetClientAsync("abc-123");
        Assert.Null(result);
    }

    [Fact]
    public async Task Transaction_DeleteRemovesRecord()
    {
        var (store, _) = CreateStore();
        await store.SaveTransactionAsync(new AuthTransaction
        {
            UpstreamState = "state-xyz",
            ClientId = "abc-123",
            ClientRedirectUri = "http://localhost:8000/callback",
            ClientCodeChallenge = "challenge",
            UpstreamPkceVerifier = "verifier",
            ExpiresAt = Epoch.AddMinutes(10),
        });

        await store.DeleteTransactionAsync("state-xyz");

        Assert.Null(await store.GetTransactionAsync("state-xyz"));
    }

    [Fact]
    public async Task AuthCode_RedeemTwice_SecondCallReturnsNull()
    {
        var (store, _) = CreateStore();
        await store.SaveAuthCodeAsync(new DevBrainAuthCode
        {
            Code = "code-1",
            ClientId = "abc-123",
            ClientRedirectUri = "http://localhost:8000/callback",
            ClientCodeChallenge = "challenge",
            UpstreamJti = "jti-1",
            ExpiresAt = Epoch.AddMinutes(5),
        });

        var first = await store.RedeemAuthCodeAsync("code-1");
        var second = await store.RedeemAuthCodeAsync("code-1");

        Assert.NotNull(first);
        Assert.Equal("jti-1", first.UpstreamJti);
        Assert.Null(second);
    }

    [Fact]
    public async Task AuthCode_ExpiredBeforeRedemption_ReturnsNullAndClearsSlot()
    {
        var (store, clock) = CreateStore();
        await store.SaveAuthCodeAsync(new DevBrainAuthCode
        {
            Code = "code-expired",
            ExpiresAt = Epoch.AddMinutes(5),
        });

        clock.Advance(TimeSpan.FromMinutes(6));

        var first = await store.RedeemAuthCodeAsync("code-expired");
        var second = await store.RedeemAuthCodeAsync("code-expired");

        Assert.Null(first);
        Assert.Null(second);
    }

    [Fact]
    public async Task RefreshToken_ConsumeRotatesCorrectly()
    {
        var (store, _) = CreateStore();
        await store.SaveRefreshAsync(new DevBrainRefreshRecord
        {
            RefreshToken = "refresh-old",
            ClientId = "abc-123",
            UpstreamJti = "jti-old",
            ExpiresAt = Epoch.AddDays(30),
        });

        var consumed = await store.ConsumeRefreshAsync("refresh-old");
        Assert.NotNull(consumed);
        Assert.Equal("jti-old", consumed.UpstreamJti);

        // Old refresh is gone.
        Assert.Null(await store.ConsumeRefreshAsync("refresh-old"));

        // Caller mints and stores a new one.
        await store.SaveRefreshAsync(new DevBrainRefreshRecord
        {
            RefreshToken = "refresh-new",
            ClientId = "abc-123",
            UpstreamJti = "jti-new",
            ExpiresAt = Epoch.AddDays(30),
        });

        var again = await store.ConsumeRefreshAsync("refresh-new");
        Assert.NotNull(again);
        Assert.Equal("jti-new", again.UpstreamJti);
    }

    [Fact]
    public async Task UpstreamToken_RoundTripPreservesEnvelopeAndClaims()
    {
        var (store, _) = CreateStore();
        var envelope = new UpstreamTokenEnvelope("at-value", "rt-value", 1700000000);

        await store.SaveUpstreamTokenAsync(new UpstreamTokenRecord
        {
            Jti = "jti-1",
            Envelope = envelope,
            UserPrincipalName = "derek@ignitesolutions.group",
            ObjectId = "00000000-0000-0000-0000-000000000001",
            TenantId = "tenant-guid",
            ExpiresAt = Epoch.AddHours(1),
        });

        var result = await store.GetUpstreamTokenAsync("jti-1");
        Assert.NotNull(result);
        Assert.Equal("derek@ignitesolutions.group", result.UserPrincipalName);
        Assert.Equal("00000000-0000-0000-0000-000000000001", result.ObjectId);
        Assert.Equal("tenant-guid", result.TenantId);
        // The envelope round-trips through the protector unchanged.
        Assert.Equal(envelope, result.Envelope);
    }

    /// <summary>
    /// State-store-level protector invocation test. Every upstream <b>write</b> must call Protect,
    /// every upstream <b>read</b> must call Unprotect — no caching, no skip paths. This is what
    /// keeps the encryption concern from silently degrading if the state store is refactored.
    /// </summary>
    [Fact]
    public async Task UpstreamToken_EveryWriteAndReadInvokesProtector()
    {
        var clock = new FakeTimeProvider(Epoch);
        var protector = new FakeUpstreamTokenProtector();
        var store = new FakeOAuthStateStore(clock, protector);

        // Three writes.
        for (var i = 0; i < 3; i++)
        {
            await store.SaveUpstreamTokenAsync(new UpstreamTokenRecord
            {
                Jti = $"jti-{i}",
                Envelope = new UpstreamTokenEnvelope($"at-{i}", $"rt-{i}", 0),
                UserPrincipalName = $"user-{i}@example.com",
                ExpiresAt = Epoch.AddHours(1),
            });
        }
        Assert.Equal(3, protector.ProtectCalls);
        Assert.Equal(0, protector.UnprotectCalls);

        // Two reads (one hit, one miss).
        var hit = await store.GetUpstreamTokenAsync("jti-0");
        var miss = await store.GetUpstreamTokenAsync("never-stored");

        Assert.NotNull(hit);
        Assert.Null(miss);
        // Hit path unprotects once; miss path short-circuits before unprotecting.
        Assert.Equal(1, protector.UnprotectCalls);
        Assert.Equal(3, protector.ProtectCalls);
    }

    [Fact]
    public async Task ReadCallCount_TracksReadsNotWrites()
    {
        var (store, _) = CreateStore();
        await store.SaveClientAsync(new RegisteredClient { ClientId = "abc", ExpiresAt = Epoch.AddDays(90) });
        await store.SaveAuthCodeAsync(new DevBrainAuthCode { Code = "c1", ExpiresAt = Epoch.AddMinutes(5) });

        Assert.Equal(0, store.ReadCallCount);

        await store.GetClientAsync("abc");
        await store.RedeemAuthCodeAsync("c1");
        await store.RedeemAuthCodeAsync("missing");

        Assert.Equal(3, store.ReadCallCount);
    }
}
