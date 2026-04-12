using DevBrain.Functions.Auth.Models;

namespace DevBrain.Functions.Auth.Services;

/// <summary>
/// Single-responsibility abstraction: turn an <see cref="UpstreamTokenEnvelope"/> into opaque bytes
/// for storage, and back again. The production implementation
/// (<see cref="DataProtectionUpstreamTokenProtector"/>) is a thin wrapper over ASP.NET Core Data
/// Protection using a stable purpose string (<c>DevBrain.OAuth.UpstreamToken</c>).
///
/// <para>
/// Kept as an interface so the state store can be unit-tested with a fake protector that tracks
/// call counts — the tests assert that every upstream record write and read invokes the protector.
/// </para>
///
/// <para>
/// Separating this from <c>IOAuthStateStore</c> means the encryption concern has one owner and one
/// purpose string, rather than being scattered across wherever upstream tokens happen to be read
/// or written. The state store uses this interface; nothing else should.
/// </para>
/// </summary>
public interface IUpstreamTokenProtector
{
    /// <summary>Encrypts the envelope into opaque bytes suitable for Cosmos storage.</summary>
    byte[] Protect(UpstreamTokenEnvelope envelope);

    /// <summary>
    /// Decrypts previously-protected bytes back into an envelope. Throws if the ciphertext has been
    /// tampered with or was produced under a different purpose string / key ring.
    /// </summary>
    UpstreamTokenEnvelope Unprotect(byte[] ciphertext);
}
