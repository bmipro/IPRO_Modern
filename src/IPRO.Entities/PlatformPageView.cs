using System;

namespace IPRO.Entities;

// 512 (2026-09-22): one view of one of the PLATFORM's own public pages -- the home page, the
// accountants and mortgage pages, the registration page -- counted the way adviser sites' views
// have been since the SEO wave (WebsitePageView): no cookie, a one-way hashed visitor, DNT honoured,
// bots skipped. Plus the campaign tags a link may carry (utm_source / utm_medium / utm_campaign),
// so the owner can see which post or ad brought a visitor. Host is the public name the visitor used
// (www.iproaccountants.com, not the platform host the request was re-addressed to).
public class PlatformPageView
{
    public long Id { get; set; }
    public string Host { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string ReferrerHost { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Medium { get; set; } = string.Empty;
    public string Campaign { get; set; } = string.Empty;
    public string VisitorHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
