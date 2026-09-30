using System;
using System.IO;
using Xunit;

namespace IPRO.IntegrationTests;

// 535a (2026-09-30): the owner, after testing 535: "especially for slideshow. There should be thumbnails at the
// bottom showing the slide images and be able to select another picture", pointing at a bakery's gallery (a
// large photo over a row of small square thumbnails, the current one outlined). The slideshow now carries that
// row: every photo's small copy as a button, the current one marked for the eye and for screen readers, the
// row scrolling itself to keep the current thumbnail in view as the arrows or a swipe move on.
public class SlideshowThumbnails535aTests
{
    [Fact]
    public void The_slideshow_has_a_row_of_thumbnails_that_pick_the_photo()
    {
        var photos = Read(@"src\IPRO.Web\Views\PublicWebsite\_GalleryPhotos.cshtml");
        var slideshow = photos[photos.IndexOf("@if (layout == \"slideshow\")", StringComparison.Ordinal)..photos.IndexOf("else", photos.IndexOf("@if (layout == \"slideshow\")", StringComparison.Ordinal), StringComparison.Ordinal)];
        Assert.Contains("class=\"site-slideshow__thumbs\"", slideshow);
        Assert.Contains("class=\"site-slideshow__thumb", slideshow);
        Assert.Contains("src=\"@image.TileUrl\"", slideshow);
        Assert.Contains("aria-label=\"Show photo @(i + 1) of @count\"", slideshow);
        Assert.Contains("aria-current=\"@(i == 0 ? \"true\" : null)\"", slideshow);

        var viewer = Read(@"src\IPRO.Web\Views\PublicWebsite\_GalleryViewer.cshtml");
        Assert.Contains(".site-slideshow__thumb.is-current", viewer);
        Assert.Contains("root.querySelectorAll('.site-slideshow__thumb')", viewer);
        Assert.Contains("setAttribute('aria-current', 'true')", viewer);
        Assert.Contains("strip.scrollTo(", viewer);
        Assert.Contains("prefers-reduced-motion", viewer);
        // The owner's second note ("and why is back black?"): a photo narrower than the slide showed the dark
        // stage as bars on both sides. The slide is see-through now and the image box is the photo itself.
        Assert.DoesNotContain("background: #0b1220", viewer);
        Assert.Contains(".site-slideshow__slide img { width: auto; height: auto; max-width: 100%; max-height: 100%; }", viewer);

        Assert.Contains("thumbnails", Read(@"DOCS\04_WEBSITE_BUILDER.md"));
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative));
    }
}
