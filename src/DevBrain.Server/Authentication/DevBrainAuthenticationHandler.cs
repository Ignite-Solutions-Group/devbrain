using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using DevBrain.Core.Auth.Middleware;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace DevBrain.Server.Authentication;

public static class DevBrainAuthenticationDefaults
{
    public const string Scheme = "DevBrainBearer";
    public const string UserPolicy = "DevBrain.User";
}

public sealed class DevBrainAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly string _resourceMetadataUri;

    public DevBrainAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IConfiguration configuration)
        : base(options, logger, encoder)
    {
        var baseUrl = (configuration["OAuth:BaseUrl"] ?? string.Empty).TrimEnd('/');
        _resourceMetadataUri = $"{baseUrl}/.well-known/oauth-protected-resource";
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authorization))
        {
            return AuthenticateResult.NoResult();
        }

        var authenticator = Context.RequestServices.GetRequiredService<JwtAuthenticator>();
        var result = await authenticator.AuthenticateAsync(authorization);
        if (!result.IsAuthenticated || result.Principal is null)
        {
            return AuthenticateResult.Fail(result.ErrorDescription ?? "Invalid bearer token.");
        }

        return AuthenticateResult.Success(
            new AuthenticationTicket(result.Principal, DevBrainAuthenticationDefaults.Scheme));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate =
            new AuthenticationHeaderValue("Bearer", $"resource_metadata=\"{_resourceMetadataUri}\"").ToString();
        return Task.CompletedTask;
    }
}
