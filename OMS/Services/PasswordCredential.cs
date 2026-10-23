using System.Security.Cryptography;

namespace OMS.Services;

public static class PasswordCredential
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 310_000;

    public static (string Hash, string Salt) Create(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public static bool Verify(string password, string encodedHash, string encodedSalt)
    {
        try
        {
            var expected = Convert.FromBase64String(encodedHash);
            var salt = Convert.FromBase64String(encodedSalt);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                password, salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
