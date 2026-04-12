using System.Security.Cryptography;
using System.Text;

namespace DevBrain.Functions.Auth.Crypto;

/// <summary>
/// RFC 7636 PKCE primitives. Used in two independent flows inside DevBrain:
/// <list type="number">
///   <item><b>Client ↔ DevBrain:</b> client supplies verifier at <c>/token</c>, validated against stored challenge.</item>
///   <item><b>DevBrain ↔ Entra:</b> DevBrain generates its own pair at <c>/authorize</c>, sends verifier upstream at <c>/callback</c>.</item>
/// </list>
///
/// <para>
/// Pure static class — no DI, no interface, no fake. Tests call these methods directly. The
/// plan explicitly rejects over-abstracting PKCE into <c>IPkceService</c> since there's nothing
/// worth mocking about a SHA256 hash.
/// </para>
///
/// <para>
/// Security note: <see cref="VerifyChallenge"/> uses <see cref="CryptographicOperations.FixedTimeEquals"/>
/// to compare hashes in constant time. This is not strictly necessary for the PKCE threat model —
/// the attacker does not get oracle feedback on verification timing — but it's cheap defense in depth
/// and removes any doubt about the comparator.
/// </para>
/// </summary>
public static class Pkce
{
    // RFC 7636 §4.1: code_verifier must be 43-128 chars of unreserved URI characters.
    // 32 random bytes → 43 base64url characters, squarely in the allowed range.
    private const int VerifierByteLength = 32;

    /// <summary>
    /// Generates a new (verifier, challenge) pair for the S256 method. The verifier is the secret
    /// that stays on the client (or inside DevBrain for the upstream hop); the challenge is what
    /// gets sent to the authorization endpoint.
    /// </summary>
    public static (string Verifier, string Challenge) GenerateChallengePair()
    {
        var verifier = GenerateVerifier();
        var challenge = DeriveChallenge(verifier);
        return (verifier, challenge);
    }

    /// <summary>
    /// Returns true if <paramref name="verifier"/> matches <paramref name="challenge"/> under the S256
    /// method. Any mismatch — length, content, tampering — returns false without throwing.
    /// </summary>
    public static bool VerifyChallenge(string? verifier, string? challenge)
    {
        if (string.IsNullOrEmpty(verifier) || string.IsNullOrEmpty(challenge))
        {
            return false;
        }

        // RFC 7636 §4.1 verifier length bound. Anything outside this range is malformed by spec
        // and must not be treated as a valid pre-image regardless of its hash.
        if (verifier.Length is < 43 or > 128)
        {
            return false;
        }

        var expected = DeriveChallenge(verifier);
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        var actualBytes = Encoding.ASCII.GetBytes(challenge);

        // FixedTimeEquals requires equal-length arrays — unequal lengths are an automatic mismatch.
        if (expectedBytes.Length != actualBytes.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static string GenerateVerifier()
    {
        var bytes = new byte[VerifierByteLength];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string DeriveChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        // Plain base64 then strip padding and swap the URL-unsafe chars, matching RFC 4648 §5.
        var base64 = Convert.ToBase64String(bytes);
        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
