using System.Security.Cryptography;
using System.Text;

namespace WorkstationGame.Api.Services;

public static class HostAccess
{
    public static bool IsAuthorized(HttpRequest request, IConfiguration configuration)
    {
        var expected = configuration["Game:HostKey"];
        var supplied = request.Headers["X-Host-Key"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(supplied))
        {
            return false;
        }

        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }
}

