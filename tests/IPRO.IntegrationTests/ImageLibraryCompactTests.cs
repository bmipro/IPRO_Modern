using System;
using System.IO;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 449 (2026-09-02). The page editor's Image Library rendered each upload as a 4-across card
// with a 16:10 image and two stacked full-width buttons -- a gallery, on a screen whose job is to
// pick a thumbnail. The owner's words: "almost distracting". It is now a picker: 6-across on a
// desktop, 4:3 thumbnails, the two actions side by side and small. The JavaScript hooks and the
// delete form are unchanged, and pinned here so a later restyle cannot silently detach them.
public class ImageLibraryCompactTests
{
    [Fact]
    public void The_image_library_is_a_compact_picker_not_a_gallery()
    {
        var view = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Views\WebsitePages\Edit.cshtml"));
        var block = LibraryBlock(view);

        // Six across on a desktop, four on a tablet, three on a phone.
        Assert.Contains("col-4 col-md-3 col-lg-2", block);
        Assert.DoesNotContain("col-xl-3", block);

        // A thumbnail, not a banner.
        Assert.Contains("aspect-ratio:4/3", block);
        Assert.DoesNotContain("aspect-ratio:16/10", block);

        // The two actions share one row instead of stacking as full-width buttons.
        Assert.Contains("d-flex gap-1", block);
        Assert.DoesNotContain("w-100 mb-2 uploaded-image-choice", block);
        Assert.DoesNotContain("btn-outline-danger w-100", block);

        // The wiring the page's JavaScript and the delete flow depend on is intact.
        Assert.Contains("uploaded-image-choice", block);
        Assert.Contains("data-image-url=\"@asset.BlobUrl\"", block);
        Assert.Contains("data-image-name=\"@asset.OriginalFileName\"", block);
        Assert.Contains("js-confirm-submit", block);
        Assert.Contains("data-confirm-message=", block);
        Assert.Contains("/portal/WebsitePages/DeleteImage/@asset.Id", block);
        // The icon-only remove button still has an accessible name.
        Assert.Contains("visually-hidden\">Remove", block);
    }

    private static string LibraryBlock(string view)
    {
        var start = view.IndexOf("@foreach (var asset in Model.MediaAssets)", StringComparison.Ordinal);
        Assert.True(start >= 0, "the library loop moved; this pin needs updating");
        var end = view.IndexOf("Browse shared starter banners", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return view[start..end];
    }

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }
}
