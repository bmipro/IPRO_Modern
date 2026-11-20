using System.Buffers.Binary;

namespace IPRO.Business.SiteImport;

// 640 (572's note, 2026-11-20): the builder refuses a picture larger than 12,000 pixels on a side or 40 million
// pixels in all (its ImageAssetPolicy); iPro's importer capped only the bytes (8 MB), and a small file can hold a
// huge picture that costs a great deal of memory to draw or resize. The importer now applies the builder's cap,
// read from the picture's own header (no decoding). A header that cannot be read is left to the checks that were
// already there.
public static class ImageDimensions
{
    public const int MaxDimension = 12_000;
    public const long MaxPixels = 40_000_000;

    public static bool WithinCap(int width, int height) =>
        width is > 0 and <= MaxDimension && height is > 0 and <= MaxDimension && (long)width * height <= MaxPixels;

    public static bool TryRead(ReadOnlySpan<byte> bytes, string extension, out int width, out int height)
    {
        width = 0;
        height = 0;
        return extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => TryJpeg(bytes, out width, out height),
            ".png" => TryPng(bytes, out width, out height),
            ".gif" => TryGif(bytes, out width, out height),
            ".webp" => TryWebp(bytes, out width, out height),
            _ => false
        };
    }

    private static bool TryPng(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (bytes.Length < 24 || !bytes[..8].SequenceEqual(signature) || !bytes.Slice(12, 4).SequenceEqual("IHDR"u8)) return false;
        width = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(16, 4));
        height = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(20, 4));
        return true;
    }

    private static bool TryGif(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        if (bytes.Length < 10 || (!bytes[..6].SequenceEqual("GIF87a"u8) && !bytes[..6].SequenceEqual("GIF89a"u8))) return false;
        width = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(6, 2));
        height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(8, 2));
        return true;
    }

    private static bool TryWebp(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        if (bytes.Length < 30 || !bytes[..4].SequenceEqual("RIFF"u8) || !bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return false;
        var chunk = bytes.Slice(12, 4);
        if (chunk.SequenceEqual("VP8X"u8))
        {
            width = 1 + (bytes[24] | (bytes[25] << 8) | (bytes[26] << 16));
            height = 1 + (bytes[27] | (bytes[28] << 8) | (bytes[29] << 16));
            return true;
        }
        if (chunk.SequenceEqual("VP8L"u8) && bytes[20] == 0x2F)
        {
            width = 1 + bytes[21] + ((bytes[22] & 0x3F) << 8);
            height = 1 + ((bytes[22] & 0xC0) >> 6) + (bytes[23] << 2) + ((bytes[24] & 0x0F) << 10);
            return true;
        }
        if (chunk.SequenceEqual("VP8 "u8) && bytes.Slice(23, 3).SequenceEqual(new byte[] { 0x9D, 0x01, 0x2A }))
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(26, 2)) & 0x3FFF;
            height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(28, 2)) & 0x3FFF;
            return true;
        }
        return false;
    }

    private static bool TryJpeg(ReadOnlySpan<byte> bytes, out int width, out int height)
    {
        width = height = 0;
        if (bytes.Length < 12 || bytes[0] != 0xFF || bytes[1] != 0xD8) return false;
        var offset = 2;
        while (offset + 4 <= bytes.Length)
        {
            while (offset < bytes.Length && bytes[offset] == 0xFF) offset++;
            if (offset >= bytes.Length) break;
            var marker = bytes[offset++];
            if (marker is 0xD8 or 0xD9 || marker is >= 0xD0 and <= 0xD7) continue;
            if (offset + 2 > bytes.Length) return false;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
            if (length < 2 || offset + length > bytes.Length) return false;
            if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF && length >= 7)
            {
                height = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 3, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 5, 2));
                return true;
            }
            offset += length;
        }
        return false;
    }
}
