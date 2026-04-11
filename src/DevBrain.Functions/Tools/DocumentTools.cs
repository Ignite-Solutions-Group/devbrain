using System.Text.Json;
using DevBrain.Functions.Models;
using DevBrain.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;

namespace DevBrain.Functions.Tools;

public sealed class DocumentTools
{
    private readonly IDocumentStore _store;

    public DocumentTools(IDocumentStore store)
    {
        _store = store;
    }

    [Function(nameof(UpsertDocument))]
    public async Task<string> UpsertDocument(
        [McpToolTrigger("UpsertDocument", "Create or replace a document by key.")]
            ToolInvocationContext context,
        [McpToolProperty("key", "Document key (e.g. sprint:license-sync).", isRequired: true)]
            string key,
        [McpToolProperty("content", "Raw text content of the document.", isRequired: true)]
            string content,
        [McpToolProperty("tags", "Optional tags for the document.")]
            string[]? tags,
        [McpToolProperty("project", "Project scope (default: \"default\"). Isolates documents by project.")]
            string? project,
        FunctionContext functionContext)
    {
        var keyError = ValidateWriteKey(key);
        if (keyError is not null)
        {
            return keyError;
        }

        try
        {
            var updatedBy = GetCallerIdentity(functionContext);
            var resolvedProject = project ?? "default";

            var document = new BrainDocument
            {
                Id = key,
                Key = key,
                Project = resolvedProject,
                Content = content,
                Tags = tags ?? [],
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = updatedBy
            };

            var saved = await _store.UpsertAsync(document);
            return JsonSerializer.Serialize(saved);
        }
        catch (Exception ex)
        {
            return $"Error upserting document: {ex.Message}";
        }
    }

    [Function(nameof(GetDocument))]
    public async Task<string> GetDocument(
        [McpToolTrigger("GetDocument", "Retrieve a document by key.")]
            ToolInvocationContext context,
        [McpToolProperty("key", "Document key to retrieve.", isRequired: true)]
            string key,
        [McpToolProperty("project", "Project scope (default: \"default\").")]
            string? project)
    {
        var document = await _store.GetAsync(key, project ?? "default");
        if (document is null)
        {
            return $"Document not found: '{key}'";
        }

        return JsonSerializer.Serialize(document);
    }

    [Function(nameof(ListDocuments))]
    public async Task<string> ListDocuments(
        [McpToolTrigger("ListDocuments", "List stored document keys, optionally filtered by prefix. If the project has no matching documents and a similarly-named project exists, a single suggestion entry (key \"_suggestion\") is returned instead.")]
            ToolInvocationContext context,
        [McpToolProperty("prefix", "Optional key prefix to filter by (e.g. sprint:).")]
            string? prefix,
        [McpToolProperty("project", "Project scope (default: \"default\").")]
            string? project)
    {
        var documents = await _store.ListAsync(project ?? "default", prefix);

        if (documents.Count == 1 && documents[0].Key == "_suggestion")
        {
            return JsonSerializer.Serialize(new[]
            {
                new { documents[0].Key, documents[0].Content }
            });
        }

        var projection = documents.Select(d => new
        {
            d.Key,
            d.Tags,
            d.UpdatedAt,
            d.UpdatedBy,
            d.Project
        });

        return JsonSerializer.Serialize(projection);
    }

    [Function(nameof(DeleteDocument))]
    public async Task<string> DeleteDocument(
        [McpToolTrigger("DeleteDocument", "Delete a document by key. Idempotent — deleting a missing key returns a not-found note rather than an error. Project-scoped.")]
            ToolInvocationContext context,
        [McpToolProperty("key", "Document key to delete (e.g. sprint:old-notes).", isRequired: true)]
            string key,
        [McpToolProperty("project", "Project scope (default: \"default\").")]
            string? project)
    {
        try
        {
            var resolvedProject = project ?? "default";
            var deleted = await _store.DeleteAsync(key, resolvedProject);

            return JsonSerializer.Serialize(new
            {
                key,
                project = resolvedProject,
                deleted,
                message = deleted
                    ? $"Deleted '{key}' from project '{resolvedProject}'."
                    : $"No document found at '{key}' in project '{resolvedProject}' (nothing to delete)."
            });
        }
        catch (Exception ex)
        {
            return $"Error deleting document: {ex.Message}";
        }
    }

    [Function(nameof(AppendDocument))]
    public async Task<string> AppendDocument(
        [McpToolTrigger("AppendDocument", "Append content to an existing document, or create it if missing. Intended for growing logs (session history, decision logs, audit trails) where UpsertDocument would force the caller to re-emit the entire existing body. Server-side concatenation is atomic from a reader's perspective. Tags are unioned with any existing tags.")]
            ToolInvocationContext context,
        [McpToolProperty("key", "Document key to append to (e.g. state:history).", isRequired: true)]
            string key,
        [McpToolProperty("content", "Text to append to the existing document body.", isRequired: true)]
            string content,
        [McpToolProperty("separator", "Separator inserted between existing content and the new content. Defaults to two newlines.")]
            string? separator,
        [McpToolProperty("tags", "Optional tags to union into the document's tag set.")]
            string[]? tags,
        [McpToolProperty("project", "Project scope (default: \"default\").")]
            string? project,
        FunctionContext functionContext)
    {
        var keyError = ValidateWriteKey(key);
        if (keyError is not null)
        {
            return keyError;
        }

        try
        {
            var updatedBy = GetCallerIdentity(functionContext);
            var resolvedProject = project ?? "default";
            var resolvedSeparator = separator ?? "\n\n";

            var saved = await _store.AppendAsync(
                key,
                resolvedProject,
                content,
                resolvedSeparator,
                tags ?? [],
                updatedBy);

            return JsonSerializer.Serialize(new
            {
                saved.Key,
                saved.Project,
                saved.Tags,
                saved.UpdatedAt,
                saved.UpdatedBy,
                ContentLength = saved.Content.Length
            });
        }
        catch (Exception ex)
        {
            return $"Error appending document: {ex.Message}";
        }
    }

    [Function(nameof(UpsertDocumentChunked))]
    public async Task<string> UpsertDocumentChunked(
        [McpToolTrigger("UpsertDocumentChunked", "Upload a document in multiple chunks. Use when a document is too large to emit in a single LLM turn. Call once per chunk with the same key and totalChunks; the final chunk triggers server-side concatenation and a normal upsert. Chunks may arrive out of order. Abandoned uploads expire via TTL.")]
            ToolInvocationContext context,
        [McpToolProperty("key", "Final document key (e.g. ref:long-spec). Must not start with '_staging:'.", isRequired: true)]
            string key,
        [McpToolProperty("content", "Text content for this chunk.", isRequired: true)]
            string content,
        [McpToolProperty("chunkIndex", "Zero-based index of this chunk within the upload.", isRequired: true)]
            int chunkIndex,
        [McpToolProperty("totalChunks", "Total number of chunks in this upload. Must match across all chunks of the same upload.", isRequired: true)]
            int totalChunks,
        [McpToolProperty("tags", "Optional tags applied to the finalized document.")]
            string[]? tags,
        [McpToolProperty("project", "Project scope (default: \"default\").")]
            string? project,
        FunctionContext functionContext)
    {
        var keyError = ValidateWriteKey(key);
        if (keyError is not null)
        {
            return keyError;
        }

        if (key.StartsWith("_staging:", StringComparison.Ordinal))
        {
            return "Keys starting with '_staging:' are reserved for chunked-upload internals.";
        }

        if (totalChunks <= 0)
        {
            return "totalChunks must be a positive integer.";
        }

        if (chunkIndex < 0 || chunkIndex >= totalChunks)
        {
            return $"chunkIndex {chunkIndex} is out of range for totalChunks {totalChunks}.";
        }

        try
        {
            var updatedBy = GetCallerIdentity(functionContext);
            var resolvedProject = project ?? "default";

            var result = await _store.UpsertChunkAsync(
                key,
                resolvedProject,
                content,
                chunkIndex,
                totalChunks,
                tags ?? [],
                updatedBy);

            return JsonSerializer.Serialize(new
            {
                key,
                project = resolvedProject,
                result.Status,
                result.ChunksReceived,
                result.TotalChunks,
                Document = result.Document is null ? null : new
                {
                    result.Document.Key,
                    result.Document.Project,
                    result.Document.Tags,
                    result.Document.UpdatedAt,
                    result.Document.UpdatedBy,
                    ContentLength = result.Document.Content.Length
                }
            });
        }
        catch (Exception ex)
        {
            return $"Error processing chunk: {ex.Message}";
        }
    }

    [Function(nameof(SearchDocuments))]
    public async Task<string> SearchDocuments(
        [McpToolTrigger("SearchDocuments", "Full-text substring search across document keys and content. If the project has no matches and a similarly-named project exists, a single suggestion entry (key \"_suggestion\") is returned instead.")]
            ToolInvocationContext context,
        [McpToolProperty("query", "Search term to match against keys and content.", isRequired: true)]
            string query,
        [McpToolProperty("project", "Project scope (default: \"default\").")]
            string? project)
    {
        try
        {
            var documents = await _store.SearchAsync(query, project ?? "default");

            if (documents.Count == 1 && documents[0].Key == "_suggestion")
            {
                return JsonSerializer.Serialize(new[]
                {
                    new { documents[0].Key, documents[0].Content }
                });
            }

            var projection = documents.Select(d => new
            {
                d.Key,
                d.Tags,
                d.UpdatedAt,
                d.Project,
                ContentExcerpt = d.Content.Length > 300 ? d.Content[..300] + "..." : d.Content
            });

            return JsonSerializer.Serialize(projection);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { results = Array.Empty<object>(), message = $"Search failed: {ex.Message}" });
        }
    }

    /// <summary>
    /// Enforces the colon-key convention on write paths. Writes that use '/' as a separator
    /// collide on id (EncodeId maps '/' → ':') but land in a different partition (raw key),
    /// producing two distinct documents that look identical from the id axis. Rejecting at
    /// the write boundary prevents the collision at the source. Reads keep the slash fallback
    /// so older callers continue to work (Postel's law).
    /// </summary>
    private static string? ValidateWriteKey(string key)
    {
        if (string.IsNullOrEmpty(key) || !key.Contains('/'))
        {
            return null;
        }

        var suggested = key.Replace('/', ':');
        return $"Keys must use ':' as separator. Got '{key}' — did you mean '{suggested}'?";
    }

    private static string GetCallerIdentity(FunctionContext functionContext)
    {
        var features = functionContext.Features;
        var claimsPrincipal = features.Get<System.Security.Claims.ClaimsPrincipal>();

        if (claimsPrincipal?.Identity?.IsAuthenticated == true)
        {
            var upn = claimsPrincipal.FindFirst("preferred_username")?.Value;
            if (!string.IsNullOrEmpty(upn))
                return upn;

            var oid = claimsPrincipal.FindFirst("oid")?.Value;
            if (!string.IsNullOrEmpty(oid))
                return oid;
        }

        return "unknown";
    }
}
