using Ganss.Xss;

namespace IPRO.Business.Services;

public static class HtmlContentSanitizer
{
    private static readonly HtmlSanitizer Sanitizer = new();

    public static string Sanitize(string? html) =>
        string.IsNullOrWhiteSpace(html) ? string.Empty : Sanitizer.Sanitize(html);
}
