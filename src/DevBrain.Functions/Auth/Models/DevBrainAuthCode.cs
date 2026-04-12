using System.Text.Json.Serialization;

namespace DevBrain.Functions.Auth.Models;

/// <summary>
/// A single-use authorization code issued by DevBrain at <c>/callback</c> and redeemed at <c>/token</c>.
/// Stored in Cosmos under key <c>code:{Code}</c>.
///
/// Redemption is atomic — see <see cref="Services.IOAuthStateStore.RedeemAuthCodeAsync"/>. Replay of a
/// redeemed code is rejected by the second caller via ETag PreconditionFailed.
/// </summary>
public sealed class DevBrainAuthCode
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("clientRedirectUri")]
    public string ClientRedirectUri { get; set; } = string.Empty;

    /// <summary>Mirror of the client's original PKCE challenge, copied from the transaction at <c>/callback</c> time.</summary>
    [JsonPropertyName("clientCodeChallenge")]
    public string ClientCodeChallenge { get; set; } = string.Empty;

    [JsonPropertyName("clientCodeChallengeMethod")]
    public string ClientCodeChallengeMethod { get; set; } = "S256";

    /// <summary>
    /// The JTI of the DevBrain JWT that <c>/token</c> will issue when this code is redeemed.
    /// Generated at <c>/callback</c> time alongside the upstream token vault record so the two
    /// halves share the same JTI.
    /// </summary>
    [JsonPropertyName("upstreamJti")]
    public string UpstreamJti { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; set; }

    [JsonPropertyName("ttl")]
    public int Ttl { get; set; }
}
