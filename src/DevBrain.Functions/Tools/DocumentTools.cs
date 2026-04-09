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
        [McpToolProperty("key", "Document key (e.g. sprint/license-sync).", isRequired: true)]
            string key,
        [McpToolProperty("content", "Raw text content of the document.", isRequired: true)]
            string content,
        [McpToolProperty("tags", "Optional tags for the document.")]
            string[]? tags,
        [McpToolProperty("project", "Project scope (default: \"default\"). Isolates documents by project.")]
            string? project,
        FunctionContext functionContext)
    {
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
        [McpToolTrigger("ListDocuments", "List stored document keys, optionally filtered by prefix.")]
            ToolInvocationContext context,
        [McpToolProperty("prefix", "Optional key prefix to filter by (e.g. sprint/).")]
            string? prefix,
        [McpToolProperty("project", "Project scope (default: \"default\").")]
            string? project)
    {
        var documents = await _store.ListAsync(project ?? "default", prefix);

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

    [Function(nameof(SearchDocuments))]
    public async Task<string> SearchDocuments(
        [McpToolTrigger("SearchDocuments", "Full-text substring search across document keys and content.")]
            ToolInvocationContext context,
        [McpToolProperty("query", "Search term to match against keys and content.", isRequired: true)]
            string query,
        [McpToolProperty("project", "Project scope (default: \"default\").")]
            string? project)
    {
        try
        {
            var documents = await _store.SearchAsync(query, project ?? "default");

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
