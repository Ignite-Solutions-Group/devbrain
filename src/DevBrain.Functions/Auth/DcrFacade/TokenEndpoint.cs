using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace DevBrain.Functions.Auth.DcrFacade;

/// <summary>
/// HTTP adapter for <c>POST /token</c>. Handles both <c>authorization_code</c> and <c>refresh_token</c>
/// grants via form-urlencoded bodies. Returns a JSON <c>access_token</c>/<c>refresh_token</c> response
/// per RFC 6749 §5.1 on success, or a <c>{"error":..., "error_description":...}</c> shape per §5.2
/// on failure.
/// </summary>
public sealed class TokenEndpoint
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TokenHandler _handler;
    private readonly ILogger<TokenEndpoint> _logger;

    public TokenEndpoint(TokenHandler handler, ILogger<TokenEndpoint> logger)
    {
        _handler = handler;
        _logger = logger;
    }

    [Function("TokenEndpoint")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "token")] HttpRequestData req)
    {
        _logger.LogInformation("POST /token received");

        using var reader = new StreamReader(req.Body);
        var formBody = await reader.ReadToEndAsync();
        var form = HttpUtility.ParseQueryString(formBody);

        var request = new TokenRequest(
            GrantType: form["grant_type"] ?? string.Empty,
            ClientId: form["client_id"],
            Code: form["code"],
            CodeVerifier: form["code_verifier"],
            RedirectUri: form["redirect_uri"],
            RefreshToken: form["refresh_token"]);

        var result = await _handler.HandleAsync(request);

        if (!result.IsSuccess)
        {
            _logger.LogWarning("POST /token: 400 error={Error}", result.ErrorCode);
            var error = req.CreateResponse(HttpStatusCode.BadRequest);
            error.Headers.Add("Content-Type", "application/json; charset=utf-8");
            error.Headers.Add("Cache-Control", "no-store");
            await JsonSerializer.SerializeAsync(error.Body, new { error = result.ErrorCode, error_description = result.ErrorDescription }, JsonOptions);
            return error;
        }

        _logger.LogInformation("POST /token: 200 OK");
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store");
        await JsonSerializer.SerializeAsync(response.Body, new TokenResponseDto(result.Response!), JsonOptions);
        return response;
    }

    /// <summary>
    /// RFC 6749 §5.1 wire format — <c>snake_case</c> property names on the JSON envelope.
    /// <c>internal</c> (not <c>private</c>) so the wire-format regression tests in
    /// <c>OAuthWireFormatTests</c> can reference it directly without widening the public API.
    /// </summary>
    internal sealed record TokenResponseDto(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("token_type")] string TokenType,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("scope")] string Scope)
    {
        public TokenResponseDto(TokenResponse response)
            : this(response.AccessToken, response.TokenType, response.ExpiresIn, response.RefreshToken, response.Scope) { }
    }
}
