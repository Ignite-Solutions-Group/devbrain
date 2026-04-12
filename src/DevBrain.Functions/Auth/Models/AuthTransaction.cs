using System.Text.Json.Serialization;

namespace DevBrain.Functions.Auth.Models;

/// <summary>
/// A pending authorization transaction created at <c>/authorize</c> and consumed at
/// <c>/callback</c>. Stored in Cosmos under key <c>txn:{UpstreamState}</c>.
///
/// Holds both sides of the PKCE double-dance: the client's challenge (validated at
/// <c>/token</c>) and DevBrain's own upstream verifier (sent to Entra at <c>/callback</c>).
/// The two PKCE pairs are independent — see sprint:devbrain-v1.6-dcr-facade "PKCE double-dance".
/// </summary>
public sealed class AuthTransaction
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("clientRedirectUri")]
    public string ClientRedirectUri { get; set; } = string.Empty;

    [JsonPropertyName("clientState")]
    public string? ClientState { get; set; }

    /// <summary>The client's PKCE code_challenge. Validated at <c>/token</c> against the client's code_verifier.</summary>
    [JsonPropertyName("clientCodeChallenge")]
    public string ClientCodeChallenge { get; set; } = string.Empty;

    [JsonPropertyName("clientCodeChallengeMethod")]
    public string ClientCodeChallengeMethod { get; set; } = "S256";

    /// <summary>The state parameter DevBrain sent upstream to Entra. Doubles as the Cosmos key suffix.</summary>
    [JsonPropertyName("upstreamState")]
    public string UpstreamState { get; set; } = string.Empty;

    /// <summary>DevBrain's own PKCE code_verifier for the upstream hop. Sent to Entra at <c>/callback</c>.</summary>
    [JsonPropertyName("upstreamPkceVerifier")]
    public string UpstreamPkceVerifier { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; set; }

    [JsonPropertyName("ttl")]
    public int Ttl { get; set; }
}
