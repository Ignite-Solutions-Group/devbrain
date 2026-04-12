using System.Text.Json.Serialization;

namespace DevBrain.Functions.Auth.Models;

/// <summary>
/// The plaintext payload that gets encrypted into <see cref="UpstreamTokenRecord.Envelope"/>'s
/// ciphertext. Carries the upstream Entra access + refresh tokens plus absolute expiry.
///
/// <para>
/// This type is ONLY ever seen in memory — <see cref="Services.IOAuthStateStore"/> routes every
/// save and every read through <see cref="Services.IUpstreamTokenProtector"/>, so on the Cosmos
/// wire the payload is opaque bytes. See <see cref="Services.DataProtectionUpstreamTokenProtector"/>.
/// </para>
/// </summary>
public sealed record UpstreamTokenEnvelope(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("expires_at")] long ExpiresAtUnixSeconds);
