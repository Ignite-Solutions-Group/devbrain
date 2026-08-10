using System.Text.Json;
using DevBrain.Core.Models;
using DevBrain.Core.Services;
using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;

namespace DevBrain.Server.Tools;

[McpServerToolType]
public sealed class ServerDocumentTools
{
    private readonly IDocumentStore _store;
    private readonly IDocumentEditService _editService;
    private readonly ITagEditService _tagEditService;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ServerDocumentTools(
        IDocumentStore store,
        IDocumentEditService editService,
        ITagEditService tagEditService,
        IHttpContextAccessor httpContextAccessor)
    {
        _store = store;
        _editService = editService;
        _tagEditService = tagEditService;
        _httpContextAccessor = httpContextAccessor;
    }

    [McpServerTool(Name = "UpsertDocument", Destructive = true, Idempotent = true), Description("Create or replace a document by key.")]
    public async Task<string> UpsertDocument(
        [Description("Document key (e.g. sprint:license-sync).")] string key,
        [Description("Raw text content of the document.")] string content,
        [Description("Optional tags for the document.")] string[]? tags,
        [Description("Project scope (default: \"default\"). Isolates documents by project.")] string? project)
    {
        var keyError = ValidateWriteKey(key);
        if (keyError is not null)
        {
            return keyError;
        }

        try
        {
            var updatedBy = GetCallerIdentity();
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

    [McpServerTool(Name = "GetDocument", ReadOnly = true, Idempotent = true), Description("Retrieve a document by key.")]
    public async Task<string> GetDocument(
        [Description("Document key to retrieve.")] string key,
        [Description("Project scope (default: \"default\").")] string? project)
    {
        var document = await _store.GetAsync(key, project ?? "default");
        if (document is null)
        {
            return $"Document not found: '{key}'";
        }

        return JsonSerializer.Serialize(document);
    }

    [McpServerTool(Name = "GetDocumentMetadata", ReadOnly = true, Idempotent = true), Description("Retrieve document metadata (key, project, tags, updatedAt, updatedBy, contentHash, contentLength) without the content body. Use to check whether a document exists, its size, and whether it has changed — without consuming tokens on the full content.")]
    public async Task<string> GetDocumentMetadata(
        [Description("Document key to retrieve metadata for.")] string key,
        [Description("Project scope (default: \"default\").")] string? project)
    {
        var document = await _store.GetMetadataAsync(key, project ?? "default");
        if (document is null)
        {
            return $"Document not found: '{key}'";
        }

        return JsonSerializer.Serialize(new
        {
            key = document.Key,
            project = document.Project,
            tags = document.Tags,
            updatedAt = document.UpdatedAt,
            updatedBy = document.UpdatedBy,
            contentHash = document.ContentHash,
            contentLength = document.ContentLength
        });
    }

    [McpServerTool(Name = "CompareDocument", ReadOnly = true, Idempotent = true), Description("Compare candidate content against a stored document without retrieving the full body. Accepts either raw content (hashed server-side) or a precomputed SHA-256 hex hash. Returns whether the stored document matches, plus metadata. Use to decide whether an import or sync is needed.")]
    public async Task<string> CompareDocument(
        [Description("Document key to compare against.")] string key,
        [Description("Candidate content to compare. Server computes its SHA-256 hash. Provide this OR contentHash, not both.")] string? content,
        [Description("Precomputed SHA-256 hex hash of the candidate content. Provide this OR content, not both.")] string? contentHash,
        [Description("Project scope (default: \"default\").")] string? project)
    {
        if (content is null && contentHash is null)
        {
            return "Provide either 'content' or 'contentHash' to compare against.";
        }

        if (content is not null && contentHash is not null)
        {
            return "Provide either 'content' or 'contentHash', not both.";
        }

        var candidateHash = contentHash ?? ContentHashing.ComputeSha256(content!);

        var document = await _store.GetMetadataAsync(key, project ?? "default");
        if (document is null)
        {
            return JsonSerializer.Serialize(new
            {
                key,
                found = false,
                match = false,
                message = $"Document not found: '{key}'"
            });
        }

        var isMatch = string.Equals(document.ContentHash, candidateHash, StringComparison.OrdinalIgnoreCase);

        return JsonSerializer.Serialize(new
        {
            key,
            found = true,
            match = isMatch,
            storedContentHash = document.ContentHash,
            storedContentLength = document.ContentLength,
            candidateHash,
            updatedAt = document.UpdatedAt,
            updatedBy = document.UpdatedBy
        });
    }

    [McpServerTool(Name = "PreviewEditDocument", ReadOnly = true, Idempotent = true), Description("Preview an exact text edit without writing. Matches literal text only. Returns match count, preview snippets, and the current content hash to pass into ApplyEditDocument.")]
    public async Task<string> PreviewEditDocument(
        [Description("Document key to edit.")] string key,
        [Description("Exact literal text to find in the document.")] string oldText,
        [Description("Replacement text to substitute for oldText. May be empty to delete the match.")] string newText,
        [Description("Expected number of literal matches. Defaults to 1; preview refuses ambiguous edits when the actual count differs.")] int? expectedOccurrences,
        [Description("When true, matching is case-sensitive. Defaults to false.")] bool? caseSensitive,
        [Description("Project scope (default: \"default\").")] string? project)
    {
        if (string.IsNullOrEmpty(oldText))
        {
            return JsonSerializer.Serialize(new EditPreviewResult
            {
                Key = key,
                Project = project ?? "default",
                Found = false,
                WouldReplace = false,
                Message = "'oldText' must be a non-empty string."
            });
        }

        var result = await _editService.PreviewAsync(
            key,
            project ?? "default",
            oldText,
            newText,
            expectedOccurrences ?? 1,
            caseSensitive ?? false);

        return JsonSerializer.Serialize(result);
    }

    [McpServerTool(Name = "ApplyEditDocument", Destructive = true, Idempotent = true), Description("Apply an exact text edit after preview. Fails if the document changed since preview, if the match count differs, or if the edit is ambiguous. Matches literal text only.")]
    public async Task<string> ApplyEditDocument(
        [Description("Document key to edit.")] string key,
        [Description("Exact literal text to find in the document.")] string oldText,
        [Description("Replacement text to substitute for oldText. May be empty to delete the match.")] string newText,
        [Description("Content hash returned by PreviewEditDocument. Apply fails if the stored document no longer matches this hash.")] string expectedContentHash,
        [Description("Expected number of literal matches. Defaults to 1; apply refuses ambiguous edits when the actual count differs.")] int? expectedOccurrences,
        [Description("When true, matching is case-sensitive. Defaults to false.")] bool? caseSensitive,
        [Description("Project scope (default: \"default\").")] string? project)
    {
        if (string.IsNullOrEmpty(oldText))
        {
            return JsonSerializer.Serialize(new EditApplyResult
            {
                Key = key,
                Project = project ?? "default",
                Applied = false,
                Message = "'oldText' must be a non-empty string."
            });
        }

        if (string.IsNullOrWhiteSpace(expectedContentHash))
        {
            return JsonSerializer.Serialize(new EditApplyResult
            {
                Key = key,
                Project = project ?? "default",
                Applied = false,
                Message = "'expectedContentHash' is required."
            });
        }

        var result = await _editService.ApplyAsync(
            key,
            project ?? "default",
            oldText,
            newText,
            expectedOccurrences ?? 1,
            caseSensitive ?? false,
            expectedContentHash,
            GetCallerIdentity());

        return JsonSerializer.Serialize(result);
    }

    [McpServerTool(Name = "ListDocuments", ReadOnly = true, Idempotent = true), Description("List stored document keys, optionally filtered by prefix. If the project has no matching documents and a similarly-named project exists, a single suggestion entry (key \"_suggestion\") is returned instead.")]
    public async Task<string> ListDocuments(
        [Description("Optional key prefix to filter by (e.g. sprint:).")] string? prefix,
        [Description("Project scope (default: \"default\").")] string? project)
    {
        var documents = await _store.ListAsync(project ?? "default", prefix);

        if (documents.Count == 1 && documents[0].Key == "_suggestion")
        {
            return JsonSerializer.Serialize(new[]
            {
                new { key = documents[0].Key, content = documents[0].Content }
            });
        }

        var projection = documents.Select(d => new
        {
            key = d.Key,
            tags = d.Tags,
            updatedAt = d.UpdatedAt,
            updatedBy = d.UpdatedBy,
            project = d.Project
        });

        return JsonSerializer.Serialize(projection);
    }

    [McpServerTool(Name = "DeleteDocument", Destructive = true, Idempotent = true), Description("Delete a document by key. Idempotent — deleting a missing key returns a not-found note rather than an error. Project-scoped.")]
    public async Task<string> DeleteDocument(
        [Description("Document key to delete (e.g. sprint:old-notes).")] string key,
        [Description("Project scope (default: \"default\").")] string? project)
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

    [McpServerTool(Name = "AppendDocument", Destructive = false, Idempotent = false), Description("Append content to an existing document, or create it if missing. Intended for growing logs (session history, decision logs, audit trails) where UpsertDocument would force the caller to re-emit the entire existing body. Server-side concatenation is atomic from a reader's perspective. Tags are unioned with any existing tags.")]
    public async Task<string> AppendDocument(
        [Description("Document key to append to (e.g. state:history).")] string key,
        [Description("Text to append to the existing document body.")] string content,
        [Description("Separator inserted between existing content and the new content. Defaults to two newlines.")] string? separator,
        [Description("Optional tags to union into the document's tag set.")] string[]? tags,
        [Description("Project scope (default: \"default\").")] string? project)
    {
        var keyError = ValidateWriteKey(key);
        if (keyError is not null)
        {
            return keyError;
        }

        try
        {
            var updatedBy = GetCallerIdentity();
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
                key = saved.Key,
                project = saved.Project,
                tags = saved.Tags,
                updatedAt = saved.UpdatedAt,
                updatedBy = saved.UpdatedBy,
                contentHash = saved.ContentHash,
                contentLength = saved.Content.Length
            });
        }
        catch (Exception ex)
        {
            return $"Error appending document: {ex.Message}";
        }
    }

    [McpServerTool(Name = "UpsertDocumentChunked", Destructive = true, Idempotent = false), Description("Upload a document in multiple chunks. Use when a document is too large to emit in a single LLM turn. Call once per chunk with the same key and totalChunks; the final chunk triggers server-side concatenation and a normal upsert. Chunks may arrive out of order. Abandoned uploads expire via TTL.")]
    public async Task<string> UpsertDocumentChunked(
        [Description("Final document key (e.g. ref:long-spec). Must not start with '_staging:'.")] string key,
        [Description("Text content for this chunk.")] string content,
        [Description("Zero-based index of this chunk within the upload.")] int chunkIndex,
        [Description("Total number of chunks in this upload. Must match across all chunks of the same upload.")] int totalChunks,
        [Description("Optional tags applied to the finalized document.")] string[]? tags,
        [Description("Project scope (default: \"default\").")] string? project)
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
            var updatedBy = GetCallerIdentity();
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
                status = result.Status,
                chunksReceived = result.ChunksReceived,
                totalChunks = result.TotalChunks,
                document = result.Document is null ? null : new
                {
                    key = result.Document.Key,
                    project = result.Document.Project,
                    tags = result.Document.Tags,
                    updatedAt = result.Document.UpdatedAt,
                    updatedBy = result.Document.UpdatedBy,
                    contentHash = result.Document.ContentHash,
                    contentLength = result.Document.Content.Length
                }
            });
        }
        catch (Exception ex)
        {
            return $"Error processing chunk: {ex.Message}";
        }
    }

    [McpServerTool(Name = "SearchDocuments", ReadOnly = true, Idempotent = true), Description("Full-text substring search across document keys and content. If the project has no matches and a similarly-named project exists, a single suggestion entry (key \"_suggestion\") is returned instead.")]
    public async Task<string> SearchDocuments(
        [Description("Search term to match against keys and content.")] string query,
        [Description("Project scope (default: \"default\").")] string? project)
    {
        try
        {
            var documents = await _store.SearchAsync(query, project ?? "default");

            if (documents.Count == 1 && documents[0].Key == "_suggestion")
            {
                return JsonSerializer.Serialize(new[]
                {
                    new { key = documents[0].Key, content = documents[0].Content }
                });
            }

            var projection = documents.Select(d => new
            {
                key = d.Key,
                tags = d.Tags,
                updatedAt = d.UpdatedAt,
                project = d.Project,
                contentExcerpt = d.Content.Length > 300 ? d.Content[..300] + "..." : d.Content
            });

            return JsonSerializer.Serialize(projection);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { results = Array.Empty<object>(), message = $"Search failed: {ex.Message}" });
        }
    }

    [McpServerTool(Name = "EditTags", Destructive = true, Idempotent = true), Description("Add and/or remove tags on a document without re-emitting its content. Provide 'add' and/or 'remove' as disjoint tag lists; the server applies the diff and records updatedAt/updatedBy. Document content is untouched. A tag present in both 'add' and 'remove' is rejected.")]
    public async Task<string> EditTags(
        [Description("Document key whose tags to edit.")] string key,
        [Description("Tags to add. Already-present tags are kept as-is (no duplicates).")] string[]? add,
        [Description("Tags to remove. Absent tags are ignored (idempotent).")] string[]? remove,
        [Description("Project scope (default: \"default\").")] string? project)
    {
        var keyError = ValidateWriteKey(key);
        if (keyError is not null)
        {
            return keyError;
        }

        try
        {
            var result = await _tagEditService.EditTagsAsync(
                key,
                project ?? "default",
                add ?? [],
                remove ?? [],
                GetCallerIdentity());

            return JsonSerializer.Serialize(result);
        }
        catch (Exception ex)
        {
            return $"Error editing tags: {ex.Message}";
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

    private string GetCallerIdentity()
    {
        var claimsPrincipal = _httpContextAccessor.HttpContext?.User;

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
