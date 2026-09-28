using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.Entities;
using IPRO.Utility;
using IPRO.Web.Infrastructure;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

// 527 (2026-09-27): the adviser's payment processors. Stripe first: the connection is Stripe's own
// sign-in and consent (Connect, Standard accounts); we keep the connected account's id and act for
// it with iPro's key, so nothing secret of the adviser's is stored and the money goes straight to
// them. Behind the client-invoicing entitlement, like the invoices themselves.
[Authorize]
public class PaymentsController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IStripeConnectService _stripe;
    private readonly IPackageEntitlementService _entitlements;
    private readonly IConfiguration _configuration;
    private readonly IDataProtector _stateProtector;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public PaymentsController(IPRODbContext db, IStripeConnectService stripe, IPackageEntitlementService entitlements, IConfiguration configuration, IDataProtectionProvider dataProtectionProvider)
    {
        _db = db;
        _stripe = stripe;
        _entitlements = entitlements;
        _configuration = configuration;
        _stateProtector = dataProtectionProvider.CreateProtector("IPRO.Web.Payments.StripeState.v1");
    }

    public async Task<IActionResult> Index()
    {
        var gate = await RequireAccessAsync();
        if (gate != null) return gate;
        return View(await BuildModelAsync());
    }

    // Stripe only redirects back to an address registered in the platform's Connect settings, so
    // the request first bounces to the canonical host (the Google Calendar precedent), then goes to
    // Stripe with a signed state that names the adviser and the minute.
    public async Task<IActionResult> StripeConnect()
    {
        var canonicalUrl = PortalUrlHelper.CanonicalRedirectUrlIfNeeded(Request, _configuration);
        if (canonicalUrl != null) return Redirect(canonicalUrl);

        var gate = await RequireAccessAsync();
        if (gate != null) return gate;

        if (!_stripe.IsConfigured)
        {
            TempData["Error"] = "Stripe is not set up on this platform yet.";
            return RedirectToAction(nameof(Index));
        }

        var state = _stateProtector.Protect($"{AgentId}|{DateTime.UtcNow.Ticks}");
        return Redirect(_stripe.BuildAuthorizationUrl(PortalUrlHelper.StripeRedirectUri(_configuration), state));
    }

    // The registered address, as a literal route (485's lesson with Google).
    [HttpGet("/Payments/StripeCallback")]
    public async Task<IActionResult> StripeCallback(string? code, string? state, string? error)
    {
        var gate = await RequireAccessAsync();
        if (gate != null) return gate;

        if (!string.IsNullOrWhiteSpace(error))
        {
            TempData["Error"] = "The Stripe connection was cancelled or refused.";
            return RedirectToAction(nameof(Index));
        }

        int agentIdFromState;
        try
        {
            var parts = _stateProtector.Unprotect(state ?? string.Empty).Split('|');
            agentIdFromState = int.Parse(parts[0]);
            if (DateTime.UtcNow.Ticks - long.Parse(parts[1]) > TimeSpan.FromMinutes(10).Ticks)
            {
                TempData["Error"] = "The Stripe connection request expired. Please try again.";
                return RedirectToAction(nameof(Index));
            }
        }
        catch
        {
            TempData["Error"] = "The Stripe connection request was invalid. Please try again.";
            return RedirectToAction(nameof(Index));
        }
        if (agentIdFromState != AgentId) return Forbid();

        if (string.IsNullOrWhiteSpace(code))
        {
            TempData["Error"] = "Stripe did not return an authorization code.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            var account = await _stripe.ExchangeCodeAsync(code);
            var connection = await _db.AgentPaymentConnections.FirstOrDefaultAsync(c => c.AgentUserId == AgentId && c.Provider == PaymentProviders.Stripe);
            if (connection == null)
            {
                connection = new AgentPaymentConnection { AgentUserId = AgentId, Provider = PaymentProviders.Stripe };
                await _db.AgentPaymentConnections.AddAsync(connection);
            }
            connection.ExternalAccountId = account.AccountId;
            connection.DisplayName = account.DisplayName;
            connection.IsLive = account.LiveMode;
            connection.IsActive = true;
            connection.ConnectedAt = DateTime.UtcNow;
            connection.DisconnectedAt = null;
            connection.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            TempData["Success"] = $"Stripe connected{(string.IsNullOrWhiteSpace(account.DisplayName) ? string.Empty : " as " + account.DisplayName)}. Card payments will settle your invoices on their own.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> StripeDisconnect()
    {
        var gate = await RequireAccessAsync();
        if (gate != null) return gate;

        var connection = await _db.AgentPaymentConnections.FirstOrDefaultAsync(c => c.AgentUserId == AgentId && c.Provider == PaymentProviders.Stripe && c.IsActive);
        if (connection == null)
        {
            TempData["Error"] = "Stripe is not connected.";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            await _stripe.DeauthorizeAsync(connection.ExternalAccountId);
        }
        catch (Exception)
        {
            // The local record closes regardless; the adviser can also revoke iPro from their Stripe dashboard.
        }
        connection.IsActive = false;
        connection.DisconnectedAt = DateTime.UtcNow;
        connection.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = "Stripe disconnected. Invoices go back to your Pay Now link, if you have one.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<PaymentsViewModel> BuildModelAsync()
    {
        var stripe = await _db.AgentPaymentConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.AgentUserId == AgentId && c.Provider == PaymentProviders.Stripe && c.IsActive);
        var paymentLink = await _db.AgentUsers.AsNoTracking().Where(a => a.Id == AgentId).Select(a => a.DefaultPaymentLink).FirstOrDefaultAsync();
        return new PaymentsViewModel { StripeConfigured = _stripe.IsConfigured, Stripe = stripe, PaymentLink = paymentLink };
    }

    private async Task<IActionResult?> RequireAccessAsync()
    {
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.ClientInvoicing);
        if (access.IsIncluded) return null;
        TempData["Error"] = access.UpgradeMessage;
        return RedirectToAction("Index", "Billing");
    }
}
