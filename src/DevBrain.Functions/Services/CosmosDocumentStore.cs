using DevBrain.Functions.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;

namespace DevBrain.Functions.Services;

public sealed class CosmosDocumentStore : IDocumentStore
{
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
}
