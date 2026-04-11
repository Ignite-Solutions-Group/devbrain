using System.Net;
using DevBrain.Functions.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;

namespace DevBrain.Functions.Services;

public sealed class CosmosDocumentStore : IDocumentStore
{
    // Bounded retries for ETag concurrency on AppendAsync. Five is enough to
    // absorb a handful of concurrent appenders without turning the tool into a
    // silent infinite-retry loop when something is genuinely wedged.
    private const int AppendMaxAttempts = 5;

    // Prefix for chunked-upload staging documents. Keeps them visually obvious
    // in list output and out of the way of real keys.
    private const string ChunkedStagingPrefix = "_staging:";

    // Staging documents self-clean after this window via the Cosmos per-item TTL.
    // Chosen to be long enough to absorb a multi-turn upload that spans slow LLM
    // rounds, but short enough that abandoned uploads don't linger indefinitely.
    private const int ChunkedStagingTtlSeconds = 4 * 60 * 60; // 4 hours

    private readonly Container _container;

    public CosmosDocumentStore(CosmosClient cosmosClient, IConfiguration configuration)
    {
        var databaseName = configuration["CosmosDb:DatabaseName"] ?? "devbrain";
        var containerName = configuration["CosmosDb:ContainerName"] ?? "documents";
        _container = cosmosClient.GetContainer(databaseName, containerName);
    }

    public async Task<BrainDocument> UpsertAsync(BrainDocument document)
    {
        document.Id = EncodeId(document.Key);
        var response = await _container.UpsertItemAsync(
            document,
            new PartitionKey(document.Key));
        return response.Resource;
    }

    private static string EncodeId(string key) => key.Replace('/', ':');

    public async Task<BrainDocument?> GetAsync(string key, string project)
    {
        // Use a query instead of ReadItemAsync because keys containing forward
        // slashes (e.g. "state/current") are misinterpreted as path separators
        // by the Cosmos DB REST API point-read endpoint.
        var queryDefinition = new QueryDefinition(
                "SELECT * FROM c WHERE c.key = @key AND c.project = @project OFFSET 0 LIMIT 1")
            .WithParameter("@key", key)
            .WithParameter("@project", project);

        using var iterator = _container.GetItemQueryIterator<BrainDocument>(queryDefinition);
        if (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            return response.FirstOrDefault();
        }

        return null;
    }

    public async Task<IReadOnlyList<BrainDocument>> ListAsync(string project, string? prefix = null)
    {
        var queryText = prefix is not null
            ? "SELECT c.key, c.tags, c.updatedAt, c.updatedBy, c.project FROM c WHERE c.project = @project AND STARTSWITH(c.key, @prefix, true) ORDER BY c.updatedAt DESC"
            : "SELECT c.key, c.tags, c.updatedAt, c.updatedBy, c.project FROM c WHERE c.project = @project ORDER BY c.updatedAt DESC";

        var queryDefinition = new QueryDefinition(queryText)
            .WithParameter("@project", project);
        if (prefix is not null)
        {
            queryDefinition = queryDefinition.WithParameter("@prefix", prefix);
        }

        var results = new List<BrainDocument>();
        using var iterator = _container.GetItemQueryIterator<BrainDocument>(queryDefinition);
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        if (results.Count == 0)
        {
            var suggestion = await GetProjectSuggestion(project);
            if (suggestion is not null)
            {
                return [BuildSuggestionDocument(project, suggestion)];
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<BrainDocument>> SearchAsync(string query, string project)
    {
        var queryText = """
            SELECT c.key, c.tags, c.updatedAt, c.content, c.project
            FROM c
            WHERE c.project = @project AND (CONTAINS(c.content, @query, true) OR CONTAINS(c.key, @query, true))
            ORDER BY c.updatedAt DESC
            OFFSET 0 LIMIT 20
            """;

        var queryDefinition = new QueryDefinition(queryText)
            .WithParameter("@project", project)
            .WithParameter("@query", query);

        var results = new List<BrainDocument>();
        using var iterator = _container.GetItemQueryIterator<BrainDocument>(queryDefinition);
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            results.AddRange(response);
        }

        if (results.Count == 0)
        {
            var suggestion = await GetProjectSuggestion(project);
            if (suggestion is not null)
            {
                return [BuildSuggestionDocument(project, suggestion)];
            }
        }

        return results;
    }

    /// <summary>
    /// Finds the closest known project name to the requested one using case-insensitive
    /// contains/startsWith matching. Returns null when no reasonable match exists, or when
    /// the requested project already exists in the store (in which case the empty result
    /// is a legitimate miss, not a mis-typed project name).
    /// </summary>
    private async Task<string?> GetProjectSuggestion(string project)
    {
        // Cross-partition scan; only runs on the empty-result path so it doesn't
        // affect the hot path. Acceptable at current scale.
        var queryDefinition = new QueryDefinition("SELECT DISTINCT VALUE c.project FROM c");

        var knownProjects = new List<string>();
        using var iterator = _container.GetItemQueryIterator<string>(queryDefinition);
        while (iterator.HasMoreResults)
        {
            var response = await iterator.ReadNextAsync();
            foreach (var value in response)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    knownProjects.Add(value);
                }
            }
        }

        // Exact match exists → the project is real, empty results are a legitimate miss.
        // Don't confuse the caller by suggesting a different project.
        if (knownProjects.Any(p => p.Equals(project, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        // Preference order: startsWith > known-contains-requested > requested-contains-known.
        var startsWithMatch = knownProjects.FirstOrDefault(p =>
            p.StartsWith(project, StringComparison.OrdinalIgnoreCase));
        if (startsWithMatch is not null)
        {
            return startsWithMatch;
        }

        var containsRequestedMatch = knownProjects.FirstOrDefault(p =>
            p.Contains(project, StringComparison.OrdinalIgnoreCase));
        if (containsRequestedMatch is not null)
        {
            return containsRequestedMatch;
        }

        var requestedContainsMatch = knownProjects.FirstOrDefault(p =>
            project.Contains(p, StringComparison.OrdinalIgnoreCase));
        return requestedContainsMatch;
    }

    private static BrainDocument BuildSuggestionDocument(string requestedProject, string suggestedProject)
    {
        return new BrainDocument
        {
            Id = "_suggestion",
            Key = "_suggestion",
            Project = requestedProject,
            Content = $"No documents found in project '{requestedProject}'. Did you mean project '{suggestedProject}'?",
            Tags = ["suggestion"],
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "system"
        };
    }

    public async Task<bool> DeleteAsync(string key, string project)
    {
        // Resolve via the project-scoped query first. This gives us the canonical
        // stored id and key (handling slash-orphans naturally) and guarantees we
        // never delete across project boundaries, even though the partition key
        // itself is not project-scoped in the container schema.
        var existing = await GetAsync(key, project);
        if (existing is null)
        {
            return false;
        }

        try
        {
            await _container.DeleteItemAsync<BrainDocument>(
                existing.Id,
                new PartitionKey(existing.Key));
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Race with a concurrent deleter. Still idempotent-success from the caller's view.
            return false;
        }
    }

    public async Task<BrainDocument> AppendAsync(
        string key,
        string project,
        string content,
        string separator,
        string[] tags,
        string updatedBy)
    {
        // Write-path callers must use colon keys, so the raw key is safe for
        // point-reads (id == EncodeId(key) == key, partition key == key).
        for (var attempt = 0; attempt < AppendMaxAttempts; attempt++)
        {
            var read = await TryReadForAppendAsync(key, project);

            if (read is null)
            {
                // No existing doc for this key in this project — create fresh.
                return await UpsertAsync(new BrainDocument
                {
                    Key = key,
                    Project = project,
                    Content = content,
                    Tags = tags,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    UpdatedBy = updatedBy
                });
            }

            var (existing, etag) = read.Value;

            var merged = new BrainDocument
            {
                Id = existing.Id,
                Key = existing.Key,
                Project = existing.Project,
                Content = existing.Content + separator + content,
                Tags = UnionTags(existing.Tags, tags),
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = updatedBy
            };

            try
            {
                var response = await _container.ReplaceItemAsync(
                    merged,
                    merged.Id,
                    new PartitionKey(merged.Key),
                    new ItemRequestOptions { IfMatchEtag = etag });
                return response.Resource;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                // Lost the ETag race — re-read and retry.
            }
        }

        throw new InvalidOperationException(
            $"AppendDocument exhausted {AppendMaxAttempts} ETag retries for key '{key}' in project '{project}'. " +
            "This suggests sustained write contention on this key — consider serializing your appenders.");
    }

    public async Task<ChunkedUpsertResult> UpsertChunkAsync(
        string key,
        string project,
        string content,
        int chunkIndex,
        int totalChunks,
        string[] tags,
        string updatedBy)
    {
        var stagingKey = ChunkedStagingPrefix + key;

        // Load or create the staging doc. Staging docs live in the same container
        // but at the `_staging:` prefix, so they never collide with real keys.
        var stagingDoc = await GetAsync(stagingKey, project) ?? new BrainDocument
        {
            Key = stagingKey,
            Project = project,
            Content = ChunkedStaging.EmptyContent(totalChunks),
            Tags = ["staging", "chunked-upload"],
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = updatedBy,
            Ttl = ChunkedStagingTtlSeconds
        };

        var staging = ChunkedStaging.Parse(stagingDoc.Content);

        // A changed totalChunks mid-upload resets the staging buffer. The caller
        // has contradicted their own earlier commitment about upload size, so the
        // previously-staged chunks may belong to a different composition.
        if (staging.TotalChunks != totalChunks)
        {
            staging = ChunkedStaging.Empty(totalChunks);
        }

        staging = staging.WithChunk(chunkIndex, content);

        if (!staging.IsComplete)
        {
            stagingDoc.Content = staging.Serialize();
            stagingDoc.UpdatedAt = DateTimeOffset.UtcNow;
            stagingDoc.UpdatedBy = updatedBy;
            stagingDoc.Tags = ["staging", "chunked-upload"];
            // Refresh the TTL on every chunk so slow uploads don't expire mid-stream.
            // Cosmos measures per-item TTL from the most recent write's `_ts`.
            stagingDoc.Ttl = ChunkedStagingTtlSeconds;
            await UpsertAsync(stagingDoc);
            return new ChunkedUpsertResult(
                Status: "staged",
                ChunksReceived: staging.ChunksReceived,
                TotalChunks: totalChunks,
                Document: null);
        }

        // All chunks received — finalize. The finalize step is two writes (upsert
        // real key, delete staging). A crash between them leaves an orphaned staging
        // doc, which the TTL will clean up; the real doc is already consistent.
        var finalDoc = await UpsertAsync(new BrainDocument
        {
            Key = key,
            Project = project,
            Content = staging.Concatenate(),
            Tags = tags,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = updatedBy
        });

        await DeleteAsync(stagingKey, project);

        return new ChunkedUpsertResult(
            Status: "finalized",
            ChunksReceived: staging.ChunksReceived,
            TotalChunks: totalChunks,
            Document: finalDoc);
    }

    private async Task<(BrainDocument existing, string etag)?> TryReadForAppendAsync(string key, string project)
    {
        try
        {
            var response = await _container.ReadItemAsync<BrainDocument>(
                key, new PartitionKey(key));
            var existing = response.Resource;

            // Cross-project key collision: the partition schema is `key`-only, so
            // two projects can theoretically land on the same physical document.
            // For Append specifically, we refuse to cross that line — silently
            // appending to another project's log would corrupt their data.
            if (!string.Equals(existing.Project, project, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Key '{key}' exists in project '{existing.Project}', not '{project}'. " +
                    "Appending across projects is not supported.");
            }

            return (existing, response.ETag);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static string[] UnionTags(string[] existing, string[] incoming)
    {
        if (incoming.Length == 0) return existing;
        if (existing.Length == 0) return incoming;

        return existing
            .Concat(incoming)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
