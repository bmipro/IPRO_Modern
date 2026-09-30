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
    // 493 (2026-09-17): the hourly reserve 10 -> 20 (bulk keeps 80 of the 100). On launch day a
    // sign-up, a password reset and an invoice must find a slot; with the bounded wait below, a
    // transactional send past the reserve is refused with an honest message, not queued for an hour.
    public int TransactionalReservePerHour { get; set; } = 20;
    // 493: the longest any send waits at the gate. Past this the answer is Deferred: a blast loop
    // pauses and resumes on a later pass (no attempt spent), a web request answers with the wait.
    // 90 seconds covers the minute window (at most 60) and stays well under Azure's 230-second
    // request cut-off; the hour window, which can be most of an hour, is never waited out in place.
    public int MaxSlotWaitSeconds { get; set; } = 90;

    // 531 (2026-09-30): Amazon SES, for the email an adviser sends to their own clients. App Service
    // settings Email__Ses__*; off until Streams names a stream.
    public SesSettings Ses { get; set; } = new();
}

// 531: Amazon SES in Canada (Central). Production access granted 2026-09-30 (50,000 a day, 14 a
// second). The router (RoutingEmailService) sends an adviser's client email through SES when its
// stream is listed in Streams and the keys are present; everything else stays on Provider. So the
// move is two setting changes, and so is the way back: clear Streams.
public class SesSettings
{
    public string Region { get; set; } = "ca-central-1";
    // For the ARNs a tenant is associated with; not a secret.
    public string AccountId { get; set; } = string.Empty;
    // The limited IAM login iPro sends with. Secrets: App Service settings only, never the repo.
    public string AccessKeyId { get; set; } = string.Empty;
    public string SecretAccessKey { get; set; } = string.Empty;
    // "" (off), "notify", "news" or "notify,news".
    public string Streams { get; set; } = string.Empty;
    // A pilot: while this lists adviser ids ("42" or "42,57"), only their mail on a switched-on stream
    // moves; empty means every adviser's.
    public string PilotAgentIds { get; set; } = string.Empty;
    public string NotifyDomain { get; set; } = "notify.iproadvisers.com";
    public string NewsDomain { get; set; } = "news.iproadvisers.com";
    // The mailbox part of the From address; replies go to the adviser (Reply-To), never here.
    public string SenderLocalPart { get; set; } = "mail";
    public string NotifyConfigurationSet { get; set; } = "ipro-notify";
    public string NewsConfigurationSet { get; set; } = "ipro-news";
    // One SES tenant per adviser, so SES can pause one adviser's mail without touching anyone else's.
    public bool UseTenants { get; set; } = true;
    // The account's maximum send rate; sends are paced to it.
    public int SendsPerSecond { get; set; } = 14;
    // SES bounce, complaint and delivery reports arrive through this SNS topic, at a URL carrying this
    // secret. Both must match before a report is read.
    public string EventTopicArn { get; set; } = string.Empty;
    public string EventSecret { get; set; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AccessKeyId) && !string.IsNullOrWhiteSpace(SecretAccessKey) && !string.IsNullOrWhiteSpace(Region);

    public bool StreamEnabled(string? stream) =>
        !string.IsNullOrWhiteSpace(stream)
        && (Streams ?? string.Empty).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(s => string.Equals(s, stream.Trim(), StringComparison.OrdinalIgnoreCase));

    public string DomainFor(string stream) => string.Equals(stream, "news", StringComparison.OrdinalIgnoreCase) ? NewsDomain : NotifyDomain;

    public string ConfigurationSetFor(string stream) =>
        string.Equals(stream, "news", StringComparison.OrdinalIgnoreCase) ? NewsConfigurationSet : NotifyConfigurationSet;
}
