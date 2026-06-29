using System.Security.Cryptography;
using System.Text;

namespace IPRO.Utility;

public static class EncryptionService
{
    public static string HashPassword(string password)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(password + "IPRO_SALT_2024"));
        return Convert.ToBase64String(bytes);
    }

    public static bool VerifyPassword(string password, string hash) =>
        HashPassword(password) == hash;

    public static string GenerateToken(int length = 32)
    {
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    public static string GenerateInvoiceNumber() =>
        $"INV-{DateTime.UtcNow:yyyyMMdd}-{Random.Shared.Next(10000, 99999)}";
}
