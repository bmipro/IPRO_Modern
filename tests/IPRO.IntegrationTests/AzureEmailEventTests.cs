using System;
using System.IO;
using System.Linq;
using Xunit;
using IPRO.Web.Controllers;

namespace IPRO.IntegrationTests;

// TODO 433, the second half of the ACS migration (2026-08-31). SendGrid's webhook pushed OUR ids
// back to us in custom args; ACS returns only its own message id, so correlation now goes through
// the ProviderMessageId every dispatcher already persists at send time.
//
// The material difference, and the reason this is not a like-for-like port: ACS emits exactly two
// event types and SEVEN statuses, and NONE of them is a spam complaint or an unsubscribe. Under
// SendGrid, "mark as spam" in Gmail suppressed the client across every channel. ACS will never
// tell us. Our own unsubscribe links are provider-independent and still carry consent; the
// complaint-driven path is gone and is recorded as a known gap rather than pretended away.
//
// Approved compensation (owner, 2026-08-31): a HARD BOUNCE now suppresses. Nothing did before --
// SendGrid's bounce set a status and stopped. A months-long bounce rate is what got the SendGrid
// account terminated, so continuing to mail an address that does not exist is the exact behaviour
// that destroys sender reputation.
public class AzureEmailEventTests
{
    // ---- the seven ACS statuses map onto the vocabulary the recorders already speak -----------

    [Theory]
    [InlineData("Delivered", "delivered")]
    [InlineData("Bounced", "bounce")]      // hard bounce: address or domain does not exist
    [InlineData("Suppressed", "dropped")]  // ACS refused to send: this address hard-bounced before
    [InlineData("Failed", "dropped")]      // terminal non-delivery
    [InlineData("Quarantined", "dropped")] // receiver quarantined it (spam/bulk/phishing)
    [InlineData("FilteredSpam", "dropped")]// receiver rejected it as spam
    [InlineData("Expanded", "processed")]  // distribution group expanded; delivery still in flight
    public void Delivery_statuses_map_to_the_recorders_vocabulary(string acsStatus, string expected)
    {
        Assert.Equal(expected, AzureEmailEventsController.MapDeliveryStatus(acsStatus));
    }

    [Theory]
    [InlineData("View", "open")]
    [InlineData("view", "open")]
    [InlineData("Click", "click")]
    [InlineData("click", "click")]
    public void Engagement_types_map_to_open_and_click(string engagement, string expected)
    {
        Assert.Equal(expected, AzureEmailEventsController.MapEngagementType(engagement));
    }

    [Fact]
    public void An_unknown_status_is_ignored_rather_than_guessed()
    {
        // A status we do not recognise must not be coerced into a terminal one -- writing
        // "dropped" for something Microsoft adds later would mark good mail as failed.
        Assert.Null(AzureEmailEventsController.MapDeliveryStatus("SomethingNew"));
        Assert.Null(AzureEmailEventsController.MapDeliveryStatus(""));
        Assert.Null(AzureEmailEventsController.MapDeliveryStatus(null));
    }

    // ---- only a hard bounce suppresses -------------------------------------------------------

    [Fact]
    public void A_hard_bounce_suppresses_the_address()
    {
        Assert.True(AzureEmailEventsController.ShouldSuppressOnStatus("Bounced"));
    }

    [Theory]
    [InlineData("Quarantined")]
    [InlineData("FilteredSpam")]
    public void A_receivers_spam_filter_is_not_the_persons_choice(string status)
    {
        // Deliberate: quarantine and spam-filtering are the RECEIVING ORGANISATION's filter, not
        // the recipient's decision. Suppressing a client because their employer's filter caught
        // one newsletter would silently cut off someone who never asked to be cut off.
        Assert.False(AzureEmailEventsController.ShouldSuppressOnStatus(status));
    }

    [Theory]
    [InlineData("Delivered")]
    [InlineData("Suppressed")]
    [InlineData("Failed")]
    [InlineData("Expanded")]
    public void Nothing_else_suppresses(string status)
    {
        Assert.False(AzureEmailEventsController.ShouldSuppressOnStatus(status));
    }

    // ---- losing a bounce must cost us a redelivery, not silence -------------------------------

    [Fact]
    public void A_failed_bounce_event_withholds_the_acknowledgement()
    {
        // The H8 trade, carried over: 200 tells Event Grid "recorded, never send it again". Only
        // the event class that now drives suppression may withhold it, so Event Grid retries.
        Assert.False(AzureEmailEventsController.ShouldAcknowledge(suppressionEventFailed: true));
        Assert.True(AzureEmailEventsController.ShouldAcknowledge(suppressionEventFailed: false));
    }

    // ---- the Event Grid subscription handshake ------------------------------------------------

    [Fact]
    public void The_subscription_validation_handshake_echoes_the_code()
    {
        // Event Grid will not deliver a single event until the endpoint echoes this back.
        Assert.Equal("ABC-123", AzureEmailEventsController.ReadValidationCode(
            "Microsoft.EventGrid.SubscriptionValidationEvent",
            "{\"validationCode\":\"ABC-123\",\"validationUrl\":\"https://x\"}"));
        Assert.Null(AzureEmailEventsController.ReadValidationCode(
            "Microsoft.Communication.EmailDeliveryReportReceived",
            "{\"validationCode\":\"ABC-123\"}"));
    }

    // ---- the endpoint is wired, anonymous, and secret-protected ------------------------------

    [Fact]
    public void The_endpoint_is_anonymous_but_not_open()
    {
        var src = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\AzureEmailEventsController.cs"));
        // Event Grid cannot authenticate as a user, so the action is anonymous...
        Assert.Contains("[AllowAnonymous]", src);
        Assert.Contains("IgnoreAntiforgeryToken", src);
        // ...and therefore MUST carry its own shared secret, compared in fixed time.
        Assert.Contains("Email:AzureEventWebhookSecret", src);
        Assert.Contains("FixedTimeEquals", src);
    }

    [Fact]
    public void Correlation_covers_every_sender_that_records_delivery()
    {
        // ACS gives only its message id, so the resolver must look across every table whose
        // dispatcher persists ProviderMessageId. Missing one means that sender's events are
        // silently discarded -- exactly the bug that left Card and Letter "Delivered" columns
        // blank for their entire existence before 2026-08-08.
        var src = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\AzureEmailEventsController.cs"));
        foreach (var table in new[]
                 {
                     "NewsLetterRecipients", "DripCampaignStepSends", "ECardRecipients",
                     "ELetterRecipients", "PollRecipients", "DidYouKnowEmailQueueItems"
                 })
        {
            Assert.Contains(table, src);
        }
    }

    [Fact]
    public void The_sendgrid_webhook_still_exists_for_rollback()
    {
        // Email:Provider can flip back to SendGrid at any time; its webhook must not have been
        // deleted in the excitement of building this one.
        var src = File.ReadAllText(FindRepoFile(@"src\IPRO.Web\Controllers\NewsletterController.cs"));
        Assert.Contains("SendGridEvents", src);
    }

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }
}
