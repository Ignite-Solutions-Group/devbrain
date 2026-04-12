using System.Text.Json;
using DevBrain.Functions.Auth.Models;
using DevBrain.Functions.Auth.Services;

namespace DevBrain.Functions.Tests.TestHelpers;

/// <summary>
/// Test double for <see cref="IUpstreamTokenProtector"/>. Round-trips envelopes via plain JSON
/// serialization (no real encryption) and records how many times <see cref="Protect"/> and
/// <see cref="Unprotect"/> have been called.
///
/// <para>
/// Used by the state-store-level protector-invocation test to assert that every upstream save and
/// every upstream read goes through the protector. Unit tests that round-trip real ciphertext use
/// <see cref="Functions.Auth.Services.DataProtectionUpstreamTokenProtector"/> backed by
/// <c>EphemeralDataProtectionProvider</c> instead — see <c>UpstreamTokenProtectorTests</c>.
/// </para>
/// </summary>
public sealed class FakeUpstreamTokenProtector : IUpstreamTokenProtector
{
    public int ProtectCalls { get; private set; }
    public int UnprotectCalls { get; private set; }

    public byte[] Protect(UpstreamTokenEnvelope envelope)
    {
        ProtectCalls++;
        return JsonSerializer.SerializeToUtf8Bytes(envelope);
    }

    public UpstreamTokenEnvelope Unprotect(byte[] ciphertext)
    {
        UnprotectCalls++;
        return JsonSerializer.Deserialize<UpstreamTokenEnvelope>(ciphertext)
            ?? throw new InvalidOperationException("Unprotected payload is empty.");
    }
}
