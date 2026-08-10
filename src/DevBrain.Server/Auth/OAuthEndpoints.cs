using System.Text.Json.Serialization;
using DevBrain.Core.Auth.DcrFacade;

namespace DevBrain.Server.Auth;

public static class OAuthEndpoints
{
    public static IEndpointRouteBuilder MapDevBrainOAuth(
        this IEndpointRouteBuilder endpoints,
        string rateLimitPolicy)
    {
        endpoints.MapGet("/.well-known/oauth-authorization-server", AuthorizationServerMetadata)
            .RequireRateLimiting(rateLimitPolicy);
        endpoints.MapGet("/.well-known/oauth-protected-resource", ProtectedResourceMetadata)
            .RequireRateLimiting(rateLimitPolicy);
        endpoints.MapPost("/register", RegisterAsync)
            .RequireRateLimiting(rateLimitPolicy);
        endpoints.MapGet("/authorize", AuthorizeAsync)
            .RequireRateLimiting(rateLimitPolicy);
        endpoints.MapGet("/callback", CallbackAsync)
            .RequireRateLimiting(rateLimitPolicy);
        endpoints.MapPost("/token", TokenAsync)
            .RequireRateLimiting(rateLimitPolicy);
        return endpoints;
    }

    private static IResult AuthorizationServerMetadata(IConfiguration configuration)
    {
        var baseUrl = BaseUrl(configuration);
        return CacheableJson(new AuthorizationServerMetadataResponse(
            Issuer: baseUrl,
            RegistrationEndpoint: $"{baseUrl}/register",
            AuthorizationEndpoint: $"{baseUrl}/authorize",
            TokenEndpoint: $"{baseUrl}/token",
            ResponseTypesSupported: ["code"],
            GrantTypesSupported: ["authorization_code", "refresh_token"],
            CodeChallengeMethodsSupported: ["S256"],
            TokenEndpointAuthMethodsSupported: ["none"],
            AuthorizationResponseIssParameterSupported: true,
            ScopesSupported: ["documents.readwrite"]));
    }

    private static IResult ProtectedResourceMetadata(IConfiguration configuration)
    {
        var baseUrl = BaseUrl(configuration);
        return CacheableJson(new ProtectedResourceMetadataResponse(
            Resource: $"{baseUrl}/mcp",
            AuthorizationServers: [baseUrl],
            BearerMethodsSupported: ["header"],
            ScopesSupported: ["documents.readwrite"]));
    }

    private static async Task<IResult> RegisterAsync(
        RegistrationRequest request,
        RegistrationHandler handler)
    {
        var result = await handler.HandleAsync(request);
        return result.IsSuccess
            ? Results.Json(new RegistrationResponseDto(result.Response!), statusCode: StatusCodes.Status201Created)
            : OAuthError(StatusCodes.Status400BadRequest, result.ErrorCode!, result.ErrorDescription!);
    }

    private static async Task<IResult> AuthorizeAsync(
        HttpRequest httpRequest,
        AuthorizationHandler handler,
        IConfiguration configuration)
    {
        var query = httpRequest.Query;
        var baseUrl = BaseUrl(configuration);
        var request = new AuthorizationRequest(
            ClientId: query["client_id"].ToString(),
            ResponseType: query["response_type"].ToString(),
            RedirectUri: query["redirect_uri"].ToString(),
            State: NullIfEmpty(query["state"].ToString()),
            CodeChallenge: query["code_challenge"].ToString(),
            CodeChallengeMethod: NullIfEmpty(query["code_challenge_method"].ToString()) ?? "plain",
            Resource: NullIfEmpty(query["resource"].ToString()),
            Issuer: baseUrl,
            CanonicalResource: $"{baseUrl}/mcp");

        var result = await handler.HandleAsync(request);
        return result.IsSuccess
            ? Results.Redirect(result.RedirectTo!.ToString())
            : OAuthError(StatusCodes.Status400BadRequest, result.ErrorCode!, result.ErrorDescription!);
    }

    private static async Task<IResult> CallbackAsync(
        HttpRequest httpRequest,
        CallbackHandler handler)
    {
        var query = httpRequest.Query;
        var result = await handler.HandleAsync(new CallbackRequest(
            Code: NullIfEmpty(query["code"].ToString()),
            State: NullIfEmpty(query["state"].ToString()),
            Error: NullIfEmpty(query["error"].ToString()),
            ErrorDescription: NullIfEmpty(query["error_description"].ToString())));

        return result.Kind == CallbackResultKind.Redirect
            ? Results.Redirect(result.RedirectTo!.ToString())
            : OAuthError(StatusCodes.Status400BadRequest, result.ErrorCode!, result.ErrorDescription!);
    }

    private static async Task<IResult> TokenAsync(
        HttpRequest httpRequest,
        TokenHandler handler)
    {
        if (!httpRequest.HasFormContentType)
        {
            return OAuthError(StatusCodes.Status400BadRequest, "invalid_request", "Content-Type must be application/x-www-form-urlencoded.");
        }

        var form = await httpRequest.ReadFormAsync();
        var result = await handler.HandleAsync(new TokenRequest(
            GrantType: form["grant_type"].ToString(),
            ClientId: NullIfEmpty(form["client_id"].ToString()),
            Code: NullIfEmpty(form["code"].ToString()),
            CodeVerifier: NullIfEmpty(form["code_verifier"].ToString()),
            RedirectUri: NullIfEmpty(form["redirect_uri"].ToString()),
            RefreshToken: NullIfEmpty(form["refresh_token"].ToString()),
            Resource: NullIfEmpty(form["resource"].ToString())));

        return result.IsSuccess
            ? NoStoreJson(new TokenResponseDto(result.Response!))
            : OAuthError(StatusCodes.Status400BadRequest, result.ErrorCode!, result.ErrorDescription!);
    }

    private static IResult CacheableJson<T>(T value) =>
        new HeaderResult(Results.Json(value), "Cache-Control", "public, max-age=3600");

    private static IResult NoStoreJson<T>(T value) =>
        new HeaderResult(Results.Json(value), "Cache-Control", "no-store");

    private static IResult OAuthError(int statusCode, string code, string description) =>
        new HeaderResult(
            Results.Json(new OAuthErrorResponse(code, description), statusCode: statusCode),
            "Cache-Control",
            "no-store");

    private static string BaseUrl(IConfiguration configuration) =>
        configuration["OAuth:BaseUrl"]!.TrimEnd('/');

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    private sealed class HeaderResult(IResult inner, string name, string value) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers[name] = value;
            await inner.ExecuteAsync(httpContext);
        }
    }

    internal sealed record AuthorizationServerMetadataResponse(
        [property: JsonPropertyName("issuer")] string Issuer,
        [property: JsonPropertyName("registration_endpoint")] string RegistrationEndpoint,
        [property: JsonPropertyName("authorization_endpoint")] string AuthorizationEndpoint,
        [property: JsonPropertyName("token_endpoint")] string TokenEndpoint,
        [property: JsonPropertyName("response_types_supported")] string[] ResponseTypesSupported,
        [property: JsonPropertyName("grant_types_supported")] string[] GrantTypesSupported,
        [property: JsonPropertyName("code_challenge_methods_supported")] string[] CodeChallengeMethodsSupported,
        [property: JsonPropertyName("token_endpoint_auth_methods_supported")] string[] TokenEndpointAuthMethodsSupported,
        [property: JsonPropertyName("authorization_response_iss_parameter_supported")] bool AuthorizationResponseIssParameterSupported,
        [property: JsonPropertyName("scopes_supported")] string[] ScopesSupported);

    internal sealed record ProtectedResourceMetadataResponse(
        [property: JsonPropertyName("resource")] string Resource,
        [property: JsonPropertyName("authorization_servers")] string[] AuthorizationServers,
        [property: JsonPropertyName("bearer_methods_supported")] string[] BearerMethodsSupported,
        [property: JsonPropertyName("scopes_supported")] string[] ScopesSupported);

    internal sealed record RegistrationResponseDto(
        [property: JsonPropertyName("client_id")] string ClientId,
        [property: JsonPropertyName("client_id_issued_at")] long ClientIdIssuedAt,
        [property: JsonPropertyName("client_name")] string? ClientName,
        [property: JsonPropertyName("redirect_uris")] string[] RedirectUris,
        [property: JsonPropertyName("application_type")] string ApplicationType,
        [property: JsonPropertyName("token_endpoint_auth_method")] string TokenEndpointAuthMethod)
    {
        public RegistrationResponseDto(RegistrationResponse response)
            : this(response.ClientId, response.ClientIdIssuedAt, response.ClientName, response.RedirectUris, response.ApplicationType, response.TokenEndpointAuthMethod)
        {
        }
    }

    internal sealed record TokenResponseDto(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("token_type")] string TokenType,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("refresh_token")] string RefreshToken,
        [property: JsonPropertyName("scope")] string Scope)
    {
        public TokenResponseDto(TokenResponse response)
            : this(response.AccessToken, response.TokenType, response.ExpiresIn, response.RefreshToken, response.Scope)
        {
        }
    }

    internal sealed record OAuthErrorResponse(
        [property: JsonPropertyName("error")] string Error,
        [property: JsonPropertyName("error_description")] string ErrorDescription);
}
