using System;
using System.IO;
using IPRO.Entities;
using Xunit;

namespace IPRO.IntegrationTests;

// 559 (2026-10-09). The owner, after entering the bakery's hours: "The setup of the hours is in the
// footer but it shows in contact us and come and see us. How does that work ... I think it is
// confusing." The card was on the Footer page because the phone and the address are there; once the
// footer line became a choice that is off (557), the hours were entered on "Footer" and shown
// everywhere but the footer. The card is on My Website now ("Business hours"), each place the hours
// show is a choice, and the two blocks that show them say where they come from.
public class HoursPlaces559Tests
{
    [Fact]
    public void Each_place_is_a_choice_and_hours_saved_earlier_keep_showing_where_they_did()
    {
        // The bakery's hours as 557 stored them: no place switches except the footer's.
        var before = WebsiteFooterSettings.FromJson("{\"Hours\":{\"Days\":[{\"Day\":2,\"Opens\":\"08:30\",\"Closes\":\"18:00\"}],\"Note\":\"\",\"ShowInFooter\":false}}").Hours;
        Assert.True(before.IsSet);
        Assert.True(before.ShowOnContact);
        Assert.True(before.ShowOnAbout);
        Assert.True(before.ShowOpenNow);
        Assert.False(before.ShowInFooter);

        // Switched off, they stay off through a save and a read.
        var hours = WebsiteBusinessHours.FromEntries(new[] { (DayOfWeek.Tuesday, true, (string?)"08:30", (string?)"18:00") }, null, out _);
        hours.ShowOnContact = false; hours.ShowOnAbout = false; hours.ShowOpenNow = false; hours.ShowInFooter = true;
        var back = WebsiteFooterSettings.FromJson(new WebsiteFooterSettings { Hours = hours }.ToJson()).Hours.Normalized();
        Assert.False(back.ShowOnContact);
        Assert.False(back.ShowOnAbout);
        Assert.False(back.ShowOpenNow);
        Assert.True(back.ShowInFooter);
        Assert.True(back.IsSet);                                   // search engines are still told

        var card = Read(@"src\IPRO.Web\Views\PublicWebsite\_BusinessHours.cshtml");
        Assert.Contains("@if (hours.IsSet && ((ViewData[\"HoursPlace\"] as string) == \"contact\" ? hours.ShowOnContact : hours.ShowOnAbout))", card);
        Assert.Contains("@if (status.Text.Length > 0 && hours.ShowOpenNow)", Read(@"src\IPRO.Web\Views\PublicWebsite\_OpenNow.cshtml"));
        foreach (var design in new[] { "_ModernManagedPage", "_ClassicManagedPage", "_EditorialManagedPage" })
        {
            var view = Read($@"src\IPRO.Web\Views\PublicWebsite\{design}.cshtml");
            // One call for the "about us" card, one for the contact block that says which it is.
            Assert.Equal(1, Count(view, "@await Html.PartialAsync(\"_BusinessHours\", Model)"));
            Assert.Equal(1, Count(view, "@await Html.PartialAsync(\"_BusinessHours\", Model, new ViewDataDictionary(ViewData) { [\"HoursPlace\"] = \"contact\" })"));
        }
        // The hours in the search-engine details do not ask a switch.
        Assert.DoesNotContain("ShowO", Read(@"src\IPRO.Web\Views\PublicWebsite\_PublicSeoHead.cshtml"));
    }

    [Fact]
    public void The_hours_are_entered_on_My_Website_and_the_footer_page_points_there()
    {
        var page = Read(@"src\IPRO.Web\Views\Website\Index.cshtml");
        Assert.Contains("@await Html.PartialAsync(\"_BusinessHoursEditor\", IPRO.Entities.WebsiteFooterSettings.FromJson(Model.FooterSettingsJson))", page);

        var editor = Read(@"src\IPRO.Web\Views\Website\_BusinessHoursEditor.cshtml");
        Assert.StartsWith("@model IPRO.Entities.WebsiteFooterSettings", editor);
        Assert.Contains("<form method=\"post\" action=\"/portal/WebsitePages/SaveHours\" class=\"card border-0 shadow-sm mt-4\" id=\"hours\">", editor);
        Assert.Contains("@Html.AntiForgeryToken()", editor);
        Assert.Contains("Business hours</h5>", editor);
        Assert.Contains("<legend class=\"form-label fw-semibold fs-6 mb-1\">Where your hours show</legend>", editor);
        foreach (var (name, flag) in new[] { ("showHoursOnContact", "ShowOnContact"), ("showHoursOnAbout", "ShowOnAbout"), ("showOpenNow", "ShowOpenNow"), ("showHoursInFooter", "ShowInFooter") })
            Assert.Contains($"name=\"{name}\" value=\"true\" checked=\"@Model.Hours.{flag}\"", editor);
        Assert.DoesNotContain("Model.Footer", editor);

        var controller = Read(@"src\IPRO.Web\Controllers\WebsitePagesController.cs");
        Assert.Contains("bool showHoursOnContact = false, bool showHoursOnAbout = false, bool showOpenNow = false)", controller);
        Assert.Contains("hours.ShowOnContact = showHoursOnContact;", controller);
        Assert.Contains("hours.ShowOnAbout = showHoursOnAbout;", controller);
        Assert.Contains("hours.ShowOpenNow = showOpenNow;", controller);
        Assert.Contains("return Redirect(\"/portal/Website#hours\");", controller);

        // The Footer page no longer has the card, and says where it went.
        var footerPage = Read(@"src\IPRO.Web\Views\WebsitePages\Footer.cshtml");
        Assert.DoesNotContain("SaveHours", footerPage);
        Assert.DoesNotContain("hours-row", footerPage);
        Assert.Contains("<a href=\"/portal/Website#hours\">My Website &gt; Business hours</a>", footerPage);

        // The two blocks that show the hours say where they come from.
        var blocks = Read(@"src\IPRO.Web\Views\WebsitePages\Edit.cshtml");
        Assert.Contains("Your opening hours show on this card when you have set them.", blocks);
        Assert.Contains("Your opening hours show beside this form when you have set them.", blocks);
        Assert.Equal(2, Count(blocks, "under <a href=\"/portal/Website#hours\">My Website &gt; Business hours</a>."));

        var guide = Read(@"DOCS\04_WEBSITE_BUILDER.md");
        Assert.Contains("2. Scroll to **Business hours**, under Website Settings.", guide);
        Assert.Contains("Under **Where your hours show**, tick the places you want", guide);
    }

    private static int Count(string text, string part) => (text.Length - text.Replace(part, string.Empty).Length) / part.Length;

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative)).Replace("\r\n", "\n");
    }
}
