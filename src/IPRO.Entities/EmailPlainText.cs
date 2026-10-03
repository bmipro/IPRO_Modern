using System;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace IPRO.Entities;

// 542 (2026-10-03): the plain-text part of every email. iPro sent HTML alone, and the owner's Amazon
// pilot put an estimate in Yahoo's Spam with its links disabled; a message with no text part is one of
// the signs a filter weighs against a sender. Every provider (SES, Azure, SendGrid) now sends a text part
// beside the HTML: the caller's own when it wrote one (the follow-ups email, the cards and letters), else
// this reading of the HTML -- the same words, a line per paragraph, each link written out after its label.
public static class EmailPlainText
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly Regex Whitespace = new(@"\s+", Options);
    private static readonly Regex Comments = new(@"<!--.*?-->", Options);
    private static readonly Regex Invisible = new(@"<(head|style|script|title)\b[^>]*>.*?</\1\s*>", Options);
    private static readonly Regex Anchors = new(@"<a\b([^>]*)>(.*?)</a\s*>", Options);
    private static readonly Regex Href = new(@"\bhref\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", Options);
    private static readonly Regex Images = new(@"<img\b[^>]*>", Options);
    private static readonly Regex LineBreaks = new(@"<br\s*/?>", Options);
    private static readonly Regex ListItems = new(@"<li\b[^>]*>", Options);
    private static readonly Regex Paragraphs = new(@"</?(p|h[1-6]|table|ul|ol|blockquote)\b[^>]*>", Options);
    private static readonly Regex Blocks = new(@"</?(div|tr|section|header|footer|article|center|hr|dl|dt|dd|html|body)\b[^>]*>", Options);
    private static readonly Regex Cells = new(@"</?(td|th)\b[^>]*>", Options);
    private static readonly Regex Tags = new(@"<[^>]*>", Options);
    private static readonly Regex Spaces = new(@"[ \t\xA0]+", Options);
    private static readonly Regex BlankRuns = new(@"\n{3,}", Options);

    // The caller's own text when there is one, else the text read out of the HTML.
    public static string Ensure(string? textBody, string? htmlBody) =>
        string.IsNullOrWhiteSpace(textBody) ? FromHtml(htmlBody) : textBody;

    public static string FromHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        // Line breaks in HTML source are only spacing; the tags decide where the lines go.
        var s = Whitespace.Replace(html, " ");
        s = Comments.Replace(s, " ");
        s = Invisible.Replace(s, " ");
        s = Anchors.Replace(s, Link);
        s = Images.Replace(s, " ");
        s = LineBreaks.Replace(s, "\n");
        s = ListItems.Replace(s, "\n- ");
        s = Paragraphs.Replace(s, "\n\n");
        s = Blocks.Replace(s, "\n");
        s = Cells.Replace(s, " ");
        s = Tags.Replace(s, string.Empty);
        // Entities last, once: "&lt;b&gt;" a person typed stays text and is never read as a tag.
        s = WebUtility.HtmlDecode(s);

        var lines = s.Split('\n').Select(line => Spaces.Replace(line, " ").Trim());
        return BlankRuns.Replace(string.Join("\n", lines), "\n\n").Trim();
    }

    // "View invoice (https://...)": the label, then where it goes. A link whose label is its address, or
    // that has no label (an image), is the address alone; one that goes nowhere is its label alone.
    private static string Link(Match anchor)
    {
        var label = Spaces.Replace(Tags.Replace(anchor.Groups[2].Value, string.Empty), " ").Trim();
        var href = Href.Match(anchor.Groups[1].Value);
        var url = href.Success ? (href.Groups[1].Success ? href.Groups[1].Value : href.Groups[2].Success ? href.Groups[2].Value : href.Groups[3].Value).Trim() : string.Empty;

        if (url.Length == 0 || url.StartsWith("#", StringComparison.Ordinal) || url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
            return label;
        if (url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
        {
            url = url[(url.IndexOf(':') + 1)..];
            var query = url.IndexOf('?');
            if (query >= 0) url = url[..query];
        }
        if (label.Length == 0 || Same(label, url)) return url;
        return $"{label} ({url})";
    }

    private static bool Same(string label, string url)
    {
        static string Bare(string value)
        {
            var decoded = WebUtility.HtmlDecode(value).Trim().TrimEnd('/');
            var scheme = decoded.IndexOf("://", StringComparison.Ordinal);
            return scheme >= 0 ? decoded[(scheme + 3)..] : decoded;
        }
        return string.Equals(Bare(label), Bare(url), StringComparison.OrdinalIgnoreCase);
    }
}
