using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using IPRO.Web.Infrastructure;
using Xunit;

namespace IPRO.IntegrationTests;

// 683 (the whole-code audit of 2026-11-26), the defects customers could hit that day.
public class LiveDefects683Tests
{
    // W10-01. The custom-forms editor builds each field card from a <template> with a script that looks parts of it up by
    // selector. 637 (accessibility) turned the Label caption from a <label> into a <div>; the script still asked for
    // '.field-label-group label', got nothing, and the error stopped every card: no form could be made or edited, and
    // no test ran the script. Every selector the script uses on a card must find something in the template it clones.
    [Theory]
    [InlineData(@"src\IPRO.Web\Views\Shared\_FormFieldBuilder.cshtml")]
    [InlineData(@"src\IPRO.Admin\Views\Shared\_FormFieldBuilder.cshtml")]
    public void Every_part_the_forms_editor_script_looks_up_is_in_the_template_it_clones(string path)
    {
        var source = Read(path);
        var document = new HtmlParser().ParseDocument(source);
        var templates = document.QuerySelectorAll("template").OfType<IHtmlTemplateElement>().ToList();
        Assert.Equal(2, templates.Count);   // the field card and the answer option

        var script = source[source.IndexOf("<script", StringComparison.Ordinal)..];
        var selectors = Regex.Matches(script, @"\b(?:card|node|optionsContainer|container)\.querySelector(?:All)?\('([^']+)'\)")
            .Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(script, @"\.closest\('([^']+)'\)").Select(m => m.Groups[1].Value))
            .Distinct()
            .ToList();
        Assert.True(selectors.Count >= 12, $"the script's lookups were not found in {path} (found {selectors.Count})");

        foreach (var selector in selectors)
        {
            Assert.True(templates.Any(t => t.Content.QuerySelector(selector) != null),
                $"{path}: the script looks up '{selector}', which is in neither template; the editor would stop there");
        }
    }

    [Fact]
    public void The_portals_caption_and_the_boxs_name_change_together_for_a_section_header()
    {
        var source = Read(@"src\IPRO.Web\Views\Shared\_FormFieldBuilder.cshtml");
        Assert.Contains("card.querySelector('.field-label-caption').textContent = caption;", source);
        Assert.Contains("card.querySelector('.field-label').setAttribute('aria-label', caption);", source);
        Assert.DoesNotContain(".field-label-group label", source[source.IndexOf("<script", StringComparison.Ordinal)..].Replace("'.field-label-group label', then found nothing", ""));
    }

    // W02-01. The client page drew its badge with FirstName[0] and LastName[0]; a contact from a website form may have
    // no last name, and the page then answered an error every time it was opened.
    [Theory]
    [InlineData("Jane", "Doe", "JD")]
    [InlineData("Jane", "", "J")]            // a website sign-up: first name and email only
    [InlineData("Jane", null, "J")]
    [InlineData("", "Doe", "D")]
    [InlineData("  ", "  doe ", "D")]
    [InlineData("", "", "?")]
    [InlineData(null, null, "?")]
    public void Initials_come_from_whichever_names_there_are(string? first, string? last, string expected)
    {
        Assert.Equal(expected, PersonInitials.Of(first, last));
    }

    [Fact]
    public void An_accented_first_letter_is_kept_whole()
    {
        var e = ((char)0xE9).ToString();   // e acute
        var o = ((char)0xF4).ToString();   // o circumflex
        Assert.Equal((char)0xC9 + "C", PersonInitials.Of(e + "lodie", "c" + o + "t" + e));
    }

    [Fact]
    public void The_client_page_never_takes_character_zero_of_a_name()
    {
        var view = Read(@"src\IPRO.Web\Views\Clients\Details.cshtml");
        Assert.Contains("@IPRO.Web.Infrastructure.PersonInitials.Of(Model.FirstName, Model.LastName)", view);
        Assert.DoesNotMatch(@"(FirstName|LastName)\[0\]", view);
    }

    // The two pages as Razor really renders them (a build cannot see a run-time error in a view). With
    // LIVE_DEFECTS_OUT set to a folder, the markup is written there to be opened in a browser.
    [Fact]
    public async System.Threading.Tasks.Task The_forms_editor_partial_renders_with_its_template_and_script()
    {
        var html = await DomainGuideScreens595Tests.RenderWebAsync("~/Views/Shared/_FormFieldBuilder.cshtml",
            new System.Collections.Generic.List<IPRO.Web.Models.FormFieldInput>(), new System.Collections.Generic.Dictionary<string, object?>());
        Assert.Contains("<template id=\"fieldTemplate\">", html);
        Assert.Contains("field-label-caption", html);
        Assert.Contains("const initialFields = [];", html);
        Write("forms-editor.html", "<form id=\"formBuilderForm\">" + html + "</form>");
    }

    [Fact]
    public async System.Threading.Tasks.Task The_unsubscribe_page_asks_with_one_button_and_changes_its_words_after()
    {
        var ask = await DomainGuideScreens595Tests.RenderWebAsync<object?>("~/Views/Newsletter/Unsubscribe.cshtml", null,
            new System.Collections.Generic.Dictionary<string, object?> { ["Ask"] = true, ["Success"] = false, ["Email"] = "bob@example.test", ["Business"] = "Global Business Solution" });
        Assert.Contains("Unsubscribe from Global Business Solution?", ask);
        Assert.Contains("Nothing has changed yet.", ask);
        Assert.Contains("<form method=\"post\" class=\"mb-0\"", ask);
        Assert.Contains("name=\"confirm\" value=\"1\"", ask);
        Assert.Single(Regex.Matches(ask, "<button "));
        Assert.DoesNotContain("You have been unsubscribed", ask);
        Write("unsubscribe-ask.html", ask);

        var done = await DomainGuideScreens595Tests.RenderWebAsync<object?>("~/Views/Newsletter/Unsubscribe.cshtml", null,
            new System.Collections.Generic.Dictionary<string, object?> { ["Success"] = true, ["Email"] = "bob@example.test", ["Business"] = "Global Business Solution" });
        Assert.Contains("You have been unsubscribed", done);
        Assert.DoesNotContain("<form", done);
    }

    private static void Write(string name, string body)
    {
        var folder = Environment.GetEnvironmentVariable("LIVE_DEFECTS_OUT");
        if (!string.IsNullOrEmpty(folder)) File.WriteAllText(Path.Combine(folder, name), body);
    }

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative));
    }
}
