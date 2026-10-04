using System;
using System.IO;
using Xunit;

namespace IPRO.IntegrationTests;

// 547 (2026-10-04). On his computer the owner saw the e-card picker five designs a row and the live
// preview cut off -- a 420 px window onto a 620 px card showed half the picture and none of the contact
// block: "can u make it 4 so we could see the live preview in whole". The picker takes 7 of 12 columns
// (four designs a row on a wide screen) and the preview draws the card as a mail app would at full
// width, measures it, and scales it to fit both its column and the window.
public class ECardPreview547Tests
{
    [Fact]
    public void The_picker_gives_the_preview_room_and_four_designs_a_row_on_a_wide_screen()
    {
        var view = Read(@"src\IPRO.Web\Views\ECards\Create.cshtml");

        Assert.Contains("<div class=\"col-lg-7\">", view);
        Assert.Contains("<div class=\"col-lg-5\">", view);
        Assert.DoesNotContain("col-lg-8", view);
        Assert.DoesNotContain("col-lg-4", view);
        Assert.Contains("@@media (min-width: 1400px) { .occasion-grid { grid-template-columns: repeat(4, minmax(0, 1fr)); } }", view);
    }

    [Fact]
    public void The_preview_shows_the_whole_card_scaled_to_fit_the_column_and_the_window()
    {
        var view = Read(@"src\IPRO.Web\Views\ECards\Create.cshtml");

        Assert.DoesNotContain("height:420px;border:1px solid #e2e8f0", view);           // the fixed window onto the card
        Assert.Contains("<iframe id=\"cardPreview\" title=\"Card preview\" scrolling=\"no\"></iframe>", view);
        Assert.Contains("preview.addEventListener('load', fitPreview);", view);
        Assert.Contains("window.addEventListener('resize', fitPreview);", view);
        Assert.Contains("var scale = Math.min(1, (previewArea.clientWidth - 2) / CARD_VIEW_WIDTH, room / height);", view);
        Assert.Contains("preview.style.transform = 'scale(' + scale + ')';", view);
        Assert.Contains(".preview-box iframe { position: absolute; top: 0; left: 0; width: 100%; height: 100%; border: 0; transform-origin: 0 0; }", view);

        // The width the card is measured at is the widest card the composer draws, with its margins.
        Assert.Contains("var CARD_VIEW_WIDTH = 668;", view);
        Assert.Contains("private const int MaxCardWidth = 620;", Read(@"src\IPRO.Business\Services\ECardHtmlComposer.cs"));
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
