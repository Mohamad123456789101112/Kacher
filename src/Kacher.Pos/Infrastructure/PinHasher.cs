using System.Security.Cryptography;
using System.Text;

namespace Kacher.Pos.Infrastructure;

/// <summary>
/// PBKDF2-HMAC-SHA256 PIN hashing. A 4-6 digit PIN has only 10^4..10^6 possibilities, so the
/// iteration count is what buys time against an offline attack on a stolen <c>pos.db</c>:
/// 210 000 iterations matches the OWASP 2023 recommendation for PBKDF2-SHA256.
/// Comparisons are fixed-time to avoid timing oracles.
/// </summary>
public static class PinHasher
{
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int KeyBytes = 32;

    public static (string Hash, string Salt) Hash(string pin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pin);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(pin), salt, Iterations, HashAlgorithmName.SHA256, KeyBytes);

        return (Convert.ToBase64String(key), Convert.ToBase64String(salt));
    }

    public static bool Verify(string pin, string hash, string salt)
    {
        if (string.IsNullOrEmpty(pin) || string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(salt))
        {
            return false;
        }

        try
        {
            var saltBytes = Convert.FromBase64String(salt);
            var expected = Convert.FromBase64String(hash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(pin), saltBytes, Iterations, HashAlgorithmName.SHA256, expected.Length);

            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
