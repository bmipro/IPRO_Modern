using System.Security.Cryptography;
using System.Text;

namespace IPRO.Utility;

public static class AdminPreviewToken
{
    public static string Create(string sharedSecret, int agentUserId, int templateId, bool useDefaults, TimeSpan validFor)
    {
        var expiresAtUnix = DateTimeOffset.UtcNow.Add(validFor).ToUnixTimeSeconds();
        var payload = $"{agentUserId}|{templateId}|{(useDefaults ? 1 : 0)}|{expiresAtUnix}";
        var signature = Sign(sharedSecret, payload);
        var combined = $"{payload}|{signature}";
        return UrlEncode(Convert.ToBase64String(Encoding.UTF8.GetBytes(combined)));
    }

    public static bool TryValidate(string? token, string? sharedSecret, out int agentUserId, out int templateId, out bool useDefaults)
    {
        agentUserId = 0;
        templateId = 0;
        useDefaults = false;

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(sharedSecret))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(UrlDecode(token)));
        }
        catch
        {
            return false;
        }

        var parts = decoded.Split('|');
        if (parts.Length != 5)
        {
            return false;
        }

        var payload = string.Join('|', parts[0], parts[1], parts[2], parts[3]);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Sign(sharedSecret, payload)), Encoding.UTF8.GetBytes(parts[4])))
        {
            return false;
        }

        if (!int.TryParse(parts[0], out agentUserId)) return false;
        if (!int.TryParse(parts[1], out templateId)) return false;
        useDefaults = parts[2] == "1";
        if (!long.TryParse(parts[3], out var expiresAtUnix)) return false;
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiresAtUnix) return false;

        return true;
    }

    private static string Sign(string secret, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return UrlEncode(Convert.ToBase64String(hash));
    }

    private static string UrlEncode(string base64) =>
        base64.Replace("+", "-").Replace("/", "_").TrimEnd('=');

    private static string UrlDecode(string urlSafeBase64)
    {
        var base64 = urlSafeBase64.Replace("-", "+").Replace("_", "/");
        return base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
    }
}
