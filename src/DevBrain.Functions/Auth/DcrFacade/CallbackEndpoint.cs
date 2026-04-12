using System.Net;
using System.Text.Json;
using System.Web;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace DevBrain.Functions.Auth.DcrFacade;

/// <summary>
/// HTTP adapter for <c>GET /callback</c>. Parses the Entra redirect's query string and delegates to
/// <see cref="CallbackHandler"/>. Returns a 302 on success (both happy path and upstream-error pass-through
/// redirect back to the client) or a 400 on local structural errors (unknown state, missing code).
/// </summary>
public sealed class CallbackEndpoint
{
    private readonly CallbackHandler _handler;
    private readonly ILogger<CallbackEndpoint> _logger;

    public CallbackEndpoint(CallbackHandler handler, ILogger<CallbackEndpoint> logger)
    {
        _handler = handler;
        _logger = logger;
    }

    [Function("CallbackEndpoint")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "callback")] HttpRequestData req)
    {
        _logger.LogInformation("GET /callback received url={Url}", req.Url);

        var query = HttpUtility.ParseQueryString(req.Url.Query);
        var request = new CallbackRequest(
            Code: query["code"],
            State: query["state"],
            Error: query["error"],
            ErrorDescription: query["error_description"]);

        var result = await _handler.HandleAsync(request);

        if (result.Kind == CallbackResultKind.Redirect)
        {
            _logger.LogInformation("GET /callback: 302 Found redirecting to client");
            var redirect = req.CreateResponse(HttpStatusCode.Found);
            redirect.Headers.Add("Location", result.RedirectTo!.ToString());
            return redirect;
        }

        _logger.LogWarning("GET /callback: 400 local error={Error}", result.ErrorCode);
        var error = req.CreateResponse(HttpStatusCode.BadRequest);
        error.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await JsonSerializer.SerializeAsync(error.Body, new { error = result.ErrorCode, error_description = result.ErrorDescription });
        return error;
    }
}
