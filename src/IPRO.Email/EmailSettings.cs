namespace IPRO.Email;

public class EmailSettings
{
    // "SendGrid" (default, the historical provider) or "Azure" (Azure Communication Services,
    // adopted 2026-08-30 after the SendGrid account suspension). The registration in each app's
    // Program.cs switches implementations on this value; both classes stay in the codebase so a
    // provider incident is a config flip, not a deploy.
    public string Provider { get; set; } = "SendGrid";
    public string SendGridApiKey { get; set; } = string.Empty;
    public string AzureCommunicationConnectionString { get; set; } = string.Empty;
    public string FromEmail { get; set; } = "no-reply@iproadvisers.com";
    public string FromName { get; set; } = "IPRO Advisers";
    public string ReplyToEmail { get; set; } = "support@iproadvisers.com";

    // Whether the provider is injecting open/click tracking into what we send (TODO 444). ACS will
    // not enable user engagement tracking on a custom domain with default sending limits (442), so
    // until Microsoft lifts them there is no pixel and no rewritten link, and Opened/Clicked can
    // never populate. There is no API that reports this state, so it is configuration: flip
    // Email__EngagementTrackingEnabled=true on BOTH App Services once the domain Overview reads
    // Enabled. Until then Email Activity says "not tracked" instead of a misleading dash or zero.
    public bool EngagementTrackingEnabled { get; set; } = false;
}
