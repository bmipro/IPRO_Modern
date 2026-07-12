using System.Text.Json;

namespace IPRO.Entities;

public class WebsiteTemplateDesign
{
    public string Renderer { get; set; } = "modern-professional";
    public string AccentColor { get; set; } = "#1457d9";
    public string BackgroundColor { get; set; } = "#f4f7fb";
    public string FontFamily { get; set; } = "Arial, Helvetica, sans-serif";
    public string HeroStyle { get; set; } = "gradient";
    public string HeaderStyle { get; set; } = "light";
    public string HeroLayout { get; set; } = "split";
    public string SectionSpacing { get; set; } = "spacious";
    public string ButtonStyle { get; set; } = "soft";
    public int Version { get; set; } = 1;

    public static WebsiteTemplateDesign FromTemplate(WebsiteTemplate? template, string? fallbackAccent = null)
    {
        var design = new WebsiteTemplateDesign
        {
            Renderer = NormalizeRenderer(template?.TemplateKey),
            AccentColor = NormalizeColor(fallbackAccent, "#1457d9")
        };

        if (string.IsNullOrWhiteSpace(template?.LayoutJson))
        {
            return design;
        }

        try
        {
            using var document = JsonDocument.Parse(template.LayoutJson);
            var root = document.RootElement;

            if (TryGetString(root, "renderer", out var renderer))
            {
                design.Renderer = NormalizeRenderer(renderer);
            }

            if (TryGetString(root, "accentColor", out var accentColor))
            {
                design.AccentColor = NormalizeColor(accentColor, design.AccentColor);
            }

            if (TryGetString(root, "backgroundColor", out var backgroundColor))
            {
                design.BackgroundColor = NormalizeColor(backgroundColor, design.BackgroundColor);
            }

            if (TryGetString(root, "fontFamily", out var fontFamily) && !string.IsNullOrWhiteSpace(fontFamily))
            {
                design.FontFamily = fontFamily.Trim();
            }

            if (TryGetString(root, "heroStyle", out var heroStyle) && !string.IsNullOrWhiteSpace(heroStyle))
            {
                design.HeroStyle = heroStyle.Trim().ToLowerInvariant();
            }

            if (TryGetString(root, "headerStyle", out var headerStyle))
            {
                design.HeaderStyle = NormalizeOption(headerStyle, new[] { "light", "dark", "overlay", "sidebar" }, design.HeaderStyle);
            }

            if (TryGetString(root, "heroLayout", out var heroLayout))
            {
                design.HeroLayout = NormalizeOption(heroLayout, new[] { "split", "centered", "image-left" }, design.HeroLayout);
            }

            if (TryGetString(root, "sectionSpacing", out var sectionSpacing))
            {
                design.SectionSpacing = NormalizeOption(sectionSpacing, new[] { "compact", "comfortable", "spacious" }, design.SectionSpacing);
            }

            if (TryGetString(root, "buttonStyle", out var buttonStyle))
            {
                design.ButtonStyle = NormalizeOption(buttonStyle, new[] { "square", "soft", "pill" }, design.ButtonStyle);
            }

            if (root.TryGetProperty("version", out var versionElement) &&
                versionElement.ValueKind == JsonValueKind.Number &&
                versionElement.TryGetInt32(out var version))
            {
                design.Version = Math.Max(1, version);
            }
        }
        catch (JsonException)
        {
            // A bad JSON value should not take down a public website. The admin UI validates and rewrites this later.
        }

        // An agent's saved theme color is an intentional per-site override of the template preset.
        if (!string.IsNullOrWhiteSpace(fallbackAccent))
        {
            design.AccentColor = NormalizeColor(fallbackAccent, design.AccentColor);
        }

        return design;
    }

    public string ToLayoutJson() => JsonSerializer.Serialize(new
    {
        renderer = Renderer,
        accentColor = AccentColor,
        backgroundColor = BackgroundColor,
        fontFamily = FontFamily,
        heroStyle = HeroStyle,
        headerStyle = HeaderStyle,
        heroLayout = HeroLayout,
        sectionSpacing = SectionSpacing,
        buttonStyle = ButtonStyle,
        version = Math.Max(1, Version)
    }, new JsonSerializerOptions { WriteIndented = true });

    private static bool TryGetString(JsonElement root, string propertyName, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return true;
    }

    private static string NormalizeRenderer(string? renderer)
    {
        var value = renderer?.Trim().ToLowerInvariant() ?? string.Empty;
        if (value.Contains("editorial", StringComparison.OrdinalIgnoreCase))
        {
            return "editorial-visual";
        }

        return value.Contains("classic", StringComparison.OrdinalIgnoreCase)
            ? "classic-sidebar"
            : "modern-professional";
    }

    private static string NormalizeOption(string? value, IEnumerable<string> allowed, string fallback)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return allowed.Contains(normalized, StringComparer.OrdinalIgnoreCase) ? normalized : fallback;
    }

    private static string NormalizeColor(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var color = value.Trim();
        if (color.Length == 7 && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit))
        {
            return color;
        }

        return fallback;
    }
}
