using IPRO.Utility;
using Microsoft.Extensions.DependencyInjection;

namespace IPRO.Admin.Infrastructure;

// One image-upload path for every SuperAdmin screen that accepts artwork.
//
// The validation here is the same three-part check the agent-facing logo and gallery uploads use:
// the extension, the browser-declared content type, and the file's actual magic bytes must all
// agree before anything reaches storage. Extension alone is trivially spoofed, and a declared
// content type is just a header.
//
// Extracted from ECardDesignsController (2026-08-15) when Starter Content needed the same thing.
// Copying it would have created a second definition that could drift -- the exact failure mode the
// 2026-08-14 audit found repeatedly (two storage-usage definitions, two consent rules, two schema
// repair sets). ECardDesignsController still has its own copy and should be migrated onto this.
public static class AdminImageUpload
{
    public const long MaxBytes = 8 * 1024 * 1024;

    public sealed record Result(bool Ok, string? Error, string? Url);

    public static async Task<Result> TryUploadAsync(
        IFormFile file, IServiceProvider services, string container, string fileNameStem)
    {
        if (file == null || file.Length == 0)
            return new Result(false, "Choose an image to upload.", null);

        if (file.Length > MaxBytes)
            return new Result(false, "Images must be 8 MB or smaller.", null);

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        var expectedContentType = extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => string.Empty
        };
        if (string.IsNullOrEmpty(expectedContentType) ||
            !string.Equals(file.ContentType, expectedContentType, StringComparison.OrdinalIgnoreCase))
            return new Result(false, "Only JPG, JPEG, PNG, GIF and WebP images are allowed.", null);

        await using var stream = file.OpenReadStream();
        if (!await HasValidImageSignatureAsync(stream, extension))
            return new Result(false, "That file does not contain a valid image.", null);

        IBlobStorageService blobs;
        try
        {
            blobs = services.GetRequiredService<IBlobStorageService>();
        }
        catch (Exception)
        {
            return new Result(false,
                "Image storage isn't configured for the admin app. Set the Azure__StorageConnectionString " +
                "and Azure__StorageAccountName app settings on ipro-prod-admin, then try again.", null);
        }

        stream.Position = 0;
        try
        {
            var url = await blobs.UploadAsync(stream, $"{fileNameStem}{extension}", container, expectedContentType, isPrivate: false);
            return new Result(true, null, url);
        }
        catch (Exception ex)
        {
            // A storage outage, a bad connection string or a wrong account key all surface here. Without
            // this the admin gets a raw 500 page with no idea whether their file was the problem.
            return new Result(false, $"The image could not be saved to storage: {ex.Message}", null);
        }
    }

    private static async Task<bool> HasValidImageSignatureAsync(Stream stream, string extension)
    {
        var header = new byte[12];
        var read = await stream.ReadAsync(header.AsMemory(0, header.Length));
        if (read < 6) return false;
        return extension switch
        {
            ".jpg" or ".jpeg" => header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
            ".png" => read >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            ".gif" => System.Text.Encoding.ASCII.GetString(header, 0, 6) is "GIF87a" or "GIF89a",
            ".webp" => read >= 12 && System.Text.Encoding.ASCII.GetString(header, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(header, 8, 4) == "WEBP",
            _ => false
        };
    }
}
