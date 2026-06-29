using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPRO.Utility;

public interface IPleskHostingService
{
    Task<PleskDomain?> CreateDomainAsync(string domainName, string username, string password, string email);
    Task<bool> DeleteDomainAsync(string domainName);
    Task<PleskDomain?> GetDomainAsync(string domainName);
    Task<bool> SuspendDomainAsync(string domainName);
    Task<bool> UnsuspendDomainAsync(string domainName);
    Task<string> GenerateAutoLoginUrlAsync(string username);
    Task<bool> CreateEmailAsync(string email, string password, string domainName);
}

public class PleskDomain
{
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string HomeDir { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class PleskHostingService : IPleskHostingService
{
    private readonly HttpClient _http;
    private readonly ILogger<PleskHostingService> _logger;
    private readonly string _baseUrl;
    private readonly string _apiKey;

    public PleskHostingService(IConfiguration config, IHttpClientFactory factory, ILogger<PleskHostingService> logger)
    {
        _baseUrl = config["Plesk:ApiUrl"] ?? "https://your-plesk-server:8443";
        _apiKey  = config["Plesk:ApiKey"] ?? "";
        _logger  = logger;
        _http    = factory.CreateClient("plesk");
        _http.DefaultRequestHeaders.Add("X-API-Key", _apiKey);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<PleskDomain?> CreateDomainAsync(string domainName, string username, string password, string email)
    {
        try
        {
            // Step 1 — Create the hosting customer
            var customerBody = JsonSerializer.Serialize(new
            {
                name = username,
                username = username,
                password = password,
                email = email,
                send_welcome_email = false
            });

            var customerRes = await _http.PostAsync($"{_baseUrl}/api/v2/customers",
                new StringContent(customerBody, Encoding.UTF8, "application/json"));

            if (!customerRes.IsSuccessStatusCode)
            {
                _logger.LogError("Plesk customer creation failed for {Domain}: {Status}", domainName, customerRes.StatusCode);
                return null;
            }

            var customerJson = await customerRes.Content.ReadAsStringAsync();
            var customerId   = JsonDocument.Parse(customerJson).RootElement.GetProperty("id").GetInt32();

            // Step 2 — Create the domain/subscription under that customer
            var domainBody = JsonSerializer.Serialize(new
            {
                name            = domainName,
                owner_client    = new { id = customerId },
                hosting_type    = "virtual",
                base_domain     = new { name = domainName },
                plan            = new { name = "Default Domain" }
            });

            var domainRes = await _http.PostAsync($"{_baseUrl}/api/v2/domains",
                new StringContent(domainBody, Encoding.UTF8, "application/json"));

            if (!domainRes.IsSuccessStatusCode)
            {
                _logger.LogError("Plesk domain creation failed for {Domain}: {Status}", domainName, domainRes.StatusCode);
                return null;
            }

            _logger.LogInformation("Plesk domain created: {Domain}", domainName);

            return new PleskDomain
            {
                Name      = domainName,
                Status    = "active",
                Username  = username,
                CreatedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plesk CreateDomain error for {Domain}", domainName);
            return null;
        }
    }

    public async Task<bool> DeleteDomainAsync(string domainName)
    {
        try
        {
            var res = await _http.DeleteAsync($"{_baseUrl}/api/v2/domains/{domainName}");
            _logger.LogInformation("Plesk domain deleted: {Domain} — {Status}", domainName, res.StatusCode);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plesk DeleteDomain error for {Domain}", domainName);
            return false;
        }
    }

    public async Task<PleskDomain?> GetDomainAsync(string domainName)
    {
        try
        {
            var res  = await _http.GetAsync($"{_baseUrl}/api/v2/domains/{domainName}");
            if (!res.IsSuccessStatusCode) return null;
            var json = await res.Content.ReadAsStringAsync();
            var doc  = JsonDocument.Parse(json).RootElement;
            return new PleskDomain
            {
                Name   = doc.GetProperty("name").GetString() ?? domainName,
                Status = doc.GetProperty("status").GetString() ?? "unknown"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plesk GetDomain error for {Domain}", domainName);
            return null;
        }
    }

    public async Task<bool> SuspendDomainAsync(string domainName)
    {
        var body = JsonSerializer.Serialize(new { status = "suspended" });
        var res  = await _http.PutAsync($"{_baseUrl}/api/v2/domains/{domainName}",
            new StringContent(body, Encoding.UTF8, "application/json"));
        return res.IsSuccessStatusCode;
    }

    public async Task<bool> UnsuspendDomainAsync(string domainName)
    {
        var body = JsonSerializer.Serialize(new { status = "active" });
        var res  = await _http.PutAsync($"{_baseUrl}/api/v2/domains/{domainName}",
            new StringContent(body, Encoding.UTF8, "application/json"));
        return res.IsSuccessStatusCode;
    }

    public async Task<string> GenerateAutoLoginUrlAsync(string username)
    {
        try
        {
            // Plesk REST API: create a one-time login session
            var body = JsonSerializer.Serialize(new { login = username });
            var res  = await _http.PostAsync($"{_baseUrl}/api/v2/auth/keys",
                new StringContent(body, Encoding.UTF8, "application/json"));
            if (!res.IsSuccessStatusCode) return string.Empty;
            var json = await res.Content.ReadAsStringAsync();
            var key  = JsonDocument.Parse(json).RootElement.GetProperty("key").GetString() ?? "";
            return $"{_baseUrl}/enterprise/rsession/?PLESKSESSID={key}";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plesk GenerateAutoLogin error for {User}", username);
            return string.Empty;
        }
    }

    public async Task<bool> CreateEmailAsync(string email, string password, string domainName)
    {
        try
        {
            var parts  = email.Split('@');
            var body   = JsonSerializer.Serialize(new
            {
                name     = parts[0],
                mailbox  = new { enabled = true, password = password }
            });
            var res = await _http.PostAsync($"{_baseUrl}/api/v2/domains/{domainName}/mail",
                new StringContent(body, Encoding.UTF8, "application/json"));
            _logger.LogInformation("Plesk email created: {Email} — {Status}", email, res.StatusCode);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plesk CreateEmail error: {Email}", email);
            return false;
        }
    }
}
