using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IPRO.Utility;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IPRO.IntegrationTests;

// 553: what is actually sent to Azure for a short address (example.com). A bare name cannot carry a
// CNAME: it reaches the app by an A record and Azure proves it by the asuid TXT record, so its
// binding must say "A" -- a binding that says "CName" for it is refused. Everything after the
// binding is the www name's own two passes: the managed certificate is asked for, comes back without
// a thumbprint while it is being issued, and is attached on the next pass.
//
// In ConfigHazardTests' collection: both classes set AzureDomainAutomationService.SiteNameProvider,
// a process-wide seam, and test classes otherwise run in parallel.
[Collection(ConfigHazardTests.AzureStatics)]
public class ShortAddressAzure553Tests : IDisposable
{
    public ShortAddressAzure553Tests() => AzureDomainAutomationService.SiteNameProvider = () => "ipro-prod-web";
    public void Dispose() => AzureDomainAutomationService.SiteNameProvider =
        () => Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME");

    [Fact]
    public async Task A_short_address_is_bound_as_an_A_record_name_and_secured_in_two_passes()
    {
        var azure = new RecordingAzure();
        var service = NewService(azure);

        // First pass: the binding, then the certificate order -- which has no thumbprint yet.
        var first = await service.EnsureRootDomainAsync("Shop553.Example.Test.");
        Assert.True(first.Success);
        Assert.True(first.BindingCreated);
        Assert.False(first.SslBound);

        var binding = azure.Puts.Single(p => p.Url.Contains("/hostNameBindings/shop553.example.test?"));
        Assert.Contains("/sites/ipro-prod-web/hostNameBindings/", binding.Url);
        Assert.Contains("\"customHostNameDnsRecordType\":\"A\"", binding.Body);
        Assert.DoesNotContain("sslState", binding.Body);
        var order = azure.Puts.Single(p => p.Url.Contains("/certificates/"));
        Assert.Contains("/certificates/managed-shop553-example-test?", order.Url);
        Assert.Contains("\"canonicalName\":\"shop553.example.test\"", order.Body);

        // Second pass: Azure has issued it; the binding is put again with the thumbprint.
        azure.Thumbprint = "ABC123";
        azure.Puts.Clear();
        var second = await service.EnsureRootDomainAsync("shop553.example.test");
        Assert.True(second.SslBound);
        var secured = azure.Puts.Last(p => p.Url.Contains("/hostNameBindings/shop553.example.test?"));
        Assert.Contains("\"customHostNameDnsRecordType\":\"A\"", secured.Body);
        Assert.Contains("\"sslState\":\"SniEnabled\"", secured.Body);
        Assert.Contains("\"thumbprint\":\"ABC123\"", secured.Body);
    }

    [Fact]
    public async Task A_refused_certificate_order_says_the_binding_itself_went_through()
    {
        // Azure binds the name and then refuses the order while its DNS check still sees the old
        // record. The caller has to be able to tell that from a name that was never bound.
        var azure = new RecordingAzure { RefuseCertificate = "Missing one DNS record for hostname shop553.example.test" };
        var result = await NewService(azure).EnsureRootDomainAsync("shop553.example.test");

        Assert.False(result.Success);
        Assert.True(result.BindingCreated);
        Assert.Contains("Missing one DNS record", result.Message);

        // ...and a binding that is itself refused (no TXT record) says the opposite.
        var refused = new RecordingAzure { RefuseBinding = "A TXT record pointing from asuid.shop553.example.test to C6D5 was not found." };
        var never = await NewService(refused).EnsureRootDomainAsync("shop553.example.test");
        Assert.False(never.Success);
        Assert.False(never.BindingCreated);
        Assert.Contains("asuid.shop553.example.test", never.Message);
        Assert.DoesNotContain(refused.Puts, p => p.Url.Contains("/certificates/"));
    }

    [Fact]
    public async Task A_www_name_is_still_bound_by_its_CNAME()
    {
        var azure = new RecordingAzure();
        var result = await NewService(azure).EnsureDomainAsync("www.shop553.example.test");

        Assert.True(result.Success);
        var binding = azure.Puts.Single(p => p.Url.Contains("/hostNameBindings/www.shop553.example.test?"));
        Assert.Contains("\"customHostNameDnsRecordType\":\"CName\"", binding.Body);
    }

    private static AzureDomainAutomationService NewService(RecordingAzure azure) => new(
        azure,
        Options.Create(new AzureDomainAutomationOptions
        {
            Enabled = true,
            TenantId = "tenant", ClientId = "client", ClientSecret = "secret",
            SubscriptionId = "sub", ResourceGroup = "rg",
            WebAppName = "ipro-prod-web",
            AppServicePlanResourceId = "/subscriptions/sub/resourceGroups/rg/providers/Microsoft.Web/serverfarms/plan",
        }),
        NullLogger<AzureDomainAutomationService>.Instance);

    // Stands in for login.microsoftonline.com and management.azure.com: a token, then every PUT
    // recorded and answered the way Azure answers (a certificate has a thumbprint only once issued).
    private sealed class RecordingAzure : HttpMessageHandler, IHttpClientFactory
    {
        public List<(string Url, string Body)> Puts { get; } = new();
        public string? Thumbprint { get; set; }
        public string? RefuseBinding { get; set; }
        public string? RefuseCertificate { get; set; }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("login.microsoftonline.com"))
            {
                return Json("{\"access_token\":\"token\",\"expires_in\":3600}");
            }

            Assert.Equal(HttpMethod.Put, request.Method);
            Puts.Add((url, request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken)));
            var certificate = url.Contains("/certificates/");
            var refusal = certificate ? RefuseCertificate : RefuseBinding;
            if (refusal != null)
            {
                return Json("{\"error\":{\"code\":\"BadRequest\",\"message\":\"" + refusal + "\"}}", HttpStatusCode.BadRequest);
            }

            return certificate
                ? Json(Thumbprint == null ? "{\"properties\":{}}" : "{\"properties\":{\"thumbprint\":\"" + Thumbprint + "\"}}")
                : Json("{}");
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
