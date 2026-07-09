using IPRO.Admin.Models;
using IPRO.DataAccess.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Admin.Controllers;

[Authorize]
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

    private static List<EmailSettingStatusViewModel> BuildSettings(EmailSetupViewModel model) =>
    [
        new()
        {
            Name = "Email__SendGridApiKey",
            Value = model.SendGridApiKeyPreview,
            IsConfigured = model.HasSendGridApiKey,
            HelpText = "SendGrid API key. It should start with SG. and must exist on both ipro-prod-web and ipro-prod-admin."
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
