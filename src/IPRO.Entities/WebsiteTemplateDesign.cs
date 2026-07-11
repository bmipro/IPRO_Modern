using System.Text.Json;

namespace IPRO.Entities;

public class WebsiteTemplateDesign
{
    public string Renderer { get; set; } = "modern-professional";
    public string AccentColor { get; set; } = "#1457d9";
    public string BackgroundColor { get; set; } = "#f4f7fb";
    public string FontFamily { get; set; } = "Arial, Helvetica, sans-serif";
    public string HeroStyle { get; set; } = "gradient";

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
        }
        catch (JsonException)
        {
            // A bad JSON value should not take down a public website. The admin UI validates and rewrites this later.
        }

        return design;
    }

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
        return value.Contains("classic", StringComparison.OrdinalIgnoreCase)
            ? "classic-sidebar"
            : "modern-professional";
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
