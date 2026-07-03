namespace DevAgent.Store;

using System.Security.Cryptography;

/// <summary>
/// PBKDF2 password hashing for the local admin user. Only the hash + salt +
/// iteration count are ever stored; verification is constant-time.
/// </summary>
public static class PasswordHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    public const int DefaultIterations = 210_000; // OWASP 2023+ guidance for PBKDF2-SHA512

    public static (string HashBase64, string SaltBase64, int Iterations) Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, DefaultIterations, HashAlgorithmName.SHA512, HashBytes);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt), DefaultIterations);
    }

    public static bool Verify(string password, string hashBase64, string saltBase64, int iterations)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(hashBase64) || string.IsNullOrEmpty(saltBase64))
        {
            return false;
        }

        var expected = Convert.FromBase64String(hashBase64);
        var salt = Convert.FromBase64String(saltBase64);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, expected.Length);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
