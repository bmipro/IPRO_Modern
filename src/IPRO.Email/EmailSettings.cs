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
}
