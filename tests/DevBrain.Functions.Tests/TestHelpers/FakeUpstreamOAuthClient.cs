using DevBrain.Functions.Auth.Services;

namespace DevBrain.Functions.Tests.TestHelpers;

/// <summary>
/// In-memory test double for <see cref="IUpstreamOAuthClient"/>. Used by <c>CallbackHandler</c> and
/// middleware tests that don't need the real HttpClient stack.
///
/// <para>
/// Defaults to returning a fixed "derek@ignitesolutions.group" identity so tests can assert per-user
/// <c>updatedBy</c> without wiring up a whole Entra tenant.
/// </para>
/// </summary>
public sealed class FakeUpstreamOAuthClient : IUpstreamOAuthClient
{
    public Func<string, string, UpstreamTokenResponse>? ExchangeResponder { get; set; }
    public Func<string, UpstreamTokenResponse>? RefreshResponder { get; set; }
    public Uri AuthorizeUriBase { get; set; } = new("https://fake-entra.example.com/authorize");

    public int ExchangeCodeCalls { get; private set; }
    public int RefreshCalls { get; private set; }
    public string? LastPkceVerifier { get; private set; }

    public Uri BuildAuthorizeUri(string upstreamState, string upstreamPkceChallenge) =>
        new($"{AuthorizeUriBase}?state={upstreamState}&code_challenge={upstreamPkceChallenge}");

    public Task<UpstreamTokenResponse> ExchangeCodeAsync(string code, string upstreamPkceVerifier)
    {
        ExchangeCodeCalls++;
        LastPkceVerifier = upstreamPkceVerifier;
        var responder = ExchangeResponder ?? DefaultResponder;
        return Task.FromResult(responder(code, upstreamPkceVerifier));
    }

    public Task<UpstreamTokenResponse> RefreshTokenAsync(string upstreamRefreshToken)
    {
        RefreshCalls++;
        var responder = RefreshResponder ?? (_ => DefaultResponder("refresh", upstreamRefreshToken));
        return Task.FromResult(responder(upstreamRefreshToken));
    }

    private static UpstreamTokenResponse DefaultResponder(string _, string __) =>
        new(
            AccessToken: "fake-access-token",
            RefreshToken: "fake-refresh-token",
            IdToken: "fake.id.token",
            ExpiresIn: TimeSpan.FromHours(1),
            UserPrincipalName: "derek@ignitesolutions.group",
            ObjectId: "00000000-0000-0000-0000-000000000001",
            TenantId: "tenant-guid");
}
