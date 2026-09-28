using IPRO.DataAccess;
using IPRO.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace IPRO.Web.Controllers;

// 527 (2026-09-27): Stripe's Connect webhook -- events from every connected account arrive here.
// The signature is checked with the endpoint's secret before the body is read as anything; an
// unknown event is acknowledged and ignored; a settlement is recorded once per event id, so
// Stripe's resends (they retry for days) never pay an invoice twice. Anonymous by nature: Stripe
// is the caller.
[AllowAnonymous]
public class PaymentsWebhookController : Controller
{
    private readonly IPRODbContext _db;
    private readonly StripeSettings _stripe;
    private readonly ILogger<PaymentsWebhookController> _logger;

    public PaymentsWebhookController(IPRODbContext db, IOptions<StripeSettings> stripe, ILogger<PaymentsWebhookController> logger)
    {
        _db = db;
        _stripe = stripe.Value;
        _logger = logger;
    }

    [HttpPost("/payments/stripe/webhook")]
    public async Task<IActionResult> Stripe()
    {
        using var reader = new StreamReader(Request.Body);
        var payload = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(payload)) return BadRequest();

        var signature = Request.Headers["Stripe-Signature"].ToString();
        if (!StripeWebhookSignature.IsValid(payload, signature, _stripe.WebhookSecret, DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
        {
            _logger.LogWarning("Stripe webhook rejected: the signature did not verify");
            return BadRequest();
        }

        var outcome = await StripeWebhookSettlement.ApplyAsync(_db, payload, DateTime.UtcNow, _logger);
        if (outcome == StripeWebhookOutcome.Malformed) return BadRequest();
        _logger.LogInformation("Stripe webhook {Outcome}", outcome);
        return Ok();
    }
}
