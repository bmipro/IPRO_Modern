using System;
using System.IO;
using System.Linq;
using Xunit;

namespace IPRO.IntegrationTests;

// 569 (2026-10-11): the owner printed an invoice. To PDF it was one page; to his printer it was two,
// in the PHONE layout (every figure a row of its own). The print dialog's default half-inch margins
// leave a Letter sheet 7.5 inches of width, which is 720 CSS pixels: exactly the width at which the
// stylesheet's phone rules begin, and they were written for any medium. The PDF had "Margins: None",
// so it was 816 pixels wide and escaped. The same stylesheet prints the platform's own invoices and
// the referral credit note.
public class PrintLayout569Tests
{
    private static readonly string Css = Read(@"src\IPRO.Web\wwwroot\css\invoice.css").Replace("\r\n", "\n");

    [Fact]
    public void A_letter_sheet_with_default_margins_is_exactly_as_wide_as_the_phone_layout_begins()
    {
        const int cssPixelsPerInch = 96;
        Assert.Equal(720, (int)((8.5 - 2 * 0.5) * cssPixelsPerInch));
    }

    [Fact]
    public void The_phone_layout_is_for_screens_and_paper_always_takes_the_table()
    {
        Assert.Contains("@media screen and (max-width: 720px) {", Css);
        // No width-only rule is left to catch a narrow sheet of paper.
        var queries = Css.Split('\n').Where(l => l.TrimStart().StartsWith("@media", StringComparison.Ordinal)).Select(l => l.Trim()).ToList();
        Assert.Equal(new[] { "@media screen and (max-width: 720px) {", "@media print {" }, queries);
    }

    [Fact]
    public void On_paper_the_invoice_is_as_wide_as_the_printable_area_not_a_fixed_eight_and_a_half_inches()
    {
        // With the dialog's margins added, a fixed 8.5 inches ran off the right edge of the 7.5 that were left.
        var print = Css[Css.IndexOf("@media print", StringComparison.Ordinal)..];
        Assert.DoesNotContain("\n    width: 8.5in;", print);
        Assert.Contains("  .page-shell {\n    width: 100%;\n    max-width: 8.5in;", print);
        Assert.Contains("  .invoice {\n    width: 100%;\n    max-width: 8.5in;", print);
        // And the sheet with no margins is as it was: the stylesheet's own padding is the margin.
        Assert.Contains("size: Letter;\n    margin: 0;", print);
        Assert.Contains("padding: 0.35in 0.5in;", print);
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
