using System.Security.Cryptography;
using System.Text;
using IPRO.Business.Services;
using Microsoft.AspNetCore.Html;
using Microsoft.Extensions.Caching.Memory;

namespace IPRO.Web.Infrastructure;

// 663 (audit 08-20 L12, found open again 2026-11-20): article text is cleaned when it is SAVED (since
// 2026-08-20), but the public pages wrote the stored text out as it was, so an article saved before then, or
// written into the database by any other road, kept whatever it held (a live <form>, an overlay). The three
// designs now clean it again as they draw it, with the same HtmlContentSanitizer the editor uses. Checked on the
// local copy before the change: of 53 articles (49 starter, 4 adviser) one changed, and only by writing "&" as
// "&amp;". Each distinct text is cleaned once and kept for an hour, so a busy page costs one cleaning.
public static class PublicArticleHtml
{
    private static readonly MemoryCache Cleaned = new(new MemoryCacheOptions { SizeLimit = 2000 });

    public static IHtmlContent Clean(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return HtmlString.Empty;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html)));
        if (Cleaned.TryGetValue(key, out string? clean) && clean != null) return new HtmlString(clean);
        clean = HtmlContentSanitizer.Sanitize(html);
        Cleaned.Set(key, clean, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromHours(1) });
        return new HtmlString(clean);
    }
}
