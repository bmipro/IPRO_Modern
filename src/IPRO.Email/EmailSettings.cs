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

    // 488 (2026-09-14): the platform's OWN open pixel and click redirect (EmailTrackingLinks),
    // built because the provider's tracking above is gated on 442. On by default; set
    // Email__PlatformTrackingEnabled=false on BOTH App Services to stop instrumenting mail --
    // the switch to use if Microsoft ever enables provider tracking, so the two do not stack.
    // Either flag being on means Email Activity shows Opened/Clicked instead of "not tracked".
    public bool PlatformTrackingEnabled { get; set; } = true;

    // 491 (2026-09-16): Azure Communication Services caps a subscription on the default limits at
    // 30 sends a minute and 100 an hour (Microsoft declined to raise them on 2026-09-16; re-apply
    // with 30 days of history). EmailSendGate paces every Azure send to these figures so a blast
    // is slowed rather than rejected. The reserve keeps a few slots a minute and an hour for
    // transactional mail (sign-in, invoices, portal invites) so a newsletter cannot hold a
    // password reset behind it. When Microsoft raises the quota: Email__SendsPerMinute and
    // Email__SendsPerHour on BOTH App Services, no deploy.
    public int SendsPerMinute { get; set; } = 30;
    public int SendsPerHour { get; set; } = 100;
    public int TransactionalReservePerMinute { get; set; } = 5;
    public int TransactionalReservePerHour { get; set; } = 10;
}
