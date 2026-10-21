using IPRO.Utility;
using Microsoft.Extensions.DependencyInjection;

namespace IPRO.Admin.Infrastructure;

// One image-upload path for every SuperAdmin screen that accepts artwork.
//
// The validation here is the same check the agent-facing logo and gallery uploads use. 585 (the
// owner's bench re-check, 2026-10-21): a picture is judged by its actual first bytes, which decide
// the type and the ending it is stored under (IPRO.Utility.UploadedPicture); the name and the
// browser-declared content type, both taken from the name, no longer turn a real picture away.
//
// Extracted from ECardDesignsController (2026-08-15) when Starter Content needed the same thing,
// and that controller was moved onto this in the same change. Copying it would have created a
// second definition that could drift -- the exact failure mode the 2026-08-14 audit found
// repeatedly (two storage-usage definitions, two consent rules, two schema repair sets).
//
// Any new SuperAdmin screen that accepts artwork calls this. Do not re-implement the checks.
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

        await using var stream = file.OpenReadStream();
        var header = await UploadedPicture.HeaderAsync(stream);
        var picture = UploadedPicture.Of(header);
        if (picture == null)
            return new Result(false, UploadedPicture.Refusal(header), null);

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
            var url = await blobs.UploadAsync(stream, $"{fileNameStem}{picture.Extension}", container, picture.ContentType, isPrivate: false);
            return new Result(true, null, url);
        }
        catch (Exception ex)
        {
            // A storage outage, a bad connection string or a wrong account key all surface here. Without
            // this the admin gets a raw 500 page with no idea whether their file was the problem.
            return new Result(false, $"The image could not be saved to storage: {ex.Message}", null);
        }
    }

}
