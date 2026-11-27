using System.Security.Claims;
using IPRO.DataAccess;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize(AuthenticationSchemes = "ClientPortal")]
public class ClientPortalProfileController : Controller
{
    private readonly IPRODbContext _db;
    private readonly IPRO.Business.Services.IEmailConsentService _consent;
    private int ClientId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private readonly Microsoft.AspNetCore.Identity.IPasswordHasher<IPRO.Entities.Client> _hasher;
    private readonly IPRO.Email.IEmailService? _email;

    public ClientPortalProfileController(IPRODbContext db, IPRO.Business.Services.IEmailConsentService consent,
        Microsoft.AspNetCore.Identity.IPasswordHasher<IPRO.Entities.Client>? hasher = null, IPRO.Email.IEmailService? email = null)
    {
        _db = db;
        _hasher = hasher ?? new Microsoft.AspNetCore.Identity.PasswordHasher<IPRO.Entities.Client>();
        _email = email;
        _consent = consent;
    }

    public async Task<IActionResult> Index()
    {
        var client = await _db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == ClientId);
        if (client == null) return NotFound();

        return View(new PortalProfileViewModel
        {
            FirstName = client.FirstName,
            LastName = client.LastName,
            Email = client.Email,
            Phone = client.Phone,
            CellPhone = client.CellPhone,
            Address = client.Address,
            City = client.City,
            Province = client.Province,
            PostalCode = client.PostalCode,
            Country = client.Country
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(PortalProfileViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == ClientId);
        if (client == null) return NotFound();

        // 678 (audit 2026-11-24 A-2): the email is the portal sign-in, and the adviser's invoices and invitations go to
        // it. Changing it takes the current password (a session left open on a shared computer is not enough), the
        // old address is told, and every session signed in under the old address ends (ClientPortalCookieRevalidator),
        // this one included: they sign in again with the new address.
        var oldEmail = client.Email ?? string.Empty;
        // 683 (audit 2026-11-26, ACC-4): "changing" is any change to the text. It was compared after Gmail's dots
        // and +tags are ignored, but the sign-in matches the text as typed, so respelling the address changed the
        // sign-in without the password being asked.
        var emailChanging = !string.Equals(oldEmail.Trim(), model.Email.Trim(), StringComparison.OrdinalIgnoreCase);
        // And an address another client of the same adviser has is refused: two clients with one sign-in address
        // meant one's wrong passwords counted against the other, and one's mail went to both.
        if (emailChanging)
        {
            var wanted = IPRO.Utility.CanonicalEmail.Canonical(model.Email.Trim());
            var others = await _db.Clients.AsNoTracking()
                .Where(c => c.AgentUserId == client.AgentUserId && c.Id != client.Id)
                .Select(c => c.Email)
                .ToListAsync();
            if (others.Any(e => IPRO.Utility.CanonicalEmail.Canonical(e) == wanted))
            {
                ModelState.AddModelError(nameof(model.Email), "That email address cannot be used here. Enter a different one, or ask your adviser.");
                model.CurrentPassword = null;
                return View(model);
            }
        }
        // The password is checked through the sign-in lock (583), as every password check is: five wrong ones lock it.
        if (emailChanging && !await CurrentPasswordProvenAsync(client, model.CurrentPassword))
        {
            ModelState.AddModelError(nameof(model.CurrentPassword), "Enter your current password to change your email.");
            model.CurrentPassword = null;
            return View(model);
        }

        client.FirstName = model.FirstName.Trim();
        client.LastName = model.LastName.Trim();
        // 538: the client replacing their own address ends a bounce on the old one (and nothing else:
        // an unsubscribe or a spam complaint stays; they undo those on their preferences page).
        var newEmail = model.Email.Trim().ToLowerInvariant();
        if (IPRO.Utility.CanonicalEmail.Canonical(client.Email) != IPRO.Utility.CanonicalEmail.Canonical(newEmail))
        {
            // 683 (W02-06): their unsubscribe or complaint is remembered for the address they are leaving too.
            if (client.EmailOptOutAt is { } optedOutAt && IPRO.Entities.EmailOptOut.ReasonOf(client) != IPRO.Entities.EmailOptOutReason.Bounced)
                await IPRO.Business.Services.ContactOptOuts.RememberAddressAsync(_db, client.AgentUserId, client.Email, optedOutAt, client.EmailOptOutSource);
            _consent.LiftBounceSuppression(client);
        }
        client.Email = newEmail;
        client.Phone = model.Phone?.Trim() ?? string.Empty;
        client.CellPhone = model.CellPhone?.Trim() ?? string.Empty;
        client.Address = model.Address?.Trim() ?? string.Empty;
        client.City = model.City?.Trim() ?? string.Empty;
        client.Province = model.Province?.Trim() ?? string.Empty;
        client.PostalCode = model.PostalCode?.Trim() ?? string.Empty;
        client.Country = model.Country?.Trim() ?? string.Empty;
        client.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        if (emailChanging)
        {
            await TellOldAddressAsync(client, oldEmail);
            TempData["Success"] = "Your email is changed. Sign in again with the new address.";
            return RedirectToAction(nameof(Index));
        }
        TempData["Success"] = "Your information was updated.";
        return RedirectToAction(nameof(Index));
    }
    private async Task<bool> CurrentPasswordProvenAsync(IPRO.Entities.Client client, string? password)
    {
        var account = new SignInAccount(SignInAccountKind.Client, client.Id);
        var attempt = await SignInLockout.TryAsync(_db, SignInDoor.ClientPortal, client.Email,
            new List<SignInCandidate>
            {
                new(account, () => !string.IsNullOrEmpty(password) && !string.IsNullOrEmpty(client.PortalPasswordHash)
                    && _hasher.VerifyHashedPassword(client, client.PortalPasswordHash!, password!) != Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed)
            },
            DateTime.UtcNow);
        return attempt.Matched.Contains(account);
    }

    // 678 (audit A-2): so a change the client did not make is seen at once.
    private async Task TellOldAddressAsync(IPRO.Entities.Client client, string oldEmail)
    {
        if (_email == null || string.IsNullOrWhiteSpace(oldEmail)) return;
        try
        {
            var agent = await _db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == client.AgentUserId);
            var business = System.Net.WebUtility.HtmlEncode(IPRO.Entities.AdviserSender.BusinessName(agent));
            var html = $"<div style=\"font-family:Arial,Helvetica,sans-serif;font-size:15px;color:#17223a;line-height:1.55\">" +
                $"<p>Hello {System.Net.WebUtility.HtmlEncode(client.FirstName)},</p>" +
                $"<p>The email for your client portal with <strong>{business}</strong> was changed from this address to a new one, so this address no longer signs in.</p>" +
                $"<p>If you made the change, there is nothing to do. <strong>If you did not,</strong> contact {business} straight away: they can put your address back and send you a new invitation.</p></div>";
            await _email.SendDetailedAsync(oldEmail, $"{client.FirstName} {client.LastName}".Trim(),
                IPRO.Entities.AdviserSender.Subject(agent, "your client portal email was changed"), html,
                customArgs: IPRO.Entities.AdviserSender.Tags(agent, IPRO.Entities.EmailStreams.Notify,
                    new Dictionary<string, string> { ["ipro_entity"] = "portal_email_changed", ["client_id"] = client.Id.ToString() }),
                replyToEmail: IPRO.Entities.AdviserSender.ReplyToEmail(agent), replyToName: IPRO.Entities.AdviserSender.ReplyToName(agent));
        }
        catch (Exception)
        {
            // The change stands; the notice is a courtesy.
        }
    }
}
