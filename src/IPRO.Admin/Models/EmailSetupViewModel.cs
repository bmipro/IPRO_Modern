using System;
using IPRO.Entities;

namespace IPRO.Admin.Models;

public class EmailSetupViewModel
{
    // Pre-launch audit (2026-08-30): this screen graded SendGrid only, so after the Azure
    // Communication Services cutover it would tell a SuperAdmin -- mid-outage -- that the problem
    // is a SendGrid key that no longer matters, and point them at the wrong settings.
    public string Provider { get; set; } = "SendGrid";
    public bool IsAzureProvider => string.Equals(Provider, "Azure", StringComparison.OrdinalIgnoreCase);
    public bool HasAzureConnectionString { get; set; }
    public string AzureConnectionStringPreview { get; set; } = string.Empty;
    // True when the provider that is actually sending has what it needs.
    public bool HasSendingCredential => IsAzureProvider ? HasAzureConnectionString : HasSendGridApiKey;
    public string ProviderDisplayName => IsAzureProvider ? "Azure Communication Services" : "SendGrid";
    public bool HasSendGridApiKey { get; set; }
    public string SendGridApiKeyPreview { get; set; } = string.Empty;
    public string FromEmail { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;
    public string ReplyToEmail { get; set; } = string.Empty;
    public string EnvironmentName { get; set; } = string.Empty;
    public int RecentFailureCount { get; set; }
    public List<EmailSettingStatusViewModel> Settings { get; set; } = new();
    public List<EmailLogViewModel> RecentLogs { get; set; } = new();
}

public class EmailSettingStatusViewModel
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool IsConfigured { get; set; }
    public string HelpText { get; set; } = string.Empty;
}

public class EmailLogViewModel
{
    public int Id { get; set; }
    public int? AgentUserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    public static EmailLogViewModel FromLog(OperateLog log) => new()
    {
        Id = log.Id,
        AgentUserId = log.AgentUserId,
        Action = log.Action,
        Description = log.Description,
        CreatedAt = log.CreatedAt
    };
}
