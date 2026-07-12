namespace IPRO.Web.Models;

public class WebsiteAnalyticsViewModel
{
    public int PeriodDays { get; set; }
    public int TotalViews { get; set; }
    public int UniqueVisitors { get; set; }
    public int Leads { get; set; }
    public decimal ConversionRate { get; set; }
    public decimal ViewChangePercent { get; set; }
    public IReadOnlyCollection<WebsiteAnalyticsDailyPoint> DailyViews { get; set; } = Array.Empty<WebsiteAnalyticsDailyPoint>();
    public IReadOnlyCollection<WebsiteAnalyticsBreakdown> TopPages { get; set; } = Array.Empty<WebsiteAnalyticsBreakdown>();
    public IReadOnlyCollection<WebsiteAnalyticsBreakdown> Referrers { get; set; } = Array.Empty<WebsiteAnalyticsBreakdown>();
    public IReadOnlyCollection<WebsiteAnalyticsBreakdown> Domains { get; set; } = Array.Empty<WebsiteAnalyticsBreakdown>();
}

public record WebsiteAnalyticsDailyPoint(DateTime Date, int Views, int Visitors);
public record WebsiteAnalyticsBreakdown(string Label, int Views, int Visitors);
