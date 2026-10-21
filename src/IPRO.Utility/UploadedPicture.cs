using System.Text;

namespace IPRO.Utility;

// 585 (the owner's bench re-check, finding 7, 2026-10-21): what an uploaded picture IS, from its first
// bytes, whatever its name says. Every picture upload judged a file by its name ending and then turned
// it away when the bytes did not match the name: the owner's "logo.jpg" was a PNG and could not be his
// logo. Pictures saved from the web often carry the wrong ending (a PNG or WebP named .jpg, a JPEG named
// .jfif). A JPEG, PNG, GIF or WebP is taken whatever it is called and stored under the ending and the
// type of what it really is; anything else is refused with a sentence that says what it is.
//
// The first bytes remain the check that matters, as before: nothing is stored under a picture's type
// that does not start like one, and an SVG (a drawing that can carry script) is still refused.
// The name and the type the browser declared (which it takes from the name) are no longer asked.
public static class UploadedPicture
{
    public sealed record Kind(string Extension, string ContentType);

    public static readonly Kind Jpeg = new(".jpg", "image/jpeg");
    public static readonly Kind Png = new(".png", "image/png");
    public static readonly Kind Gif = new(".gif", "image/gif");
    public static readonly Kind Webp = new(".webp", "image/webp");

    // The endings each kind may keep when it already has one of them (".jpeg" stays ".jpeg").
    private static readonly Dictionary<Kind, string[]> OwnEndings = new()
    {
        [Jpeg] = new[] { ".jpg", ".jpeg" },
        [Png] = new[] { ".png" },
        [Gif] = new[] { ".gif" },
        [Webp] = new[] { ".webp" }
    };

    // How many first bytes Of and Describe read.
    public const int HeaderLength = 32;

    // A JPEG, PNG, GIF or WebP by its first bytes; null for anything else.
    public static Kind? Of(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return Jpeg;
        if (header.Length >= 8 && header[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return Png;
        if (header.Length >= 6 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8))) return Gif;
        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8)) return Webp;
        return null;
    }

    // What a file that is none of the four is, in words ("a BMP picture"), or null when it cannot tell.
    public static string? Describe(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 2 && header[0] == (byte)'B' && header[1] == (byte)'M') return "a BMP picture";
        if (header.Length >= 4 && (header[..4].SequenceEqual("II*\0"u8) || header[..4].SequenceEqual("MM\0*"u8))) return "a TIFF picture";
        if (header.Length >= 12 && header.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            var brand = Encoding.ASCII.GetString(header.Slice(8, 4));
            if (brand is "heic" or "heix" or "hevc" or "heim" or "heis" or "mif1" or "msf1") return "a HEIC photo (an iPhone's usual format)";
            if (brand is "avif" or "avis") return "an AVIF picture";
        }
        if (header.Length >= 4 && header[..4].SequenceEqual("%PDF"u8)) return "a PDF document";
        if (header.Length >= 4 && header[..4].SequenceEqual("8BPS"u8)) return "a Photoshop file";
        if (header.Length >= 4 && header[0] == 0 && header[1] == 0 && header[2] == 1 && header[3] == 0) return "an icon file (ICO)";
        var text = Encoding.UTF8.GetString(header).TrimStart((char)0xFEFF, ' ', '\t', '\r', '\n');
        if (text.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
            || (text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && text.Contains("<svg", StringComparison.OrdinalIgnoreCase)))
            return "an SVG drawing";
        return null;
    }

    // Why a file is not taken, in one sentence.
    public static string Refusal(ReadOnlySpan<byte> header) => Describe(header) is { } kind
        ? $"This file is {kind}. Save it as PNG or JPG and try again."
        : "This file is not a JPG, PNG, GIF or WebP picture. Save it as PNG or JPG and try again.";

    // The first bytes of an upload, read and the stream put back at its start.
    public static async Task<byte[]> HeaderAsync(Stream stream)
    {
        var header = new byte[HeaderLength];
        var read = 0;
        while (read < header.Length)
        {
            var n = await stream.ReadAsync(header.AsMemory(read, header.Length - read));
            if (n == 0) break;
            read += n;
        }
        stream.Position = 0;
        return header[..read];
    }

    // The name a picture is stored under: the name it came with, ending as what it is ("logo.jpg" that
    // is a PNG becomes "logo.png", "photo.jfif" becomes "photo.jpg").
    public static string StoredName(string? fileName, Kind kind)
    {
        var name = Path.GetFileName(fileName ?? string.Empty);
        var stem = Path.GetFileNameWithoutExtension(name);
        if (stem.Length == 0) stem = "picture";
        var ending = Path.GetExtension(name).ToLowerInvariant();
        return stem + (OwnEndings[kind].Contains(ending) ? ending : kind.Extension);
    }
}
