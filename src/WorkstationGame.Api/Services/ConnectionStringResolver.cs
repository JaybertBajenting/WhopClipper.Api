using Npgsql;

namespace WorkstationGame.Api.Services;

public static class ConnectionStringResolver
{
    public static string Resolve(IConfiguration configuration)
    {
        var raw = configuration.GetConnectionString("GameDatabase") ??
                  configuration["DATABASE_URL"] ??
                  throw new InvalidOperationException("A GameDatabase connection string is required.");

        if (!raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return raw;
        }

        var uri = new Uri(raw);
        var userInfo = uri.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty,
            Database = uri.AbsolutePath.TrimStart('/'),
            SslMode = SslMode.Require,
            Pooling = true,
            MaxPoolSize = 20
        };
        return builder.ConnectionString;
    }
}
