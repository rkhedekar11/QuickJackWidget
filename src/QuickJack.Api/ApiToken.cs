using System.Security.Cryptography;
using System.Text.Json;
using QuickJack.Core.Storage;

namespace QuickJack.Api;

/// <summary>
/// The bearer token clients present, and the discovery file that tells them where to find it.
/// <para>
/// The token's entire value as a credential is that other accounts — and, critically, web
/// pages — cannot read it. It is written with inheritance disabled and a single ACE for the
/// current user.
/// </para>
/// </summary>
public static class ApiToken
{
    public static string LoadOrCreate(QuickJackPaths paths)
    {
        paths.EnsureUserDirectory();

        if (File.Exists(paths.ApiToken))
        {
            var existing = File.ReadAllText(paths.ApiToken).Trim();
            if (existing.Length >= 32) return existing;
        }

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(paths.ApiToken, token);

        // If the ACL cannot be tightened the token is readable by anyone who can already read
        // the profile, which is a materially weaker position — say so rather than pretend.
        if (!WindowsAcl.TryRestrictToCurrentUser(paths.ApiToken))
        {
            throw new InvalidOperationException(
                $"Could not restrict permissions on {paths.ApiToken}. Refusing to use a " +
                "token other accounts might be able to read.");
        }

        return token;
    }

    /// <summary>Writes the file clients read to discover the port and token location.</summary>
    public static void WriteEndpointFile(QuickJackPaths paths, int port)
    {
        var json = JsonSerializer.Serialize(new
        {
            port,
            baseUrl = $"http://127.0.0.1:{port}",
            tokenPath = paths.ApiToken,
        }, new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(paths.Endpoint, json);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Constant-time comparison, so a wrong token cannot be recovered by timing.</summary>
    public static bool Matches(string expected, string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return false;

        var a = System.Text.Encoding.UTF8.GetBytes(expected);
        var b = System.Text.Encoding.UTF8.GetBytes(presented);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
