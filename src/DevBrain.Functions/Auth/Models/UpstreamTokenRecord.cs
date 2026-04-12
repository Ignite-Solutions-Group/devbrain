namespace DevBrain.Functions.Auth.Models;

/// <summary>
/// In-memory representation of an upstream token vault entry. Holds the plaintext
/// <see cref="UpstreamTokenEnvelope"/> (encrypted on its way to Cosmos by
/// <see cref="Services.IUpstreamTokenProtector"/>) plus the user claims needed to rehydrate a
/// <see cref="System.Security.Claims.ClaimsPrincipal"/> at tool-call time without calling Entra back.
///
/// <para>
/// Stored in Cosmos under key <c>upstream:{Jti}</c>. The JTI is the <c>jti</c> claim of the
/// corresponding DevBrain JWT issued to the client. This mapping is what lets the middleware
/// go from "a validated JWT" to "the real Entra user's UPN/OID/tid" on every tool call.
/// </para>
///
/// <para>
/// Note: there is no <c>EncryptedPayload</c> property on this type. The state store wraps/unwraps
/// via <see cref="Services.IUpstreamTokenProtector"/> and keeps the Cosmos DTO (with opaque bytes)
/// private. Callers only ever handle the plaintext shape.
/// </para>
/// </summary>
public sealed class UpstreamTokenRecord
{
    public string Id { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string Jti { get; set; } = string.Empty;

    /// <summary>
    /// The plaintext upstream token envelope. Serialized and encrypted by the state store on save,
    /// decrypted on read. Defaults to an empty envelope so tests that don't exercise the round-trip
    /// can omit it without wiring a full payload.
    /// </summary>
    public UpstreamTokenEnvelope Envelope { get; set; } = new(string.Empty, string.Empty, 0);

    public string UserPrincipalName { get; set; } = string.Empty;

    public string ObjectId { get; set; } = string.Empty;

    public string TenantId { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public int Ttl { get; set; }
}
