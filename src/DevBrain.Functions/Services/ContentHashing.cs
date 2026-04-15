using System.Security.Cryptography;
using System.Text;

namespace DevBrain.Functions.Services;

internal static class ContentHashing
{
    public static string ComputeSha256(string content)
    {
        var normalized = NormalizeForHash(content);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// Normalizes content before hashing so that trivial formatting differences
    /// (line-ending style, trailing whitespace) don't produce different hashes
    /// for semantically identical documents. The stored content is never modified.
    /// </summary>
    public static string NormalizeForHash(string content) =>
        content.ReplaceLineEndings("\n").TrimEnd();
}
