using System.Security.Cryptography;
using System.Text;

namespace OMS.Services;

internal static class QrTokenCredential
{
    public static string Hash(string token)
    {
        var value = token.Trim();
        if (value.Length == 0 || value.Length > 200 || value.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("QR_CODE harus berisi token teks QR yang valid.");

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    public static string ValidateHash(string hash)
    {
        var value = hash.Trim();
        if (value.Length != 64 || !value.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Kolom QR_CODE_HASH harus berisi hash SHA-256 yang valid.");

        return value.ToLowerInvariant();
    }
}
