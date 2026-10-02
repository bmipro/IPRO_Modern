using System;
using System.IO;
using System.Linq;
using System.Text;
using IPRO.Entities;
using SkiaSharp;
using Xunit;

namespace IPRO.IntegrationTests;

// 539 (2026-10-02). The owner: "can u also push these images for shared use among the agents", with a
// folder of his own photographs. The product already shares images among advisers in one place -- the
// starter-banner gallery behind the newsletter's Banner Image and the page editor's "Browse shared
// starter banners" -- so that is where they went: 27 of the 34, each cut to the gallery's shape and
// saved at up to twice its size. Nothing ever checked that gallery against its folder, so these do.
public class SharedBanners539Tests
{
    private static readonly string Folder = Path.Combine(RepoRoot(), "src", "IPRO.Web", "wwwroot", "images", "starter-banners");

    [Fact]
    public void Every_banner_in_the_gallery_is_a_file_and_every_file_is_in_the_gallery()
    {
        var listed = WebsiteStarterBannerCatalog.All.Select(b => b.FileName).ToList();
        var onDisk = Directory.GetFiles(Folder).Select(Path.GetFileName).ToList();

        Assert.DoesNotContain(listed, f => !onDisk.Contains(f, StringComparer.Ordinal));   // a picker tile with a broken image
        Assert.DoesNotContain(onDisk, f => !listed.Contains(f, StringComparer.Ordinal));   // a file nobody can pick
        Assert.Equal(listed.Count, listed.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(listed.Count, WebsiteStarterBannerCatalog.All.Select(b => b.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(56, listed.Count);   // the 29 it had and the owner's 27
        // The address an adviser's block and newsletter store: lower-case path, the file as named.
        Assert.All(WebsiteStarterBannerCatalog.All, b => Assert.Equal($"/images/starter-banners/{b.FileName}", b.Url));
    }

    [Fact]
    public void Every_banner_is_the_gallerys_shape_and_light_enough_to_load_as_a_grid()
    {
        foreach (var banner in WebsiteStarterBannerCatalog.All)
        {
            var path = Path.Combine(Folder, banner.FileName);
            using var codec = SKCodec.Create(path);
            Assert.True(codec != null, $"{banner.FileName} is not an image SkiaSharp can read");
            var (width, height) = (codec!.Info.Width, codec.Info.Height);
            // 3.6 : 1, the strip the newsletter email and the pickers are drawn for (640 x 178).
            Assert.True(Math.Abs(width / (double)height - 3.6) <= 0.02, $"{banner.FileName} is {width}x{height}, not 3.6 : 1");
            Assert.InRange(width, 640, 1280);
            Assert.Equal(SKEncodedImageFormat.Jpeg, codec.EncodedFormat);
            Assert.True(new FileInfo(path).Length <= 200 * 1024, $"{banner.FileName} is {new FileInfo(path).Length / 1024} KB");
        }
    }

    [Fact]
    public void The_owners_photographs_are_in_their_groups_and_carry_no_camera_data()
    {
        var added = new (string File, string Name, string Category)[]
        {
            ("people-silhouettes.jpg", "People in silhouette", "Business"),
            ("security-camera.jpg", "Security camera", "Insurance"),
            ("keeping-watch.jpg", "Keeping watch", "Insurance"),
            ("global-vision.jpg", "Global vision", "General"),
            ("angel-statue.jpg", "Angel statue", "General"),
            ("blue-globe.jpg", "Blue globe", "General"),
            ("glass-marble.jpg", "Glass marble", "General"),
            ("autumn-valley.jpg", "Autumn valley", "Nature and landscapes"),
            ("mountain-range.jpg", "Mountain range", "Nature and landscapes"),
            ("sandy-beach.jpg", "Sandy beach", "Nature and landscapes"),
            ("blue-sky.jpg", "Blue sky", "Nature and landscapes"),
            ("summer-clouds.jpg", "Summer clouds", "Nature and landscapes"),
            ("niagara-falls.jpg", "Niagara Falls", "Nature and landscapes"),
            ("frozen-shoreline.jpg", "Frozen shoreline", "Nature and landscapes"),
            ("grand-canyon.jpg", "Grand Canyon", "Nature and landscapes"),
            ("canyon-horizon.jpg", "Canyon horizon", "Nature and landscapes"),
            ("above-the-clouds.jpg", "Above the clouds", "Nature and landscapes"),
            ("whale-off-the-coast.jpg", "Whale off the coast", "Nature and landscapes"),
            ("country-road.jpg", "Country road", "Nature and landscapes"),
            ("pebbles.jpg", "Pebbles", "Nature and landscapes"),
            ("toronto-skyline.jpg", "Toronto skyline", "Cities and places"),
            ("toronto-harbour.jpg", "Toronto from the harbour", "Cities and places"),
            ("old-city-rooftops.jpg", "Old city rooftops", "Cities and places"),
            ("colourful-houses.jpg", "Colourful houses", "Cities and places"),
            ("working-harbour.jpg", "Working harbour", "Cities and places"),
            ("stairway.jpg", "The way up", "Cities and places"),
            ("colourful-bricks.jpg", "Colourful bricks", "Cities and places"),
        };
        Assert.Equal(27, added.Length);
        foreach (var (file, name, category) in added)
        {
            var banner = Assert.Single(WebsiteStarterBannerCatalog.All, b => b.FileName == file);
            Assert.Equal(name, banner.Name);
            Assert.Equal(category, banner.Category);
            // A phone or camera writes where and when a photo was taken into the file. These go on
            // every adviser's public site and into their newsletters: the files are re-encoded, and
            // carry no Exif block at all.
            var head = Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(Folder, file)).Take(65536).Select(b => b < 128 ? b : (byte)'?').ToArray());
            Assert.DoesNotContain("Exif", head);
        }

        // The page editor's menu groups by category in the order each first appears: the five it had, then the two new.
        Assert.Equal(
            new[] { "Family and lifestyle", "Business", "Insurance", "Adviser", "General", "Nature and landscapes", "Cities and places" },
            WebsiteStarterBannerCatalog.All.Select(b => b.Category).Distinct().ToArray());
        // The originals -- camera files of up to 8 MB -- are not in the repository; what ships is the 27 strips.
        Assert.True(Directory.GetFiles(Folder).Sum(f => new FileInfo(f).Length) < 4 * 1024 * 1024);

        // The guides name the two new groups where they describe the gallery.
        Assert.Contains("Nature and landscapes", File.ReadAllText(Path.Combine(RepoRoot(), "DOCS", "30_IMAGE_LIBRARY.md")));
        Assert.Contains("Cities and places", File.ReadAllText(Path.Combine(RepoRoot(), "DOCS", "04_WEBSITE_BUILDER.md")));
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }
}
