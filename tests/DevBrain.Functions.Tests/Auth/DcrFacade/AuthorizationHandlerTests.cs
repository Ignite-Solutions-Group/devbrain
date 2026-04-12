using DevBrain.Functions.Auth.DcrFacade;
using DevBrain.Functions.Auth.Models;
using DevBrain.Functions.Auth.Services;
using DevBrain.Functions.Tests.Auth.Services;
using DevBrain.Functions.Tests.TestHelpers;
using Microsoft.Extensions.Time.Testing;

namespace DevBrain.Functions.Tests.Auth.DcrFacade;

public sealed class AuthorizationHandlerTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 4, 11, 0, 0, 0, TimeSpan.Zero);
    private const string ClientId = "test-client-id";
    private const string ValidRedirect = "https://localhost:8000/callback";
    private const string ClientChallenge = "VGhpcy1pcy1hLWZha2UtY29kZS1jaGFsbGVuZ2UtZm9yLXRlc3Rpbmc"; // 43+ chars

    private static async Task<(AuthorizationHandler handler, FakeOAuthStateStore store, StubUpstream upstream)> CreateWithRegisteredClientAsync()
    {
        var clock = new FakeTimeProvider(Epoch);
        var store = new FakeOAuthStateStore(clock);
        var upstream = new StubUpstream();
        var handler = new AuthorizationHandler(store, upstream, clock);

        await store.SaveClientAsync(new RegisteredClient
        {
            ClientId = ClientId,
            RedirectUris = [ValidRedirect],
            ExpiresAt = Epoch.AddDays(90),
        });

        return (handler, store, upstream);
    }

    private static AuthorizationRequest ValidRequest() => new(
        ClientId: ClientId,
        ResponseType: "code",
        RedirectUri: ValidRedirect,
        State: "client-state-xyz",
        CodeChallenge: ClientChallenge,
        CodeChallengeMethod: "S256");

    [Fact]
    public async Task ValidRequest_PersistsTransaction_AndReturnsUpstreamRedirect()
    {
        var (handler, store, upstream) = await CreateWithRegisteredClientAsync();

        var result = await handler.HandleAsync(ValidRequest());

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.RedirectTo);
        Assert.Equal("https://stub-upstream.example.com/authorize", $"{result.RedirectTo!.Scheme}://{result.RedirectTo.Host}{result.RedirectTo.AbsolutePath}");

        // The handler passed upstream-side PKCE values, not the client's.
        Assert.NotEqual(ClientChallenge, upstream.LastPkceChallenge);
        Assert.NotNull(upstream.LastState);

        var txn = await store.GetTransactionAsync(upstream.LastState!);
        Assert.NotNull(txn);
        Assert.Equal(ClientId, txn.ClientId);
        Assert.Equal(ClientChallenge, txn.ClientCodeChallenge);           // client's challenge stored
        Assert.NotEqual(ClientChallenge, txn.UpstreamPkceVerifier);       // upstream verifier is independent
        Assert.Equal("client-state-xyz", txn.ClientState);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task MissingClientId_ReturnsError(string? clientId)
    {
        var (handler, _, _) = await CreateWithRegisteredClientAsync();

        var result = await handler.HandleAsync(ValidRequest() with { ClientId = clientId! });

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_request", result.ErrorCode);
    }

    [Fact]
    public async Task WrongResponseType_ReturnsError()
    {
        var (handler, _, _) = await CreateWithRegisteredClientAsync();

        var result = await handler.HandleAsync(ValidRequest() with { ResponseType = "token" });

        Assert.False(result.IsSuccess);
        Assert.Equal("unsupported_response_type", result.ErrorCode);
    }

    [Fact]
    public async Task MissingCodeChallenge_ReturnsError()
    {
        var (handler, _, _) = await CreateWithRegisteredClientAsync();

        var result = await handler.HandleAsync(ValidRequest() with { CodeChallenge = "" });

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_request", result.ErrorCode);
    }

    [Fact]
    public async Task PlainPkceMethod_Rejected()
    {
        var (handler, _, _) = await CreateWithRegisteredClientAsync();

        var result = await handler.HandleAsync(ValidRequest() with { CodeChallengeMethod = "plain" });

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_request", result.ErrorCode);
        Assert.Contains("S256", result.ErrorDescription, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownClientId_ReturnsError()
    {
        var (handler, _, _) = await CreateWithRegisteredClientAsync();

        var result = await handler.HandleAsync(ValidRequest() with { ClientId = "never-registered" });

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_client", result.ErrorCode);
    }

    [Fact]
    public async Task UnregisteredRedirectUri_ReturnsError()
    {
        var (handler, _, _) = await CreateWithRegisteredClientAsync();

        var result = await handler.HandleAsync(ValidRequest() with { RedirectUri = "https://evil.example.com/callback" });

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_redirect_uri", result.ErrorCode);
    }

    [Fact]
    public async Task RedirectUri_ExactMatchOnly_NoSubstringMatches()
    {
        // Guard against naive substring matching on redirect URIs — a common mistake that opens
        // a confused-deputy path. Only exact string match is accepted.
        var (handler, _, _) = await CreateWithRegisteredClientAsync();

        var sneaky = await handler.HandleAsync(ValidRequest() with { RedirectUri = ValidRedirect + "/extra" });
        Assert.False(sneaky.IsSuccess);

        var prefix = await handler.HandleAsync(ValidRequest() with { RedirectUri = "https://localhost:8000" });
        Assert.False(prefix.IsSuccess);
    }

    // ----- test double for IUpstreamOAuthClient that records what was passed to BuildAuthorizeUri -----
    private sealed class StubUpstream : IUpstreamOAuthClient
    {
        public string? LastState { get; private set; }
        public string? LastPkceChallenge { get; private set; }

        public Uri BuildAuthorizeUri(string upstreamState, string upstreamPkceChallenge)
        {
            LastState = upstreamState;
            LastPkceChallenge = upstreamPkceChallenge;
            return new Uri($"https://stub-upstream.example.com/authorize?state={upstreamState}");
        }

        public Task<UpstreamTokenResponse> ExchangeCodeAsync(string code, string upstreamPkceVerifier) =>
            throw new NotImplementedException("Not exercised by AuthorizationHandler tests.");

        public Task<UpstreamTokenResponse> RefreshTokenAsync(string upstreamRefreshToken) =>
            throw new NotImplementedException("Not exercised by AuthorizationHandler tests.");
    }
}
