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

// 527 (2026-09-28): the adviser's payment methods. The adviser enters what each service gave them
// -- a PayPal.me name, a Stripe Payment Link, a Square link, an Interac e-Transfer email, any other
// link -- and every invoice they send shows a Pay button per method. No account on iPro's side.
// The Stripe Connect actions (slice 1, 2026-09-27) stay for the day the platform has keys; until
// then their card is not shown. Behind the client-invoicing entitlement, like the invoices.
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
        return View(await BuildModelAsync(null));
    }

    // Every method is checked before anything is written: one bad entry sends the whole form back
    // with the message beside that field and nothing changed.
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(PaymentsForm form)
    {
        var gate = await RequireAccessAsync();
        if (gate != null) return gate;

        var values = new Dictionary<string, string>();
        Check(PaymentMethodKinds.PayPal, form.PayPal, "Form.PayPal", values);
        Check(PaymentMethodKinds.Stripe, form.StripeLink, "Form.StripeLink", values);
        Check(PaymentMethodKinds.Square, form.SquareLink, "Form.SquareLink", values);
        Check(PaymentMethodKinds.ETransfer, form.ETransferEmail, "Form.ETransferEmail", values);
        Check(PaymentMethodKinds.Other, form.OtherLink, "Form.OtherLink", values);
        var note = (form.ETransferNote ?? string.Empty).Trim();
        if (note.Length > PaymentMethodLinks.NoteMaxLength)
        {
            ModelState.AddModelError("Form.ETransferNote", $"Keep the note under {PaymentMethodLinks.NoteMaxLength} characters.");
        }
        if (!ModelState.IsValid) return View("Index", await BuildModelAsync(form));

        foreach (var method in PaymentMethodKinds.All)
        {
            var value = values[method];
            if (value.Length == 0) await DeleteAsync(method);
            else await UpsertAsync(method, value, method == PaymentMethodKinds.ETransfer ? note : string.Empty);
        }

        // The Profile's single link from before 527 is retired for this adviser: this page is the one place now.
        var agentId = AgentId;
        var empty = string.Empty;
        await _db.AgentUsers.Where(a => a.Id == agentId).ExecuteUpdateAsync(u => u.SetProperty(a => a.DefaultPaymentLink, empty));

        TempData["Success"] = values.Values.Any(v => v.Length > 0)
            ? "Saved. Your invoices now show a Pay button for each method you entered."
            : "Saved. Your invoices offer no payment method until you enter one here.";
        return RedirectToAction(nameof(Index));
    }

    // -- Stripe Connect (slice 1, 2026-09-27): dormant until the platform has keys -------------------

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

        TempData["Success"] = "Stripe disconnected. Invoices go back to the payment methods you entered on this page.";
        return RedirectToAction(nameof(Index));
    }

    // -- helpers -------------------------------------------------------------------------------------

    private void Check(string method, string? raw, string field, Dictionary<string, string> values)
    {
        if (PaymentMethodLinks.TryNormalize(method, raw, out var value, out var error))
        {
            values[method] = value;
            return;
        }
        values[method] = string.Empty;
        ModelState.AddModelError(field, error);
    }

    // Set-based, like the reminder settings (523): an update when the row exists, else an insert
    // that a concurrent second save turns into an update.
    private async Task UpsertAsync(string method, string value, string note)
    {
        var agentId = AgentId;
        var now = DateTime.UtcNow;
        var updated = await _db.AgentPaymentMethods
            .Where(m => m.AgentUserId == agentId && m.Method == method)
            .ExecuteUpdateAsync(u => u
                .SetProperty(m => m.Value, value)
                .SetProperty(m => m.Note, note)
                .SetProperty(m => m.UpdatedAt, now));
        if (updated > 0) return;

        await _db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO `AgentPaymentMethods`
            (`AgentUserId`, `Method`, `Value`, `Note`, `CreatedAt`, `UpdatedAt`)
            VALUES ({agentId}, {method}, {value}, {note}, {now}, {now})
            ON DUPLICATE KEY UPDATE `Value` = {value}, `Note` = {note}, `UpdatedAt` = {now}");
    }

    private async Task DeleteAsync(string method)
    {
        var agentId = AgentId;
        await _db.AgentPaymentMethods.Where(m => m.AgentUserId == agentId && m.Method == method).ExecuteDeleteAsync();
    }

    private async Task<PaymentsViewModel> BuildModelAsync(PaymentsForm? posted)
    {
        var agentId = AgentId;
        var saved = await _db.AgentPaymentMethods.AsNoTracking().Where(m => m.AgentUserId == agentId).ToListAsync();
        var legacy = saved.Count == 0
            ? await _db.AgentUsers.AsNoTracking().Where(a => a.Id == agentId).Select(a => a.DefaultPaymentLink).FirstOrDefaultAsync()
            : null;
        legacy = string.IsNullOrWhiteSpace(legacy) ? null : legacy.Trim();
        var stripe = await _db.AgentPaymentConnections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.AgentUserId == agentId && c.Provider == PaymentProviders.Stripe && c.IsActive);
        return new PaymentsViewModel
        {
            Form = posted ?? FormFrom(saved, legacy),
            HasSavedMethods = saved.Count > 0,
            LegacyLink = legacy,
            StripeConfigured = _stripe.IsConfigured,
            Stripe = stripe
        };
    }

    private static PaymentsForm FormFrom(List<AgentPaymentMethod> saved, string? legacy)
    {
        string? Of(string method) => saved.FirstOrDefault(m => m.Method == method)?.Value;
        var form = new PaymentsForm
        {
            PayPal = Of(PaymentMethodKinds.PayPal),
            StripeLink = Of(PaymentMethodKinds.Stripe),
            SquareLink = Of(PaymentMethodKinds.Square),
            ETransferEmail = Of(PaymentMethodKinds.ETransfer),
            ETransferNote = saved.FirstOrDefault(m => m.Method == PaymentMethodKinds.ETransfer)?.Note,
            OtherLink = Of(PaymentMethodKinds.Other)
        };
        if (saved.Count == 0 && PaymentMethodLinks.FromLegacyLink(legacy) is { } legacyEntry)
        {
            switch (legacyEntry.Method)
            {
                case PaymentMethodKinds.PayPal: form.PayPal = legacyEntry.Value; break;
                case PaymentMethodKinds.Stripe: form.StripeLink = legacyEntry.Value; break;
                case PaymentMethodKinds.Square: form.SquareLink = legacyEntry.Value; break;
                default: form.OtherLink = legacyEntry.Value; break;
            }
        }
        return form;
    }

    private async Task<IActionResult?> RequireAccessAsync()
    {
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.ClientInvoicing);
        if (access.IsIncluded) return null;
        TempData["Error"] = access.UpgradeMessage;
        return RedirectToAction("Index", "Billing");
    }
}
