using System;
using System.IO;
using System.Linq;
using IPRO.Entities;
using Xunit;

namespace IPRO.IntegrationTests;

// 557 (2026-10-08). The owner's first real use of 556's Hours card, for the bakery:
//   1. "why is not saving?" -- every box read "08:30 --": the browser's time box had its digits but no
//      AM/PM, and the browser refused the form, pointing at one box. The times are dropdowns now.
//   2. "I dont want it to be in the footer? It is ugly" -- the footer line is a choice, off unless ticked,
//      so a site that saved hours before 557 loses the line without anyone doing anything.
public class HoursEditor557Tests
{
    [Fact]
    public void A_time_is_picked_from_a_list_and_cannot_be_half_entered()
    {
        var choices = WebsiteBusinessHours.TimeChoices();
        Assert.Equal(96, choices.Count);                                   // every quarter hour
        Assert.Equal(("00:00", "midnight"), choices[0]);
        Assert.Equal(("08:30", "8:30 a.m."), choices.Single(c => c.Value == "08:30"));
        Assert.Equal(("12:00", "noon"), choices.Single(c => c.Value == "12:00"));
        Assert.Equal(("23:45", "11:45 p.m."), choices[95]);
        // Every choice is a time the save accepts.
        Assert.All(choices, c => Assert.Equal(c.Value, WebsiteDayHours.Clean(c.Value)));

        // The bakery's Tuesday was saved as 8:39: it is offered, in its place, so it is seen and can be corrected.
        var withOdd = WebsiteBusinessHours.TimeChoices("08:39");
        Assert.Equal(97, withOdd.Count);
        Assert.Equal(new[] { "08:30", "08:39", "08:45" }, withOdd.SkipWhile(c => c.Value != "08:30").Take(3).Select(c => c.Value));
        Assert.Equal(96, WebsiteBusinessHours.TimeChoices("08:30").Count);
        Assert.Equal(96, WebsiteBusinessHours.TimeChoices("not a time").Count);

        var editor = Read(@"src\IPRO.Web\Views\Website\_BusinessHoursEditor.cshtml");   // 559: the card moved to My Website
        Assert.DoesNotContain("type=\"time\"", editor);
        Assert.Contains("<select class=\"form-select\" id=\"opens_@key\" name=\"opens_@key\">@foreach (var time in IPRO.Entities.WebsiteBusinessHours.TimeChoices(opensNow))", editor);
        Assert.Contains("<select class=\"form-select\" id=\"closes_@key\" name=\"closes_@key\">@foreach (var time in IPRO.Entities.WebsiteBusinessHours.TimeChoices(closesNow))", editor);
        // A closed day, once ticked, starts at whole hours rather than at nothing.
        Assert.Contains("var opensNow = row.IsOpen ? row.Opens : IPRO.Entities.WebsiteBusinessHours.DefaultOpens;", editor);
        Assert.Equal(("09:00", "17:00"), (WebsiteBusinessHours.DefaultOpens, WebsiteBusinessHours.DefaultCloses));
        // The copy button and the closed toggle read the dropdowns.
        Assert.Contains("var times = open[0].querySelectorAll('select');", editor);
        Assert.Contains(".hours-row.is-closed .hours-row__times select,", editor);
    }

    [Fact]
    public void The_footer_line_is_a_choice_and_off_unless_ticked()
    {
        // Hours saved before 557 (the bakery's): no footer line.
        var before = WebsiteFooterSettings.FromJson("{\"Hours\":{\"Days\":[{\"Day\":2,\"Opens\":\"08:30\",\"Closes\":\"18:00\"}],\"Note\":\"\"}}");
        Assert.True(before.Hours.IsSet);
        Assert.False(before.Hours.ShowInFooter);

        var hours = WebsiteBusinessHours.FromEntries(new[] { (DayOfWeek.Tuesday, true, (string?)"08:30", (string?)"18:00") }, null, out _);
        Assert.False(hours.ShowInFooter);
        hours.ShowInFooter = true;
        var back = WebsiteFooterSettings.FromJson(new WebsiteFooterSettings { Hours = hours }.ToJson());
        Assert.True(back.Hours.ShowInFooter);                              // kept through a save and a read
        Assert.True(back.Hours.Normalized().ShowInFooter);

        var footer = Read(@"src\IPRO.Web\Views\PublicWebsite\_PublicFooterContent.cshtml");
        Assert.Contains("@if (footer.Hours.IsSet && footer.Hours.ShowInFooter)", footer);
        var controller = Read(@"src\IPRO.Web\Controllers\WebsitePagesController.cs");
        Assert.Contains("hours.ShowInFooter = showHoursInFooter;", controller);
        var editor = Read(@"src\IPRO.Web\Views\Website\_BusinessHoursEditor.cshtml");   // 559: the card moved to My Website
        Assert.Contains("name=\"showHoursInFooter\" value=\"true\" checked=\"@Model.Hours.ShowInFooter\"", editor);
        Assert.DoesNotContain("They show at the bottom of every page", editor);
        // The cards and the line under the button do not ask.
        Assert.DoesNotContain("ShowInFooter", Read(@"src\IPRO.Web\Views\PublicWebsite\_BusinessHours.cshtml"));
        Assert.DoesNotContain("ShowInFooter", Read(@"src\IPRO.Web\Views\PublicWebsite\_OpenNow.cshtml"));
        Assert.Contains("the footer starts unticked", Read(@"DOCS\04_WEBSITE_BUILDER.md"));   // 559 reworded the step
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative)).Replace("\r\n", "\n");
    }
}
