using System.Security.Cryptography;

namespace IPRO.Utility;

public static class EncryptionService
{
    public static string GenerateToken(int length = 32)
    {
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    public static string GenerateInvoiceNumber() =>
        $"INV-{DateTime.UtcNow:yyyyMMdd}-{Random.Shared.Next(10000, 99999)}";
}
