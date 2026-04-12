using DevBrain.Functions.Auth.Middleware;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;

namespace DevBrain.Functions.Tests.Auth.Middleware;

/// <summary>
/// Regression tests for the v1.6 post-deploy bug where the standard
/// <c>FunctionContext.GetHttpRequestDataAsync()</c> path returned null for MCP tool trigger
/// invocations, leaving <see cref="McpJwtValidationMiddleware"/> unable to read the bearer token.
///
/// <para>
/// These tests exercise the extraction chain directly (FunctionContext.Items →
/// ToolInvocationContext → HttpTransport → Headers["Authorization"]) without constructing a
/// <see cref="Microsoft.Azure.Functions.Worker.FunctionContext"/>, so we don't need a Functions
/// host to diagnose the auth path.
/// </para>
/// </summary>
public sealed class McpToolContextAccessorTests
{
    [Fact]
    public void TryGetAuthorizationHeader_WithHttpTransport_ReturnsBearerValue()
    {
        var items = new Dictionary<object, object>
        {
            [McpToolContextAccessor.ToolInvocationContextItemKey] = new ToolInvocationContext
            {
                Name = "UpsertDocument",
                Transport = new HttpTransport("http-streamable")
                {
                    Headers =
                    {
                        ["Authorization"] = "Bearer test-token-value",
                    },
                },
            },
        };

        var result = McpToolContextAccessor.TryGetAuthorizationHeader(items, out var failureReason);

        Assert.Equal("Bearer test-token-value", result);
        Assert.Equal(string.Empty, failureReason);
    }

    [Fact]
    public void TryGetAuthorizationHeader_AuthorizationHeaderLookupIsCaseInsensitive()
    {
        // HttpTransport.Headers uses an ordinal-ignore-case comparer (see source). If a client
        // sends 'authorization' instead of 'Authorization', the lookup must still hit.
        var items = new Dictionary<object, object>
        {
            [McpToolContextAccessor.ToolInvocationContextItemKey] = new ToolInvocationContext
            {
                Name = "UpsertDocument",
                Transport = new HttpTransport("http-streamable")
                {
                    Headers =
                    {
                        ["authorization"] = "Bearer lowercase-key",
                    },
                },
            },
        };

        var result = McpToolContextAccessor.TryGetAuthorizationHeader(items, out _);

        Assert.Equal("Bearer lowercase-key", result);
    }

    [Fact]
    public void TryGetAuthorizationHeader_MissingToolContextItem_ReturnsNullWithReason()
    {
        var items = new Dictionary<object, object>();

        var result = McpToolContextAccessor.TryGetAuthorizationHeader(items, out var failureReason);

        Assert.Null(result);
        Assert.Equal("tool_context_missing", failureReason);
    }

    [Fact]
    public void TryGetAuthorizationHeader_ToolContextItemWrongType_ReturnsNullWithReason()
    {
        var items = new Dictionary<object, object>
        {
            [McpToolContextAccessor.ToolInvocationContextItemKey] = "not-a-tool-context",
        };

        var result = McpToolContextAccessor.TryGetAuthorizationHeader(items, out var failureReason);

        Assert.Null(result);
        Assert.StartsWith("tool_context_wrong_type:", failureReason);
    }

    [Fact]
    public void TryGetAuthorizationHeader_ToolContextHasNoTransport_ReturnsNullWithReason()
    {
        var items = new Dictionary<object, object>
        {
            [McpToolContextAccessor.ToolInvocationContextItemKey] = new ToolInvocationContext
            {
                Name = "UpsertDocument",
                Transport = null,
            },
        };

        var result = McpToolContextAccessor.TryGetAuthorizationHeader(items, out var failureReason);

        Assert.Null(result);
        Assert.Equal("non_http_transport:null", failureReason);
    }

    [Fact]
    public void TryGetAuthorizationHeader_NonHttpTransport_ReturnsNullWithTransportName()
    {
        // Distinct from the null-transport case above: here the tool invocation has a real
        // Transport subclass that is NOT HttpTransport (e.g., a future stdio or pipe transport
        // variant). TryGetHttpTransport returns false, and the accessor's failure reason should
        // carry the transport's Name so logs can distinguish "unknown transport type" from
        // "transport entirely missing".
        var items = new Dictionary<object, object>
        {
            [McpToolContextAccessor.ToolInvocationContextItemKey] = new ToolInvocationContext
            {
                Name = "UpsertDocument",
                Transport = new StubNonHttpTransport { Name = "stdio" },
            },
        };

        var result = McpToolContextAccessor.TryGetAuthorizationHeader(items, out var failureReason);

        Assert.Null(result);
        Assert.Equal("non_http_transport:stdio", failureReason);
    }

    /// <summary>
    /// Minimal non-<see cref="HttpTransport"/> subclass of <see cref="Transport"/> so we can test
    /// the "non-null, non-HTTP transport" branch of <see cref="McpToolContextAccessor.TryGetAuthorizationHeader"/>.
    /// The extension's public transport surface currently only includes <see cref="HttpTransport"/>,
    /// but the base class is non-sealed so tests can exercise other-transport branches.
    /// </summary>
    private sealed class StubNonHttpTransport : Transport
    {
    }

    [Fact]
    public void TryGetAuthorizationHeader_HttpTransportWithNoAuthorizationHeader_ReturnsNullWithReason()
    {
        var items = new Dictionary<object, object>
        {
            [McpToolContextAccessor.ToolInvocationContextItemKey] = new ToolInvocationContext
            {
                Name = "UpsertDocument",
                Transport = new HttpTransport("http-streamable")
                {
                    Headers =
                    {
                        ["Content-Type"] = "application/json",
                        ["X-Forwarded-For"] = "203.0.113.1",
                    },
                },
            },
        };

        var result = McpToolContextAccessor.TryGetAuthorizationHeader(items, out var failureReason);

        Assert.Null(result);
        Assert.Equal("authorization_header_missing", failureReason);
    }

    [Fact]
    public void TryGetAuthorizationHeader_NullItemsDictionary_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => McpToolContextAccessor.TryGetAuthorizationHeader(null!, out _));
    }

    [Fact]
    public void ToolInvocationContextItemKey_MatchesExtensionInternalConstant()
    {
        // Guard against drift. This literal mirrors
        // Microsoft.Azure.Functions.Worker.Extensions.Mcp.Constants.ToolInvocationContextKey,
        // which is internal to the extension. If the extension ever changes the key string, this
        // assertion will still hold, but the runtime behavior will silently break — which is why
        // there's also a post-deploy check via the diagnostic middleware to verify the tool
        // context actually lands at this key on the next redeploy.
        Assert.Equal("ToolInvocationContext", McpToolContextAccessor.ToolInvocationContextItemKey);
    }
}
