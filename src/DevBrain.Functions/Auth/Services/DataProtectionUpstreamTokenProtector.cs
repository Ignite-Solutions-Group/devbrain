using System.Text.Json;
using DevBrain.Functions.Auth.Models;
using Microsoft.AspNetCore.DataProtection;

namespace DevBrain.Functions.Auth.Services;

/// <summary>
/// Production implementation of <see cref="IUpstreamTokenProtector"/>. Backed by an
/// <see cref="IDataProtector"/> derived from the configured <see cref="IDataProtectionProvider"/>.
///
/// <para>
/// <b>Purpose string</b> is <c>DevBrain.OAuth.UpstreamToken</c>. Stable across releases — do not
/// rename. Changing the purpose string would invalidate every existing upstream vault entry,
/// forcing every user to re-authenticate. If the envelope shape ever needs to change incompatibly,
/// introduce a second protector with a versioned suffix and run both for a rotation window; do
/// not overload the existing purpose string.
/// </para>
///
/// <para>
/// <b>Key material</b> comes from the <see cref="IDataProtectionProvider"/> wired up in Program.cs —
/// in production that's a blob-backed key ring protected by an Azure Key Vault key. Key rotation
/// and retention are handled by Data Protection's key manager; this class is agnostic to rotation.
/// </para>
/// </summary>
public sealed class DataProtectionUpstreamTokenProtector : IUpstreamTokenProtector
{
    /// <summary>
    /// Data Protection purpose string. Stable across releases — any change invalidates all
    /// existing vault entries. See class remarks.
    /// </summary>
    public const string PurposeString = "DevBrain.OAuth.UpstreamToken";

    private readonly IDataProtector _protector;

    public DataProtectionUpstreamTokenProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector(PurposeString);
    }

    public byte[] Protect(UpstreamTokenEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(envelope);
        return _protector.Protect(plaintext);
    }

    public UpstreamTokenEnvelope Unprotect(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        var plaintext = _protector.Unprotect(ciphertext);
        return JsonSerializer.Deserialize<UpstreamTokenEnvelope>(plaintext)
            ?? throw new InvalidOperationException("Unprotected upstream token payload deserialized to null.");
    }
}
