using System.Text.Json.Serialization;

namespace DevBrain.Functions.Auth.Models;

/// <summary>
/// A DevBrain refresh token. Rotated on every use (see
/// <see cref="Services.IOAuthStateStore.ConsumeRefreshAsync"/>) — FastMCP's rotation pattern,
/// adopted because a long-lived non-rotating refresh is a meaningfully worse stolen-token story.
/// Stored in Cosmos under key <c>refresh:{RefreshToken}</c>.
/// </summary>
public sealed class DevBrainRefreshRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; set; } = string.Empty;

    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>JTI of the most recently issued DevBrain JWT for this session. Used to look up the upstream token vault entry on refresh.</summary>
    [JsonPropertyName("upstreamJti")]
    public string UpstreamJti { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; set; }

    [JsonPropertyName("ttl")]
    public int Ttl { get; set; }
}
