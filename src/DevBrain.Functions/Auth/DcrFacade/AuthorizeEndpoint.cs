using System.Net;
using System.Text.Json;
using System.Web;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace DevBrain.Functions.Auth.DcrFacade;

/// <summary>
/// HTTP adapter for <c>GET /authorize</c>. Parses the query string, calls the handler, returns a 302
/// to the upstream Entra <c>/authorize</c> URL on success or a 400 on structural errors.
/// </summary>
public sealed class AuthorizeEndpoint
{
    private readonly AuthorizationHandler _handler;
    private readonly ILogger<AuthorizeEndpoint> _logger;

    public AuthorizeEndpoint(AuthorizationHandler handler, ILogger<AuthorizeEndpoint> logger)
    {
        _handler = handler;
        _logger = logger;
    }

    [Function("AuthorizeEndpoint")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "authorize")] HttpRequestData req)
    {
        _logger.LogInformation("GET /authorize received url={Url}", req.Url);

        var query = HttpUtility.ParseQueryString(req.Url.Query);
        var request = new AuthorizationRequest(
            ClientId: query["client_id"] ?? string.Empty,
            ResponseType: query["response_type"] ?? string.Empty,
            RedirectUri: query["redirect_uri"] ?? string.Empty,
            State: query["state"],
            CodeChallenge: query["code_challenge"] ?? string.Empty,
            CodeChallengeMethod: query["code_challenge_method"] ?? "plain");

        var result = await _handler.HandleAsync(request);

        if (!result.IsSuccess)
        {
            _logger.LogWarning("GET /authorize: rejected error={Error}", result.ErrorCode);
            var error = req.CreateResponse(HttpStatusCode.BadRequest);
            error.Headers.Add("Content-Type", "application/json; charset=utf-8");
            await JsonSerializer.SerializeAsync(error.Body, new { error = result.ErrorCode, error_description = result.ErrorDescription });
            return error;
        }

        _logger.LogInformation("GET /authorize: 302 Found redirecting to upstream");
        var redirect = req.CreateResponse(HttpStatusCode.Found);
        redirect.Headers.Add("Location", result.RedirectTo!.ToString());
        return redirect;
    }
}
