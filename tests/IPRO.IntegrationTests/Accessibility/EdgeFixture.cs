using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace IPRO.IntegrationTests.Accessibility;

// 578: one Edge for a test class (its tests run one after another), with axe-core put into every
// page it opens before the page's own scripts run.
public sealed class EdgeFixture : IAsyncLifetime
{
    internal HeadlessEdge Edge { get; private set; } = null!;
    private readonly List<string> _pageFolders = new();

    public async Task InitializeAsync()
    {
        Edge = await HeadlessEdge.StartAsync();
        await Edge.SendAsync("Page.addScriptToEvaluateOnNewDocument", new JsonObject { ["source"] = PublicSitePages.AxeSource() });
        // Bootstrap scrolls smoothly unless a visitor asks for less motion; asking keeps every scroll
        // instant, so what a test reads after a key press is where the page has settled.
        await Edge.SendAsync("Emulation.setEmulatedMedia", new JsonObject
        {
            ["features"] = new JsonArray(new JsonObject { ["name"] = "prefers-reduced-motion", ["value"] = "reduce" })
        });
    }

    public async Task DisposeAsync()
    {
        if (Edge != null) await Edge.DisposeAsync();
        foreach (var folder in _pageFolders) HeadlessEdge.DeleteFolder(folder);
    }

    /// <summary>Renders a page, opens it at the given width and returns axe's WCAG A/AA findings.</summary>
    internal async Task<List<string>> AxeAsync(string html, string name, int width = 1280, params string[] onlyRules)
    {
        await OpenAsync(html, name, width);
        return await AxeOpenPageAsync(onlyRules);
    }

    internal async Task OpenAsync(string html, string name, int width = 1280)
    {
        await Edge.SetViewportAsync(width, 900);
        var file = PublicSitePages.WriteToDisk(html, name);
        _pageFolders.Add(Path.GetDirectoryName(file)!);
        await Edge.OpenAsync(file);
    }

    internal async Task<List<string>> AxeOpenPageAsync(params string[] onlyRules)
    {
        var runOnly = onlyRules.Length == 0
            ? "{type:'tag',values:['wcag2a','wcag2aa','wcag21a','wcag21aa','wcag22aa']}"
            : "{type:'rule',values:" + JsonSerializer.Serialize(onlyRules) + "}";
        var json = await Edge.EvaluateAsync<string>(
            "axe.run(document,{runOnly:" + runOnly + ",resultTypes:['violations'],iframes:false})" +
            ".then(function(r){return JSON.stringify(r.violations.map(function(v){return v.nodes.map(function(n){" +
            "return v.id+' at '+n.target.join(' ')+': '+(n.failureSummary||'').replace(/\\s+/g,' ');});}));})");
        return JsonSerializer.Deserialize<List<List<string>>>(json!)!.SelectMany(n => n).ToList();
    }
}
