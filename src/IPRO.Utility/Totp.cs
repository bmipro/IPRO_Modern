using System.Security.Cryptography;
using System.Text;

namespace IPRO.Utility;

// 593 (2026-10-20): the codes an authenticator app shows (Google Authenticator, Microsoft Authenticator,
// 1Password and the rest all speak this): RFC 6238 time-based one-time passwords. A 20-byte secret is
// shared once, at set-up, as a QR code and as typed letters (Base32); from then on the app and iPro each
// work out the same 6 digits from the secret and the time, a new code every 30 seconds.
//
// A phone's clock can be a little off, so a code from the 30 seconds before or after is accepted too
// (one step of drift each way). The step a code belonged to is returned, so the caller can refuse any
// step at or before the last one used: no code works twice.
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;
    public const int SecretBytes = 20;
    public const int DriftSteps = 1;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    public static long StepAt(DateTime utc) =>
        (long)Math.Floor((utc.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds / StepSeconds);

    // RFC 4226 section 5.3: HMAC-SHA1 of the step as 8 big-endian bytes, dynamic truncation, last 6 digits.
    public static string CodeAt(byte[] secret, long step)
    {
        var counter = new byte[8];
        for (var i = 7; i >= 0; i--) { counter[i] = (byte)(step & 0xFF); step >>= 8; }
        var hash = HMACSHA1.HashData(secret, counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    // The step whose code this is, within one step of `utc` and after `lastUsedStep`; null for no match.
    public static long? MatchStep(byte[] secret, string? typed, DateTime utc, long lastUsedStep)
    {
        var code = NormalizeCode(typed);
        if (code == null) return null;
        var now = StepAt(utc);
        long? matched = null;
        // Every candidate is compared, in fixed time, whatever matched first.
        for (var step = now - DriftSteps; step <= now + DriftSteps; step++)
        {
            var equal = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(CodeAt(secret, step)), Encoding.ASCII.GetBytes(code));
            if (equal && step > lastUsedStep && matched == null) matched = step;
        }
        return matched;
    }

    // Six digits, with any spaces or dashes a person types between them taken out; null for anything else.
    public static string? NormalizeCode(string? typed)
    {
        if (typed == null) return null;
        var digits = new StringBuilder();
        foreach (var c in typed)
        {
            if (c is ' ' or '-') continue;
            if (c is < '0' or > '9') return null;
            digits.Append(c);
        }
        return digits.Length == Digits ? digits.ToString() : null;
    }

    // ---- the secret as people and apps see it ---------------------------------------------------------

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string ToBase32(byte[] data)
    {
        var result = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                result.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) result.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return result.ToString();
    }

    public static byte[]? FromBase32(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var raw in text)
        {
            if (raw is ' ' or '-' or '=') continue;
            var index = Base32Alphabet.IndexOf(char.ToUpperInvariant(raw));
            if (index < 0) return null;
            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return bytes.ToArray();
    }

    // The key typed by hand, in groups of four so it can be read off a screen.
    public static string GroupedKey(byte[] secret)
    {
        var key = ToBase32(secret);
        return string.Join(" ", Enumerable.Range(0, (key.Length + 3) / 4).Select(i => key.Substring(i * 4, Math.Min(4, key.Length - i * 4))));
    }

    // What the QR code holds (the Key Uri Format the apps read): the app shows "issuer (account)".
    public static string OtpAuthUri(string issuer, string accountName, byte[] secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(accountName)}" +
        $"?secret={ToBase32(secret)}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
}
