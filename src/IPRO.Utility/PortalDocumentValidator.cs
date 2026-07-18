using System.Text;

namespace IPRO.Utility;

public static class PortalDocumentValidator
{
    private static readonly IReadOnlyDictionary<string, string> AllowedExtensions = new Dictionary<string, string>
    {
        [".pdf"] = "application/pdf",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
    };

    public static async Task<(bool IsValid, string ContentType, string? Error)> ValidateAsync(string fileName, Stream stream)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!AllowedExtensions.TryGetValue(extension, out var contentType))
        {
            return (false, string.Empty, "That file type isn't allowed. Allowed types: PDF, Word, Excel, images (JPG/PNG/GIF/WebP), TXT, CSV.");
        }

        if (!await HasValidSignatureAsync(stream, extension))
        {
            return (false, string.Empty, "That file's contents don't match its extension.");
        }

        return (true, contentType, null);
    }

    private static async Task<bool> HasValidSignatureAsync(Stream stream, string extension)
    {
        if (extension is ".txt" or ".csv") return true;

        var header = new byte[12];
        var read = await stream.ReadAsync(header.AsMemory(0, header.Length));
        if (read < 4) return false;

        return extension switch
        {
            ".pdf" => Encoding.ASCII.GetString(header, 0, 4) == "%PDF",
            ".jpg" or ".jpeg" => header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
            ".png" => read >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            ".gif" => read >= 6 && Encoding.ASCII.GetString(header, 0, 6) is "GIF87a" or "GIF89a",
            ".webp" => read >= 12 && Encoding.ASCII.GetString(header, 0, 4) == "RIFF" && Encoding.ASCII.GetString(header, 8, 4) == "WEBP",
            ".doc" or ".xls" => read >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }),
            ".docx" or ".xlsx" => header[0] == 0x50 && header[1] == 0x4B && (header[2] == 0x03 || header[2] == 0x05 || header[2] == 0x07),
            _ => false
        };
    }
}
