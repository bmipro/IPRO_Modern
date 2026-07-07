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
                    DateOfBirth  = TryParseDate(GetField(csv, "Date of Birth", "Birthdate", "Birthday", "DOB")),
                    CompanyName  = GetField(csv, "Company", "Company Name", "Organization") ?? "",
                    Email        = email,
                    Email2       = GetField(csv, "Email 2", "E-mail Address 2", "EmailAddress2") ?? "",
                    Phone        = GetField(csv, "Phone", "Home Phone", "Home Number", "Home Phone 1") ?? "",
                    HomePhone2   = GetField(csv, "Home Phone 2", "Home Number 2") ?? "",
                    BusinessPhone = GetField(csv, "Business Phone", "Business Number", "Business Phone 1") ?? "",
                    BusinessPhone2 = GetField(csv, "Business Phone 2", "Business Number 2") ?? "",
                    CellPhone    = GetField(csv, "Mobile Phone", "Cell Phone", "Cell Number", "Cell Phone 1") ?? "",
                    CellPhone2   = GetField(csv, "Mobile Phone 2", "Cell Phone 2", "Cell Number 2") ?? "",
                    Fax          = GetField(csv, "Fax", "Fax Number", "Fax Phone", "Fax Number 1") ?? "",
                    Fax2         = GetField(csv, "Fax 2", "Fax Number 2") ?? "",
                    Address      = GetField(csv, "Address", "Business Street", "Home Street") ?? "",
                    UnitNumber   = GetField(csv, "Unit", "Unit Number", "Apt", "Suite") ?? "",
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
                DateOfBirth  = c.DateOfBirth?.ToString("yyyy-MM-dd") ?? "",
                CompanyName  = c.CompanyName,
                Email        = c.Email,
                Email2       = c.Email2,
                Phone        = c.Phone,
                HomePhone2   = c.HomePhone2,
                BusinessPhone = c.BusinessPhone,
                BusinessPhone2 = c.BusinessPhone2,
                CellPhone    = c.CellPhone,
                CellPhone2   = c.CellPhone2,
                Fax          = c.Fax,
                Fax2         = c.Fax2,
                Address      = c.Address,
                UnitNumber   = c.UnitNumber,
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

    private static DateTime? TryParseDate(string? value)
    {
        return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
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
    public string DateOfBirth { get; set; } = "";
    public string CompanyName { get; set; } = "";
    public string Email { get; set; } = "";
    public string Email2 { get; set; } = "";
    public string Phone { get; set; } = "";
    public string HomePhone2 { get; set; } = "";
    public string BusinessPhone { get; set; } = "";
    public string BusinessPhone2 { get; set; } = "";
    public string CellPhone { get; set; } = "";
    public string CellPhone2 { get; set; } = "";
    public string Fax { get; set; } = "";
    public string Fax2 { get; set; } = "";
    public string Address { get; set; } = "";
    public string UnitNumber { get; set; } = "";
    public string City { get; set; } = "";
    public string Province { get; set; } = "";
    public string PostalCode { get; set; } = "";
    public string Country { get; set; } = "";
    public bool Subscribed { get; set; }
}
