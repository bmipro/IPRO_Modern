using System;
using System.IO;
using Xunit;

namespace IPRO.IntegrationTests;

// 587 (2026-10-17): the owner, looking at Website Analytics: "on top of each blue column there should be a
// number ... the way it is not informing right away". Each day's page views now stand on top of its bar
// (the bar measures views; the visitors stay in the bar's tooltip), and the tallest bar leaves room for it.
public class AnalyticsBarLabels587Tests
{
    [Fact]
    public void Each_bar_carries_its_page_views_on_top()
    {
        var view = Read(@"src\IPRO.Web\Views\WebsiteAnalytics\Index.cshtml");

        Assert.Contains("<strong class=\"analytics-bar-value\">@point.Views.ToString(\"N0\", System.Globalization.CultureInfo.GetCultureInfo(\"en-CA\"))</strong>", view);
        // The number comes before the bar in the column, so it sits on top of it.
        Assert.True(view.IndexOf("class=\"analytics-bar-value\"", StringComparison.Ordinal) < view.IndexOf("<div class=\"analytics-bar\"", StringComparison.Ordinal));
        // Room above the tallest bar, and the tooltip still names the visitors.
        Assert.Contains("var height = Math.Max(4m, point.Views * 82m / maxDailyViews);", view);
        Assert.Contains("title=\"@point.Date.ToString(\"MMM d\"): @point.Views views, @point.Visitors visitors\"", view);
        Assert.Contains(".analytics-bar-value{", view);
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
