using System.Net;
using DevBrain.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace DevBrain.Functions.Tools;

/// <summary>
/// Administrative HTTP endpoints that are not exposed as MCP tools.
/// Gated by <see cref="AuthorizationLevel.Admin"/> (requires the Function App master key).
/// </summary>
public sealed class AdminFunctions
{
    private readonly IDocumentStore _store;

    public AdminFunctions(IDocumentStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Re-upserts every document to backfill computed metadata fields (contentHash,
    /// contentLength). Idempotent — safe to run multiple times. Not exposed as an
    /// MCP tool; invoke via HTTP with the Function App master key.
    /// </summary>
    [Function(nameof(TouchAllDocuments))]
    public async Task<HttpResponseData> TouchAllDocuments(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "ops/touch")] HttpRequestData req)
    {
        var touched = await _store.TouchAllAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            touched,
            message = $"Re-upserted {touched} document(s). contentHash and contentLength are now populated."
        });
        return response;
    }
}
