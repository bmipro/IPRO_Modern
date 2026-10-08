using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using IPRO.Entities;
using IPRO.Web.Infrastructure;
using Xunit;

namespace IPRO.IntegrationTests;

// 556 (2026-10-07). The owner: "How do you suggest the bakery would display their hours of operation?"
// The bakery's site showed no hours anywhere and the platform had nowhere to put them. Hours are now
// entered once (My Website > Footer > Hours) and shown in the footer, on the contact and "about us"
// cards, as an "Open now" line under a page's first button, and to search engines. Found on the way:
// the consent line under the bakery's contact form said "this adviser".
public class BusinessHours556Tests
{
    private static readonly string Dash = WebsiteBusinessHours.Dash.ToString();
    private static readonly string Dot = WebsiteBusinessHours.Dot.ToString();

    // Closed Monday; Tuesday to Saturday 7:30 to 6; Sunday 8 to 4.
    private static WebsiteBusinessHours Bakery() => WebsiteBusinessHours.FromEntries(
        WebsiteBusinessHours.WeekOrder.Select(day => day switch
        {
            DayOfWeek.Monday => (day, false, (string?)"07:30", (string?)"18:00"),
            DayOfWeek.Sunday => (day, true, "08:00", "16:00"),
            _ => (day, true, "07:30", "18:00"),
        }), "Closed on statutory holidays", out _);

    private static DateTime At(DayOfWeek day, int hour, int minute = 0)
    {
        var date = new DateTime(2026, 10, 5);                       // a Monday
        while (date.DayOfWeek != day) date = date.AddDays(1);
        return date.AddHours(hour).AddMinutes(minute);
    }

    [Fact]
    public void The_week_reads_the_way_a_shop_door_has_it()
    {
        var hours = Bakery();
        Assert.True(hours.IsSet);
        // The footer: neighbouring days with the same hours share a line.
        Assert.Equal(new[]
        {
            "Mon Closed",
            $"Tue{Dash}Sat 7:30 a.m. {Dash} 6 p.m.",
            $"Sun 8 a.m. {Dash} 4 p.m.",
        }, hours.Lines());
        // The cards: seven rows, Monday first.
        var week = hours.Week();
        Assert.Equal(7, week.Count);
        Assert.Equal(("Monday", "Closed", false), (week[0].Name, week[0].Text, week[0].Open));
        Assert.Equal(("Sunday", $"8 a.m. {Dash} 4 p.m.", true), (week[6].Name, week[6].Text, week[6].Open));

        Assert.Equal("noon", WebsiteBusinessHours.Clock("12:00"));
        Assert.Equal("midnight", WebsiteBusinessHours.Clock("00:00"));
        Assert.Equal("12:30 a.m.", WebsiteBusinessHours.Clock("00:30"));
        Assert.Equal("12:05 p.m.", WebsiteBusinessHours.Clock("12:05"));
        Assert.Equal("9 a.m.", WebsiteBusinessHours.Clock("9:00"));
        Assert.Equal(string.Empty, WebsiteBusinessHours.Clock("25:00"));
        Assert.Equal(string.Empty, WebsiteBusinessHours.Clock("soon"));
    }

    [Fact]
    public void Open_now_is_answered_on_the_business_own_clock()
    {
        var hours = Bakery();
        Assert.Equal((true, $"Open now {Dot} closes 6 p.m."), hours.StatusAt(At(DayOfWeek.Wednesday, 10)));
        Assert.Equal((true, $"Open now {Dot} closes 6 p.m."), hours.StatusAt(At(DayOfWeek.Wednesday, 7, 30)));   // the minute the door opens
        Assert.Equal((false, $"Closed now {Dot} opens 7:30 a.m."), hours.StatusAt(At(DayOfWeek.Wednesday, 6)));
        Assert.Equal((false, $"Closed now {Dot} opens tomorrow 7:30 a.m."), hours.StatusAt(At(DayOfWeek.Wednesday, 18)));   // closing time is closed
        Assert.Equal((false, $"Closed now {Dot} opens tomorrow 8 a.m."), hours.StatusAt(At(DayOfWeek.Saturday, 21)));
        // Sunday evening: Monday is closed, so the next opening is named by its day.
        Assert.Equal((false, $"Closed now {Dot} opens Tuesday 7:30 a.m."), hours.StatusAt(At(DayOfWeek.Sunday, 17)));
        Assert.Equal((false, $"Closed now {Dot} opens tomorrow 7:30 a.m."), hours.StatusAt(At(DayOfWeek.Monday, 12)));

        // One day a week: the next opening is that same day next week.
        var saturdays = WebsiteBusinessHours.FromEntries(new[] { (DayOfWeek.Saturday, true, (string?)"09:00", (string?)"13:00") }, null, out _);
        Assert.Equal((false, $"Closed now {Dot} opens Saturday 9 a.m."), saturdays.StatusAt(At(DayOfWeek.Saturday, 14)));
        Assert.Equal((false, $"Closed now {Dot} opens tomorrow 9 a.m."), saturdays.StatusAt(At(DayOfWeek.Friday, 14)));

        // No hours: nothing is said, anywhere.
        Assert.Equal((false, string.Empty), new WebsiteBusinessHours().StatusAt(At(DayOfWeek.Wednesday, 10)));
        Assert.False(new WebsiteBusinessHours().IsSet);

        // The clock is the owner's time zone, not the server's.
        var view = Read(@"src\IPRO.Web\Infrastructure\BusinessHoursClock.cs");
        Assert.Contains("AgentLocalTime.FromUtc(DateTime.UtcNow, agent?.TimeZone)", view);
    }

    [Fact]
    public void A_closing_time_before_the_opening_time_runs_past_midnight()
    {
        // Friday 6 p.m. to 2 a.m.
        var bar = WebsiteBusinessHours.FromEntries(new[] { (DayOfWeek.Friday, true, (string?)"18:00", (string?)"02:00") }, null, out var halfSet);
        Assert.Empty(halfSet);
        Assert.Equal((true, $"Open now {Dot} closes 2 a.m."), bar.StatusAt(At(DayOfWeek.Friday, 23)));
        Assert.Equal((true, $"Open now {Dot} closes 2 a.m."), bar.StatusAt(At(DayOfWeek.Saturday, 1)));    // still Friday's night
        Assert.False(bar.StatusAt(At(DayOfWeek.Saturday, 2)).Open);
        Assert.False(bar.StatusAt(At(DayOfWeek.Friday, 1)).Open);                                          // Thursday had no night
        Assert.Equal((false, $"Closed now {Dot} opens 6 p.m."), bar.StatusAt(At(DayOfWeek.Friday, 12)));
    }

    [Fact]
    public void A_day_without_both_times_is_closed_and_the_owner_is_told()
    {
        var hours = WebsiteBusinessHours.FromEntries(new[]
        {
            (DayOfWeek.Monday, true, (string?)"09:00", (string?)""),          // no closing time
            (DayOfWeek.Tuesday, true, "09:00", "09:00"),                      // the same time twice
            (DayOfWeek.Wednesday, true, "9am", "5pm"),                        // not times
            (DayOfWeek.Thursday, false, "09:00", "17:00"),                    // times, but not ticked open
            (DayOfWeek.Friday, true, "9:00", "17:00"),
        }, "  " + new string('x', 300), out var halfSet);

        Assert.Equal(new[] { "Monday", "Tuesday", "Wednesday" }, halfSet);
        Assert.Equal(7, hours.Days.Count);                                    // always a whole week, Monday first
        Assert.Equal((int)DayOfWeek.Monday, hours.Days[0].Day);
        Assert.Equal(new[] { "Friday" }, hours.Week().Where(d => d.Open).Select(d => d.Name));
        Assert.Equal("09:00", hours.For(DayOfWeek.Friday).Opens);             // stored as HH:mm
        Assert.Equal(string.Empty, hours.For(DayOfWeek.Thursday).Opens);
        Assert.Equal(WebsiteBusinessHours.NoteMaxLength, hours.Note.Length);
    }

    [Fact]
    public void Hours_live_beside_the_phone_and_the_address_and_an_older_site_has_none()
    {
        // A footer saved before 556.
        var older = WebsiteFooterSettings.FromJson("{\"Phone\":\"(416)-886-0458\",\"SocialLinks\":[]}");
        Assert.False(older.Hours.IsSet);
        Assert.Equal(string.Empty, older.BusinessKind);
        Assert.Equal(7, older.Hours.Days.Count);

        var settings = new WebsiteFooterSettings { Phone = "(416)-886-0458", Hours = Bakery(), BusinessKind = "Bakery" };
        var back = WebsiteFooterSettings.FromJson(settings.ToJson());
        Assert.Equal("(416)-886-0458", back.Phone);
        Assert.Equal(Bakery().Lines(), back.Hours.Lines());
        Assert.Equal("Closed on statutory holidays", back.Hours.Note);
        Assert.Equal("Bakery", back.BusinessKind);
        // Only what the form posts is stored for a day.
        Assert.DoesNotContain("IsOpen", settings.ToJson());
        Assert.DoesNotContain("RunsPastMidnight", settings.ToJson());

        // Hand-edited or damaged values cannot reach a page.
        var odd = WebsiteFooterSettings.FromJson("{\"BusinessKind\":\"<script>\",\"Hours\":{\"Days\":[{\"Day\":2,\"Opens\":\"x\",\"Closes\":\"18:00\"}],\"Note\":null}}");
        Assert.Equal(string.Empty, odd.BusinessKind);
        Assert.False(odd.Hours.IsSet);
        Assert.Equal(string.Empty, odd.Hours.Note);

        var controller = Read(@"src\IPRO.Web\Controllers\WebsitePagesController.cs");
        Assert.Contains("public async Task<IActionResult> SaveHours(string? hoursNote, string? businessKind)", controller);
        Assert.Contains("settings.BusinessKind = WebsiteBusinessKinds.Normalize(businessKind);", controller);
        var saveHours = controller.Substring(controller.IndexOf("public async Task<IActionResult> SaveHours", StringComparison.Ordinal) - 60, 60);
        Assert.Contains("[HttpPost, ValidateAntiForgeryToken]", saveHours);
    }

    [Fact]
    public void Search_engines_are_told_the_kind_of_business_and_its_hours()
    {
        var schema = Bakery().ToSchema();
        Assert.Equal(2, schema.Count);                                        // two sets of hours; Monday is left out
        Assert.Equal("OpeningHoursSpecification", schema[0]["@type"]);
        Assert.Equal(new[] { "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" }, (string[])schema[0]["dayOfWeek"]!);
        Assert.Equal(("07:30", "18:00"), ((string)schema[0]["opens"]!, (string)schema[0]["closes"]!));
        Assert.Equal(new[] { "Sunday" }, (string[])schema[1]["dayOfWeek"]!);
        Assert.Contains("\"opens\":\"08:00\"", JsonSerializer.Serialize(schema));

        // Every site said "ProfessionalService" before; one that has not chosen still does.
        Assert.Equal("ProfessionalService", WebsiteBusinessKinds.SchemaType(""));
        Assert.Equal("ProfessionalService", WebsiteBusinessKinds.SchemaType("Anything"));
        Assert.Equal("Bakery", WebsiteBusinessKinds.SchemaType("bakery"));
        Assert.Equal("SportsActivityLocation", WebsiteBusinessKinds.SchemaType("SportsActivityLocation"));
        Assert.Equal(string.Empty, WebsiteBusinessKinds.Options[0].Value);
        Assert.Equal(WebsiteBusinessKinds.Options.Count, WebsiteBusinessKinds.Options.Select(o => o.Value).Distinct().Count());

        var head = Read(@"src\IPRO.Web\Views\PublicWebsite\_PublicSeoHead.cshtml");
        Assert.Contains("[\"@type\"] = IPRO.Entities.WebsiteBusinessKinds.SchemaType(businessDetails.BusinessKind),", head);
        // Added only when there are hours: a null entry in a dictionary would be written out as null.
        Assert.Contains("if (businessDetails.Hours.IsSet) structuredData[\"openingHoursSpecification\"] = businessDetails.Hours.ToSchema();", head);
        Assert.DoesNotContain("ToSchema() : null", head);
    }

    [Fact]
    public void All_three_designs_show_the_hours_in_the_same_places()
    {
        const string hours = "@await Html.PartialAsync(\"_BusinessHours\", Model)";
        const string openNow = "@await Html.PartialAsync(\"_OpenNow\", Model)";
        foreach (var design in new[] { "_ModernManagedPage", "_ClassicManagedPage", "_EditorialManagedPage" })
        {
            var view = Read($@"src\IPRO.Web\Views\PublicWebsite\{design}.cshtml");
            Assert.Equal(2, Count(view, hours));       // the "about us" card and the contact block
            Assert.Equal(1, Count(view, openNow));     // under the banner's button
        }
        // Each renders nothing until hours are set.
        var card = Read(@"src\IPRO.Web\Views\PublicWebsite\_BusinessHours.cshtml");
        Assert.Contains("@if (hours.IsSet)", card);
        Assert.Contains("<tr class=\"@(day.Day == now.DayOfWeek ? \"is-today\" : null)\"><th scope=\"row\">@day.Name</th><td>@day.Text</td></tr>", card);
        Assert.Contains("@if (status.Text.Length > 0)", Read(@"src\IPRO.Web\Views\PublicWebsite\_OpenNow.cshtml"));

        var footer = Read(@"src\IPRO.Web\Views\PublicWebsite\_PublicFooterContent.cshtml");
        Assert.Contains("@if (footer.Hours.IsSet)", footer);
        Assert.Contains("@foreach (var line in footer.Hours.Lines()) { <span>@line</span> }", footer);
        // One stylesheet for the three designs.
        Assert.Contains(".site-hours__week .is-today th, .site-hours__week .is-today td { font-weight: 700; }", Read(@"src\IPRO.Web\Views\PublicWebsite\_ManagedPageStyles.cshtml"));

        var editor = Read(@"src\IPRO.Web\Views\WebsitePages\Footer.cshtml");
        Assert.Contains("action=\"/portal/WebsitePages/SaveHours\"", editor);
        Assert.Contains("name=\"open_@key\" value=\"true\" checked=\"@row.IsOpen\"", editor);
        Assert.Contains("name=\"opens_@key\" value=\"@row.Opens\"", editor);
        Assert.Contains("name=\"closes_@key\" value=\"@row.Closes\"", editor);
        Assert.Contains("<script nonce=\"@Context.GetCspNonce()\">", editor);

        var guide = Read(@"DOCS\04_WEBSITE_BUILDER.md");
        Assert.Contains("## Show Your Hours", guide);
        Assert.Contains("\"Open now\" follows the time zone on your **Profile**", guide);
    }

    [Fact]
    public void A_form_names_the_business_it_goes_to()
    {
        Assert.Equal("L'Avenue Boulangerie Inc.", BusinessHoursClock.ConsentParty(new AgentUser { CompanyName = " L'Avenue Boulangerie Inc. ", FirstName = "Lena" }));
        Assert.Equal("Lena Baker", BusinessHoursClock.ConsentParty(new AgentUser { FirstName = "Lena", LastName = "Baker" }));
        Assert.Equal("this business", BusinessHoursClock.ConsentParty(new AgentUser()));
        Assert.Equal("this business", BusinessHoursClock.ConsentParty(null));

        var lead = Read(@"src\IPRO.Web\Views\PublicWebsite\_WebsiteLeadForm.cshtml");
        var custom = Read(@"src\IPRO.Web\Views\PublicWebsite\_WebsiteCustomForm.cshtml");
        Assert.Contains("$\"I agree that {consentParty} may use my information to respond to this request.\"", lead);
        Assert.Contains("$\"I agree that {consentParty} may use my information to follow up about this download.\"", lead);
        Assert.Contains("I agree that @(ViewData[\"ConsentParty\"] as string ?? \"this business\") may use my information", custom);
        Assert.DoesNotContain("this adviser may use", lead);
        Assert.DoesNotContain("this adviser may use", custom);
        // Both ways in set the name.
        Assert.Contains("ViewData[\"ConsentParty\"] = IPRO.Web.Infrastructure.BusinessHoursClock.ConsentParty(Model.Website.AgentUser);", Read(@"src\IPRO.Web\Views\PublicWebsite\Index.cshtml"));
        Assert.Contains("ViewData[\"ConsentParty\"] = IPRO.Web.Infrastructure.BusinessHoursClock.ConsentParty(agent);", Read(@"src\IPRO.Web\Views\PublicWebsite\StandaloneForm.cshtml"));
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
