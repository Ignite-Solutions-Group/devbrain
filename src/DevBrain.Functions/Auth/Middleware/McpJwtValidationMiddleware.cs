using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace DevBrain.Functions.Auth.Middleware;

/// <summary>
/// Thin worker-middleware adapter over <see cref="JwtAuthenticator"/>. Only registered for MCP tool
/// trigger invocations (via <c>UseWhen</c> in Program.cs) so the DCR facade HTTP endpoints don't
/// try to gate themselves.
///
/// <para>
/// The middleware:
/// </para>
/// <list type="number">
///   <item>Pulls the HTTP request out of <see cref="FunctionContext"/> (MCP tool triggers carry their webhook HTTP request through the worker).</item>
///   <item>Reads the <c>Authorization</c> header and hands it to <see cref="JwtAuthenticator.AuthenticateAsync"/>.</item>
///   <item>On success: writes the <see cref="ClaimsPrincipal"/> into <see cref="FunctionContext.Features"/> so <c>DocumentTools.GetCallerIdentity</c> sees it unchanged.</item>
///   <item>On failure: short-circuits with a 401 response carrying an RFC 6750 <c>WWW-Authenticate</c> header.</item>
/// </list>
/// </summary>
public sealed class McpJwtValidationMiddleware : IFunctionsWorkerMiddleware
{
    private readonly JwtAuthenticator _authenticator;
    private readonly ILogger<McpJwtValidationMiddleware> _logger;

    public McpJwtValidationMiddleware(JwtAuthenticator authenticator, ILogger<McpJwtValidationMiddleware> logger)
    {
        _authenticator = authenticator;
        _logger = logger;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var fn = context.FunctionDefinition.Name;
        _logger.LogDebug("MCP JWT middleware entering function={Function} invocationId={InvocationId}", fn, context.InvocationId);

        // For MCP tool trigger invocations the standard HTTP accessor returns null — the worker
        // sees MCP as a custom trigger type, not an HTTP trigger, so IHttpRequestDataFeature is
        // never populated. The MCP extension surfaces the webhook's original HTTP headers via
        // FunctionContext.Items["ToolInvocationContext"] → HttpTransport.Headers instead. See
        // McpToolContextAccessor remarks for the full chain.
        //
        // We still try GetHttpRequestDataAsync() first as a defensive fallback: if a future
        // version of the MCP extension ever does expose an HttpRequestData, or if this middleware
        // somehow runs for a non-MCP HTTP trigger, we want to use that path.
        string? authHeader = null;
        string headerSource = "none";
        string? accessorFailureReason = null;

        var httpRequest = await TryGetHttpRequestDataAsync(context, fn);
        if (httpRequest is not null)
        {
            authHeader = httpRequest.Headers.TryGetValues("Authorization", out var values)
                ? values.FirstOrDefault()
                : null;
            headerSource = "HttpRequestData";
        }
        else
        {
            // MCP tool trigger path — extract from the tool invocation context.
            var mcpHeader = McpToolContextAccessor.TryGetAuthorizationHeader(context.Items, out var failureReason);
            if (mcpHeader is not null)
            {
                authHeader = mcpHeader;
                headerSource = "McpToolContext";
            }
            else
            {
                // Capture the reason so we can include it in the downstream rejection log —
                // otherwise the accessor's diagnostic signal ("non_http_transport", "tool_context_missing",
                // etc.) is lost when the authenticator fires "missing_authorization".
                accessorFailureReason = failureReason;
            }
        }

        _logger.LogDebug(
            "MCP JWT middleware resolved auth header function={Function} invocationId={InvocationId} source={Source} hasAuthHeader={HasAuthHeader}",
            fn, context.InvocationId, headerSource, !string.IsNullOrEmpty(authHeader));

        var result = await _authenticator.AuthenticateAsync(authHeader);
        if (!result.IsAuthenticated)
        {
            _logger.LogWarning(
                "MCP JWT rejected function={Function} invocationId={InvocationId} error={Error} description={Description} accessorFailureReason={AccessorFailureReason}",
                fn, context.InvocationId, result.ErrorCode, result.ErrorDescription, accessorFailureReason ?? "(n/a)");

            if (httpRequest is not null)
            {
                // HTTP-backed path: return a proper RFC 6750 bearer challenge.
                await WriteUnauthorizedResponseAsync(context, httpRequest, result.ErrorCode!, result.ErrorDescription!);
                return;
            }

            // MCP tool trigger path: no HttpRequestData to build a 401 from. Throw so the MCP
            // extension's executor surfaces the rejection as a JSON-RPC tool-invocation error.
            // This is the best available signal at the worker layer — a spec-compliant 401 +
            // WWW-Authenticate challenge would require interception at the webhook level, which
            // is out of scope for this worker middleware.
            throw new UnauthorizedAccessException(
                $"MCP JWT validation failed: {result.ErrorCode} — {result.ErrorDescription}");
        }

        // Populate the ClaimsPrincipal feature so DocumentTools.GetCallerIdentity continues to work
        // unchanged — it already reads from FunctionContext.Features.Get<ClaimsPrincipal>().
        context.Features.Set(result.Principal);

        var upn = result.Principal?.FindFirst("preferred_username")?.Value;
        _logger.LogInformation(
            "MCP JWT accepted function={Function} invocationId={InvocationId} upn={Upn}",
            fn, context.InvocationId, upn);

        await next(context);
    }

    private async Task<HttpRequestData?> TryGetHttpRequestDataAsync(FunctionContext context, string functionName)
    {
        try
        {
            return await context.GetHttpRequestDataAsync();
        }
        catch (OperationCanceledException)
        {
            // Never swallow cancellation — let it propagate so the host can shut the invocation
            // down cleanly. Debug-level swallowing of this would delay timeout/shutdown handling.
            throw;
        }
        catch (Exception ex)
        {
            // Non-HTTP-backed invocations (including MCP tool triggers) may throw or return null
            // here depending on the SDK version. Log at Debug because this is the expected path
            // for MCP triggers, not an error.
            _logger.LogDebug(ex,
                "MCP JWT middleware: GetHttpRequestDataAsync threw function={Function} — expected for MCP tool triggers, falling back to MCP tool context accessor",
                functionName);
            return null;
        }
    }

    private static async Task WriteUnauthorizedResponseAsync(FunctionContext context, HttpRequestData request, string error, string description)
    {
        var response = request.CreateResponse(HttpStatusCode.Unauthorized);
        // RFC 6750 §3 — Bearer challenge with error details.
        response.Headers.Add("WWW-Authenticate", $"Bearer error=\"{error}\", error_description=\"{description.Replace("\"", "'")}\"");
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await JsonSerializer.SerializeAsync(response.Body, new { error, error_description = description });

        // Short-circuit the pipeline by setting the invocation result directly.
        var invocationResult = context.GetInvocationResult();
        invocationResult.Value = response;
    }
}
