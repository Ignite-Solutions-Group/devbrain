using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace DevBrain.Functions.Auth.DcrFacade;

/// <summary>
/// HTTP adapter for <c>POST /register</c>. Parses the RFC 7591 body, delegates to
/// <see cref="RegistrationHandler"/>, serializes the response.
///
/// <para>
/// Auth: <see cref="AuthorizationLevel.Anonymous"/>. The entire DCR facade is anonymous — the proxy
/// IS the gate, and clients need to be able to register before they have any credentials to present.
/// </para>
/// </summary>
public sealed class RegisterEndpoint
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly RegistrationHandler _handler;
    private readonly ILogger<RegisterEndpoint> _logger;

    public RegisterEndpoint(RegistrationHandler handler, ILogger<RegisterEndpoint> logger)
    {
        _handler = handler;
        _logger = logger;
    }

    [Function("RegisterEndpoint")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "register")] HttpRequestData req)
    {
        _logger.LogInformation("POST /register received");

        // TODO(v1.6 Claude Desktop DCR parsing investigation): temporary diagnostic logging.
        // Clients are hitting "empty redirect_uris" rejection somewhere between the wire and
        // RegistrationHandler.HandleAsync; we need the raw body + Content-Type to know whether
        // the problem is a missing field, a wrong content type (e.g., form-urlencoded instead
        // of JSON), a casing mismatch (`redirectUris` vs `redirect_uris`), or something else
        // entirely. Warning level so App Insights doesn't sample it out. Remove before v1.7.
        var contentType = req.Headers.TryGetValues("Content-Type", out var ctValues)
            ? string.Join(",", ctValues)
            : "(none)";

        string rawBody;
        using (var reader = new StreamReader(req.Body))
        {
            rawBody = await reader.ReadToEndAsync();
        }

        const int MaxBodyLogLength = 2000;
        var loggedBody = rawBody.Length <= MaxBodyLogLength
            ? rawBody
            : rawBody[..MaxBodyLogLength] + "... (truncated)";

        _logger.LogWarning(
            "RegistrationHandler: raw body content-type={ContentType} body={Body}",
            contentType, loggedBody);

        RegistrationRequest? body;
        try
        {
            body = JsonSerializer.Deserialize<RegistrationRequest>(rawBody, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "POST /register: request body is not valid JSON");
            return await WriteErrorAsync(req, HttpStatusCode.BadRequest, "invalid_client_metadata", "Request body is not valid JSON.");
        }

        if (body is null)
        {
            _logger.LogWarning("POST /register: request body is null");
            return await WriteErrorAsync(req, HttpStatusCode.BadRequest, "invalid_client_metadata", "Request body is required.");
        }

        var result = await _handler.HandleAsync(body);
        if (!result.IsSuccess)
        {
            _logger.LogWarning("POST /register: handler rejected error={Error}", result.ErrorCode);
            return await WriteErrorAsync(req, HttpStatusCode.BadRequest, result.ErrorCode!, result.ErrorDescription!);
        }

        _logger.LogInformation("POST /register: 201 Created clientId={ClientId}", result.Response!.ClientId);
        var response = req.CreateResponse(HttpStatusCode.Created);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await JsonSerializer.SerializeAsync(response.Body, new RegistrationResponseDto(result.Response!), JsonOptions);
        return response;
    }

    private static async Task<HttpResponseData> WriteErrorAsync(HttpRequestData req, HttpStatusCode status, string code, string description)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await JsonSerializer.SerializeAsync(response.Body, new { error = code, error_description = description }, JsonOptions);
        return response;
    }

    /// <summary>
    /// Wire-format DTO for the RFC 7591 §3.2.1 registration response. Separated from the internal
    /// <see cref="RegistrationResponse"/> record so the JSON property names can use <c>snake_case</c>
    /// without polluting the domain type. <c>internal</c> (not <c>private</c>) so
    /// <c>OAuthWireFormatTests</c> can reference it directly without widening the public API.
    /// </summary>
    internal sealed record RegistrationResponseDto(
        [property: JsonPropertyName("client_id")] string ClientId,
        [property: JsonPropertyName("client_id_issued_at")] long ClientIdIssuedAt,
        [property: JsonPropertyName("client_name")] string? ClientName,
        [property: JsonPropertyName("redirect_uris")] string[] RedirectUris,
        [property: JsonPropertyName("token_endpoint_auth_method")] string TokenEndpointAuthMethod)
    {
        public RegistrationResponseDto(RegistrationResponse response)
            : this(response.ClientId, response.ClientIdIssuedAt, response.ClientName, response.RedirectUris, response.TokenEndpointAuthMethod) { }
    }
}
