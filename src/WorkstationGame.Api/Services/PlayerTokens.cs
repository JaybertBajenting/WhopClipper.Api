using System.Security.Cryptography;
using System.Text;

namespace WorkstationGame.Api.Services;

public static class PlayerTokens
{
    public static string Create() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool Matches(string token, string expectedHash)
    {
        var actual = Encoding.UTF8.GetBytes(Hash(token));
        var expected = Encoding.UTF8.GetBytes(expectedHash);
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

