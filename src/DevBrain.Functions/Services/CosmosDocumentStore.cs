using System.Net;
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
        var response = await _container.UpsertItemAsync(
            document,
            new PartitionKey(document.Key));
        return response.Resource;
    }

    public async Task<BrainDocument?> GetAsync(string key, string project)
    {
        try
        {
            var response = await _container.ReadItemAsync<BrainDocument>(
                key,
                new PartitionKey(key));
            var doc = response.Resource;
            return doc.Project == project ? doc : null;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
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

        return results;
    }
}
