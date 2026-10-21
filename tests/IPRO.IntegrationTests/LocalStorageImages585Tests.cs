using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace IPRO.IntegrationTests;

// 585 (the owner's bench re-check, finding 6, 2026-10-21): a logo replaced in the builder showed as a
// broken picture in the look on the bench. The file was there, on the local storage emulator
// (http://127.0.0.1:10000/devstoreaccount1/...), but the portal's Content-Security-Policy lets pictures
// come only from Azure's storage (https://*.blob.core.windows.net), so the browser refused it. In
// Development, and only for a storage address on this machine, that address is allowed too, in the
// portal and in SuperAdmin alike. Production's policies stay exactly as they were.
public class LocalStorageImages585Tests
{
    private const string Emulator = "UseDevelopmentStorage=true";
    private const string AzureAccount = "DefaultEndpointsProtocol=https;AccountName=iprostorage;AccountKey=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA;EndpointSuffix=core.windows.net";

    // The policies production sends, word for word (the nonce changes with every request).
    private const string PortalPolicy =
        "default-src 'self'; script-src 'self' 'nonce-N' https://cdn.jsdelivr.net https://cdnjs.cloudflare.com https://cdn.tiny.cloud https://cdn.jsdelivr.net/npm/chart.js@4.4.0; " +
        "style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://cdnjs.cloudflare.com; " +
        "img-src 'self' data: blob: https://*.blob.core.windows.net https://cdn.tiny.cloud; " +
        "font-src 'self' https://cdnjs.cloudflare.com; connect-src 'self'; frame-src 'self' https://www.google.com https://www.youtube-nocookie.com; frame-ancestors 'self';";
    private const string AdminPolicy =
        "default-src 'self'; script-src 'self' 'nonce-N' https://cdn.jsdelivr.net https://cdnjs.cloudflare.com; " +
        "style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://cdnjs.cloudflare.com; " +
        "img-src 'self' data: blob: https://portal.example.test https://*.blob.core.windows.net; " +
        "font-src 'self' https://cdnjs.cloudflare.com; connect-src 'self'; frame-src 'none'; frame-ancestors 'self';";

    [Theory]
    [InlineData(Emulator)]
    [InlineData(AzureAccount)]
    [InlineData(null)]
    public async Task Productions_policies_are_unchanged_whatever_the_storage(string? storage)
    {
        Assert.Equal(PortalPolicy, await PortalPolicyAsync("Production", storage));
        Assert.Equal(AdminPolicy, await AdminPolicyAsync("Production", storage));
        Assert.Equal(PortalPolicy, await PortalPolicyAsync("Staging", storage));
    }

    [Fact]
    public async Task In_development_pictures_from_the_local_storage_emulator_are_allowed()
    {
        Assert.Equal(PortalPolicy.Replace("https://cdn.tiny.cloud; font-src", "https://cdn.tiny.cloud http://127.0.0.1:10000; font-src"),
            await PortalPolicyAsync("Development", Emulator));
        Assert.Equal(AdminPolicy.Replace("https://*.blob.core.windows.net; font-src", "https://*.blob.core.windows.net http://127.0.0.1:10000; font-src"),
            await AdminPolicyAsync("Development", Emulator));
        // An emulator named by its own endpoint, as a bench may configure it.
        Assert.Contains(" http://localhost:10010; font-src", await PortalPolicyAsync("Development",
            "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA;BlobEndpoint=http://localhost:10010/devstoreaccount1;"));
    }

    [Theory]
    [InlineData(AzureAccount)]
    [InlineData("DefaultEndpointsProtocol=https;AccountName=x;AccountKey=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA;BlobEndpoint=https://files.example.test/x;")]
    [InlineData("not a connection string")]
    [InlineData(null)]
    public async Task In_development_only_storage_on_this_machine_is_added(string? storage)
    {
        Assert.Equal(PortalPolicy, await PortalPolicyAsync("Development", storage));
        Assert.Equal(AdminPolicy, await AdminPolicyAsync("Development", storage));
    }

    // The apps build the middleware themselves (UseSecurityHeaders): what it needs is found there.
    [Fact]
    public async Task Each_app_builds_the_policy_from_its_own_environment_and_settings()
    {
        foreach (var useHeaders in new System.Action<Microsoft.AspNetCore.Builder.WebApplication>[]
                 {
                     app => IPRO.Web.Middleware.SecurityHeadersMiddlewareExtensions.UseSecurityHeaders(app),
                     app => IPRO.Admin.Middleware.SecurityHeadersMiddlewareExtensions.UseSecurityHeaders(app)
                 })
        {
            var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(new Microsoft.AspNetCore.Builder.WebApplicationOptions { EnvironmentName = "Development" });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Azure:StorageConnectionString"] = Emulator });
            await using var app = builder.Build();
            useHeaders(app);
            var pipeline = ((Microsoft.AspNetCore.Builder.IApplicationBuilder)app).Build();
            var context = new DefaultHttpContext { RequestServices = app.Services };
            await pipeline(context);
            Assert.Contains(" http://127.0.0.1:10000; font-src", context.Response.Headers["Content-Security-Policy"].ToString());
        }
    }

    private static async Task<string> PortalPolicyAsync(string environment, string? storage)
    {
        var middleware = new IPRO.Web.Middleware.SecurityHeadersMiddleware(_ => Task.CompletedTask, new Environment(environment), Configuration(storage));
        var context = new DefaultHttpContext();
        await middleware.InvokeAsync(context);
        return Nonce(context.Response.Headers["Content-Security-Policy"].ToString());
    }

    private static async Task<string> AdminPolicyAsync(string environment, string? storage)
    {
        var middleware = new IPRO.Admin.Middleware.SecurityHeadersMiddleware(_ => Task.CompletedTask, Configuration(storage), new Environment(environment));
        var context = new DefaultHttpContext();
        await middleware.InvokeAsync(context);
        return Nonce(context.Response.Headers["Content-Security-Policy"].ToString());
    }

    private static string Nonce(string policy) => Regex.Replace(policy, "'nonce-[^']+'", "'nonce-N'");

    private static IConfiguration Configuration(string? storage) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["App:BaseUrl"] = "https://portal.example.test/",
        ["Azure:StorageConnectionString"] = storage
    }).Build();

    private sealed class Environment(string name) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "IPRO.Web";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = name;
    }
}
