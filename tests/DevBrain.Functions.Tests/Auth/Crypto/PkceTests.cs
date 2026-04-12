using DevBrain.Functions.Auth.Crypto;

namespace DevBrain.Functions.Tests.Auth.Crypto;

/// <summary>
/// Unit tests for the PKCE primitives. The endpoint-level downgrade test (acceptance gate #2)
/// lives in <c>TokenEndpointTests</c> and exercises /token with a mismatched verifier; this file
/// proves the underlying primitive rejects each malformed case on its own.
/// </summary>
public sealed class PkceTests
{
    [Fact]
    public void GeneratedPair_Verifies()
    {
        var (verifier, challenge) = Pkce.GenerateChallengePair();

        // RFC 7636 §4.1: verifier must be 43-128 URL-safe chars.
        Assert.InRange(verifier.Length, 43, 128);
        Assert.DoesNotContain('+', verifier);
        Assert.DoesNotContain('/', verifier);
        Assert.DoesNotContain('=', verifier);

        Assert.True(Pkce.VerifyChallenge(verifier, challenge));
    }

    [Fact]
    public void GenerateChallengePair_ProducesDistinctValues()
    {
        var (v1, c1) = Pkce.GenerateChallengePair();
        var (v2, c2) = Pkce.GenerateChallengePair();

        Assert.NotEqual(v1, v2);
        Assert.NotEqual(c1, c2);
    }

    [Fact]
    public void VerifyChallenge_TamperedVerifier_ReturnsFalse()
    {
        var (verifier, challenge) = Pkce.GenerateChallengePair();

        // Flip one character in the verifier. SHA256 avalanches so the challenge won't match.
        var tampered = verifier[..^1] + (verifier[^1] == 'A' ? 'B' : 'A');

        Assert.False(Pkce.VerifyChallenge(tampered, challenge));
    }

    [Fact]
    public void VerifyChallenge_TamperedChallenge_ReturnsFalse()
    {
        var (verifier, challenge) = Pkce.GenerateChallengePair();
        var tampered = challenge[..^1] + (challenge[^1] == 'A' ? 'B' : 'A');

        Assert.False(Pkce.VerifyChallenge(verifier, tampered));
    }

    [Theory]
    [InlineData(null, "challenge")]
    [InlineData("", "challenge")]
    [InlineData("verifier-long-enough-to-pass-length-check-xxxxxxxxx", null)]
    [InlineData("verifier-long-enough-to-pass-length-check-xxxxxxxxx", "")]
    public void VerifyChallenge_NullOrEmpty_ReturnsFalse(string? verifier, string? challenge)
    {
        Assert.False(Pkce.VerifyChallenge(verifier, challenge));
    }

    [Fact]
    public void VerifyChallenge_VerifierTooShort_ReturnsFalse()
    {
        // 42 characters — one under the RFC 7636 minimum of 43. Must be rejected even if we knew the hash.
        var shortVerifier = new string('a', 42);
        var challenge = "any-challenge-value-would-still-fail";

        Assert.False(Pkce.VerifyChallenge(shortVerifier, challenge));
    }

    [Fact]
    public void VerifyChallenge_VerifierTooLong_ReturnsFalse()
    {
        // 129 characters — one over the RFC 7636 maximum of 128.
        var longVerifier = new string('a', 129);
        var challenge = "any-challenge-value-would-still-fail";

        Assert.False(Pkce.VerifyChallenge(longVerifier, challenge));
    }

    [Fact]
    public void VerifyChallenge_VerifierFromDifferentPair_ReturnsFalse()
    {
        var (_, challengeA) = Pkce.GenerateChallengePair();
        var (verifierB, _) = Pkce.GenerateChallengePair();

        Assert.False(Pkce.VerifyChallenge(verifierB, challengeA));
    }
}
