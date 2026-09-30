using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using IPRO.Web.Controllers;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace IPRO.IntegrationTests;

// 535 (2026-09-30): the Photo Gallery, grown up. A potential client asked for a gallery; iPro had one
// (grid or carousel, click to enlarge) that stored each photo as it came -- a 3-5 MB phone original with
// its GPS position, shown whole in every tile -- one upload at a time, with no captions, no order and a
// viewer that could only close. Now: every upload becomes a 1600 px view and an 800 px tile with the
// metadata gone; up to 20 photos at a time; captions; arrows to reorder; four layouts (grid, carousel,
// masonry, slideshow) in all three site styles; a viewer with arrows, keys and swipe; and photos from
// before today get their tile from a one-time backfill that deletes nothing.
public class Gallery535Tests
{
    // ------------------------------------------------------------------ what is kept of a photo --

    [Fact]
    public void A_phone_photo_becomes_a_1600px_view_and_an_800px_tile_without_its_metadata()
    {
        var original = WithExif(Jpeg(3000, 2000), orientation: 1);
        Assert.Contains("iPro535Camera", Encoding.ASCII.GetString(original));

        var photo = GalleryImages.Process(original);

        Assert.NotNull(photo);
        Assert.Equal((1600, 1067), (photo!.Full.Width, photo.Full.Height));
        Assert.Equal("image/jpeg", photo.Full.ContentType);
        Assert.NotNull(photo.Tile);
        Assert.Equal((800, 533), (photo.Tile!.Width, photo.Tile.Height));
        Assert.Equal((3000, 2000), (photo.SourceWidth, photo.SourceHeight));
        foreach (var bytes in new[] { photo.Full.Bytes, photo.Tile.Bytes })
        {
            var text = Encoding.ASCII.GetString(bytes);
            Assert.DoesNotContain("iPro535Camera", text);
            Assert.DoesNotContain("Exif", text);
        }
        Assert.Equal(photo.Full.Bytes.LongLength + photo.Tile.Bytes.LongLength, photo.TotalBytes);
    }

    [Fact]
    public void The_cameras_orientation_is_applied_so_a_portrait_photo_stays_upright()
    {
        // Stored landscape, red on the left; the flag says "turn 90 degrees clockwise to view".
        var original = WithExif(Jpeg(300, 200, left: SKColors.Red, right: SKColors.Blue), orientation: 6);

        var photo = GalleryImages.Process(original);

        Assert.NotNull(photo);
        Assert.Equal((200, 300), (photo!.Full.Width, photo.Full.Height));
        using var upright = SKBitmap.Decode(photo.Full.Bytes);
        var top = upright.GetPixel(100, 60);
        var bottom = upright.GetPixel(100, 240);
        Assert.True(top.Red > 180 && top.Blue < 90, $"the top should be red, was {top}");
        Assert.True(bottom.Blue > 180 && bottom.Red < 90, $"the bottom should be blue, was {bottom}");
        Assert.Null(photo.Tile);
    }

    [Fact]
    public void Transparency_stays_png_an_opaque_png_becomes_jpeg_and_an_animation_is_kept()
    {
        var logo = GalleryImages.Process(Png(1000, 500, transparent: true));
        Assert.NotNull(logo);
        Assert.Equal("image/png", logo!.Full.ContentType);
        Assert.Equal((800, 400), (logo.Tile!.Width, logo.Tile.Height));
        Assert.Equal("image/png", logo.Tile.ContentType);

        var screenshot = GalleryImages.Process(Png(400, 300, transparent: false));
        Assert.NotNull(screenshot);
        Assert.Equal("image/jpeg", screenshot!.Full.ContentType);
        Assert.Null(screenshot.Tile);

        var gif = AnimatedGif();
        var animation = GalleryImages.Process(gif);
        Assert.NotNull(animation);
        Assert.Equal("image/gif", animation!.Full.ContentType);
        Assert.Equal(gif, animation.Full.Bytes);
        Assert.Null(animation.Tile);

        Assert.Null(GalleryImages.Process(Encoding.ASCII.GetBytes("this is not an image at all")));
        Assert.Null(GalleryImages.SelfCheck());
    }

    [Fact]
    public void Four_layouts()
    {
        Assert.Equal(new[] { "grid", "carousel", "masonry", "slideshow" }, WebsiteBlockLayoutVariants.Gallery);
        Assert.Equal("masonry", WebsiteBlockLayoutVariants.Normalize(WebsiteBlockTypes.Gallery, "Masonry"));
        Assert.Equal("slideshow", WebsiteBlockLayoutVariants.Normalize(WebsiteBlockTypes.Gallery, "slideshow"));
        Assert.Equal(string.Empty, WebsiteBlockLayoutVariants.Normalize(WebsiteBlockTypes.Gallery, "collage"));
    }

    // ------------------------------------------------------------------ the page editor --

    [Fact]
    public async Task Several_photos_upload_in_one_go_each_as_a_view_and_a_tile()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var site = await SeedGalleryAsync(db);
        var blob = new MemoryBlob();
        var controller = NewPagesController(db, site.AgentId, blob);

        var result = await controller.UploadGalleryImages(site.BlockId, new List<IFormFile>
        {
            FormFile(Jpeg(2400, 1600), "kitchen.jpg", "image/jpeg"),
            FormFile(Jpeg(600, 400), "detail.jpg", "image/jpeg"),
            FormFile(Encoding.ASCII.GetBytes("plain text pretending"), "fake.jpg", "image/jpeg")
        });

        Assert.IsType<RedirectToActionResult>(result);
        var images = await ImagesAsync(db, site.BlockId);
        Assert.Equal(2, images.Count);

        var kitchen = images[0];
        Assert.Equal((1600, 1067), (kitchen.Width, kitchen.Height));
        Assert.NotEqual(string.Empty, kitchen.ThumbUrl);
        Assert.NotEqual(kitchen.Url, kitchen.ThumbUrl);
        Assert.Equal(blob.Files[kitchen.Url].LongLength + blob.Files[kitchen.ThumbUrl].LongLength, kitchen.FileSizeBytes);
        Assert.Equal("image/jpeg", blob.ContentTypes[kitchen.Url]);

        var detail = images[1];
        Assert.Equal((600, 400), (detail.Width, detail.Height));
        Assert.Equal(string.Empty, detail.ThumbUrl);
        Assert.Equal(detail.Url, detail.TileUrl);
        Assert.Equal(3, blob.Files.Count);

        var message = controller.TempData["Success"]?.ToString() ?? string.Empty;
        Assert.Contains("2 photos added", message);
        Assert.Contains("fake.jpg", message);
    }

    [Fact]
    public async Task One_batch_takes_at_most_twenty_photos()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var site = await SeedGalleryAsync(db);
        var controller = NewPagesController(db, site.AgentId, new MemoryBlob());
        var small = Jpeg(120, 90);

        await controller.UploadGalleryImages(site.BlockId,
            Enumerable.Range(1, 21).Select(i => FormFile(small, $"p{i}.jpg", "image/jpeg")).ToList());

        Assert.Equal(WebsitePagesController.GalleryBatchMax, (await ImagesAsync(db, site.BlockId)).Count);
        Assert.Contains("20 at a time", controller.TempData["Success"]?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task The_storage_limit_counts_what_is_stored()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var site = await SeedGalleryAsync(db, limitMb: 1);
        // Another gallery on the page already holds all but 20 KB of the 1 MB pool.
        db.Add(new WebsiteContentBlock
        {
            WebsitePageId = site.PageId, BlockType = WebsiteBlockTypes.Gallery, SortOrder = 1, IsVisible = true,
            SettingsJson = new WebsiteGallerySettings { Images = { new WebsiteGalleryImage { Url = "https://blob.example.test/website-gallery/full.jpg", FileSizeBytes = 1024 * 1024 - 20_000 } } }.ToJson()
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var blob = new MemoryBlob();
        var controller = NewPagesController(db, site.AgentId, blob);

        await controller.UploadGalleryImages(site.BlockId, new List<IFormFile> { FormFile(Jpeg(2400, 1600, noise: true), "big.jpg", "image/jpeg") });

        Assert.Empty(await ImagesAsync(db, site.BlockId));
        Assert.Empty(blob.Files);
        Assert.Contains("storage limit", controller.TempData["Error"]?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task Captions_save_trimmed_and_capped_and_arrows_reorder()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var site = await SeedGalleryAsync(db);
        var controller = NewPagesController(db, site.AgentId, new MemoryBlob());
        await controller.UploadGalleryImages(site.BlockId, new List<IFormFile>
        {
            FormFile(Jpeg(200, 150), "a.jpg", "image/jpeg"),
            FormFile(Jpeg(200, 150), "b.jpg", "image/jpeg"),
            FormFile(Jpeg(200, 150), "c.jpg", "image/jpeg")
        });
        var urls = (await ImagesAsync(db, site.BlockId)).Select(i => i.Url).ToList();

        await controller.SaveGalleryCaptions(site.BlockId,
            new List<string> { urls[0], urls[1], "https://elsewhere.example/x.jpg" },
            new List<string> { "  Kitchen, after  ", new string('x', 250), "ignored" });
        var images = await ImagesAsync(db, site.BlockId);
        Assert.Equal("Kitchen, after", images[0].Caption);
        Assert.Equal(WebsiteGalleryImage.CaptionMaxLength, images[1].Caption.Length);
        Assert.Equal(string.Empty, images[2].Caption);

        await controller.MoveGalleryImage(site.BlockId, urls[2], -1);
        Assert.Equal(new[] { urls[0], urls[2], urls[1] }, (await ImagesAsync(db, site.BlockId)).Select(i => i.Url).ToArray());
        await controller.MoveGalleryImage(site.BlockId, urls[0], -1);
        Assert.Equal(new[] { urls[0], urls[2], urls[1] }, (await ImagesAsync(db, site.BlockId)).Select(i => i.Url).ToArray());
        await controller.MoveGalleryImage(site.BlockId, urls[0], 1);
        Assert.Equal(new[] { urls[2], urls[0], urls[1] }, (await ImagesAsync(db, site.BlockId)).Select(i => i.Url).ToArray());
        Assert.Equal("Kitchen, after", (await ImagesAsync(db, site.BlockId))[1].Caption);
    }

    [Fact]
    public async Task Removing_a_photo_deletes_its_view_and_its_tile_and_erasure_finds_both()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var site = await SeedGalleryAsync(db);
        var blob = new MemoryBlob();
        var controller = NewPagesController(db, site.AgentId, blob);
        await controller.UploadGalleryImages(site.BlockId, new List<IFormFile>
        {
            FormFile(Jpeg(2000, 1500), "one.jpg", "image/jpeg"),
            FormFile(Jpeg(2000, 1500), "two.jpg", "image/jpeg")
        });
        var images = await ImagesAsync(db, site.BlockId);

        var preview = await AgentDataEraser.PreviewAsync(db, site.AgentId);
        foreach (var image in images)
        {
            Assert.Contains(image.Url, preview.Blobs);
            Assert.Contains(image.ThumbUrl, preview.Blobs);
        }

        await controller.DeleteGalleryImage(site.BlockId, images[0].Url);

        Assert.Single(await ImagesAsync(db, site.BlockId));
        Assert.Contains(images[0].Url, blob.Deleted);
        Assert.Contains(images[0].ThumbUrl, blob.Deleted);
        Assert.DoesNotContain(images[1].Url, blob.Deleted);
    }

    // ------------------------------------------------------------------ photos from before 535 --

    [Fact]
    public async Task Old_photos_get_a_tile_and_keep_their_original_and_nothing_is_deleted()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var site = await SeedGalleryAsync(db);
        var blob = new MemoryBlob();
        var original = WithExif(Jpeg(2000, 1500), orientation: 1);
        var photoUrl = blob.Put("website-gallery", "old-photo.jpg", original, "image/jpeg");
        var gifUrl = blob.Put("website-gallery", "old.gif", AnimatedGif(), "image/gif");
        const string missingUrl = "https://blob.example.test/website-gallery/gone.jpg";
        var block = await db.WebsiteContentBlocks.SingleAsync(b => b.Id == site.BlockId);
        block.SettingsJson = new WebsiteGallerySettings
        {
            Images =
            {
                new WebsiteGalleryImage { Url = photoUrl, FileSizeBytes = original.Length, Caption = "Before" },
                new WebsiteGalleryImage { Url = gifUrl, FileSizeBytes = 100 },
                new WebsiteGalleryImage { Url = missingUrl, FileSizeBytes = 5 }
            }
        }.ToJson();
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var first = await GalleryTileBackfill.RunAsync(db, blob, NullLogger.Instance, maxPhotos: 50, CancellationToken.None);

        Assert.Equal(new GalleryTileBackfill.Result(Tiled: 1, Measured: 2, Unreadable: 1), first);
        var images = await ImagesAsync(db, site.BlockId);
        Assert.Equal(photoUrl, images[0].Url);
        Assert.Equal((2000, 1500), (images[0].Width, images[0].Height));
        Assert.NotEqual(string.Empty, images[0].ThumbUrl);
        Assert.Equal(original.Length + blob.Files[images[0].ThumbUrl].LongLength, images[0].FileSizeBytes);
        Assert.Equal("Before", images[0].Caption);
        Assert.Equal(string.Empty, images[1].ThumbUrl);
        Assert.Equal(1, images[1].Width);
        Assert.Equal(-1, images[2].Width);
        Assert.Empty(blob.Deleted);

        var second = await GalleryTileBackfill.RunAsync(db, blob, NullLogger.Instance, maxPhotos: 50, CancellationToken.None);
        Assert.Equal(new GalleryTileBackfill.Result(0, 0, 0), second);
    }

    // ------------------------------------------------------------------ the sites and the editor --

    [Fact]
    public void Every_site_style_draws_the_gallery_and_its_viewer_through_the_shared_partials()
    {
        foreach (var (shell, prefix) in new[] { ("_ModernManagedPage.cshtml", "mp"), ("_ClassicManagedPage.cshtml", "cp"), ("_EditorialManagedPage.cshtml", "ep") })
        {
            var source = Read(Path.Combine(@"src\IPRO.Web\Views\PublicWebsite", shell));
            Assert.Contains($"[\"GalleryPrefix\"] = \"{prefix}\"", source);
            Assert.Contains("Html.PartialAsync(\"_GalleryPhotos\", block,", source);
            Assert.Contains("Html.PartialAsync(\"_GalleryViewer\",", source);
            Assert.DoesNotContain($"{prefix}-lightbox", source);
            Assert.Contains($".{prefix}-gallery-masonry", source);
            Assert.Contains($".{prefix}-gallery-slideshow", source);
        }

        var photos = Read(@"src\IPRO.Web\Views\PublicWebsite\_GalleryPhotos.cshtml");
        Assert.Contains("data-full=\"@image.Url\"", photos);
        Assert.Contains("src=\"@image.TileUrl\"", photos);
        Assert.Contains("site-slideshow", photos);

        var viewer = Read(@"src\IPRO.Web\Views\PublicWebsite\_GalleryViewer.cshtml");
        foreach (var needle in new[] { "'ArrowLeft'", "'ArrowRight'", "'Escape'", "touchstart", "aria-modal=\"true\"", "nonce=\"@Context.GetCspNonce()\"" })
            Assert.Contains(needle, viewer);

        var editor = Read(@"src\IPRO.Web\Views\WebsitePages\Edit.cshtml");
        Assert.Contains("action=\"/portal/WebsitePages/UploadGalleryImages\"", editor);
        Assert.Contains("name=\"images\" multiple", editor);
        Assert.Contains("/portal/WebsitePages/SaveGalleryCaptions", editor);
        Assert.Contains("/portal/WebsitePages/MoveGalleryImage", editor);
        Assert.Contains("<option value=\"masonry\"", editor);
        Assert.Contains("<option value=\"slideshow\"", editor);

        Assert.Contains("MapGet(\"/health/imaging\"", Read(@"src\IPRO.Web\Program.cs"));
        Assert.Contains("GalleryTileBackfillService", Read(@"src\IPRO.Web\Program.cs"));

        var guide = Read(@"DOCS\04_WEBSITE_BUILDER.md");
        foreach (var needle in new[] { "**Masonry**", "**Slideshow**", "up to 20", "location" })
            Assert.Contains(needle, guide);
    }

    // ------------------------------------------------------------------ helpers --

    private sealed record Site(int AgentId, int PageId, int BlockId);

    private static async Task<Site> SeedGalleryAsync(IPRODbContext db, int limitMb = 500)
    {
        var rule = new BillingRule { PackageName = $"GA-{Guid.NewGuid():N}"[..20], MonthlyPrice = 90m, AnnualPrice = 900m };
        db.Add(rule);
        await db.SaveChangesAsync();
        db.Add(new PackageFeature { BillingRuleId = rule.Id, FeatureCode = PackageFeatureCodes.FileUploadCapacity, FeatureName = "Storage", IsIncluded = true, LimitValue = limitMb });

        var agent = new AgentUser
        {
            UserName = $"ga-{Guid.NewGuid():N}"[..20],
            Email = $"ga-{Guid.NewGuid():N}"[..12] + "@example.test",
            FirstName = "Gallery", LastName = "Owner",
            DomainName = $"ga-{Guid.NewGuid():N}"[..24],
            Country = "Canada", Province = "Ontario"
        };
        db.Add(agent);
        await db.SaveChangesAsync();
        agent.PackageId = rule.Id;
        db.Add(new IPRO.Entities.Billing
        {
            AgentUserId = agent.Id, BillingRuleId = rule.Id, Amount = 90m,
            Status = BillingStatus.Active, Period = BillingPeriod.Monthly,
            StartDate = DateTime.UtcNow.AddDays(-10), NextBillingDate = DateTime.UtcNow.AddDays(20)
        });

        var template = new WebsiteTemplate { TemplateKey = $"tk-{Guid.NewGuid():N}"[..16], Name = "T", BusinessType = "All" };
        db.Add(template);
        await db.SaveChangesAsync();
        var website = new AgentWebsite { AgentUserId = agent.Id, TemplateId = template.Id, IsPublished = true, CustomDomain = $"ga-{Guid.NewGuid():N}"[..14] + ".example.test" };
        db.Add(website);
        await db.SaveChangesAsync();
        var page = new WebsitePage { AgentWebsiteId = website.Id, Title = "Our work", Slug = "our-work", IsPublished = true, IsHomePage = true, SortOrder = 0 };
        db.Add(page);
        await db.SaveChangesAsync();
        var block = new WebsiteContentBlock { WebsitePageId = page.Id, BlockType = WebsiteBlockTypes.Gallery, Heading = "Our work", SortOrder = 0, IsVisible = true, SettingsJson = "{}" };
        db.Add(block);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new Site(agent.Id, page.Id, block.Id);
    }

    private static async Task<List<WebsiteGalleryImage>> ImagesAsync(IPRODbContext db, int blockId)
    {
        db.ChangeTracker.Clear();
        var json = await db.WebsiteContentBlocks.AsNoTracking().Where(b => b.Id == blockId).Select(b => b.SettingsJson).SingleAsync();
        return WebsiteGallerySettings.FromJson(json).Images;
    }

    private static WebsitePagesController NewPagesController(IPRODbContext db, int agentId, MemoryBlob blob)
    {
        var controller = new WebsitePagesController(db, new PackageEntitlementService(new UnitOfWork(db), db), blob);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, agentId.ToString()) }, "test"))
        };
        controller.ControllerContext = new ControllerContext { HttpContext = context };
        controller.TempData = new Microsoft.AspNetCore.Mvc.ViewFeatures.TempDataDictionary(context, new NullTempData());
        return controller;
    }

    private static IFormFile FormFile(byte[] bytes, string name, string contentType) =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "images", name) { Headers = new HeaderDictionary(), ContentType = contentType };

    private static byte[] Jpeg(int width, int height, bool noise = false, SKColor? left = null, SKColor? right = null)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(200, 180, 150));
            if (left is { } l) { using var paint = new SKPaint { Color = l }; canvas.DrawRect(0, 0, width / 2f, height, paint); }
            if (right is { } r) { using var paint = new SKPaint { Color = r }; canvas.DrawRect(width / 2f, 0, width / 2f, height, paint); }
        }
        if (noise)
        {
            var pixels = new byte[width * height * 4];
            new Random(535).NextBytes(pixels);
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static byte[] Png(int width, int height, bool transparent)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, transparent ? SKAlphaType.Premul : SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(transparent ? SKColors.Transparent : SKColors.White);
            using var paint = new SKPaint { Color = new SKColor(20, 87, 217), IsAntialias = true };
            canvas.DrawCircle(width / 2f, height / 2f, Math.Min(width, height) / 3f, paint);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // A camera's EXIF block right after the JPEG's start marker: the camera's make (a stand-in for the GPS
    // block, so the test can see it is gone) and the orientation flag.
    private static byte[] WithExif(byte[] jpeg, ushort orientation)
    {
        var make = Encoding.ASCII.GetBytes("iPro535Camera\0");
        var tiff = new List<byte> { 0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, 0x02, 0x00 };
        void Entry(ushort tag, ushort type, uint count, uint value)
        {
            tiff.AddRange(BitConverter.GetBytes(tag));
            tiff.AddRange(BitConverter.GetBytes(type));
            tiff.AddRange(BitConverter.GetBytes(count));
            tiff.AddRange(BitConverter.GetBytes(value));
        }
        Entry(0x010F, 2, (uint)make.Length, 38);   // Make, ASCII, its text after the directory
        Entry(0x0112, 3, 1, orientation);          // Orientation, SHORT
        tiff.AddRange(new byte[] { 0, 0, 0, 0 });  // no next directory
        tiff.AddRange(make);

        var payload = Encoding.ASCII.GetBytes("Exif\0\0").Concat(tiff).ToArray();
        var length = payload.Length + 2;
        var app1 = new byte[] { 0xFF, 0xE1, (byte)(length >> 8), (byte)(length & 0xFF) }.Concat(payload);
        return jpeg.Take(2).Concat(app1).Concat(jpeg.Skip(2)).ToArray();
    }

    // Two 1x1 frames: the smallest file a GIF decoder calls animated.
    private static byte[] AnimatedGif()
    {
        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes("GIF89a"));
        bytes.AddRange(new byte[] { 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF });
        bytes.AddRange(new byte[] { 0x21, 0xFF, 0x0B });
        bytes.AddRange(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
        bytes.AddRange(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 });
        for (var frame = 0; frame < 2; frame++)
        {
            bytes.AddRange(new byte[] { 0x21, 0xF9, 0x04, 0x00, 0x0A, 0x00, 0x00, 0x00 });
            bytes.AddRange(new byte[] { 0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00 });
            bytes.AddRange(new byte[] { 0x02, 0x02, 0x44, 0x01, 0x00 });
        }
        bytes.Add(0x3B);
        return bytes.ToArray();
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative));
    }

    private sealed class MemoryBlob : IPRO.Utility.IBlobStorageService
    {
        public readonly Dictionary<string, byte[]> Files = new();
        public readonly Dictionary<string, string> ContentTypes = new();
        public readonly List<string> Deleted = new();

        public string Put(string container, string fileName, byte[] bytes, string contentType)
        {
            var url = $"https://blob.example.test/{container}/{Guid.NewGuid():N}_{fileName}";
            Files[url] = bytes;
            ContentTypes[url] = contentType;
            return url;
        }

        public async Task<string> UploadAsync(Stream fileStream, string fileName, string containerName, string contentType, bool isPrivate)
        {
            using var copy = new MemoryStream();
            await fileStream.CopyToAsync(copy);
            return Put(containerName, fileName, copy.ToArray(), contentType);
        }

        public Task<bool> DeleteAsync(string blobUrl)
        {
            Deleted.Add(blobUrl);
            return Task.FromResult(Files.Remove(blobUrl));
        }

        public Task<Stream?> DownloadAsync(string blobUrl) =>
            Task.FromResult<Stream?>(Files.TryGetValue(blobUrl, out var bytes) ? new MemoryStream(bytes) : null);

        public Task<List<string>> ListAsync(string containerName) => Task.FromResult(Files.Keys.ToList());
        public string GetPublicUrl(string containerName, string fileName) => $"https://blob.example.test/{containerName}/{fileName}";
        public Task EnsureContainerAccessAsync(string containerName, bool isPrivate) => Task.CompletedTask;
    }

    private sealed class NullTempData : Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
