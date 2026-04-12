using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace DevBrain.Functions.Auth.Middleware;

/// <summary>
/// Unconditional diagnostic middleware that logs every function invocation's name, binding types,
/// and outcome. Runs for every function — MCP tool triggers, HTTP-trigger OAuth endpoints, and
/// any future additions. <b>Not</b> registered behind a <c>UseWhen</c> predicate.
///
/// <para>
/// <b>Why this exists:</b> the v1.6 post-deploy investigation revealed that
/// <see cref="McpJwtValidationMiddleware"/> was not firing for tool calls, and the working theory
/// was a string/case mismatch in the <c>UseWhen</c> binding-type predicate. This middleware runs
/// <b>before</b> any conditional middleware and independently of whether the gate is working — so
/// on the next deploy its output will definitively show the actual binding type string emitted
/// by the MCP extension at runtime, the order of binding values, and whether invocations that
/// look like tool calls from the client side are reaching the worker at all.
/// </para>
///
/// <para>
/// <b>Logging shape:</b>
/// </para>
/// <list type="bullet">
///   <item><b>Entry:</b> function name, binding types joined by <c>|</c>, invocation id. Information level.</item>
///   <item><b>Success:</b> function name, invocation id, duration. Information level.</item>
///   <item><b>Exception:</b> function name, invocation id, duration, exception (full stack trace). Error level, then re-throws.</item>
/// </list>
///
/// <para>
/// Cost: one log call per invocation. Negligible for a DCR facade + MCP tool surface, expensive
/// for hot-path RPC workloads (not applicable here). Removable once diagnosis is complete.
/// </para>
/// </summary>
public sealed class InvocationDiagnosticMiddleware : IFunctionsWorkerMiddleware
{
    private readonly ILogger<InvocationDiagnosticMiddleware> _logger;

    public InvocationDiagnosticMiddleware(ILogger<InvocationDiagnosticMiddleware> logger)
    {
        _logger = logger;
    }

    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        var name = context.FunctionDefinition.Name;
        var inputBindingTypes = string.Join('|', context.FunctionDefinition.InputBindings.Values.Select(b => $"{b.Name}:{b.Type}"));
        var outputBindingTypes = string.Join('|', context.FunctionDefinition.OutputBindings.Values.Select(b => $"{b.Name}:{b.Type}"));

        _logger.LogInformation(
            "Invocation start function={Function} invocationId={InvocationId} inputBindings=[{InputBindings}] outputBindings=[{OutputBindings}]",
            name, context.InvocationId, inputBindingTypes, outputBindingTypes);

        var start = TimeProvider.System.GetTimestamp();

        try
        {
            await next(context);

            var elapsedMs = TimeProvider.System.GetElapsedTime(start).TotalMilliseconds;
            _logger.LogInformation(
                "Invocation success function={Function} invocationId={InvocationId} durationMs={DurationMs}",
                name, context.InvocationId, elapsedMs);
        }
        catch (Exception ex)
        {
            var elapsedMs = TimeProvider.System.GetElapsedTime(start).TotalMilliseconds;
            _logger.LogError(ex,
                "Invocation failed function={Function} invocationId={InvocationId} durationMs={DurationMs} exceptionType={ExceptionType}",
                name, context.InvocationId, elapsedMs, ex.GetType().FullName);
            throw;
        }
    }
}
