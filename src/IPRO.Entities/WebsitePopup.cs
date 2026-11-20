namespace IPRO.Entities;

// 529 (the owner, 2026-09-27, from Squarespace's "Promotional Pop-Up"; built 2026-11-19): a pop-up on the
// adviser's website, written and switched on and off by the adviser in the portal (Website > Header & Menu,
// beside the announcement bar). It offers the site's newsletter sign-up (the same form, consent box,
// security question and spam guards as a newsletter block, posted to SubmitLead) or one or two buttons.
// Kept in the header settings with the announcement bar, so the database needs no new column. Every plan
// has it (PackageFeatureCodes.PromotionalPopup), and SuperAdmin can take it off a package.
// Drawn by _PublicPopup in the three designs inside a <template>, so it is no part of the page's text until
// a small script opens it: after some seconds, at a scroll depth, or on leaving. On a phone it is always a
// small bar along the bottom that never covers the page (Google's rule for intrusive pop-ups on phones).
public class WebsitePopup
{
    public const int MaxHeadlineLength = 80;
    public const int MaxTextLength = 400;
    public const int MaxButtonTextLength = 40;
    public const int MaxDelaySeconds = 60;
    // The visitor's browser keeps this after the pop-up is closed (for RepeatDays) or after a sign-up through
    // it (for SignedUpDays). Its value is the pop-up's SeenKey, so a changed pop-up shows again. Named in the
    // privacy policy (_LegalPrivacy.cshtml).
    public const string SeenCookie = "ipro_popup_seen";
    public const int SignedUpDays = 365;
    // The pop-up's sign-up form is a newsletter form with this block number. No block is number 0, so a
    // form the server turns down comes back in the pop-up and nowhere else (PublicFormRetry.LeadForm), and
    // a sign-up through it returns as "submitted=popup", which no newsletter block on the page answers to.
    public const int FormBlockId = 0;
    public const string SubmittedResult = "popup";

    public static readonly string[] Layouts = { "card", "corner", "bar" };
    public static readonly string[] Actions = { "signup", "buttons" };
    public static readonly string[] Triggers = { "delay", "scroll", "exit" };
    public static readonly int[] ScrollPercents = { 25, 50, 75 };
    public static readonly int[] RepeatChoices = { 1, 3, 7, 14, 30 };

    public bool Enabled { get; set; }
    public string Layout { get; set; } = "card";
    public string Headline { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    // A picture from the website's own media library (WebsiteMediaAsset.Id), with that picture's description
    // as its alt text. A picture deleted from the library simply stops showing.
    public int? ImageAssetId { get; set; }
    public string Action { get; set; } = "signup";
    public string Button1Text { get; set; } = string.Empty;
    public string Button1Url { get; set; } = string.Empty;
    public string Button2Text { get; set; } = string.Empty;
    public string Button2Url { get; set; } = string.Empty;
    public string Trigger { get; set; } = "delay";
    public int DelaySeconds { get; set; } = 5;
    public int ScrollPercent { get; set; } = 50;
    public bool HomePageOnly { get; set; } = true;
    public int RepeatDays { get; set; } = 7;

    // Shown at all: switched on, with a headline (it names the dialog for a screen reader), and with something
    // to do (the sign-up, or at least one button with words and a link).
    public bool IsShown => Enabled && Headline.Length > 0 && (IsSignup || Buttons.Count > 0);
    public bool IsSignup => Action == "signup";

    // Why a switched-on pop-up does not show, in the adviser's words; empty when it shows or is switched off.
    // The portal's card says it beside the switch (the owner's first try on staging, 2026-11-19: switched on,
    // no headline, and the save's warning was out of sight, so it looked like nothing happened).
    public string MissingPart =>
        !Enabled ? string.Empty
        : Headline.Length == 0 ? "it needs a headline"
        : !IsSignup && Buttons.Count == 0 ? "it needs a button with words and a link (or offer your newsletter sign-up instead)"
        : string.Empty;

    // When it opens, in the adviser's words: the card's status line and the preview's note.
    public string OpensWhen => Trigger switch
    {
        "scroll" => $"once a visitor has scrolled {ScrollPercent}% of the page",
        "exit" => "when a visitor is about to leave the page (on phones, halfway down)",
        _ => DelaySeconds == 0 ? "as the page opens" : $"{DelaySeconds} {(DelaySeconds == 1 ? "second" : "seconds")} after the page opens"
    };

    public IReadOnlyList<(string Text, string Url)> Buttons
    {
        get
        {
            var list = new List<(string, string)>();
            if (IsSignup) return list;
            if (Button1Text.Length > 0 && Button1Url.Length > 0) list.Add((Button1Text, Button1Url));
            if (Button2Text.Length > 0 && Button2Url.Length > 0) list.Add((Button2Text, Button2Url));
            return list;
        }
    }

    // The text's paragraphs (the adviser may press Enter between them).
    public IReadOnlyList<string> Paragraphs => Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    // Eight hex characters of what the pop-up says and offers: what the seen cookie remembers, so a visitor
    // who closed it sees it again once it says something new. Layout, timing and pages are not part of it.
    public string SeenKey
    {
        get
        {
            var parts = string.Join("\n", Headline, Text, Action, Button1Text, Button1Url, Button2Text, Button2Url, ImageAssetId?.ToString() ?? string.Empty);
            var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(parts));
            return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
        }
    }

    public WebsitePopup Normalized()
    {
        Layout = OneOf(Layout, Layouts, "card");
        Action = OneOf(Action, Actions, "signup");
        Trigger = OneOf(Trigger, Triggers, "delay");
        Headline = Clip(OneLine(Headline), MaxHeadlineLength);
        Text = Clip(Paragraphed(Text), MaxTextLength);
        Button1Text = Clip(OneLine(Button1Text), MaxButtonTextLength);
        Button2Text = Clip(OneLine(Button2Text), MaxButtonTextLength);
        Button1Url = WebsiteAnnouncementBar.SafeLink(Button1Url) ?? string.Empty;
        Button2Url = WebsiteAnnouncementBar.SafeLink(Button2Url) ?? string.Empty;
        DelaySeconds = Math.Clamp(DelaySeconds, 0, MaxDelaySeconds);
        ScrollPercent = ScrollPercents.Contains(ScrollPercent) ? ScrollPercent : 50;
        RepeatDays = RepeatChoices.Contains(RepeatDays) ? RepeatDays : 7;
        if (ImageAssetId is <= 0) ImageAssetId = null;
        return this;
    }

    private static string OneOf(string? value, string[] allowed, string fallback)
    {
        var text = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return allowed.Contains(text) ? text : fallback;
    }

    private static string OneLine(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // Each line made one line of single spaces; empty lines dropped, so paragraphs are separated by one "\n".
    private static string Paragraphed(string? value) =>
        string.Join('\n', (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(OneLine).Where(line => line.Length > 0));

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max].TrimEnd();
}
