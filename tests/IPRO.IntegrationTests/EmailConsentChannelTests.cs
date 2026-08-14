using System;
using IPRO.Business.Services;
using IPRO.Entities;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IPRO.IntegrationTests;

// Regression suite for the 2026-08-14 ultra-audit CRITICAL: drip campaigns sat entirely outside the
// consent system. The check was not merely omitted -- EmailChannel had no member for drip, so the
// gap was invisible to anyone reading EmailConsentService, and unsubscribed clients kept receiving
// every remaining step of a multi-week sequence. Testimonial requests had the same shape.
//
// These tests assert the rule for EVERY channel, so a future sender added without a consent check
// shows up here as a missing case rather than as a customer complaint.
public class EmailConsentChannelTests
{
    private static EmailConsentService NewService() =>
        new(null!, new ConfigurationBuilder().AddInMemoryCollection().Build());

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
}
