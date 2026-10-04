using System;
using System.Linq;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.Entities;
using Xunit;

namespace IPRO.IntegrationTests;

// 548 (2026-10-04). E-cards and e-letters were drawn at a fixed 620 px, so a phone shrank the whole
// email to fit and the words came out about half size -- the greeting, the letter's text, the
// adviser's name and number. The invoice and portal emails (ClientLetter) already fill a phone's
// width, which is why they read well on the owner's phone. Asked for a recommendation, then "go ahead
// with the reflow for both": the card and the letter now fill the width up to 620 px, the picture
// scales, the words keep their size, and the contact details and the photo sit side by side where
// there is room and one above the other on a phone. Outlook on Windows ignores max-width and
// inline-block, so a table only it reads ([if mso]) keeps today's fixed layout there.
public class EmailReflow548Tests
{
    private const string MsoOpen620 = "<!--[if mso]><table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" border=\"0\" width=\"620\" align=\"center\"><tr><td><![endif]-->";
    private const string MsoClose = "<!--[if mso]></td></tr></table><![endif]-->";

    [Fact]
    public void A_card_fills_a_phones_width_up_to_its_own_and_Outlook_keeps_the_fixed_width()
    {
        var html = ECardHtmlComposer.Wrap(new ECard { Subject = "Happy birthday", Message = "Many happy returns." }, Agent(), Design("birthday-balloons"), "https://app.test");

        Assert.Contains("width=\"100%\" style=\"width:100%;max-width:620px;background:#111111;", html);
        Assert.DoesNotContain("width=\"620\" style=\"max-width:620px;", html);            // the old fixed card
        Assert.Contains(MsoOpen620, html);
        Assert.Contains(MsoClose, html);
        Assert.Contains("style=\"display:block;width:100%;max-width:620px;height:auto;border:0;\"", html);   // the picture scales

        // A narrower card keeps its own width as the most it grows to.
        var roses = ECardHtmlComposer.Wrap(new ECard(), Agent(), ECardDesignSeeder.BuildDefaults().Single(d => d.Key == "anniversary-1"), "https://app.test");
        Assert.Contains("style=\"width:100%;max-width:467px;", roses);
        Assert.Contains("width=\"467\" align=\"center\"><tr><td><![endif]-->", roses);
    }

    [Fact]
    public void The_contact_details_and_the_photo_sit_side_by_side_where_there_is_room_and_wrap_on_a_phone()
    {
        var html = ECardHtmlComposer.Wrap(new ECard(), Agent(), Design("birthday-balloons"), "https://app.test");

        // 620 - 2 x 34 padding - 140 photo - 2 slack = 410: side by side on a computer, the photo
        // under the details once the card is narrower.
        Assert.Contains("<div style=\"display:inline-block;width:100%;max-width:410px;vertical-align:top;\">", html);
        Assert.Contains("<div style=\"display:inline-block;width:140px;vertical-align:top;\">", html);
        Assert.Contains("<!--[if mso]></td><td width=\"140\" align=\"right\" valign=\"top\"><![endif]-->", html);
        Assert.Contains("<tr><td style=\"padding:0 34px 30px;font-size:0;\">", html);    // no gap between the two blocks
        Assert.Contains("style=\"display:block;margin-left:auto;width:132px;height:auto;border:3px solid #ffffff;\"", html);
        // A long address breaks instead of pushing the card wider than the phone.
        Assert.Contains("<td style=\"overflow-wrap:anywhere;word-break:break-word;\"><a href=\"mailto:pat548@example.test\"", html);

        // The matted Norooz card (480 px): 480 - 68 - 140 - 2.
        Assert.Contains("max-width:270px;", ECardHtmlComposer.Wrap(new ECard(), Agent(), Design("norooz-goldfish"), "https://app.test"));

        // No photo, no columns: the details alone.
        var noPhoto = Agent();
        noPhoto.PhotoUrl = string.Empty;
        var bare = ECardHtmlComposer.Wrap(new ECard(), noPhoto, Design("birthday-balloons"), "https://app.test");
        Assert.DoesNotContain("display:inline-block", bare);
        Assert.Contains("Pat Adviser", bare);
    }

    [Fact]
    public void A_letter_fills_a_phones_width_up_to_620_and_Outlook_keeps_the_fixed_width()
    {
        var html = ELetterHtmlComposer.Wrap(new ELetter { Body = "Dear client,\n\nThank you." }, Agent(), null);

        Assert.Contains("width=\"100%\" style=\"width:100%;max-width:620px;background:#ffffff;\"", html);
        Assert.DoesNotContain("width=\"620\" style=\"max-width:620px;", html);
        Assert.Contains(MsoOpen620, html);
        Assert.Contains(MsoClose, html);
        Assert.Contains("Thank you.", html);
    }

    [Fact]
    public void The_plain_text_parts_carry_none_of_the_Outlook_comments()
    {
        var card = ECardHtmlComposer.Wrap(new ECard { Subject = "Hello", Message = "A note." }, Agent(), Design("birthday-balloons"), "https://app.test");
        var letter = ELetterHtmlComposer.Wrap(new ELetter { Body = "Dear client" }, Agent(), null);

        // 542's derived text part drops comments; a provider sending only the HTML still reads it as text.
        Assert.DoesNotContain("mso", EmailPlainText.FromHtml(card));
        Assert.DoesNotContain("mso", EmailPlainText.FromHtml(letter));
        Assert.Contains("A note.", EmailPlainText.FromHtml(card));
    }

    [Fact]
    public void The_guides_say_what_a_phone_shows()
    {
        Assert.Contains("On a phone the card fills the screen's width", Read(@"DOCS\18_ECARDS.md"));
        Assert.Contains("On a phone the letter fills the screen's width", Read(@"DOCS\19_ELETTERS.md"));
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "IPRO.sln")))
            dir = System.IO.Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return System.IO.File.ReadAllText(System.IO.Path.Combine(dir!, relative));
    }

    // ---- harness -----------------------------------------------------------------------------------

    private static ECardDesign Design(string key) => ECardCollectionSeeder.BuildCollection().Single(d => d.Key == key);

    private static AgentUser Agent() => new()
    {
        FirstName = "Pat", LastName = "Adviser", CompanyName = "Global Business Solution",
        Email = "pat548@example.test", Phone = "416-555-0148", DomainName = "pat548.example.test",
        PhotoUrl = "https://iprostorage.blob.core.windows.net/photos/pat548.jpg"
    };
}
