using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using IPRO.Utility;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPRO.IntegrationTests;

// 585 (the owner's bench re-check, finding 7, 2026-10-21): a logo saved long ago as "logo.jpg" was
// really a PNG. Every picture upload, in iPro and in the builder, judged a file by its name and then
// found its first bytes did not match the name, and turned it away ("That file does not contain a
// valid supported image."). Pictures saved from the web often carry the wrong ending (a PNG or WebP
// named .jpg, a JPEG named .jfif). A picture is now judged by what it is: JPEG, PNG, GIF or WebP is
// taken and stored under the ending and type of what it really is; anything else is refused with a
// sentence that says what the file is.
public class UploadedPictures585Tests
{
    private static readonly byte[] PngBytes = Gallery535Tests.Png(40, 30, transparent: false);
    private static readonly byte[] JpegBytes = Gallery535Tests.Jpeg(40, 30);
    private static readonly byte[] Bmp = Bytes("BM", 0x36, 0, 0, 0, 0, 0, 0, 0, 0x36, 0, 0, 0, 0x28, 0);

    [Fact]
    public void A_picture_is_what_its_first_bytes_say_whatever_its_name()
    {
        Assert.Equal(("image/png", ".png"), Kind(PngBytes));
        Assert.Equal(("image/jpeg", ".jpg"), Kind(JpegBytes));
        Assert.Equal(("image/gif", ".gif"), Kind(Bytes("GIF89a", 1, 0, 1, 0, 0, 0, 0)));
        Assert.Equal(("image/webp", ".webp"), Kind(Bytes("RIFF", 30, 0, 0, 0, "WEBPVP8 ")));
        Assert.Null(UploadedPicture.Of(Bmp));

        // Stored under the ending of what it is; a JPEG's own ".jpeg" stays.
        Assert.Equal("logo.png", UploadedPicture.StoredName("logo.jpg", UploadedPicture.Of(PngBytes)!));
        Assert.Equal("photo.jpg", UploadedPicture.StoredName("photo.jfif", UploadedPicture.Of(JpegBytes)!));
        Assert.Equal("photo.jpg", UploadedPicture.StoredName("photo.JPE", UploadedPicture.Of(JpegBytes)!));
        Assert.Equal("photo.jpeg", UploadedPicture.StoredName("photo.jpeg", UploadedPicture.Of(JpegBytes)!));
        Assert.Equal("picture.png", UploadedPicture.StoredName("", UploadedPicture.Of(PngBytes)!));
    }

    [Theory]
    [InlineData("BMP", "This file is a BMP picture. Save it as PNG or JPG and try again.")]
    [InlineData("SVG", "This file is an SVG drawing. Save it as PNG or JPG and try again.")]
    [InlineData("PDF", "This file is a PDF document. Save it as PNG or JPG and try again.")]
    [InlineData("HEIC", "This file is a HEIC photo (an iPhone's usual format). Save it as PNG or JPG and try again.")]
    [InlineData("TIFF", "This file is a TIFF picture. Save it as PNG or JPG and try again.")]
    [InlineData("TEXT", "This file is not a JPG, PNG, GIF or WebP picture. Save it as PNG or JPG and try again.")]
    public void Anything_else_is_refused_saying_what_it_is(string kind, string sentence)
    {
        var bytes = kind switch
        {
            "BMP" => Bmp,
            "SVG" => Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?>\n<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>"),
            "PDF" => Encoding.ASCII.GetBytes("%PDF-1.7\n%..."),
            "HEIC" => Bytes(0, 0, 0, 0x18, "ftypheic", 0, 0, 0, 0),
            "TIFF" => Bytes("II*", 0, 8, 0, 0, 0, 0, 0, 0, 0),
            _ => Encoding.ASCII.GetBytes("just some words, not a picture")
        };
        Assert.Null(UploadedPicture.Of(bytes));
        Assert.Equal(sentence, UploadedPicture.Refusal(bytes));
    }

    [Fact]
    public async Task The_media_library_takes_a_png_named_jpg_and_a_jpeg_named_jfif_as_what_they_are()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var site = await Gallery535Tests.SeedGalleryAsync(db);
        var blob = new Gallery535Tests.MemoryBlob();

        // The owner's logo.jpg: a PNG, sent by the browser as image/jpeg because of its name.
        var controller = Gallery535Tests.NewPagesController(db, site.AgentId, blob);
        await controller.UploadImage(site.PageId, Upload(PngBytes, "logo.jpg", "image/jpeg"));
        Assert.Equal("Image uploaded and added to your media library.", (string?)controller.TempData["Success"]);
        controller = Gallery535Tests.NewPagesController(db, site.AgentId, blob);
        await controller.UploadImage(site.PageId, Upload(JpegBytes, "photo.jfif", "image/pjpeg"));
        Assert.Equal("Image uploaded and added to your media library.", (string?)controller.TempData["Success"]);

        var stored = await db.WebsiteMediaAssets.AsNoTracking().OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(new[] { "image/png", "image/jpeg" }, stored.Select(a => a.ContentType));
        Assert.EndsWith("_logo.png", stored[0].BlobUrl);
        Assert.EndsWith("_photo.jpg", stored[1].BlobUrl);
        Assert.Equal("image/png", blob.ContentTypes[stored[0].BlobUrl]);

        // Anything else is refused, saying what it is; nothing is stored.
        controller = Gallery535Tests.NewPagesController(db, site.AgentId, blob);
        await controller.UploadImage(site.PageId, Upload(Bmp, "scan.jpg", "image/jpeg"));
        Assert.Equal("This file is a BMP picture. Save it as PNG or JPG and try again.", (string?)controller.TempData["Error"]);
        Assert.Equal(2, await db.WebsiteMediaAssets.CountAsync());
    }

    [Fact]
    public async Task A_gallery_takes_a_png_named_jpg_and_says_what_a_bmp_is()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        var site = await Gallery535Tests.SeedGalleryAsync(db);
        var blob = new Gallery535Tests.MemoryBlob();
        var controller = Gallery535Tests.NewPagesController(db, site.AgentId, blob);
        await controller.UploadGalleryImages(site.BlockId, new List<IFormFile> { Upload(PngBytes, "logo.jpg", "image/jpeg", "images"), Upload(Bmp, "scan.jpg", "image/jpeg", "images") });
        db.ChangeTracker.Clear();
        var json = await db.WebsiteContentBlocks.AsNoTracking().Where(b => b.Id == site.BlockId).Select(b => b.SettingsJson).SingleAsync();
        Assert.Single(IPRO.Entities.WebsiteGallerySettings.FromJson(json).Images);
        Assert.Contains("scan.jpg (a BMP picture: save it as PNG or JPG)", (string?)controller.TempData["Error"] ?? (string?)controller.TempData["Success"] ?? string.Empty);
    }

    [Fact]
    public async Task SuperAdmins_artwork_upload_takes_a_png_named_jpg_as_a_png()
    {
        var blob = new Gallery535Tests.MemoryBlob();
        var services = new ServiceCollection().AddSingleton<IBlobStorageService>(blob).BuildServiceProvider();
        var result = await IPRO.Admin.Infrastructure.AdminImageUpload.TryUploadAsync(Upload(PngBytes, "card.jpg", "image/jpeg"), services, "ecard-designs", "design");
        Assert.True(result.Ok, result.Error);
        Assert.EndsWith("_design.png", result.Url);
        Assert.Equal("image/png", blob.ContentTypes[result.Url!]);

        var refused = await IPRO.Admin.Infrastructure.AdminImageUpload.TryUploadAsync(Upload(Bmp, "card.jpg", "image/jpeg"), services, "ecard-designs", "design");
        Assert.False(refused.Ok);
        Assert.Equal("This file is a BMP picture. Save it as PNG or JPG and try again.", refused.Error);
    }

    private static (string, string) Kind(byte[] bytes)
    {
        var kind = UploadedPicture.Of(bytes);
        Assert.NotNull(kind);
        return (kind!.ContentType, kind.Extension);
    }

    private static IFormFile Upload(byte[] bytes, string name, string contentType, string field = "image") =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, field, name) { Headers = new HeaderDictionary(), ContentType = contentType };

    private static byte[] Bytes(params object[] parts)
    {
        var bytes = new List<byte>();
        foreach (var part in parts)
        {
            if (part is string text) bytes.AddRange(Encoding.ASCII.GetBytes(text));
            else bytes.Add(Convert.ToByte(part));
        }
        while (bytes.Count < 32) bytes.Add(0);
        return bytes.ToArray();
    }
}
