using IPRO.Admin.Models;
using IPRO.DataAccess.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Admin.Controllers;

[Authorize(Policy = "SuperAdmin")]
public class EmailSetupController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;

    public EmailSetupController(IUnitOfWork uow, IConfiguration configuration, IWebHostEnvironment environment)
    {
        _uow = uow;
        _configuration = configuration;
        _environment = environment;
    }

    public async Task<IActionResult> Index()
    {
        var provider = _configuration["Email:Provider"] ?? "SendGrid";
        var azureConnectionString = _configuration["Email:AzureCommunicationConnectionString"] ?? string.Empty;
        var apiKey = _configuration["Email:SendGridApiKey"] ?? string.Empty;
        var fromEmail = _configuration["Email:FromEmail"] ?? string.Empty;
        var fromName = _configuration["Email:FromName"] ?? string.Empty;
        var replyToEmail = _configuration["Email:ReplyToEmail"] ?? string.Empty;
        var recentLogs = (await _uow.OperateLogs.FindAsync(l =>
                l.Module == "Billing" &&
                (l.Action == "InvoiceEmailFailed" || l.Action == "InvoiceEmail" || l.Action == "BillingIssueEmail")))
            .OrderByDescending(l => l.CreatedAt)
            .Take(25)
            .Select(EmailLogViewModel.FromLog)
            .ToList();

        var model = new EmailSetupViewModel
        {
            Provider = provider,
            HasAzureConnectionString = IsAzureConnectionStringConfigured(azureConnectionString),
            AzureConnectionStringPreview = MaskApiKey(azureConnectionString),
            HasSendGridApiKey = IsSendGridKeyConfigured(apiKey),
            SendGridApiKeyPreview = MaskApiKey(apiKey),
            FromEmail = fromEmail,
            FromName = fromName,
            ReplyToEmail = replyToEmail,
            EnvironmentName = _environment.EnvironmentName,
            RecentLogs = recentLogs,
            RecentFailureCount = recentLogs.Count(l => l.Action == "InvoiceEmailFailed")
        };

        model.Settings = BuildSettings(model);
        return View(model);
    }

    private static bool IsAzureConnectionStringConfigured(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains("endpoint=", StringComparison.OrdinalIgnoreCase);

    private static List<EmailSettingStatusViewModel> BuildSettings(EmailSetupViewModel model) =>
    [
        new()
        {
            Name = "Email__Provider",
            Value = model.Provider,
            IsConfigured = true,
            HelpText = "Which service actually sends: \"Azure\" for Azure Communication Services, anything else for SendGrid. It must match on both ipro-prod-web and ipro-prod-admin -- a split leaves one app unable to send."
        },
        new()
        {
            Name = model.IsAzureProvider ? "Email__AzureCommunicationConnectionString" : "Email__SendGridApiKey",
            Value = model.IsAzureProvider ? model.AzureConnectionStringPreview : model.SendGridApiKeyPreview,
            IsConfigured = model.HasSendingCredential,
            HelpText = model.IsAzureProvider
                ? "Azure Communication Services connection string (starts with endpoint=). Required on both ipro-prod-web and ipro-prod-admin."
                : "SendGrid API key. It should start with SG. and must exist on both ipro-prod-web and ipro-prod-admin."
        },
        new()
        {
            Name = "Email__FromEmail",
            Value = string.IsNullOrWhiteSpace(model.FromEmail) ? "Missing" : model.FromEmail,
            IsConfigured = !string.IsNullOrWhiteSpace(model.FromEmail),
            HelpText = "Sender address. This exact email or its domain must be verified in SendGrid."
        },
        new()
        {
            Name = "Email__FromName",
            Value = string.IsNullOrWhiteSpace(model.FromName) ? "Missing" : model.FromName,
            IsConfigured = !string.IsNullOrWhiteSpace(model.FromName),
            HelpText = "Display name shown in recipients' inboxes."
        },
        new()
        {
            Name = "Email__ReplyToEmail",
            Value = string.IsNullOrWhiteSpace(model.ReplyToEmail) ? "Missing" : model.ReplyToEmail,
            IsConfigured = !string.IsNullOrWhiteSpace(model.ReplyToEmail),
            HelpText = "Replies go here. Use support or billing if users should be able to respond."
        }
    ];

    private static bool IsSendGridKeyConfigured(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.StartsWith("SG.", StringComparison.Ordinal) &&
        !value.Contains("YOUR_SENDGRID_KEY", StringComparison.OrdinalIgnoreCase);

    private static string MaskApiKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Missing";
        if (!value.StartsWith("SG.", StringComparison.Ordinal)) return "Configured but does not look like a SendGrid key";
        return value.Length <= 12 ? "SG..." : $"{value[..5]}...{value[^4..]}";
    }
}
