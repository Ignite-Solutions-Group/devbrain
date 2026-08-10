using System.Web;
using DevBrain.Core.Auth.DcrFacade;
using DevBrain.Core.Auth.Models;
using DevBrain.Core.Auth.Services;
using DevBrain.Functions.Tests.Auth.Services;
using DevBrain.Functions.Tests.TestHelpers;
using Microsoft.Extensions.Time.Testing;

namespace DevBrain.Functions.Tests.Auth.DcrFacade;

/// <summary>
/// Unit tests for <see cref="CallbackHandler"/>. Covers acceptance gate #4 (expired transaction)
/// and the integration-level invariants that tie /authorize, /callback, and /token together.
/// </summary>
public sealed class CallbackHandlerTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 4, 11, 0, 0, 0, TimeSpan.Zero);
    private const string ClientId = "test-client";
    private const string ClientRedirect = "https://localhost:8000/callback";
    private const string ClientState = "client-state-xyz";
    private const string UpstreamState = "upstream-state-abc";
    private const string ClientChallenge = "client-code-challenge-value-matching-pkce-format";
    private const string UpstreamVerifier = "upstream-verifier-from-authorize";
    private const string Issuer = "https://devbrain.example.com";
    private const string Resource = "https://devbrain.example.com/mcp";

    private sealed record Harness(
        CallbackHandler Handler,
        FakeOAuthStateStore Store,
        FakeUpstreamOAuthClient Upstream,
        FakeTimeProvider Clock);

    private static Harness Create(RecordingLogger<CallbackHandler>? logger = null)
    {
        var clock = new FakeTimeProvider(Epoch);
        var store = new FakeOAuthStateStore(clock);
        var upstream = new FakeUpstreamOAuthClient();
        var handler = new CallbackHandler(store, upstream, clock, logger);
        return new Harness(handler, store, upstream, clock);
    }

    private static async Task SeedTransactionAsync(Harness h)
    {
        await h.Store.SaveTransactionAsync(new AuthTransaction
        {
            UpstreamState = UpstreamState,
            ClientId = ClientId,
            ClientRedirectUri = ClientRedirect,
            ClientState = ClientState,
            Issuer = Issuer,
            Resource = Resource,
            ClientCodeChallenge = ClientChallenge,
            ClientCodeChallengeMethod = "S256",
            UpstreamPkceVerifier = UpstreamVerifier,
            CreatedAt = Epoch,
            ExpiresAt = Epoch.AddSeconds(600),
        });
    }

    [Fact]
    public async Task HappyPath_ExchangesUpstream_CreatesCodeAndVault_RedirectsToClient()
    {
        var h = Create();
        await SeedTransactionAsync(h);

        var result = await h.Handler.HandleAsync(new CallbackRequest("entra-code-123", UpstreamState, null, null));

        Assert.Equal(CallbackResultKind.Redirect, result.Kind);
        Assert.NotNull(result.RedirectTo);

        // Redirect target is the client's redirect_uri with code + state.
        Assert.Equal("localhost", result.RedirectTo!.Host);
        Assert.Equal("/callback", result.RedirectTo.AbsolutePath);
        var query = HttpUtility.ParseQueryString(result.RedirectTo.Query);
        Assert.NotNull(query["code"]);
        Assert.Equal(ClientState, query["state"]);
        Assert.Equal(Issuer, query["iss"]);

        // The DevBrain auth code exists and ties through to the upstream vault.
        var devbrainCode = query["code"]!;
        var redeemed = await h.Store.RedeemAuthCodeAsync(devbrainCode);
        Assert.NotNull(redeemed);
        Assert.Equal(ClientId, redeemed.ClientId);
        Assert.Equal(ClientChallenge, redeemed.ClientCodeChallenge);
        Assert.Equal(Resource, redeemed.Resource);

        var upstreamRecord = await h.Store.GetUpstreamTokenAsync(redeemed.UpstreamJti);
        Assert.NotNull(upstreamRecord);
        Assert.Equal("derek@ignitesolutions.group", upstreamRecord.UserPrincipalName);
        Assert.Contains("DevBrain.User", upstreamRecord.Roles);

        // Upstream was called exactly once with DevBrain's PKCE verifier, not the client's challenge.
        Assert.Equal(1, h.Upstream.ExchangeCodeCalls);
        Assert.Equal(UpstreamVerifier, h.Upstream.LastPkceVerifier);

        // The transaction is consumed.
        Assert.Null(await h.Store.GetTransactionAsync(UpstreamState));
    }

    /// <summary>Gate #4: transactions older than 600s are rejected before any upstream call.</summary>
    [Fact]
    public async Task ExpiredTransaction_Rejected_DoesNotCallUpstream()
    {
        var h = Create();
        await SeedTransactionAsync(h);

        h.Clock.Advance(TimeSpan.FromSeconds(601));

        var result = await h.Handler.HandleAsync(new CallbackRequest("entra-code-123", UpstreamState, null, null));

        Assert.Equal(CallbackResultKind.LocalError, result.Kind);
        Assert.Equal("invalid_state", result.ErrorCode);
        Assert.Equal(0, h.Upstream.ExchangeCodeCalls);
    }

    [Fact]
    public async Task UnknownState_Rejected()
    {
        var h = Create();
        await SeedTransactionAsync(h);

        var result = await h.Handler.HandleAsync(new CallbackRequest("entra-code-123", "never-seen-state", null, null));

        Assert.Equal(CallbackResultKind.LocalError, result.Kind);
        Assert.Equal("invalid_state", result.ErrorCode);
        Assert.Equal(0, h.Upstream.ExchangeCodeCalls);
    }

    [Fact]
    public async Task MissingCode_Rejected()
    {
        var h = Create();
        await SeedTransactionAsync(h);

        var result = await h.Handler.HandleAsync(new CallbackRequest(null, UpstreamState, null, null));

        Assert.Equal(CallbackResultKind.LocalError, result.Kind);
        Assert.Equal("invalid_request", result.ErrorCode);
        Assert.Equal(0, h.Upstream.ExchangeCodeCalls);
    }

    [Fact]
    public async Task UpstreamError_ForwardedToClientRedirect()
    {
        var h = Create();
        await SeedTransactionAsync(h);

        // User denied consent at Entra. Entra redirects to /callback with ?error=access_denied.
        var result = await h.Handler.HandleAsync(new CallbackRequest(
            Code: null,
            State: UpstreamState,
            Error: "access_denied",
            ErrorDescription: "User cancelled"));

        Assert.Equal(CallbackResultKind.Redirect, result.Kind);
        var query = HttpUtility.ParseQueryString(result.RedirectTo!.Query);
        Assert.Equal("access_denied", query["error"]);
        Assert.Equal("User cancelled", query["error_description"]);
        Assert.Equal(ClientState, query["state"]);

        // Upstream was NOT called — we detected the error parameter and short-circuited.
        Assert.Equal(0, h.Upstream.ExchangeCodeCalls);

        // Transaction is consumed so the error can't be replayed.
        Assert.Null(await h.Store.GetTransactionAsync(UpstreamState));
    }

    [Fact]
    public async Task Diagnostics_DoNotRenderRawCallbackErrors()
    {
        var logger = new RecordingLogger<CallbackHandler>();
        var h = Create(logger);
        await SeedTransactionAsync(h);
        const string maliciousDescription = "User cancelled\r\nFORGED-CALLBACK-LOG";

        var result = await h.Handler.HandleAsync(new CallbackRequest(
            Code: null,
            State: UpstreamState,
            Error: "access_denied",
            ErrorDescription: maliciousDescription));

        Assert.Equal(CallbackResultKind.Redirect, result.Kind);
        Assert.Equal(
            maliciousDescription,
            HttpUtility.ParseQueryString(result.RedirectTo!.Query)["error_description"]);
        Assert.NotEmpty(logger.Messages);
        Assert.All(logger.Messages, message =>
        {
            Assert.DoesNotContain(maliciousDescription, message, StringComparison.Ordinal);
            Assert.DoesNotContain('\r', message);
            Assert.DoesNotContain('\n', message);
        });
    }

    [Fact]
    public async Task UpstreamException_ForwardedToClientRedirectAsServerError()
    {
        var h = Create();
        await SeedTransactionAsync(h);

        h.Upstream.ExchangeResponder = (_, _) => throw new UpstreamOAuthException("Entra returned 500");

        var result = await h.Handler.HandleAsync(new CallbackRequest("entra-code-123", UpstreamState, null, null));

        Assert.Equal(CallbackResultKind.Redirect, result.Kind);
        var query = HttpUtility.ParseQueryString(result.RedirectTo!.Query);
        Assert.Equal("server_error", query["error"]);

        // Transaction is consumed so a retry with the same state would fail.
        Assert.Null(await h.Store.GetTransactionAsync(UpstreamState));
    }

    [Fact]
    public async Task UpstreamError_UnknownState_LocalError()
    {
        var h = Create();

        // Error return with a state we never issued — no transaction to forward to.
        var result = await h.Handler.HandleAsync(new CallbackRequest(
            Code: null,
            State: "never-seen",
            Error: "access_denied",
            ErrorDescription: null));

        Assert.Equal(CallbackResultKind.LocalError, result.Kind);
    }

    [Fact]
    public async Task IssuedCodeAndVault_ShareSameJti()
    {
        var h = Create();
        await SeedTransactionAsync(h);

        var result = await h.Handler.HandleAsync(new CallbackRequest("entra-code-123", UpstreamState, null, null));

        var devbrainCode = HttpUtility.ParseQueryString(result.RedirectTo!.Query)["code"]!;
        var redeemed = await h.Store.RedeemAuthCodeAsync(devbrainCode);
        var upstreamRecord = await h.Store.GetUpstreamTokenAsync(redeemed!.UpstreamJti);

        Assert.NotNull(upstreamRecord);
        Assert.Equal(redeemed.UpstreamJti, upstreamRecord.Jti);
    }
}
