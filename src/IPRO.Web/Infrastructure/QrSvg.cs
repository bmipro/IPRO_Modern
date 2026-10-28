using System.Globalization;
using System.Text;
using QRCoder;

namespace IPRO.Web.Infrastructure;

// 593: the set-up page's QR code, drawn as inline SVG (no picture file, no outside service: the secret in
// it never leaves the page). QRCoder works out the modules; one path draws the dark ones.
public static class QrSvg
{
    public static string Render(string text, string label)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var matrix = data.ModuleMatrix;
        var size = matrix.Count;
        var path = new StringBuilder();
        for (var y = 0; y < size; y++)
        {
            var row = matrix[y];
            for (var x = 0; x < size; x++)
                if (row[x]) path.Append(CultureInfo.InvariantCulture, $"M{x},{y}h1v1h-1z");
        }
        return $"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {size} {size}" width="220" height="220" role="img" aria-label="{System.Net.WebUtility.HtmlEncode(label)}" shape-rendering="crispEdges"><rect width="{size}" height="{size}" fill="#ffffff"/><path d="{path}" fill="#000000"/></svg>""";
    }
}
