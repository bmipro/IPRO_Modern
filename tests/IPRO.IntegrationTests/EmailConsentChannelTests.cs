using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using IPRO.Business.Services;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// Regression suite for the 2026-08-14 ultra-audit CRITICAL: drip campaigns sat entirely outside the
// consent system. The check was not merely omitted -- EmailChannel had no member for drip, so the
// gap was invisible to anyone reading EmailConsentService, and unsubscribed clients kept receiving
// every remaining step of a multi-week sequence. Testimonial requests had the same shape.
//
// These tests assert the rule for EVERY channel, so a future sender added without a consent check
// shows up here as a missing case rather than as a customer complaint.
//
// This class covers the READ (IsSuppressed). EmailConsentWriteTests below covers the WRITE, which
// is where the second, larger hole was: until 2026-08-17 the preferences page was the only thing in
// the product that could set EmailOptOutAt at all.
public class EmailConsentChannelTests
{
    private static EmailConsentService NewService() =>
        new(null!,
            new ConfigurationBuilder().AddInMemoryCollection().Build(),
            NullLogger<EmailConsentService>.Instance,
            Array.Empty<IUnsubscribeNotifier>());

    private static Client OptedOutClient() => new()
    {
        Email = "opted.out@example.com",
        IsNewsletterSubscribed = true,       // deliberately still true: the global opt-out must win
        EmailOptOutAt = DateTime.UtcNow.AddDays(-1)
    };

    private static Client SubscribedClient() => new()
    {
        Email = "subscribed@example.com",
        IsNewsletterSubscribed = true
    };

    [Theory]
    [InlineData(EmailChannel.Newsletter)]
    [InlineData(EmailChannel.ECard)]
    [InlineData(EmailChannel.ELetter)]
    [InlineData(EmailChannel.Poll)]
    [InlineData(EmailChannel.DidYouKnow)]
    [InlineData(EmailChannel.DripCampaign)]
    [InlineData(EmailChannel.TestimonialRequest)]
    public void An_opted_out_client_is_suppressed_on_every_channel(EmailChannel channel)
    {
        Assert.True(NewService().IsSuppressed(OptedOutClient(), channel));
    }

    [Theory]
    [InlineData(EmailChannel.Newsletter)]
    [InlineData(EmailChannel.ECard)]
    [InlineData(EmailChannel.ELetter)]
    [InlineData(EmailChannel.Poll)]
    [InlineData(EmailChannel.DidYouKnow)]
    [InlineData(EmailChannel.DripCampaign)]
    [InlineData(EmailChannel.TestimonialRequest)]
    public void A_subscribed_client_is_not_suppressed_on_any_channel(EmailChannel channel)
    {
        Assert.False(NewService().IsSuppressed(SubscribedClient(), channel));
    }

    // The one documented exception, and it needs both halves: a SuperAdmin-marked greeting design
    // AND the client explicitly choosing to keep greetings after unsubscribing.
    [Fact]
    public void A_greeting_ecard_survives_opt_out_only_with_both_halves()
    {
        var service = NewService();
        var client = OptedOutClient();

        Assert.True(service.IsSuppressed(client, EmailChannel.ECard, designSurvivesOptOut: true));

        client.GreetingsOptInAt = DateTime.UtcNow;
        Assert.False(service.IsSuppressed(client, EmailChannel.ECard, designSurvivesOptOut: true));

        // The opt-in alone must not reopen the other channels.
        Assert.True(service.IsSuppressed(client, EmailChannel.DripCampaign));
        Assert.True(service.IsSuppressed(client, EmailChannel.ECard, designSurvivesOptOut: false));
    }

    [Fact]
    public void A_client_with_no_email_address_is_always_suppressed()
    {
        Assert.True(NewService().IsSuppressed(new Client { Email = "" }, EmailChannel.DripCampaign));
    }

    // IsNewsletterSubscribed governs the two BULK BROADCAST channels. A contact who arrived through
    // a website form without ticking "send me the newsletter" is created with this false and no
    // opt-out, and mailing them a poll is the same CASL problem as mailing them a newsletter --
    // PollDispatcher filtered on exactly this flag from the day it was written.
    [Theory]
    [InlineData(EmailChannel.Newsletter)]
    [InlineData(EmailChannel.Poll)]
    public void A_client_who_never_opted_into_bulk_mail_gets_no_broadcast(EmailChannel channel)
    {
        var client = SubscribedClient();
        client.IsNewsletterSubscribed = false;

        Assert.True(NewService().IsSuppressed(client, channel));
    }

    // The other side of the same rule. Those channels are not broadcasts -- the agent picks the
    // recipient one at a time, or the person asked for that specific thing -- and none of them has
    // ever consulted this flag. An agent unticking the newsletter box must not silently stop that
    // client's birthday cards or their meeting-follow-up letters.
    [Theory]
    [InlineData(EmailChannel.ECard)]
    [InlineData(EmailChannel.ELetter)]
    [InlineData(EmailChannel.DidYouKnow)]
    [InlineData(EmailChannel.DripCampaign)]
    [InlineData(EmailChannel.TestimonialRequest)]
    public void Unticking_the_newsletter_flag_does_not_suppress_the_one_to_one_channels(EmailChannel channel)
    {
        var client = SubscribedClient();
        client.IsNewsletterSubscribed = false;

        Assert.False(NewService().IsSuppressed(client, channel));
    }
}

// The WRITE half (JOBS-4). Before 2026-08-17, EmailOptOutAt was written in exactly two places, both
// inside EmailPreferencesController -- so a person who hit "this is spam" in their mail client, or
// used the one-click header SendGrid reports back as an `unsubscribe` event, was recorded as
// unsubscribed on NOTHING. EmailDeliveryTracker mapped `spamreport` to a row status and dropped
// `unsubscribe` entirely; NewsLetterService set only IsNewsletterSubscribed. All four other channels
// kept mailing them. That is the CASL exposure.
//
// These tests drive the REAL webhook recorders rather than calling SuppressAllAsync directly. A test
// that calls the suppression method proves the method works; only driving the recorder proves the
// recorder calls it, and the recorder not calling it was the entire bug.
public class EmailConsentWriteTests
{
    [Theory]
    [InlineData("ecard", "spamreport")]
    [InlineData("ecard", "unsubscribe")]
    [InlineData("ecard", "group_unsubscribe")]
    [InlineData("eletter", "spamreport")]
    [InlineData("eletter", "unsubscribe")]
    [InlineData("eletter", "group_unsubscribe")]
    [InlineData("poll", "spamreport")]
    [InlineData("poll", "unsubscribe")]
    [InlineData("poll", "group_unsubscribe")]
    [InlineData("didyouknow", "spamreport")]
    [InlineData("didyouknow", "unsubscribe")]
    [InlineData("didyouknow", "group_unsubscribe")]
    public async Task A_complaint_on_any_channel_suppresses_every_channel(string entityKind, string eventName)
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var seed = await SeedAsync(db);
        var recipientId = entityKind switch
        {
            "ecard" => seed.ECardRecipientId,
            "eletter" => seed.ELetterRecipientId,
            "poll" => seed.PollRecipientId,
            _ => seed.DidYouKnowQueueItemId
        };

        await NewTracker(db).RecordAsync(entityKind, recipientId, eventName, "msg-1", null, DateTime.UtcNow);

        db.ChangeTracker.Clear();
        var client = await db.Clients.FirstAsync(c => c.Id == seed.ClientId);

        Assert.NotNull(client.EmailOptOutAt);
        Assert.False(client.IsNewsletterSubscribed);

        var consent = NewConsent(db);
        foreach (var channel in Enum.GetValues<EmailChannel>())
        {
            Assert.True(consent.IsSuppressed(client, channel),
                $"A '{eventName}' on a {entityKind} left {channel} unsuppressed.");
        }
    }

    [Theory]
    [InlineData("spamreport")]
    [InlineData("unsubscribe")]
    [InlineData("group_unsubscribe")]
    public async Task A_complaint_on_a_newsletter_suppresses_every_channel(string eventName)
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var seed = await SeedAsync(db);

        await NewNewsletterService(db)
            .RecordRecipientEventAsync(seed.NewsLetterRecipientId, eventName, "msg-1", null, DateTime.UtcNow);

        db.ChangeTracker.Clear();
        var client = await db.Clients.FirstAsync(c => c.Id == seed.ClientId);

        // This used to set IsNewsletterSubscribed and nothing else -- the client stayed reachable on
        // every other channel.
        Assert.NotNull(client.EmailOptOutAt);
        Assert.True(NewConsent(db).IsSuppressed(client, EmailChannel.ECard));
    }

    [Theory]
    [InlineData("spamreport")]
    [InlineData("unsubscribe")]
    [InlineData("group_unsubscribe")]
    public async Task A_complaint_on_a_drip_step_suppresses_and_cancels_the_campaign(string eventName)
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var seed = await SeedAsync(db);

        // RecordDripStepEventAsync had no case for these events at all: they fell out of the switch
        // and the campaign carried on sending its remaining steps on schedule.
        await NewNewsletterService(db)
            .RecordDripStepEventAsync(seed.DripStepSendId, eventName, "msg-1", null, DateTime.UtcNow);

        db.ChangeTracker.Clear();
        var client = await db.Clients.FirstAsync(c => c.Id == seed.ClientId);
        var enrollment = await db.DripCampaignEnrollments.FirstAsync(e => e.Id == seed.EnrollmentId);

        Assert.NotNull(client.EmailOptOutAt);
        Assert.Equal(DripCampaignEnrollmentStatus.Cancelled, enrollment.Status);
    }

    // Invariant 8: an unsubscribe means the mail was delivered and read. Recording it must not
    // rewrite the delivery history -- the Delivered column is the thing EmailDeliveryTracker exists
    // to populate, and marking these rows Failed would quietly corrupt every send's statistics.
    [Fact]
    public async Task An_unsubscribe_does_not_mark_a_delivered_recipient_as_failed()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var seed = await SeedAsync(db);
        var tracker = NewTracker(db);

        await tracker.RecordAsync("ecard", seed.ECardRecipientId, "delivered", "msg-1", null, DateTime.UtcNow);
        await tracker.RecordAsync("ecard", seed.ECardRecipientId, "unsubscribe", "msg-1", null, DateTime.UtcNow);

        db.ChangeTracker.Clear();
        var recipient = await db.ECardRecipients.FirstAsync(r => r.Id == seed.ECardRecipientId);

        Assert.Equal(ECardRecipientStatuses.Sent, recipient.Status);
        Assert.NotNull(recipient.DeliveredAt);
        Assert.Null(recipient.BouncedAt);
        Assert.Equal(string.Empty, recipient.FailureReason);

        // ...and the suppression still happened.
        Assert.NotNull((await db.Clients.FirstAsync(c => c.Id == seed.ClientId)).EmailOptOutAt);
    }

    // SendGrid redelivers events on its own retry schedule, and a person can click unsubscribe
    // twice. The second pass must not re-retire queue items, re-cancel enrollments or re-notify.
    [Fact]
    public async Task Suppression_is_idempotent()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var seed = await SeedAsync(db);
        var client = await db.Clients.FirstAsync(c => c.Id == seed.ClientId);
        var consent = NewConsent(db);

        var first = await consent.SuppressAllAsync(client, "test");
        var second = await consent.SuppressAllAsync(client, "test");

        Assert.False(first.WasAlreadySuppressed);
        Assert.True(second.WasAlreadySuppressed);
        Assert.Equal(0, second.QueuedItemsRetired);
        Assert.Equal(0, second.EnrollmentsCancelled);

        // The first pass's opt-out timestamp survives; a repeat event must not move it forward.
        var optOutAfterFirst = client.EmailOptOutAt;
        await consent.SuppressAllAsync(client, "test");
        Assert.Equal(optOutAfterFirst, client.EmailOptOutAt);
    }

    [Fact]
    public async Task Suppression_retires_queued_mail_and_cancels_enrollments()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var seed = await SeedAsync(db);
        var client = await db.Clients.FirstAsync(c => c.Id == seed.ClientId);

        var result = await NewConsent(db).SuppressAllAsync(client, "test");

        Assert.Equal(1, result.QueuedItemsRetired);
        Assert.Equal(1, result.EnrollmentsCancelled);

        db.ChangeTracker.Clear();
        var queued = await db.DidYouKnowEmailQueueItems.FirstAsync(q => q.Id == seed.DidYouKnowQueueItemId);
        Assert.Equal(DidYouKnowQueueStatuses.Failed, queued.Status);
        Assert.NotNull(queued.SentAtUtc);   // retired, so the dispatcher will never pick it up
    }

    [Fact]
    public async Task Resubscribe_is_the_exact_inverse_of_the_consent_flags()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var seed = await SeedAsync(db);
        var client = await db.Clients.FirstAsync(c => c.Id == seed.ClientId);
        var consent = NewConsent(db);

        await consent.SuppressAllAsync(client, "test");
        await consent.ResubscribeAsync(client);

        Assert.Null(client.EmailOptOutAt);
        Assert.True(client.IsNewsletterSubscribed);
        foreach (var channel in Enum.GetValues<EmailChannel>())
        {
            Assert.False(consent.IsSuppressed(client, channel));
        }

        // But it does NOT revive the specific sends they were opted out of at the time. Consent to
        // receive future mail is not a request to be sent the backlog they missed.
        db.ChangeTracker.Clear();
        var queued = await db.DidYouKnowEmailQueueItems.FirstAsync(q => q.Id == seed.DidYouKnowQueueItemId);
        Assert.Equal(DidYouKnowQueueStatuses.Failed, queued.Status);
        var enrollment = await db.DripCampaignEnrollments.FirstAsync(e => e.Id == seed.EnrollmentId);
        Assert.Equal(DripCampaignEnrollmentStatus.Cancelled, enrollment.Status);
    }

    // Invariant 6: transactional mail must always send. RecordAsync dispatches only on the four
    // marketing kinds, so a complaint reported against anything else cannot reach the write path
    // and cannot suppress a password reset or an invoice link.
    [Fact]
    public async Task An_unrecognised_entity_kind_suppresses_nothing()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();

        var seed = await SeedAsync(db);

        await NewTracker(db).RecordAsync("passwordreset", seed.ClientId, "spamreport", "msg-1", null, DateTime.UtcNow);

        db.ChangeTracker.Clear();
        Assert.Null((await db.Clients.FirstAsync(c => c.Id == seed.ClientId)).EmailOptOutAt);
    }

    private static EmailConsentService NewConsent(IPRODbContext db) =>
        new(db,
            new ConfigurationBuilder().AddInMemoryCollection().Build(),
            NullLogger<EmailConsentService>.Instance,
            Array.Empty<IUnsubscribeNotifier>());   // no notifier: IPRO.Admin registers none either

    private static EmailDeliveryTracker NewTracker(IPRODbContext db) =>
        new(db, NullLogger<EmailDeliveryTracker>.Instance, NewConsent(db));

    private static NewsLetterService NewNewsletterService(IPRODbContext db) =>
        new(new UnitOfWork(db), NewConsent(db), db);

    private sealed record Seed(
        int ClientId,
        int ECardRecipientId,
        int ELetterRecipientId,
        int PollRecipientId,
        int DidYouKnowQueueItemId,
        int NewsLetterRecipientId,
        int EnrollmentId,
        int DripStepSendId);

    // One client reachable on all six senders at once, plus one queued Did You Know item and one
    // active drip enrollment for the sweep assertions.
    private static async Task<Seed> SeedAsync(IPRODbContext db)
    {
        var rule = new BillingRule { PackageName = $"Pkg-{Guid.NewGuid():N}"[..20], MonthlyPrice = 60m };
        db.Add(rule);
        await db.SaveChangesAsync();

        var agent = new AgentUser
        {
            UserName = $"consent-{Guid.NewGuid():N}"[..20],
            Email = "consent.agent@example.com",
            FirstName = "Consent",
            LastName = "Agent",
            DomainName = $"consent-{Guid.NewGuid():N}"[..24],
            PackageId = rule.Id
        };
        db.Add(agent);
        await db.SaveChangesAsync();

        var client = new Client
        {
            AgentUserId = agent.Id,
            FirstName = "Chris",
            LastName = "Complainer",
            Email = "chris.complainer@example.com",
            IsNewsletterSubscribed = true
        };
        db.Clients.Add(client);

        var ecard = new ECard { AgentUserId = agent.Id, Occasion = "Birthday", Subject = "Happy birthday" };
        var eletter = new ELetter { AgentUserId = agent.Id, TemplateKey = "welcome", Subject = "Welcome" };
        var poll = new PollSurvey { AgentUserId = agent.Id, Title = "Q3", Subject = "One question" };
        var article = new Article { AgentUserId = agent.Id, Title = "Did you know", Content = "<p>x</p>" };
        var newsletter = new NewsLetter { AgentUserId = agent.Id, Subject = "August", HtmlBody = "<p>x</p>" };
        var campaign = new DripCampaign { AgentUserId = agent.Id, Name = "Onboarding" };
        db.AddRange(ecard, eletter, poll, article, newsletter, campaign);
        await db.SaveChangesAsync();

        var step = new DripCampaignStep { DripCampaignId = campaign.Id, Subject = "Step 1", HtmlBody = "<p>x</p>" };
        db.Add(step);
        await db.SaveChangesAsync();

        var enrollment = new DripCampaignEnrollment
        {
            AgentUserId = agent.Id,
            DripCampaignId = campaign.Id,
            ClientId = client.Id,
            Status = DripCampaignEnrollmentStatus.Active
        };
        db.Add(enrollment);
        await db.SaveChangesAsync();

        var ecardRecipient = new ECardRecipient { ECardId = ecard.Id, ClientId = client.Id, Email = client.Email };
        var eletterRecipient = new ELetterRecipient { ELetterId = eletter.Id, ClientId = client.Id, Email = client.Email };
        var pollRecipient = new PollRecipient { PollSurveyId = poll.Id, ClientId = client.Id, Email = client.Email };
        var newsletterRecipient = new NewsLetterRecipient { NewsLetterId = newsletter.Id, ClientId = client.Id, Email = client.Email };
        var queueItem = new DidYouKnowEmailQueueItem
        {
            ArticleId = article.Id,
            ClientId = client.Id,
            ScheduledForUtc = DateTime.UtcNow.AddDays(1)
        };
        var stepSend = new DripCampaignStepSend
        {
            DripCampaignEnrollmentId = enrollment.Id,
            DripCampaignStepId = step.Id,
            Email = client.Email
        };
        db.AddRange(ecardRecipient, eletterRecipient, pollRecipient, newsletterRecipient, queueItem, stepSend);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return new Seed(client.Id, ecardRecipient.Id, eletterRecipient.Id, pollRecipient.Id,
            queueItem.Id, newsletterRecipient.Id, enrollment.Id, stepSend.Id);
    }
}
