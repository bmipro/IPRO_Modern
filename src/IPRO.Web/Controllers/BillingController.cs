using System.Security.Claims;
using System.Text.Json;
using IPRO.Billing;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace IPRO.Web.Controllers;

[Authorize]
public class BillingController : Controller
{
    private readonly IBillingService _billing;
    private readonly IUnitOfWork _uow;
    private readonly IConfiguration _configuration;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public BillingController(IBillingService billing, IUnitOfWork uow, IConfiguration configuration)
    {
        _billing = billing;
        _uow = uow;
        _configuration = configuration;
    }

    public async Task<IActionResult> Index()
    {
        ViewBag.Packages     = await _billing.GetPackagesAsync();
        ViewBag.Subscription = await _billing.GetActiveSubscriptionAsync(AgentId);
        ViewBag.PendingChange = await _billing.GetPendingChangeAsync(AgentId);
        ViewBag.Invoices     = await _billing.GetInvoicesAsync(AgentId);
        ViewBag.PackageFeatures = await _uow.PackageFeatures.GetAllAsync();
        return View();
    }

    public async Task<IActionResult> Invoice(int id)
    {
        var invoice = await _uow.Invoices.FirstOrDefaultAsync(i => i.Id == id && i.AgentUserId == AgentId);
        if (invoice == null)
        {
            return NotFound();
        }

        invoice.Billing = await _uow.Billings.GetByIdAsync(invoice.BillingId) ?? invoice.Billing;
        invoice.LineItems = (await _uow.InvoiceLineItems.FindAsync(i => i.InvoiceId == invoice.Id))
            .OrderBy(i => i.SortOrder)
            .ToList();

        ViewBag.Agent = await _uow.AgentUsers.GetByIdAsync(AgentId);
        ViewBag.Package = invoice.Billing == null
            ? null
            : await _uow.BillingRules.GetByIdAsync(invoice.Billing.BillingRuleId);
        ViewBag.CompanyName = _configuration["BillingCompany:Name"] ?? "IPRO Advisers";
        ViewBag.CompanyEmail = _configuration["BillingCompany:Email"] ?? "billing@iproadvisers.com";
        ViewBag.CompanyWebsite = _configuration["BillingCompany:Website"] ?? "www.iProAdvisers.com";
        ViewBag.CompanyTaxNumber = _configuration["BillingCompany:TaxRegistrationNumber"] ?? "";

        return View(invoice);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Subscribe(int billingRuleId, BillingPeriod period)
    {
        var result = await _billing.CreateSubscriptionAsync(
            AgentId,
            billingRuleId,
            period,
            Url.ActionLink(nameof(PayPalReturn)) ?? $"{Request.Scheme}://{Request.Host}/Billing/PayPalReturn",
            Url.ActionLink(nameof(Cancel)) ?? $"{Request.Scheme}://{Request.Host}/Billing/Cancel");

        if (!result.Success)
        {
            TempData["Error"] = result.Message;
            return RedirectToAction(nameof(Index));
        }

        if (result.RequiresPayment && !string.IsNullOrWhiteSpace(result.ApprovalUrl))
        {
            return Redirect(result.ApprovalUrl);
        }

        TempData["Success"] = result.Message;
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> PayPalReturn(string token, string subscription_id)
    {
        var result = await _billing.CapturePaymentAsync(AgentId, !string.IsNullOrWhiteSpace(subscription_id) ? subscription_id : token);
        if (result.Success)
        {
            TempData["Success"] = result.Message;
        }
        else
        {
            TempData["Error"] = result.Message;
        }

        return RedirectToAction(nameof(Index));
    }

    public IActionResult Success() { TempData["Success"] = "Subscription activated!"; return RedirectToAction(nameof(Index)); }
    public async Task<IActionResult> Cancel(string? token)
    {
        var cancelled = await _billing.CancelPendingPaymentByOrderAsync(AgentId, token ?? string.Empty);
        TempData[cancelled ? "Warning" : "Error"] = cancelled
            ? "PayPal checkout was cancelled. You can choose a package again when you are ready."
            : "PayPal checkout was cancelled, but we could not find the pending checkout to close.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResumePayment(int invoiceId)
    {
        var result = await _billing.ResumePaymentAsync(
            AgentId,
            invoiceId,
            Url.ActionLink(nameof(PayPalReturn)) ?? $"{Request.Scheme}://{Request.Host}/Billing/PayPalReturn",
            Url.ActionLink(nameof(Cancel)) ?? $"{Request.Scheme}://{Request.Host}/Billing/Cancel");

        if (!result.Success)
        {
            TempData["Error"] = result.Message;
            return RedirectToAction(nameof(Index));
        }

        if (result.RequiresPayment && !string.IsNullOrWhiteSpace(result.ApprovalUrl))
        {
            return Redirect(result.ApprovalUrl);
        }

        TempData["Success"] = result.Message;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelPendingPayment(int invoiceId)
    {
        var cancelled = await _billing.CancelPendingPaymentAsync(AgentId, invoiceId);
        TempData[cancelled ? "Success" : "Error"] = cancelled
            ? "Pending payment cancelled. You can choose a package again when you are ready."
            : "We could not cancel that pending payment.";

        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelSubscription() { await _billing.CancelSubscriptionAsync(AgentId); TempData["Success"] = "Subscription cancelled."; return RedirectToAction(nameof(Index)); }

    [AllowAnonymous, HttpPost("/billing/webhook")]
    public async Task<IActionResult> Webhook()
    {
        using var reader = new StreamReader(Request.Body);
        var payload = await reader.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(payload))
        {
            return BadRequest();
        }

        using var document = JsonDocument.Parse(payload);
        var eventType = document.RootElement.TryGetProperty("event_type", out var eventTypeElement)
            ? eventTypeElement.GetString() ?? string.Empty
            : string.Empty;
        var resource = document.RootElement.TryGetProperty("resource", out var resourceElement)
            ? resourceElement
            : default;
        var amount = 0m;
        if (resource.ValueKind == JsonValueKind.Object &&
            resource.TryGetProperty("amount", out var amountElement) &&
            amountElement.ValueKind == JsonValueKind.Object &&
            amountElement.TryGetProperty("total", out var totalElement))
        {
            decimal.TryParse(totalElement.GetString(), out amount);
        }
        else if (resource.ValueKind == JsonValueKind.Object &&
            resource.TryGetProperty("amount", out amountElement) &&
            amountElement.ValueKind == JsonValueKind.Object &&
            amountElement.TryGetProperty("value", out var valueElement))
        {
            decimal.TryParse(valueElement.GetString(), out amount);
        }

        var headers = new PayPalWebhookHeaders
        {
            TransmissionId = Request.Headers["PayPal-Transmission-Id"].ToString(),
            TransmissionTime = Request.Headers["PayPal-Transmission-Time"].ToString(),
            TransmissionSignature = Request.Headers["PayPal-Transmission-Sig"].ToString(),
            CertificateUrl = Request.Headers["PayPal-Cert-Url"].ToString(),
            AuthenticationAlgorithm = Request.Headers["PayPal-Auth-Algo"].ToString()
        };

        var handled = await _billing.HandleWebhookAsync(eventType, payload, headers, amount);
        return handled ? Ok() : Unauthorized();
    }
}
