using System.Text.Json.Serialization;

namespace DevBrain.Functions.Models;

public sealed class BrainDocument
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("project")]
    public string Project { get; set; } = "default";

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public string[] Tags { get; set; } = [];

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("updatedBy")]
    public string UpdatedBy { get; set; } = string.Empty;

    /// <summary>
    /// Per-item TTL in seconds. Null for normal documents (no expiration). Set only
    /// by <see cref="Services.CosmosDocumentStore.UpsertChunkAsync"/> on chunked-upload
    /// staging documents so abandoned uploads self-clean. Requires the Cosmos container
    /// to have <c>defaultTtl</c> enabled (see infra/main.bicep).
    /// </summary>
    [JsonPropertyName("ttl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Ttl { get; set; }
}
