using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using DevBrain.Functions.Services;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = FunctionsApplication.CreateBuilder(args);

builder.Services.AddSingleton(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var endpoint = configuration["CosmosDb:AccountEndpoint"]
        ?? throw new InvalidOperationException("CosmosDb:AccountEndpoint is required.");
    return new CosmosClient(endpoint, (TokenCredential)new DefaultAzureCredential(), new CosmosClientOptions
    {
        UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions()
    });
});

builder.Services.AddSingleton<IDocumentStore, CosmosDocumentStore>();

builder.Build().Run();
