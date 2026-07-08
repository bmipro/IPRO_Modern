using System.Security.Claims;
using IPRO.Billing;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Web.Controllers;

[Authorize]
public class BillingController : Controller
{
    private readonly IBillingService _billing;
    private readonly IUnitOfWork _uow;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public BillingController(IBillingService billing, IUnitOfWork uow)
    {
        _billing = billing;
        _uow = uow;
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

    public async Task<IActionResult> PayPalReturn(string token)
    {
        var result = await _billing.CapturePaymentAsync(AgentId, token);
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
    public async Task<IActionResult> Webhook([FromBody] PayPalWebhookPayload p)
    {
        await _billing.HandleWebhookAsync(p.EventType, p.Resource?.Id ?? "", p.Resource?.TransactionId ?? "", p.Resource?.Amount?.Value ?? 0);
        return Ok();
    }
}
public record PayPalWebhookPayload(string EventType, WebhookResource? Resource);
public record WebhookResource(string Id, string TransactionId, WebhookAmount? Amount);
public record WebhookAmount(decimal Value);
