using System.Text.Json.Serialization;

namespace DevBrain.Functions.Auth.Models;

/// <summary>
/// A client registered via RFC 7591 Dynamic Client Registration.
/// Stored in Cosmos under key <c>client:{ClientId}</c>.
///
/// Note: every issued <see cref="ClientId"/> maps internally to the same upstream
/// Entra app. The <c>client_id</c> is effectively just a handle for the client's
/// declared <see cref="RedirectUris"/>. See sprint:devbrain-v1.6-dcr-facade.
/// </summary>
public sealed class RegisteredClient
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("clientName")]
    public string? ClientName { get; set; }

    [JsonPropertyName("redirectUris")]
    public string[] RedirectUris { get; set; } = [];

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; set; }

    [JsonPropertyName("ttl")]
    public int Ttl { get; set; }
}
