using System.Security.Cryptography;
using System.Text;
using DevBrain.Functions.Auth.Models;
using DevBrain.Functions.Auth.Services;
using Microsoft.AspNetCore.DataProtection;

namespace DevBrain.Functions.Tests.Auth.Services;

/// <summary>
/// Unit tests for <see cref="DataProtectionUpstreamTokenProtector"/> — acceptance gate #9.
///
/// <para>
/// Production wiring (blob-backed key ring + Key Vault–protected master key) is exercised at
/// deploy time, not here. These tests use <see cref="EphemeralDataProtectionProvider"/> which
/// generates an in-memory key with the same Data Protection surface; that's enough to prove
/// round-trip preservation, that ciphertext is not equal to plaintext, and that tampering is
/// detected. The azd deploy story covers the production key material.
/// </para>
/// </summary>
public sealed class UpstreamTokenProtectorTests
{
    private static readonly UpstreamTokenEnvelope SampleEnvelope = new(
        AccessToken: "eyJ0eXAiOiJKV1QiLCJhbGciOiJub25lIn0.plaintext-access-token.sig",
        RefreshToken: "plaintext-refresh-token-value",
        ExpiresAtUnixSeconds: 1_700_000_000);

    private static DataProtectionUpstreamTokenProtector CreateProtector() =>
        new(new EphemeralDataProtectionProvider());

    [Fact]
    public void Protect_Unprotect_RoundTripsEnvelopeExactly()
    {
        var protector = CreateProtector();

        var ciphertext = protector.Protect(SampleEnvelope);
        var result = protector.Unprotect(ciphertext);

        Assert.Equal(SampleEnvelope, result);
    }

    [Fact]
    public void Protect_CiphertextDoesNotContainPlaintextTokenValues()
    {
        var protector = CreateProtector();

        var ciphertext = protector.Protect(SampleEnvelope);
        var asString = Encoding.UTF8.GetString(ciphertext);

        // The concrete access/refresh token strings must not appear verbatim in the ciphertext
        // envelope. If they do, something has bypassed the protector.
        Assert.DoesNotContain("plaintext-access-token", asString, StringComparison.Ordinal);
        Assert.DoesNotContain("plaintext-refresh-token-value", asString, StringComparison.Ordinal);
    }

    [Fact]
    public void Protect_ProducesDistinctCiphertextsAcrossCalls()
    {
        // Data Protection includes a random IV, so two Protect calls on the same plaintext should
        // produce different ciphertexts. This is a smoke test that we're not hitting some
        // degenerate identity path in the fake/ephemeral provider.
        var protector = CreateProtector();

        var c1 = protector.Protect(SampleEnvelope);
        var c2 = protector.Protect(SampleEnvelope);

        Assert.NotEqual(c1, c2);
    }

    [Fact]
    public void Unprotect_TamperedCiphertext_Throws()
    {
        var protector = CreateProtector();
        var ciphertext = protector.Protect(SampleEnvelope);

        // Flip a byte in the middle. Data Protection's AEAD construction detects the tamper and
        // throws on Unprotect. The exact exception type is
        // Microsoft.AspNetCore.DataProtection.CryptographicException, which derives from
        // System.Security.Cryptography.CryptographicException.
        ciphertext[ciphertext.Length / 2] ^= 0xFF;

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(ciphertext));
    }

    [Fact]
    public void Unprotect_TruncatedCiphertext_Throws()
    {
        var protector = CreateProtector();
        var ciphertext = protector.Protect(SampleEnvelope);

        var truncated = ciphertext.AsSpan(0, ciphertext.Length - 4).ToArray();

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(truncated));
    }

    [Fact]
    public void Unprotect_CiphertextFromDifferentKeyRing_Throws()
    {
        // Two independent ephemeral providers → two independent key rings. A ciphertext from one
        // cannot be unprotected by the other even though both use the same purpose string.
        var protectorA = new DataProtectionUpstreamTokenProtector(new EphemeralDataProtectionProvider());
        var protectorB = new DataProtectionUpstreamTokenProtector(new EphemeralDataProtectionProvider());

        var ciphertext = protectorA.Protect(SampleEnvelope);

        Assert.ThrowsAny<CryptographicException>(() => protectorB.Unprotect(ciphertext));
    }

    [Fact]
    public void PurposeString_IsStableConstant()
    {
        // Guard against accidental rename — changing this constant invalidates every existing
        // vault entry in production. See remarks on DataProtectionUpstreamTokenProtector.
        Assert.Equal("DevBrain.OAuth.UpstreamToken", DataProtectionUpstreamTokenProtector.PurposeString);
    }
}
