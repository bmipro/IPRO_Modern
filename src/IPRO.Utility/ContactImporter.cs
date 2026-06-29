using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using IPRO.Entities;
using Microsoft.Extensions.Logging;

namespace IPRO.Utility;

public interface IContactImporter
{
    Task<ImportResult> ImportCsvAsync(Stream stream, int agentId);
    Task<ImportResult> ImportVCardAsync(Stream stream, int agentId);
    byte[] ExportToCsvAsync(IEnumerable<Client> clients);
}

public record ImportResult(int Imported, int Skipped, int Errors, List<string> Messages);

public class ContactImporter : IContactImporter
{
    private readonly ILogger<ContactImporter> _logger;

    public ContactImporter(ILogger<ContactImporter> logger) => _logger = logger;

    // ── CSV Import ────────────────────────────────────────
    public async Task<ImportResult> ImportCsvAsync(Stream stream, int agentId)
    {
        var imported = 0; var skipped = 0; var errors = 0;
        var messages = new List<string>();
        var clients  = new List<Client>();

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
            HeaderValidated = null,
            TrimOptions = TrimOptions.Trim
        };

        using var csv = new CsvReader(reader, config);
        await csv.ReadAsync();
        csv.ReadHeader();

        while (await csv.ReadAsync())
        {
            try
            {
                var email = GetField(csv, "Email", "E-mail Address", "EmailAddress");
                if (string.IsNullOrWhiteSpace(email)) { skipped++; continue; }

                clients.Add(new Client
                {
                    AgentUserId  = agentId,
                    FirstName    = GetField(csv, "First Name", "FirstName", "Given Name") ?? "",
                    LastName     = GetField(csv, "Last Name", "LastName", "Surname") ?? "",
                    Email        = email,
                    Phone        = GetField(csv, "Phone", "Business Phone", "Mobile Phone", "Home Phone") ?? "",
                    Address      = GetField(csv, "Address", "Business Street", "Home Street") ?? "",
                    City         = GetField(csv, "City", "Business City", "Home City") ?? "",
                    Province     = GetField(csv, "Province", "State", "Business State") ?? "",
                    PostalCode   = GetField(csv, "PostalCode", "Postal Code", "ZIP", "Business Postal Code") ?? "",
                    Country      = GetField(csv, "Country", "Business Country") ?? "Canada",
                    CreatedAt    = DateTime.UtcNow,
                    UpdatedAt    = DateTime.UtcNow
                });
                imported++;
            }
            catch (Exception ex)
            {
                errors++;
                messages.Add($"Row {csv.CurrentIndex}: {ex.Message}");
                _logger.LogWarning(ex, "CSV import error at row {Row}", csv.CurrentIndex);
            }
        }

        return new ImportResult(imported, skipped, errors, messages);
    }

    // ── vCard Import ──────────────────────────────────────
    public async Task<ImportResult> ImportVCardAsync(Stream stream, int agentId)
    {
        var imported = 0; var skipped = 0; var errors = 0;
        var messages = new List<string>();

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = await reader.ReadToEndAsync();
        var cards   = content.Split("BEGIN:VCARD", StringSplitOptions.RemoveEmptyEntries);

        foreach (var card in cards)
        {
            try
            {
                var lines  = card.Split('\n').Select(l => l.Trim().TrimEnd('\r')).ToList();
                var email  = GetVCardField(lines, "EMAIL");
                if (string.IsNullOrWhiteSpace(email)) { skipped++; continue; }

                var fn     = GetVCardField(lines, "FN") ?? "";
                var n      = GetVCardField(lines, "N")?.Split(';') ?? Array.Empty<string>();
                var adr    = GetVCardField(lines, "ADR")?.Split(';') ?? Array.Empty<string>();

                imported++;
            }
            catch (Exception ex)
            {
                errors++;
                messages.Add($"vCard error: {ex.Message}");
            }
        }

        return new ImportResult(imported, skipped, errors, messages);
    }

    // ── CSV Export ────────────────────────────────────────
    public byte[] ExportToCsvAsync(IEnumerable<Client> clients)
    {
        using var ms     = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.UTF8);
        using var csv    = new CsvWriter(writer, CultureInfo.InvariantCulture);

        csv.WriteHeader<ClientCsvRecord>();
        csv.NextRecord();
        foreach (var c in clients)
        {
            csv.WriteRecord(new ClientCsvRecord
            {
                FirstName    = c.FirstName,
                LastName     = c.LastName,
                Email        = c.Email,
                Phone        = c.Phone,
                Address      = c.Address,
                City         = c.City,
                Province     = c.Province,
                PostalCode   = c.PostalCode,
                Country      = c.Country,
                Subscribed   = c.IsNewsletterSubscribed
            });
            csv.NextRecord();
        }
        writer.Flush();
        return ms.ToArray();
    }

    private static string? GetField(CsvReader csv, params string[] fieldNames)
    {
        foreach (var name in fieldNames)
        {
            try { var val = csv.GetField<string>(name); if (!string.IsNullOrWhiteSpace(val)) return val; }
            catch { /* field not found, try next */ }
        }
        return null;
    }

    private static string? GetVCardField(List<string> lines, string property) =>
        lines.FirstOrDefault(l => l.StartsWith(property + ":", StringComparison.OrdinalIgnoreCase)
                               || l.StartsWith(property + ";", StringComparison.OrdinalIgnoreCase))
             ?.Split(':', 2).ElementAtOrDefault(1);
}

public class ClientCsvRecord
{
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Address { get; set; } = "";
    public string City { get; set; } = "";
    public string Province { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string Country { get; set; } = "";
    public bool Subscribed { get; set; }
}
