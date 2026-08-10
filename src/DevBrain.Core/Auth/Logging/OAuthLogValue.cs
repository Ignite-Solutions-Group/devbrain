using System.Security.Cryptography;
using System.Text;

namespace DevBrain.Core.Auth.Logging;

internal static class OAuthLogValue
{
    private const string Missing = "none";

    public static string Fingerprint(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return Missing;
        }

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(value), hash);
        return Convert.ToHexString(hash[..6]).ToLowerInvariant();
    }
}
