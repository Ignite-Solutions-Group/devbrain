using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DevBrain.Functions.Auth.DcrFacade;

/// <summary>
/// Options for the DCR facade's discovery documents. Bound from <c>OAuth:BaseUrl</c> in
/// configuration. The base URL is the public-facing root — e.g. <c>https://devbrain.example.com</c>.
/// All discovery endpoint URLs are derived from this base.
/// </summary>
public sealed class DcrFacadeDiscoveryOptions
{
    public string BaseUrl { get; set; } = string.Empty;
}

/// <summary>
/// Serves the two <c>.well-known</c> discovery documents that let MCP clients learn where to
/// register, authorize, and exchange tokens.
///
/// <para>
/// Why these live in DevBrain at all (rather than pointing at Entra): Claude.ai web ignores
/// discovery endpoints that don't live under the MCP server's own domain root
/// (claude-ai-mcp issue #82). DevBrain hosting its own <c>.well-known</c> URLs and proxying to
/// Entra internally is the fix.
/// </para>
/// </summary>
public sealed class DiscoveryEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _baseUrl;
    private readonly ILogger<DiscoveryEndpoints> _logger;

    public DiscoveryEndpoints(IConfiguration configuration, ILogger<DiscoveryEndpoints> logger)
    {
        _baseUrl = (configuration["OAuth:BaseUrl"] ?? string.Empty).TrimEnd('/');
        _logger = logger;
    }

    /// <summary>
    /// RFC 8414 — OAuth 2.0 Authorization Server Metadata. Clients POST to <c>/register</c>, GET
    /// <c>/authorize</c>, POST <c>/token</c>. All endpoints live on DevBrain itself; Entra is never
    /// referenced in this document.
    /// </summary>
    [Function("DiscoveryAuthorizationServer")]
    public async Task<HttpResponseData> AuthorizationServer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = ".well-known/oauth-authorization-server")] HttpRequestData req)
    {
        _logger.LogInformation("GET /.well-known/oauth-authorization-server received url={Url} baseUrl={BaseUrl}", req.Url, _baseUrl);

        var metadata = new AuthorizationServerMetadata(
            Issuer: _baseUrl,
            RegistrationEndpoint: $"{_baseUrl}/register",
            AuthorizationEndpoint: $"{_baseUrl}/authorize",
            TokenEndpoint: $"{_baseUrl}/token",
            ResponseTypesSupported: ["code"],
            GrantTypesSupported: ["authorization_code", "refresh_token"],
            CodeChallengeMethodsSupported: ["S256"],
            TokenEndpointAuthMethodsSupported: ["none"],
            ScopesSupported: ["documents.readwrite"]);

        return await WriteJsonAsync(req, metadata);
    }

    /// <summary>
    /// RFC 9728 — OAuth 2.0 Protected Resource Metadata. Tells clients which authorization server
    /// to use for this resource. Points at DevBrain's own <c>/authorize</c>, NOT Entra's.
    /// </summary>
    [Function("DiscoveryProtectedResource")]
    public async Task<HttpResponseData> ProtectedResource(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = ".well-known/oauth-protected-resource")] HttpRequestData req)
    {
        _logger.LogInformation("GET /.well-known/oauth-protected-resource received url={Url} baseUrl={BaseUrl}", req.Url, _baseUrl);

        var metadata = new ProtectedResourceMetadata(
            Resource: $"{_baseUrl}/runtime/webhooks/mcp",
            AuthorizationServers: [_baseUrl],
            BearerMethodsSupported: ["header"],
            ScopesSupported: ["documents.readwrite"]);

        return await WriteJsonAsync(req, metadata);
    }

    private static async Task<HttpResponseData> WriteJsonAsync<T>(HttpRequestData req, T body)
    {
        var response = req.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        response.Headers.Add("Cache-Control", "public, max-age=3600");
        await JsonSerializer.SerializeAsync(response.Body, body, JsonOptions);
        return response;
    }

    // Wire-format DTOs are internal (not private) so OAuthWireFormatTests can reference them
    // directly and pin the emitted JSON field names against the governing RFCs.

    internal sealed record AuthorizationServerMetadata(
        [property: JsonPropertyName("issuer")] string Issuer,
        [property: JsonPropertyName("registration_endpoint")] string RegistrationEndpoint,
        [property: JsonPropertyName("authorization_endpoint")] string AuthorizationEndpoint,
        [property: JsonPropertyName("token_endpoint")] string TokenEndpoint,
        [property: JsonPropertyName("response_types_supported")] string[] ResponseTypesSupported,
        [property: JsonPropertyName("grant_types_supported")] string[] GrantTypesSupported,
        [property: JsonPropertyName("code_challenge_methods_supported")] string[] CodeChallengeMethodsSupported,
        [property: JsonPropertyName("token_endpoint_auth_methods_supported")] string[] TokenEndpointAuthMethodsSupported,
        [property: JsonPropertyName("scopes_supported")] string[] ScopesSupported);

    internal sealed record ProtectedResourceMetadata(
        [property: JsonPropertyName("resource")] string Resource,
        [property: JsonPropertyName("authorization_servers")] string[] AuthorizationServers,
        [property: JsonPropertyName("bearer_methods_supported")] string[] BearerMethodsSupported,
        [property: JsonPropertyName("scopes_supported")] string[] ScopesSupported);
}
