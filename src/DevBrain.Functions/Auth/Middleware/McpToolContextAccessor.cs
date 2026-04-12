using Microsoft.Azure.Functions.Worker.Extensions.Mcp;

namespace DevBrain.Functions.Auth.Middleware;

/// <summary>
/// Pure helper that walks the MCP extension's worker-side context surface to extract HTTP headers
/// from a tool trigger invocation. Held separate from <see cref="McpJwtValidationMiddleware"/> so
/// the extraction logic can be unit-tested without constructing a <see cref="Microsoft.Azure.Functions.Worker.FunctionContext"/>.
///
/// <para>
/// <b>Why this exists:</b> for MCP tool trigger invocations on the isolated worker, the standard
/// <c>FunctionContext.GetHttpRequestDataAsync()</c> extension method returns <c>null</c> — the
/// worker sees MCP as a custom trigger type, not an HTTP trigger, so <c>IHttpRequestDataFeature</c>
/// is never populated. The HTTP request that backed the webhook invocation at
/// <c>/runtime/webhooks/mcp</c> reaches the worker via the MCP-specific channel described below,
/// not via the HTTP feature set. This was the root cause of the v1.6 post-deploy bug where
/// <see cref="McpJwtValidationMiddleware"/> threw on every tool call.
/// </para>
///
/// <para><b>How the MCP extension exposes HTTP headers</b> (from reading Azure/azure-functions-mcp-extension):</para>
/// <list type="number">
///   <item>The host-side extension (<c>Microsoft.Azure.Functions.Extensions.Mcp</c>) captures
///         <c>HttpContext.Request.Headers</c> at webhook receive time and stores them in a
///         <c>Transport</c> object on the <c>ToolInvocationContext</c>, then serializes the whole
///         thing into <c>BindingContext.BindingData</c>.</item>
///   <item>The worker-side extension (<c>Microsoft.Azure.Functions.Worker.Extensions.Mcp</c>)
///         registers <c>FunctionsMcpContextMiddleware</c> automatically via its extension startup.
///         That middleware deserializes the tool invocation context and places it in
///         <see cref="Microsoft.Azure.Functions.Worker.FunctionContext.Items"/> under the literal
///         key <c>"ToolInvocationContext"</c>.</item>
///   <item>The deserialized <see cref="ToolInvocationContext.Transport"/> is polymorphic; for
///         HTTP-originated invocations it's an <see cref="HttpTransport"/> subtype carrying a
///         case-insensitive <c>Headers</c> dictionary with the original webhook headers.</item>
/// </list>
///
/// <para>
/// The worker SDK exposes <see cref="ToolInvocationContextExtensions.TryGetHttpTransport"/> as
/// the public API for step 3. Step 2 uses an <see cref="FunctionContext.Items"/> lookup against
/// the literal key <c>"ToolInvocationContext"</c>; the matching <c>Constants</c> class in the
/// extension is <c>internal</c>, so we repeat the literal here with a comment. The key string is
/// effectively part of the public contract between
/// <c>FunctionsMcpContextMiddleware</c> (writer) and
/// <c>ToolInvocationContextConverter</c> (the extension's own reader) — changing it would break
/// the extension's own parameter binding path.
/// </para>
///
/// <para>
/// <b>Ordering:</b> <c>FunctionsMcpContextMiddleware</c> runs before any user-registered
/// middleware, so by the time <see cref="McpJwtValidationMiddleware"/> executes the item is
/// already present. If it's ever absent (e.g., the MCP extension doesn't run for a specific
/// invocation), the helper returns null and the caller decides how to fail.
/// </para>
/// </summary>
internal static class McpToolContextAccessor
{
    /// <summary>
    /// Well-known key under which <c>FunctionsMcpContextMiddleware</c> stores the deserialized
    /// <see cref="ToolInvocationContext"/>. Mirrors
    /// <c>Microsoft.Azure.Functions.Worker.Extensions.Mcp.Constants.ToolInvocationContextKey</c>,
    /// which is <c>internal</c> in the extension — the literal is duplicated here intentionally.
    /// See class remarks.
    /// </summary>
    public const string ToolInvocationContextItemKey = "ToolInvocationContext";

    /// <summary>
    /// Attempts to read the <c>Authorization</c> header from the HTTP transport attached to the
    /// current MCP tool invocation.
    /// </summary>
    /// <param name="items">
    /// The <see cref="FunctionContext.Items"/> dictionary. The Worker SDK types this as
    /// <see cref="IDictionary{TKey, TValue}"/> of <see cref="object"/> to <see cref="object"/>,
    /// so this parameter matches that shape exactly. Taken as a generic dictionary so the method
    /// is unit-testable without constructing a real <see cref="FunctionContext"/>.
    /// </param>
    /// <param name="failureReason">
    /// On null return, a short machine-parsable reason string indicating which step of the walk
    /// failed. Intended for middleware logging, not for end users.
    /// </param>
    /// <returns>
    /// The full <c>Authorization</c> header value (e.g., <c>"Bearer eyJ..."</c>) if all four
    /// steps succeed:
    /// <list type="number">
    ///   <item>The items dictionary has the <c>"ToolInvocationContext"</c> key.</item>
    ///   <item>The value is a <see cref="ToolInvocationContext"/>.</item>
    ///   <item>Its <see cref="ToolInvocationContext.Transport"/> is an <see cref="HttpTransport"/>.</item>
    ///   <item>The transport's <see cref="HttpTransport.Headers"/> dictionary contains the
    ///         <c>Authorization</c> key (case-insensitive per <see cref="HttpTransport"/>'s
    ///         comparer).</item>
    /// </list>
    /// Returns <c>null</c> if any step fails; <paramref name="failureReason"/> indicates which.
    /// </returns>
    public static string? TryGetAuthorizationHeader(
        IDictionary<object, object> items,
        out string failureReason)
    {
        ArgumentNullException.ThrowIfNull(items);

        if (!items.TryGetValue(ToolInvocationContextItemKey, out var raw))
        {
            failureReason = "tool_context_missing";
            return null;
        }

        if (raw is not ToolInvocationContext toolContext)
        {
            failureReason = $"tool_context_wrong_type:{raw?.GetType().FullName ?? "null"}";
            return null;
        }

        if (!toolContext.TryGetHttpTransport(out var httpTransport) || httpTransport is null)
        {
            failureReason = $"non_http_transport:{toolContext.Transport?.Name ?? "null"}";
            return null;
        }

        if (!httpTransport.Headers.TryGetValue("Authorization", out var authHeader))
        {
            failureReason = "authorization_header_missing";
            return null;
        }

        failureReason = string.Empty;
        return authHeader;
    }
}
