using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using IPRO.Scheduler;
using IPRO.Utility;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IPRO.IntegrationTests;

// 675 (2026-11-23): the *.247advisers.com certificate renews itself from Let's Encrypt. The bought one (672) lasts 199
// days, the industry limit drops to 100 days from 2027-03-15, and SSLs.com would not even renew it before 2026-12-25.
//
// These run the whole path against a fake Let's Encrypt (which checks every signed request: signature, nonce, URL, the
// account named by its key or its URL) and a fake Azure (the DNS zone, the certificate list, the upload, the binding):
//   - Off by default, and only in the web app itself;
//   - On: the check record written in the Azure zone, seen on its name servers, answered, removed; the certificate
//     packaged with its key and chain (triple DES, as App Service takes it), uploaded under a name of its own and bound
//     with the binding's other properties kept; this job's expired uploads removed, nothing else;
//   - nothing done while its own certificate has more than 30 days left; Test mode uses Let's Encrypt's test service
//     and switches nothing;
//   - a failed check names the CNAME to look at, and still removes the record;
//   - the certificate check (CertificateExpiryJob) warns 21 days ahead with the switch On, in words that fit.
[Collection(ConfigHazardTests.AzureStatics)]
public class FreeAddressCertificate675Tests : IDisposable
{
    private const string Sectigo = "ipro-production-CanadaEastwebspace-Linux-260711134222";
    private const string SectigoThumbprint = "8D75D9D26C8F1B91825C1E9B020BF7D1EEBF7A7D";
    private static readonly DateTime SectigoExpires = new(2027, 1, 25, 23, 59, 59, DateTimeKind.Utc);

    private readonly Func<TimeSpan, CancellationToken, Task> _serviceDelay = FreeAddressCertificateService.Delay;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve = FreeAddressCertificateService.ResolveHost;
    private readonly Func<string, CancellationToken, Task<string?>> _served = FreeAddressCertificateService.ServedThumbprint;
    private readonly Func<DateTime> _clock = FreeAddressCertificateService.UtcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _acmeDelay = AcmeClient.Delay;
    private readonly List<string> _servedAsked = new();

    public FreeAddressCertificate675Tests()
    {
        AzureDomainAutomationService.SiteNameProvider = () => "ipro-prod-web";
        FreeAddressCertificateService.Delay = (_, _) => Task.CompletedTask;
        AcmeClient.Delay = (_, _) => Task.CompletedTask;
        FreeAddressCertificateService.ResolveHost = (_, _) => Task.FromResult(new[] { IPAddress.Parse("198.51.100.7") });
    }

    public void Dispose()
    {
        AzureDomainAutomationService.SiteNameProvider = () => Environment.GetEnvironmentVariable("WEBSITE_SITE_NAME");
        FreeAddressCertificateService.Delay = _serviceDelay;
        FreeAddressCertificateService.ResolveHost = _resolve;
        FreeAddressCertificateService.ServedThumbprint = _served;
        FreeAddressCertificateService.UtcNow = _clock;
        AcmeClient.Delay = _acmeDelay;
        ZoneDns.QueryHook = null;
    }

    // ------------------------------------------------------------------------------------------------- the switch --

    [Theory]
    [InlineData("On", FreeAddressCertificateMode.On)]
    [InlineData(" on ", FreeAddressCertificateMode.On)]
    [InlineData("TEST", FreeAddressCertificateMode.Test)]
    [InlineData("", FreeAddressCertificateMode.Off)]
    [InlineData(null, FreeAddressCertificateMode.Off)]
    [InlineData("yes", FreeAddressCertificateMode.Off)]
    [InlineData("2", FreeAddressCertificateMode.Off)]
    public void The_switch_is_off_unless_it_says_On_or_Test(string? value, FreeAddressCertificateMode expected)
    {
        Assert.Equal(expected, FreeAddressCertificateService.ModeFrom(Config(value)));
    }

    [Fact]
    public async Task Off_by_default_it_asks_nobody_anything()
    {
        var cloud = Use(new FakeCloud());
        var outcome = await Service(cloud, null).RunAsync(CancellationToken.None);

        Assert.False(outcome.Switched);
        Assert.True(outcome.Routine);
        Assert.Empty(cloud.Arm);
        Assert.Equal(0, cloud.AcmeRequests);
    }

    [Fact]
    public async Task On_anywhere_but_the_web_app_it_refuses_before_asking_Azure()
    {
        var cloud = Use(new FakeCloud());
        AzureDomainAutomationService.SiteNameProvider = () => "ipro-prod-admin";
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(cloud, "On").RunAsync(CancellationToken.None));
        Assert.Contains("ipro-prod-admin", refused.Message);

        AzureDomainAutomationService.SiteNameProvider = () => null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(cloud, "On").RunAsync(CancellationToken.None));

        AzureDomainAutomationService.SiteNameProvider = () => "ipro-prod-web";
        var incomplete = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(cloud, "On", enabled: false).RunAsync(CancellationToken.None));
        Assert.Contains("incomplete", incomplete.Message);

        Assert.Empty(cloud.Arm);
        Assert.Equal(0, cloud.AcmeRequests);
    }

    // ------------------------------------------------------------------------------------------------ renewing --

    [Fact]
    public async Task On_it_replaces_the_bought_certificate_and_touches_nothing_else()
    {
        var cloud = Use(new FakeCloud());
        cloud.Certificates[Sectigo] = (SectigoThumbprint, SectigoExpires);
        cloud.Certificates["ipro-free-address-20250101-000000"] = ("AAAA", DateTime.UtcNow.AddDays(-10));      // ours, expired
        cloud.Certificates["ipro-free-address-20261001-000000"] = ("BBBB", DateTime.UtcNow.AddDays(5));        // ours, not yet
        cloud.Certificates["managed-www-example-com"] = ("CCCC", DateTime.UtcNow.AddDays(-10));                // not ours
        cloud.BoundThumbprint = SectigoThumbprint;

        var outcome = await Service(cloud, "On").RunAsync(CancellationToken.None);

        Assert.True(outcome.Switched, outcome.Summary);
        Assert.False(outcome.Routine);
        Assert.Equal("acme-v02.api.letsencrypt.org", cloud.DirectoryHost);

        // The certificate: uploaded under a name of its own, packaged as App Service takes it, and bound.
        var uploaded = Assert.Single(cloud.Uploads);
        Assert.StartsWith("ipro-free-address-", uploaded.Name);
        Assert.NotEqual(FreeAddressCertificateService.TestCertificateName, uploaded.Name);
        Assert.Equal(uploaded.Thumbprint, cloud.BoundThumbprint);
        Assert.Contains("\"serverFarmId\":\"/subscriptions/sub/resourceGroups/ipro-production/providers/Microsoft.Web/serverfarms/plan\"", uploaded.Body);
        Assert.Contains("\"location\":\"Canada East\"", uploaded.Body);

        using (var binding = JsonDocument.Parse(cloud.BindingPut!))
        {
            var properties = binding.RootElement.GetProperty("properties");
            Assert.Equal("ipro-prod-web", properties.GetProperty("siteName").GetString());
            Assert.Equal("Verified", properties.GetProperty("hostNameType").GetString());
            Assert.Equal("SniEnabled", properties.GetProperty("sslState").GetString());
            Assert.Equal(uploaded.Thumbprint, properties.GetProperty("thumbprint").GetString());
        }
        Assert.Contains("certificate-check.247advisers.com", _servedAsked);

        // The check record: written, then gone.
        Assert.Single(cloud.TxtHistory);
        Assert.Empty(cloud.Txt);

        // Only this job's expired upload is removed: the bought one, a younger one of ours and another host's stay.
        Assert.DoesNotContain("ipro-free-address-20250101-000000", cloud.Certificates.Keys);
        Assert.Contains(Sectigo, cloud.Certificates.Keys);
        Assert.Contains("ipro-free-address-20261001-000000", cloud.Certificates.Keys);
        Assert.Contains("managed-www-example-com", cloud.Certificates.Keys);
        Assert.DoesNotContain(cloud.Arm, r => r.Method == HttpMethod.Delete && r.Path.Contains(Sectigo, StringComparison.Ordinal));

        // Only its own names in Azure: the zone's record set, the certificates, the one binding.
        foreach (var (method, path, _) in cloud.Arm.Where(r => r.Method != HttpMethod.Get))
        {
            Assert.True(
                path.EndsWith("/dnsZones/acme-dns.247advisers.com/TXT/_acme-challenge", StringComparison.Ordinal) ||
                path.Contains("/providers/Microsoft.Web/certificates/ipro-free-address-", StringComparison.Ordinal) ||
                path.EndsWith("/sites/ipro-prod-web/hostNameBindings/*.247advisers.com", StringComparison.Ordinal),
                method + " " + path);
        }
    }

    [Fact]
    public async Task Its_own_certificate_with_more_than_30_days_left_is_left_alone()
    {
        var cloud = Use(new FakeCloud());
        cloud.Certificates["ipro-free-address-20261101-063000"] = ("OWN1", DateTime.UtcNow.AddDays(31));
        cloud.BoundThumbprint = "OWN1";

        var outcome = await Service(cloud, "On").RunAsync(CancellationToken.None);

        Assert.False(outcome.Switched);
        Assert.True(outcome.Routine);
        Assert.Contains("ipro-free-address-20261101-063000", outcome.Summary);
        Assert.Equal(0, cloud.AcmeRequests);
        Assert.DoesNotContain(cloud.Arm, r => r.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task Its_own_certificate_inside_30_days_is_renewed()
    {
        var cloud = Use(new FakeCloud());
        cloud.Certificates["ipro-free-address-20261101-063000"] = ("OWN1", DateTime.UtcNow.AddDays(29));
        cloud.BoundThumbprint = "OWN1";

        var outcome = await Service(cloud, "On").RunAsync(CancellationToken.None);

        Assert.True(outcome.Switched);
        Assert.NotEqual("OWN1", cloud.BoundThumbprint);
        Assert.Contains("ipro-free-address-20261101-063000", cloud.Certificates.Keys);   // kept until it expires
    }

    [Fact]
    public async Task Test_mode_uses_the_test_service_and_switches_nothing()
    {
        var cloud = Use(new FakeCloud());
        cloud.Certificates[Sectigo] = (SectigoThumbprint, SectigoExpires);
        cloud.BoundThumbprint = SectigoThumbprint;

        var outcome = await Service(cloud, "Test").RunAsync(CancellationToken.None);

        Assert.False(outcome.Switched);
        Assert.False(outcome.Routine);
        Assert.StartsWith("Test passed", outcome.Summary);
        Assert.Equal("acme-staging-v02.api.letsencrypt.org", cloud.DirectoryHost);
        Assert.Equal(FreeAddressCertificateService.TestCertificateName, Assert.Single(cloud.Uploads).Name);
        Assert.DoesNotContain(FreeAddressCertificateService.TestCertificateName, cloud.Certificates.Keys);   // removed again
        Assert.Null(cloud.BindingPut);
        Assert.Equal(SectigoThumbprint, cloud.BoundThumbprint);
        Assert.Empty(cloud.Txt);
    }

    [Fact]
    public async Task A_stale_nonce_is_retried_with_the_one_that_came_back()
    {
        var cloud = Use(new FakeCloud { BadNonceOnce = true });
        cloud.BoundThumbprint = SectigoThumbprint;
        cloud.Certificates[Sectigo] = (SectigoThumbprint, SectigoExpires);

        var outcome = await Service(cloud, "On").RunAsync(CancellationToken.None);

        Assert.True(outcome.Switched);
        Assert.False(cloud.BadNonceOnce);
    }

    // --------------------------------------------------------------------------------------------- when it fails --

    [Fact]
    public async Task A_failed_check_names_the_CNAME_removes_the_record_and_uploads_nothing()
    {
        var cloud = Use(new FakeCloud { CnameInPlace = false });
        cloud.Certificates[Sectigo] = (SectigoThumbprint, SectigoExpires);
        cloud.BoundThumbprint = SectigoThumbprint;

        var failed = await Assert.ThrowsAsync<AcmeException>(() => Service(cloud, "On").RunAsync(CancellationToken.None));

        Assert.Contains("No TXT record found at _acme-challenge.247advisers.com", failed.Message);   // Let's Encrypt's words
        Assert.Contains("_acme-challenge.247advisers.com at the domain's DNS host is a CNAME to _acme-challenge.acme-dns.247advisers.com", failed.Message);
        Assert.Contains("DOCS/20_CERTIFICATES.md", failed.Message);
        Assert.Single(cloud.TxtHistory);
        Assert.Empty(cloud.Txt);
        Assert.Empty(cloud.Uploads);
        Assert.Null(cloud.BindingPut);
    }

    [Fact]
    public async Task A_record_Azure_does_not_show_is_never_offered_to_Lets_Encrypt()
    {
        var cloud = Use(new FakeCloud());
        cloud.Certificates[Sectigo] = (SectigoThumbprint, SectigoExpires);
        cloud.BoundThumbprint = SectigoThumbprint;
        ZoneDns.QueryHook = (_, name, _, _) => Task.FromResult<ZoneDns.Answer?>(new ZoneDns.Answer(ZoneDns.NameError, true, Array.Empty<ZoneDns.Record>()));
        var clock = DateTime.UtcNow;
        FreeAddressCertificateService.UtcNow = () => clock = clock.AddSeconds(10);

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(cloud, "On").RunAsync(CancellationToken.None));

        Assert.Contains("did not show on Azure's name servers", failed.Message);
        Assert.False(cloud.ChallengeAnswered);
        Assert.Empty(cloud.Txt);
        Assert.Empty(cloud.Uploads);
    }

    [Fact]
    public async Task A_missing_zone_says_so()
    {
        var cloud = Use(new FakeCloud { ZoneMissing = true });
        cloud.Certificates[Sectigo] = (SectigoThumbprint, SectigoExpires);
        cloud.BoundThumbprint = SectigoThumbprint;

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(cloud, "On").RunAsync(CancellationToken.None));

        Assert.Contains("acme-dns.247advisers.com does not exist", failed.Message);
        Assert.Equal(0, cloud.AcmeRequests);
    }

    // ------------------------------------------------------------------------------------------- the pieces --

    [Fact]
    public void The_names_GoDaddy_and_Azure_hold()
    {
        Assert.Equal("acme-dns.247advisers.com", FreeAddressCertificateService.ZoneName("247advisers.com"));
        Assert.Equal("_acme-challenge.247advisers.com", FreeAddressCertificateService.ChallengeName("247advisers.com"));
        Assert.Equal("_acme-challenge.acme-dns.247advisers.com", FreeAddressCertificateService.ChallengeTarget("247advisers.com"));
        Assert.Equal("247advisers.com", FreeAddressCertificateService.RootDomain(Config(null)));
        Assert.True(FreeAddressCertificateService.IsOurs("ipro-free-address-20261201-063000"));
        Assert.False(FreeAddressCertificateService.IsOurs(Sectigo));
        Assert.False(FreeAddressCertificateService.IsOurs("managed--www-maison1850-com-ea1544ef92f3fdc8"));
    }

    [Fact]
    public void The_job_runs_once_a_day_before_the_certificate_check_and_never_retries_on_its_own()
    {
        var program = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Program.cs"));
        Assert.Contains("RecurringJob.AddOrUpdate<FreeAddressCertificateJob>(\"free-address-certificate\", job => job.RunAsync(), \"30 6 * * *\");", program);
        Assert.Contains("builder.Services.AddScoped<FreeAddressCertificateService>();", program);

        var retry = typeof(FreeAddressCertificateJob).GetCustomAttribute<Hangfire.AutomaticRetryAttribute>();
        Assert.NotNull(retry);
        Assert.Equal(0, retry!.Attempts);
    }

    [Fact]
    public void SuperAdmin_cannot_give_an_adviser_the_names_the_certificate_uses()
    {
        var agents = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Controllers\AgentsController.cs"));
        Assert.Contains("|| domain == \"certificate-check.\" + zone", agents);
        Assert.Contains("|| domain == FreeAddressCertificateService.ZoneName(zone)", agents);
        Assert.Contains("|| domain.EndsWith(\".\" + FreeAddressCertificateService.ZoneName(zone), StringComparison.Ordinal))", agents);
        // A sign-up name cannot be one: letters and digits only.
        Assert.Equal("acmedns", SignUpIdentifier.From("acme-dns"));
    }

    // ------------------------------------------------------------------------------------ the certificate check --

    [Fact]
    public void With_the_switch_On_the_check_warns_21_days_ahead_and_points_at_the_job()
    {
        Assert.Equal(21, CertificateExpiryJob.FreeAddressThreshold(Config("On"), 30));
        Assert.Equal(60, CertificateExpiryJob.FreeAddressThreshold(Config(null), 30));
        Assert.Equal(60, CertificateExpiryJob.FreeAddressThreshold(Config("Test"), 30));
        Assert.Equal(CertificateExpiryJob.FreeAddressAutoNote, CertificateExpiryJob.FreeAddressNoteFor(Config("On")));
        Assert.Equal(CertificateExpiryJob.FreeAddressNote, CertificateExpiryJob.FreeAddressNoteFor(Config("Off")));

        var problem = "certificate-check.247advisers.com (" + string.Format(CertificateExpiryJob.FreeAddressAutoNote, "247advisers.com") + "): 20 days left (expires 2027-03-01)";
        var message = CertificateExpiryJob.FailureMessage(new[] { problem }, 30);
        Assert.Contains("renews itself from Let's Encrypt", message);
        Assert.Contains("free-address-certificate", message);
        Assert.DoesNotContain("Azure never renews it", message);
    }

    [Fact]
    public void An_email_about_the_free_address_alone_no_longer_sends_anyone_to_a_CAA_record()
    {
        var auto = "certificate-check.247advisers.com (" + string.Format(CertificateExpiryJob.FreeAddressAutoNote, "247advisers.com") + "): 20 days left (expires 2027-03-01)";
        var byHand = "certificate-check.247advisers.com (" + string.Format(CertificateExpiryJob.FreeAddressNote, "247advisers.com") + "): 55 days left (expires 2027-01-25)";

        var selfRenewed = CertificateExpiryJob.AlertHtml(new[] { auto }, 30);
        Assert.DoesNotContain("digicert.com", selfRenewed);
        Assert.Contains("Job Scheduler", selfRenewed);
        Assert.Contains("renews itself from Let's Encrypt", selfRenewed);

        var handRenewed = CertificateExpiryJob.AlertHtml(new[] { byHand }, 30);
        Assert.DoesNotContain("digicert.com", handRenewed);
        Assert.Contains("renew it with the seller", handRenewed);

        var both = CertificateExpiryJob.AlertHtml(new[] { auto, "app.iproadvisers.com: 12 days left (expires 2026-10-07)" }, 30);
        Assert.Contains("digicert.com", both);
        Assert.Contains("renews itself from Let's Encrypt", both);
    }

    // ---------------------------------------------------------------------------------------------- helpers --

    private FakeCloud Use(FakeCloud cloud)
    {
        ZoneDns.QueryHook = (_, name, type, _) =>
        {
            Assert.Equal(ZoneDns.TXT, type);
            Assert.Equal("_acme-challenge.acme-dns.247advisers.com", name);
            return Task.FromResult<ZoneDns.Answer?>(new ZoneDns.Answer(ZoneDns.NoError, true,
                cloud.Txt.Select(v => new ZoneDns.Record(ZoneDns.TXT, name, v)).ToList()));
        };
        FreeAddressCertificateService.ServedThumbprint = (host, _) =>
        {
            _servedAsked.Add(host);
            return Task.FromResult(cloud.BoundThumbprint);
        };
        return cloud;
    }

    private static IConfiguration Config(string? mode) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FreeAddressCertificate:Mode"] = mode }).Build();

    private static FreeAddressCertificateService Service(FakeCloud cloud, string? mode, bool enabled = true) =>
        new(cloud, Options.Create(new AzureDomainAutomationOptions
        {
            Enabled = enabled,
            TenantId = "tenant",
            ClientId = "client",
            ClientSecret = "secret",
            SubscriptionId = "sub",
            ResourceGroup = "ipro-production",
            WebAppName = "ipro-prod-web",
            AppServicePlanResourceId = "/subscriptions/sub/resourceGroups/ipro-production/providers/Microsoft.Web/serverfarms/plan",
            Location = "Canada East"
        }), Config(mode), NullLogger<FreeAddressCertificateService>.Instance);

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }

    private static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    private static string ToBase64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // Let's Encrypt and Azure, as far as this job meets them.
    private sealed class FakeCloud : HttpMessageHandler, IHttpClientFactory
    {
        // Azure
        public Dictionary<string, (string Thumbprint, DateTime Expires)> Certificates { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? BoundThumbprint { get; set; }
        public bool ZoneMissing { get; init; }
        public bool CnameInPlace { get; init; } = true;
        public List<string> Txt { get; } = new();
        public List<string> TxtHistory { get; } = new();
        public List<(HttpMethod Method, string Path, string Body)> Arm { get; } = new();
        public List<(string Name, string Thumbprint, string Body)> Uploads { get; } = new();
        public string? BindingPut { get; private set; }

        // Let's Encrypt
        public string? DirectoryHost { get; private set; }
        public int AcmeRequests { get; private set; }
        public bool BadNonceOnce { get; set; }
        public bool ChallengeAnswered { get; private set; }
        private readonly HashSet<string> _nonces = new();
        private ECDsa? _accountKey;
        private string? _accountUrl;
        private const string Token = "token-675";
        private string _authorization = "pending";
        private string? _problem;
        private string? _chain;
        private readonly RSA _caKey = RSA.Create(2048);
        private readonly X509Certificate2 _ca;

        public FakeCloud()
        {
            var request = new CertificateRequest("CN=Fake 675 CA", _caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            _ca = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            if (uri.Host == "login.microsoftonline.com")
                return Json("{\"access_token\":\"token\",\"expires_in\":3600}");
            if (uri.Host == "management.azure.com")
            {
                Assert.Equal("Bearer token", request.Headers.Authorization?.ToString());
                return Azure(request.Method, uri, body);
            }
            if (uri.Host.EndsWith(".api.letsencrypt.org", StringComparison.Ordinal))
            {
                AcmeRequests++;
                return Acme(request, uri, body);
            }
            throw new InvalidOperationException("675: unexpected request to " + uri);
        }

        // ---------------------------------------------------------------------------------------------- Azure --

        private HttpResponseMessage Azure(HttpMethod method, Uri uri, string body)
        {
            var path = Uri.UnescapeDataString(uri.AbsolutePath);
            Arm.Add((method, path, body));
            const string zone = "/subscriptions/sub/resourceGroups/ipro-production/providers/Microsoft.Network/dnsZones/acme-dns.247advisers.com";

            if (path == zone + "/TXT/_acme-challenge")
            {
                Assert.Equal("?api-version=2018-05-01", uri.Query);
                if (method == HttpMethod.Put)
                {
                    using var json = JsonDocument.Parse(body);
                    var properties = json.RootElement.GetProperty("properties");
                    Assert.Equal(60, properties.GetProperty("TTL").GetInt32());
                    Txt.Clear();
                    foreach (var record in properties.GetProperty("TXTRecords").EnumerateArray())
                        foreach (var value in record.GetProperty("value").EnumerateArray())
                            Txt.Add(value.GetString()!);
                    TxtHistory.AddRange(Txt);
                    return Json("{}", HttpStatusCode.Created);
                }
                if (method == HttpMethod.Delete)
                {
                    Txt.Clear();
                    return Json("{}");
                }
            }

            if (path == zone && method == HttpMethod.Get)
            {
                return ZoneMissing
                    ? Json("{\"error\":{\"code\":\"ResourceNotFound\",\"message\":\"not found\"}}", HttpStatusCode.NotFound)
                    : Json("{\"name\":\"acme-dns.247advisers.com\",\"properties\":{\"nameServers\":[\"ns1-01.azure-dns.com.\",\"ns2-01.azure-dns.net.\"]}}");
            }

            const string certificates = "/subscriptions/sub/resourceGroups/ipro-production/providers/Microsoft.Web/certificates";
            if (path == certificates && method == HttpMethod.Get)
            {
                return Json(JsonSerializer.Serialize(new
                {
                    value = Certificates.Select(c => new
                    {
                        name = c.Key,
                        properties = new { thumbprint = c.Value.Thumbprint, expirationDate = c.Value.Expires.ToString("yyyy-MM-ddTHH:mm:ss+00:00") }
                    })
                }));
            }

            if (path.StartsWith(certificates + "/", StringComparison.Ordinal))
            {
                var name = path[(certificates.Length + 1)..];
                if (method == HttpMethod.Put) return Upload(name, body);
                if (method == HttpMethod.Delete)
                {
                    return Certificates.Remove(name)
                        ? Json("{}")
                        : Json("{\"error\":{\"code\":\"ResourceNotFound\",\"message\":\"not found\"}}", HttpStatusCode.NotFound);
                }
            }

            if (path == "/subscriptions/sub/resourceGroups/ipro-production/providers/Microsoft.Web/sites/ipro-prod-web/hostNameBindings/*.247advisers.com")
            {
                if (method == HttpMethod.Get)
                {
                    return Json(JsonSerializer.Serialize(new
                    {
                        name = "ipro-prod-web/*.247advisers.com",
                        properties = new { siteName = "ipro-prod-web", hostNameType = "Verified", sslState = "SniEnabled", thumbprint = BoundThumbprint }
                    }));
                }
                if (method == HttpMethod.Put)
                {
                    BindingPut = body;
                    using var json = JsonDocument.Parse(body);
                    BoundThumbprint = json.RootElement.GetProperty("properties").GetProperty("thumbprint").GetString();
                    return Json(body);
                }
            }

            throw new InvalidOperationException("675: unexpected Azure call " + method + " " + path);
        }

        // The PFX as App Service takes it: opens with its password, the key with the certificate, the chain after it,
        // triple DES (pbeWithSHAAnd3-KeyTripleDES-CBC), not PBES2.
        private HttpResponseMessage Upload(string name, string body)
        {
            using var json = JsonDocument.Parse(body);
            var properties = json.RootElement.GetProperty("properties");
            var pfx = Convert.FromBase64String(properties.GetProperty("pfxBlob").GetString()!);
            var password = properties.GetProperty("password").GetString()!;
            Assert.True(password.Length >= 24);

            var tripleDes = new byte[] { 0x06, 0x0A, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x0C, 0x01, 0x03 };
            var pbes2 = new byte[] { 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x05, 0x0D };
            Assert.True(pfx.AsSpan().IndexOf(tripleDes) >= 0, "the PFX is not triple DES");
            Assert.True(pfx.AsSpan().IndexOf(pbes2) < 0, "the PFX uses PBES2, which App Service refuses");

            var loaded = X509CertificateLoader.LoadPkcs12Collection(pfx, password);
            try
            {
                Assert.Equal(2, loaded.Count);
                var leaf = Assert.Single(loaded.Cast<X509Certificate2>(), c => c.HasPrivateKey);
                Assert.True(leaf.MatchesHostname("anyone.247advisers.com"));
                Assert.Contains(loaded.Cast<X509Certificate2>(), c => c.Subject == "CN=Fake 675 CA");
                Assert.Equal(2048, leaf.GetRSAPrivateKey()!.KeySize);
                Certificates[name] = (leaf.Thumbprint, leaf.NotAfter.ToUniversalTime());
                Uploads.Add((name, leaf.Thumbprint, body));
                return Json(JsonSerializer.Serialize(new { name, properties = new { thumbprint = leaf.Thumbprint } }));
            }
            finally
            {
                foreach (var certificate in loaded) certificate.Dispose();
            }
        }

        // --------------------------------------------------------------------------------------- Let's Encrypt --

        private HttpResponseMessage Acme(HttpRequestMessage request, Uri uri, string body)
        {
            DirectoryHost ??= uri.Host;
            Assert.Equal(DirectoryHost, uri.Host);
            var origin = uri.GetLeftPart(UriPartial.Authority);
            var path = uri.AbsolutePath;

            if (path == "/directory" && request.Method == HttpMethod.Get)
            {
                return Json(JsonSerializer.Serialize(new
                {
                    newNonce = origin + "/acme/new-nonce",
                    newAccount = origin + "/acme/new-acct",
                    newOrder = origin + "/acme/new-order"
                }));
            }
            if (path == "/acme/new-nonce") return Nonce(new HttpResponseMessage(HttpStatusCode.OK));

            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("application/jose+json", request.Content!.Headers.ContentType!.MediaType);
            var payload = Verify(uri, body);

            switch (path)
            {
                case "/acme/new-acct":
                    Assert.Contains("\"termsOfServiceAgreed\":true", payload);
                    _accountUrl = origin + "/acme/acct/675";
                    return Created(_accountUrl, "{\"status\":\"valid\"}");

                case "/acme/new-order":
                    if (BadNonceOnce)
                    {
                        BadNonceOnce = false;
                        return Nonce(Json("{\"type\":\"urn:ietf:params:acme:error:badNonce\",\"detail\":\"JWS has an invalid anti-replay nonce\"}", HttpStatusCode.BadRequest));
                    }
                    Assert.Equal("{\"identifiers\":[{\"type\":\"dns\",\"value\":\"*.247advisers.com\"}]}", payload);
                    return Created(origin + "/acme/order/1", Order(origin));

                case "/acme/authz/1":
                    Assert.Equal(string.Empty, payload);
                    return Nonce(Json(JsonSerializer.Serialize(new
                    {
                        status = _authorization,
                        identifier = new { type = "dns", value = "247advisers.com" },
                        wildcard = true,
                        challenges = new object[]
                        {
                            _problem == null
                                ? (object)new { type = "dns-01", url = origin + "/acme/chall/1", token = Token, status = _authorization }
                                : new { type = "dns-01", url = origin + "/acme/chall/1", token = Token, status = _authorization, error = new { type = "urn:ietf:params:acme:error:unauthorized", detail = _problem } }
                        }
                    })));

                case "/acme/chall/1":
                    Assert.Equal("{}", payload);
                    ChallengeAnswered = true;
                    // What Let's Encrypt reads: _acme-challenge.247advisers.com, through the CNAME into the Azure zone.
                    var seen = CnameInPlace ? Txt : new List<string>();
                    if (seen.Contains(ExpectedTxt()))
                    {
                        _authorization = "valid";
                    }
                    else
                    {
                        _authorization = "invalid";
                        _problem = "No TXT record found at _acme-challenge.247advisers.com";
                    }
                    return Nonce(Json("{\"type\":\"dns-01\",\"status\":\"processing\",\"token\":\"" + Token + "\"}"));

                case "/acme/order/1":
                    Assert.Equal(string.Empty, payload);
                    return Nonce(Json(Order(origin)));

                case "/acme/finalize/1":
                    Assert.Equal("valid", _authorization);
                    using (var json = JsonDocument.Parse(payload))
                        Issue(FromBase64Url(json.RootElement.GetProperty("csr").GetString()!));
                    return Nonce(Json(Order(origin, finalizing: true)));

                case "/acme/cert/1":
                    Assert.Equal(string.Empty, payload);
                    Assert.Contains("application/pem-certificate-chain", request.Headers.Accept.ToString());
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_chain!, Encoding.ASCII, "application/pem-certificate-chain") };
                    return Nonce(response);
            }
            throw new InvalidOperationException("675: unexpected ACME call " + path);
        }

        private string Order(string origin, bool finalizing = false)
        {
            var status = _authorization switch
            {
                "invalid" => "invalid",
                "valid" => finalizing ? "processing" : _chain != null ? "valid" : "ready",
                _ => "pending"
            };
            return JsonSerializer.Serialize(new
            {
                status,
                authorizations = new[] { origin + "/acme/authz/1" },
                finalize = origin + "/acme/finalize/1",
                certificate = status == "valid" ? origin + "/acme/cert/1" : null
            });
        }

        // The CSR: RSA 2048, for the wildcard only. Signed by the fake CA for 90 days, sent back with the CA after it.
        private void Issue(byte[] csrDer)
        {
            var request = CertificateRequest.LoadSigningRequest(csrDer, HashAlgorithmName.SHA256,
                CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions, RSASignaturePadding.Pkcs1);
            Assert.Equal("CN=*.247advisers.com", request.SubjectName.Name);
            var san = request.CertificateExtensions.Single(e => e.Oid?.Value == "2.5.29.17");
            Assert.Equal(new[] { "*.247advisers.com" }, new X509SubjectAlternativeNameExtension(san.RawData).EnumerateDnsNames().ToArray());
            Assert.Equal(2048, request.PublicKey.GetRSAPublicKey()!.KeySize);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            using var leaf = request.Create(_ca, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(90), RandomNumberGenerator.GetBytes(8));
            _chain = leaf.ExportCertificatePem() + "\n" + _ca.ExportCertificatePem() + "\n";
        }

        // Every signed request: ES256 over the account key, a nonce this server gave and nobody used, the URL it was
        // sent to, the key itself only on newAccount and the account's URL after that.
        private string Verify(Uri uri, string body)
        {
            using var jws = JsonDocument.Parse(body);
            var protectedPart = jws.RootElement.GetProperty("protected").GetString()!;
            var payloadPart = jws.RootElement.GetProperty("payload").GetString()!;
            var signature = FromBase64Url(jws.RootElement.GetProperty("signature").GetString()!);
            using var header = JsonDocument.Parse(FromBase64Url(protectedPart));
            var h = header.RootElement;

            Assert.Equal("ES256", h.GetProperty("alg").GetString());
            Assert.Equal(uri.ToString(), h.GetProperty("url").GetString());
            Assert.True(_nonces.Remove(h.GetProperty("nonce").GetString()!), "a nonce this server never gave, or one used twice");

            ECDsa key;
            if (h.TryGetProperty("jwk", out var jwk))
            {
                Assert.Equal("/acme/new-acct", uri.AbsolutePath);
                Assert.False(h.TryGetProperty("kid", out _));
                Assert.Equal("EC", jwk.GetProperty("kty").GetString());
                Assert.Equal("P-256", jwk.GetProperty("crv").GetString());
                key = ECDsa.Create(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = FromBase64Url(jwk.GetProperty("x").GetString()!), Y = FromBase64Url(jwk.GetProperty("y").GetString()!) }
                });
                _accountKey?.Dispose();
                _accountKey = key;
            }
            else
            {
                Assert.Equal(_accountUrl, h.GetProperty("kid").GetString());
                key = _accountKey!;
            }

            Assert.True(key.VerifyData(Encoding.ASCII.GetBytes(protectedPart + "." + payloadPart), signature, HashAlgorithmName.SHA256), "a bad JWS signature");
            return payloadPart.Length == 0 ? string.Empty : Encoding.UTF8.GetString(FromBase64Url(payloadPart));
        }

        // RFC 8555 section 8.4 and RFC 7638, written out again here rather than borrowed from the client.
        private string ExpectedTxt()
        {
            var q = _accountKey!.ExportParameters(false).Q;
            var jwk = "{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"" + ToBase64Url(q.X!) + "\",\"y\":\"" + ToBase64Url(q.Y!) + "\"}";
            var thumbprint = ToBase64Url(SHA256.HashData(Encoding.UTF8.GetBytes(jwk)));
            return ToBase64Url(SHA256.HashData(Encoding.UTF8.GetBytes(Token + "." + thumbprint)));
        }

        private HttpResponseMessage Created(string location, string body)
        {
            var response = Json(body, HttpStatusCode.Created);
            response.Headers.Location = new Uri(location);
            return Nonce(response);
        }

        private HttpResponseMessage Nonce(HttpResponseMessage response)
        {
            var nonce = Guid.NewGuid().ToString("N");
            _nonces.Add(nonce);
            response.Headers.Add("Replay-Nonce", nonce);
            return response;
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _ca.Dispose();
                _caKey.Dispose();
                _accountKey?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
