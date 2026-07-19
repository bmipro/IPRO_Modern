using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.DataAccess;
using IPRO.DataAccess.Repositories;
using IPRO.Email;
using IPRO.Entities;
using IPRO.Utility;
using IPRO.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace IPRO.Web.Controllers;

[Authorize]
public class ClientsController : Controller
{
    private readonly IClientService _clients;
    private readonly IContactImporter _importer;
    private readonly IUnitOfWork _uow;
    private readonly IPRODbContext _db;
    private readonly IPackageEntitlementService _entitlements;
    private readonly IEmailService _email;
    private readonly IBlobStorageService _blob;
    private readonly IGoogleCalendarService _googleCalendar;
    private readonly IDataProtector _googleTokenProtector;
    private int AgentId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public ClientsController(IClientService clients, IContactImporter importer, IUnitOfWork uow, IPRODbContext db, IPackageEntitlementService entitlements, IEmailService email, IBlobStorageService blob, IGoogleCalendarService googleCalendar, IDataProtectionProvider dataProtectionProvider)
    {
        _clients = clients; _importer = importer; _uow = uow; _db = db; _entitlements = entitlements; _email = email; _blob = blob;
        _googleCalendar = googleCalendar;
        _googleTokenProtector = dataProtectionProvider.CreateProtector("IPRO.Web.GoogleCalendar.Tokens.v1");
    }

    public async Task<IActionResult> Index(string? search, int? accountTypeId, string? newsletter)
    {
        var query = _db.Clients
            .Include(c => c.Categories)
            .Where(c => c.AgentUserId == AgentId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            search = search.Trim();
            query = query.Where(c =>
                c.FirstName.Contains(search) ||
                c.LastName.Contains(search) ||
                c.Email.Contains(search) ||
                c.Email2.Contains(search) ||
                c.CompanyName.Contains(search) ||
                c.Phone.Contains(search) ||
                c.HomePhone2.Contains(search) ||
                c.BusinessPhone.Contains(search) ||
                c.BusinessPhone2.Contains(search) ||
                c.CellPhone.Contains(search) ||
                c.CellPhone2.Contains(search) ||
                c.City.Contains(search) ||
                c.Categories.Any(cat => cat.Name.Contains(search)));
        }

        if (accountTypeId.HasValue)
        {
            query = query.Where(c => c.Categories.Any(cat => cat.Id == accountTypeId.Value));
        }

        if (string.Equals(newsletter, "yes", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(c => c.IsNewsletterSubscribed);
        }
        else if (string.Equals(newsletter, "no", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(c => !c.IsNewsletterSubscribed);
        }

        ViewBag.Search     = search;
        ViewBag.AccountTypeId = accountTypeId;
        ViewBag.Newsletter = newsletter;
        ViewBag.TotalCount = await _clients.GetCountAsync(AgentId);
        ViewBag.ContactLimit = await GetContactLimitStatusAsync();
        ViewBag.ImportAccess = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.OutlookImport);
        ViewBag.AccountTypes = await _db.ClientCategories
            .Where(c => c.AgentUserId == AgentId)
            .OrderBy(c => c.Name)
            .ToListAsync();
        return View(await query.OrderByDescending(c => c.CreatedAt).ToListAsync());
    }

    public async Task<IActionResult> Details(int id)
    {
        var client = await _db.Clients
            .Include(c => c.Categories)
            .Include(c => c.FollowUps)
            .Include(c => c.LifeEvents)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (client == null || client.AgentUserId != AgentId) return NotFound();
        var comments = (await _clients.GetCommentsAsync(id)).ToList();
        ViewBag.Comments = comments;
        ViewBag.Timeline = BuildClientTimeline(client, comments);
        ViewBag.PortalAccess = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.ClientPortal);
        ViewBag.PortalDocuments = await _db.PortalDocuments.AsNoTracking().Where(d => d.ClientId == id).OrderByDescending(d => d.UploadedAt).ToListAsync();
        ViewBag.LifeEventAccess = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.LifeEventReminders);
        return View(client);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> InvitePortal(int id)
    {
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.ClientPortal);
        if (!access.IsIncluded)
        {
            TempData["Error"] = access.UpgradeMessage;
            return RedirectToAction(nameof(Details), new { id });
        }

        var client = await _db.Clients.Include(c => c.AgentUser).FirstOrDefaultAsync(c => c.Id == id && c.AgentUserId == AgentId);
        if (client == null) return NotFound();
        if (string.IsNullOrWhiteSpace(client.Email))
        {
            TempData["Error"] = "This client has no email address on file.";
            return RedirectToAction(nameof(Details), new { id });
        }

        client.PortalInviteToken = Guid.NewGuid().ToString("N");
        client.PortalPasswordHash = null;
        client.PortalActivatedAt = null;
        await _db.SaveChangesAsync();

        var activateUrl = Url.Action("Activate", "ClientPortalAccount", new { token = client.PortalInviteToken }, Request.Scheme);
        var companyName = client.AgentUser.CompanyName;
        var html = $"""
            <div style="font-family:Arial,sans-serif;max-width:640px;margin:auto;color:#17223a">
              <div style="padding:22px;background:#0f7a52;color:white"><h1 style="margin:0;font-size:24px">{System.Net.WebUtility.HtmlEncode(companyName)} Client Portal</h1></div>
              <div style="padding:24px;border:1px solid #dce4ef;border-top:0">
                <p>{System.Net.WebUtility.HtmlEncode(companyName)} has invited you to their client portal, where you can message your advisor, view documents and invoices, and request appointments.</p>
                <p><a href="{activateUrl}" style="display:inline-block;padding:11px 18px;background:#0f7a52;color:white;text-decoration:none;border-radius:6px">Activate My Account</a></p>
              </div>
            </div>
            """;
        await _email.SendDetailedAsync(client.Email, $"{client.FirstName} {client.LastName}".Trim(), $"{companyName} invited you to their client portal", html);

        TempData["Success"] = $"Portal invite sent to {client.Email}.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadPortalDocument(int clientId, IFormFile file)
    {
        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == clientId && c.AgentUserId == AgentId);
        if (client == null) return NotFound();

        if (file == null || file.Length == 0)
        {
            TempData["Error"] = "Choose a file to upload.";
            return RedirectToAction(nameof(Details), new { id = clientId });
        }
        if (file.Length > 20 * 1024 * 1024)
        {
            TempData["Error"] = "That file is larger than the 20 MB upload limit.";
            return RedirectToAction(nameof(Details), new { id = clientId });
        }

        await using var stream = file.OpenReadStream();
        var validation = await PortalDocumentValidator.ValidateAsync(file.FileName, stream);
        if (!validation.IsValid)
        {
            TempData["Error"] = validation.Error;
            return RedirectToAction(nameof(Details), new { id = clientId });
        }
        stream.Position = 0;
        var url = await _blob.UploadAsync(stream, file.FileName, "portal-documents", validation.ContentType, isPrivate: true);

        _db.PortalDocuments.Add(new PortalDocument
        {
            ClientId = clientId,
            UploadedByClient = false,
            FileName = Path.GetFileName(file.FileName),
            BlobUrl = url,
            ContentType = validation.ContentType,
            FileSizeBytes = file.Length
        });
        await _db.SaveChangesAsync();

        TempData["Success"] = "Document uploaded to the client portal.";
        return RedirectToAction(nameof(Details), new { id = clientId });
    }

    public async Task<IActionResult> DownloadPortalDocument(int id)
    {
        var document = await _db.PortalDocuments.Include(d => d.Client).AsNoTracking().FirstOrDefaultAsync(d => d.Id == id && d.Client.AgentUserId == AgentId);
        if (document == null) return NotFound();

        var stream = await _blob.DownloadAsync(document.BlobUrl);
        if (stream == null) return NotFound();

        return File(stream, string.IsNullOrWhiteSpace(document.ContentType) ? "application/octet-stream" : document.ContentType, document.FileName);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeletePortalDocument(int id)
    {
        var document = await _db.PortalDocuments.Include(d => d.Client).FirstOrDefaultAsync(d => d.Id == id && d.Client.AgentUserId == AgentId);
        if (document == null) return NotFound();

        var clientId = document.ClientId;
        await _blob.DeleteAsync(document.BlobUrl);
        _db.PortalDocuments.Remove(document);
        await _db.SaveChangesAsync();

        TempData["Success"] = "Document deleted.";
        return RedirectToAction(nameof(Details), new { id = clientId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokePortal(int id)
    {
        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == id && c.AgentUserId == AgentId);
        if (client == null) return NotFound();

        client.PortalPasswordHash = null;
        client.PortalInviteToken = null;
        client.PortalActivatedAt = null;
        await _db.SaveChangesAsync();

        TempData["Success"] = "Portal access revoked.";
        return RedirectToAction(nameof(Details), new { id });
    }

    public async Task<IActionResult> FollowUps(int id, string status = "open", int page = 1)
    {
        const int pageSize = 10;
        page = Math.Max(page, 1);
        status = string.IsNullOrWhiteSpace(status) ? "open" : status.Trim().ToLowerInvariant();

        var client = await _db.Clients
            .FirstOrDefaultAsync(c => c.Id == id && c.AgentUserId == AgentId);
        if (client == null) return NotFound();

        var query = _db.ClientFollowUps
            .Where(f => f.ClientId == id);

        query = status switch
        {
            "completed" => query.Where(f => f.IsCompleted),
            "overdue" => query.Where(f => !f.IsCompleted && f.DueAt.Date < DateTime.Today),
            "all" => query,
            _ => query.Where(f => !f.IsCompleted)
        };

        var totalCount = await query.CountAsync();
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Min(page, totalPages);

        ViewBag.Client = client;
        ViewBag.Status = status;
        ViewBag.Page = page;
        ViewBag.TotalPages = totalPages;
        ViewBag.TotalCount = totalCount;

        var followUps = await query
            .OrderBy(f => f.IsCompleted)
            .ThenBy(f => f.IsCompleted ? DateTime.MaxValue : f.DueAt)
            .ThenByDescending(f => f.CompletedAt ?? f.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return View(followUps);
    }

    [HttpGet("Clients/FollowUps")]
    public async Task<IActionResult> FollowUpQueue(string status = "open", int page = 1)
    {
        const int pageSize = 15;
        page = Math.Max(page, 1);
        status = string.IsNullOrWhiteSpace(status) ? "open" : status.Trim().ToLowerInvariant();

        var today = DateTime.Today;
        var nextWeek = today.AddDays(7);
        var query = _db.ClientFollowUps
            .Include(f => f.Client)
            .Where(f => f.Client.AgentUserId == AgentId);

        query = status switch
        {
            "completed" => query.Where(f => f.IsCompleted),
            "overdue" => query.Where(f => !f.IsCompleted && f.DueAt.Date < today),
            "today" => query.Where(f => !f.IsCompleted && f.DueAt.Date == today),
            "upcoming" => query.Where(f => !f.IsCompleted && f.DueAt.Date > today && f.DueAt.Date <= nextWeek),
            "all" => query,
            _ => query.Where(f => !f.IsCompleted)
        };

        var totalCount = await query.CountAsync();
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Min(page, totalPages);

        ViewBag.Status = status;
        ViewBag.Page = page;
        ViewBag.TotalPages = totalPages;
        ViewBag.TotalCount = totalCount;

        var followUps = await query
            .OrderBy(f => f.IsCompleted)
            .ThenBy(f => f.IsCompleted ? DateTime.MaxValue : f.DueAt)
            .ThenByDescending(f => f.CompletedAt ?? f.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return View(followUps);
    }

    public async Task<IActionResult> Calendar(int? year, int? month)
    {
        var calendarAccess = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.CalendarScheduler);
        if (!calendarAccess.IsIncluded)
        {
            TempData["Error"] = calendarAccess.UpgradeMessage;
            return RedirectToAction("Index", "Billing");
        }

        var today = DateTime.Today;
        var selectedMonth = new DateTime(
            year.GetValueOrDefault(today.Year),
            month.GetValueOrDefault(today.Month),
            1);
        var monthStart = selectedMonth.Date;
        var monthEnd = monthStart.AddMonths(1);

        ViewBag.MonthStart = monthStart;
        ViewBag.PreviousMonth = monthStart.AddMonths(-1);
        ViewBag.NextMonth = monthStart.AddMonths(1);

        var followUps = await _db.ClientFollowUps
            .Include(f => f.Client)
            .Where(f => f.Client.AgentUserId == AgentId &&
                        f.DueAt >= monthStart &&
                        f.DueAt < monthEnd)
            .OrderBy(f => f.DueAt)
            .ThenBy(f => f.IsCompleted)
            .ToListAsync();

        ViewBag.ExternalEvents = await _db.ExternalCalendarEvents
            .Where(e => e.AgentUserId == AgentId && e.StartAt >= monthStart && e.StartAt < monthEnd)
            .OrderBy(e => e.StartAt)
            .ToListAsync();

        return View(followUps);
    }

    public async Task<IActionResult> Create()
    {
        var limitStatus = await GetContactLimitStatusAsync();
        if (!limitStatus.CanAdd)
        {
            TempData["Error"] = limitStatus.Message;
            return RedirectToAction(nameof(Index));
        }

        ViewBag.ContactLimit = limitStatus;
        await LoadAccountTypesAsync();
        return View(new Client());
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(Client model, int[] categoryIds)
    {
        NormalizeClient(model);
        ClearOptionalClientModelState();
        ValidateClient(model);
        await ValidateUniqueEmailAsync(model.Email);
        var limitStatus = await GetContactLimitStatusAsync();
        if (!limitStatus.CanAdd)
        {
            ModelState.AddModelError("", limitStatus.Message);
        }

        if (!ModelState.IsValid)
        {
            ViewBag.ContactLimit = limitStatus;
            await LoadAccountTypesAsync(categoryIds);
            return View(model);
        }

        model.AgentUserId = AgentId;
        model.CreatedAt = DateTime.UtcNow;
        model.UpdatedAt = DateTime.UtcNow;
        await ApplyCategoriesAsync(model, categoryIds);
        await _db.Clients.AddAsync(model);
        await _db.SaveChangesAsync();

        TempData["Success"] = $"{model.FirstName} {model.LastName} added successfully.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(int id)
    {
        var client = await _db.Clients
            .Include(c => c.Categories)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (client == null || client.AgentUserId != AgentId) return NotFound();
        await LoadAccountTypesAsync(client.Categories.Select(c => c.Id));
        return View(client);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Client model, int[] categoryIds)
    {
        var client = await _db.Clients
            .Include(c => c.Categories)
            .FirstOrDefaultAsync(c => c.Id == model.Id);
        if (client == null || client.AgentUserId != AgentId) return NotFound();

        model.AgentUserId = client.AgentUserId;
        NormalizeClient(model);
        ClearOptionalClientModelState();
        ValidateClient(model);
        await ValidateUniqueEmailAsync(model.Email, client.Id);
        if (!ModelState.IsValid)
        {
            await LoadAccountTypesAsync(categoryIds);
            return View(model);
        }

        ApplyClientFields(client, model);
        client.UpdatedAt = DateTime.UtcNow;
        client.Categories.Clear();
        await ApplyCategoriesAsync(client, categoryIds);
        await _db.SaveChangesAsync();

        TempData["Success"] = "Client updated.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> AccountTypes()
    {
        ViewBag.AccountTypes = (await _uow.ClientCategories.FindAsync(c => c.AgentUserId == AgentId))
            .OrderBy(c => c.Name)
            .ToList();
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateAccountType(string name, string? description)
    {
        name = name?.Trim() ?? "";
        description = description?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["Error"] = "Account type name is required.";
            return RedirectToAction(nameof(AccountTypes));
        }

        var exists = await _uow.ClientCategories.ExistsAsync(c =>
            c.AgentUserId == AgentId && c.Name == name);
        if (exists)
        {
            TempData["Error"] = "That account type already exists.";
            return RedirectToAction(nameof(AccountTypes));
        }

        await _uow.ClientCategories.AddAsync(new ClientCategory
        {
            AgentUserId = AgentId,
            Name = name,
            Description = description
        });
        await _uow.SaveChangesAsync();

        TempData["Success"] = "Account type added.";
        return RedirectToAction(nameof(AccountTypes));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteAccountType(int id)
    {
        var accountType = await _db.ClientCategories
            .Include(c => c.Clients)
            .FirstOrDefaultAsync(c => c.Id == id && c.AgentUserId == AgentId);
        if (accountType == null) return NotFound();

        _db.ClientCategories.Remove(accountType);
        await _db.SaveChangesAsync();
        TempData["Warning"] = "Account type deleted.";
        return RedirectToAction(nameof(AccountTypes));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateAccountType(int id, string name, string? description)
    {
        name = name?.Trim() ?? "";
        description = description?.Trim() ?? "";

        var accountType = await _db.ClientCategories
            .FirstOrDefaultAsync(c => c.Id == id && c.AgentUserId == AgentId);
        if (accountType == null) return NotFound();

        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["Error"] = "Account type name is required.";
            return RedirectToAction(nameof(AccountTypes));
        }

        var duplicate = await _db.ClientCategories.AnyAsync(c =>
            c.AgentUserId == AgentId &&
            c.Id != id &&
            c.Name.ToLower() == name.ToLower());
        if (duplicate)
        {
            TempData["Error"] = "That account type already exists.";
            return RedirectToAction(nameof(AccountTypes));
        }

        accountType.Name = name;
        accountType.Description = description;
        await _db.SaveChangesAsync();
        TempData["Success"] = "Account type updated.";
        return RedirectToAction(nameof(AccountTypes));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        var client = await _clients.GetByIdAsync(id);
        if (client == null || client.AgentUserId != AgentId) return NotFound();
        await _clients.DeleteAsync(id);
        TempData["Success"] = "Client deleted.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddComment(int clientId, string comment)
    {
        var client = await _clients.GetByIdAsync(clientId);
        if (client == null || client.AgentUserId != AgentId) return NotFound();
        await _clients.AddCommentAsync(new ClientComment { ClientId = clientId, Comment = comment });
        return RedirectToAction(nameof(Details), new { id = clientId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddFollowUp(int clientId, string title, DateTime dueAt, string? notes)
    {
        title = title?.Trim() ?? "";
        notes = notes?.Trim() ?? "";

        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == clientId && c.AgentUserId == AgentId);
        if (client == null) return NotFound();

        if (string.IsNullOrWhiteSpace(title))
        {
            TempData["Error"] = "Follow-up title is required.";
            return RedirectToAction(nameof(Details), new { id = clientId });
        }

        if (dueAt == default)
        {
            TempData["Error"] = "Follow-up date is required.";
            return RedirectToAction(nameof(Details), new { id = clientId });
        }

        await _db.ClientFollowUps.AddAsync(new ClientFollowUp
        {
            ClientId = clientId,
            Title = title,
            Notes = notes,
            DueAt = dueAt,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        TempData["Success"] = "Follow-up added.";
        return RedirectToAction(nameof(Details), new { id = clientId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddLifeEvent(int clientId, ClientLifeEventType eventType, string label, DateTime eventDate, int reminderDaysBefore)
    {
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.LifeEventReminders);
        if (!access.IsIncluded)
        {
            TempData["Error"] = access.UpgradeMessage;
            return RedirectToAction(nameof(Details), new { id = clientId });
        }

        var client = await _db.Clients.FirstOrDefaultAsync(c => c.Id == clientId && c.AgentUserId == AgentId);
        if (client == null) return NotFound();

        label = label?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(label))
        {
            TempData["Error"] = "A label is required for the life event.";
            return RedirectToAction(nameof(Details), new { id = clientId });
        }

        if (eventDate == default)
        {
            TempData["Error"] = "An event date is required.";
            return RedirectToAction(nameof(Details), new { id = clientId });
        }

        await _db.ClientLifeEvents.AddAsync(new ClientLifeEvent
        {
            ClientId = clientId,
            EventType = eventType,
            Label = label,
            EventDate = eventDate,
            ReminderDaysBefore = reminderDaysBefore <= 0 ? 7 : reminderDaysBefore
        });
        await _db.SaveChangesAsync();

        TempData["Success"] = "Life event added.";
        return RedirectToAction(nameof(Details), new { id = clientId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteLifeEvent(int id)
    {
        var lifeEvent = await _db.ClientLifeEvents
            .Include(e => e.Client)
            .FirstOrDefaultAsync(e => e.Id == id);
        if (lifeEvent == null || lifeEvent.Client.AgentUserId != AgentId) return NotFound();

        var clientId = lifeEvent.ClientId;
        _db.ClientLifeEvents.Remove(lifeEvent);
        await _db.SaveChangesAsync();

        TempData["Warning"] = "Life event removed.";
        return RedirectToAction(nameof(Details), new { id = clientId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CompleteFollowUp(int id, string? returnUrl = null)
    {
        var followUp = await _db.ClientFollowUps
            .Include(f => f.Client)
            .FirstOrDefaultAsync(f => f.Id == id);
        if (followUp == null || followUp.Client.AgentUserId != AgentId) return NotFound();

        followUp.IsCompleted = true;
        followUp.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = "Follow-up completed.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        return RedirectToAction(nameof(Details), new { id = followUp.ClientId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteFollowUp(int id, string? returnUrl = null)
    {
        var followUp = await _db.ClientFollowUps
            .Include(f => f.Client)
            .FirstOrDefaultAsync(f => f.Id == id);
        if (followUp == null || followUp.Client.AgentUserId != AgentId) return NotFound();

        var clientId = followUp.ClientId;

        if (!string.IsNullOrWhiteSpace(followUp.GoogleEventId))
        {
            await TryDeleteGoogleEventAsync(followUp.GoogleEventId);
        }

        _db.ClientFollowUps.Remove(followUp);
        await _db.SaveChangesAsync();

        TempData["Warning"] = "Follow-up deleted.";
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return Redirect(returnUrl);
        }

        return RedirectToAction(nameof(Details), new { id = clientId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportCsv(IFormFile file)
    {
        var importAccess = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.OutlookImport);
        if (!importAccess.IsIncluded)
        {
            TempData["Error"] = importAccess.UpgradeMessage;
            return RedirectToAction(nameof(Index));
        }

        if (file == null || file.Length == 0)
        { TempData["Error"] = "Please select a CSV file."; return RedirectToAction(nameof(Index)); }

        using var stream = file.OpenReadStream();
        var result = await _importer.ImportCsvAsync(stream, AgentId);
        var limitStatus = await GetContactLimitStatusAsync();
        var remainingSlots = limitStatus.IsUnlimited ? int.MaxValue : Math.Max(0, limitStatus.Remaining);
        if (remainingSlots <= 0)
        {
            TempData["Error"] = limitStatus.Message;
            return RedirectToAction(nameof(Index));
        }

        var existingEmails = await _db.Clients
            .Where(c => c.AgentUserId == AgentId)
            .Select(c => c.Email.ToLower())
            .ToListAsync();
        var seenEmails = existingEmails.ToHashSet();
        var toImport = new List<Client>();
        var skipped = result.Skipped;

        foreach (var client in result.Clients)
        {
            NormalizeClient(client);
            if (string.IsNullOrWhiteSpace(client.FirstName) ||
                string.IsNullOrWhiteSpace(client.LastName) ||
                string.IsNullOrWhiteSpace(client.Email) ||
                seenEmails.Contains(client.Email))
            {
                skipped++;
                continue;
            }

            if (toImport.Count >= remainingSlots)
            {
                skipped++;
                continue;
            }

            client.AgentUserId = AgentId;
            client.CreatedAt = DateTime.UtcNow;
            client.UpdatedAt = DateTime.UtcNow;
            seenEmails.Add(client.Email);
            toImport.Add(client);
        }

        if (toImport.Count > 0)
        {
            await _db.Clients.AddRangeAsync(toImport);
            await _db.SaveChangesAsync();
        }

        var limitNote = limitStatus.IsUnlimited ? "" : $" Package limit remaining after import: {Math.Max(0, remainingSlots - toImport.Count)}.";
        TempData["Success"] = $"Import complete: {toImport.Count} imported, {skipped} skipped, {result.Errors} errors.{limitNote}";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> ExportCsv()
    {
        var clients = await _clients.GetByAgentAsync(AgentId);
        var bytes   = _importer.ExportToCsvAsync(clients);
        return File(bytes, "text/csv", $"clients-export-{DateTime.Now:yyyyMMdd}.csv");
    }

    private async Task LoadAccountTypesAsync(IEnumerable<int>? selectedIds = null)
    {
        ViewBag.AccountTypes = (await _uow.ClientCategories.FindAsync(c => c.AgentUserId == AgentId))
            .OrderBy(c => c.Name)
            .ToList();
        ViewBag.SelectedCategoryIds = selectedIds?.ToHashSet() ?? new HashSet<int>();
    }

    private async Task ApplyCategoriesAsync(Client client, IEnumerable<int> categoryIds)
    {
        var selectedIds = categoryIds.Distinct().ToList();
        if (selectedIds.Count == 0) return;

        var categories = await _db.ClientCategories
            .Where(c => c.AgentUserId == AgentId && selectedIds.Contains(c.Id))
            .ToListAsync();
        foreach (var category in categories)
        {
            client.Categories.Add(category);
        }
    }

    private static void ApplyClientFields(Client client, Client model)
    {
        client.FirstName = model.FirstName;
        client.LastName = model.LastName;
        client.DateOfBirth = model.DateOfBirth;
        client.CompanyName = model.CompanyName;
        client.Email = model.Email;
        client.Email2 = model.Email2;
        client.Phone = model.Phone;
        client.HomePhone2 = model.HomePhone2;
        client.BusinessPhone = model.BusinessPhone;
        client.BusinessPhone2 = model.BusinessPhone2;
        client.CellPhone = model.CellPhone;
        client.CellPhone2 = model.CellPhone2;
        client.Fax = model.Fax;
        client.Fax2 = model.Fax2;
        client.Address = model.Address;
        client.UnitNumber = model.UnitNumber;
        client.City = model.City;
        client.Province = model.Province;
        client.PostalCode = model.PostalCode;
        client.Country = model.Country;
        client.IsNewsletterSubscribed = model.IsNewsletterSubscribed;
        client.Notes = model.Notes;
    }

    private static void NormalizeClient(Client client)
    {
        client.FirstName = client.FirstName?.Trim() ?? "";
        client.LastName = client.LastName?.Trim() ?? "";
        client.CompanyName = client.CompanyName?.Trim() ?? "";
        client.Email = (client.Email?.Trim() ?? "").ToLowerInvariant();
        client.Email2 = (client.Email2?.Trim() ?? "").ToLowerInvariant();
        client.Phone = client.Phone?.Trim() ?? "";
        client.HomePhone2 = client.HomePhone2?.Trim() ?? "";
        client.BusinessPhone = client.BusinessPhone?.Trim() ?? "";
        client.BusinessPhone2 = client.BusinessPhone2?.Trim() ?? "";
        client.CellPhone = client.CellPhone?.Trim() ?? "";
        client.CellPhone2 = client.CellPhone2?.Trim() ?? "";
        client.Fax = client.Fax?.Trim() ?? "";
        client.Fax2 = client.Fax2?.Trim() ?? "";
        client.Address = client.Address?.Trim() ?? "";
        client.UnitNumber = client.UnitNumber?.Trim() ?? "";
        client.City = client.City?.Trim() ?? "";
        client.Province = client.Province?.Trim() ?? "";
        client.PostalCode = client.PostalCode?.Trim() ?? "";
        client.Country = string.IsNullOrWhiteSpace(client.Country) ? "Canada" : client.Country.Trim();
        client.Notes = client.Notes?.Trim() ?? "";
    }

    private void ValidateClient(Client client)
    {
        if (string.IsNullOrWhiteSpace(client.FirstName)) ModelState.AddModelError("", "First name is required.");
        if (string.IsNullOrWhiteSpace(client.LastName)) ModelState.AddModelError("", "Last name is required.");
        if (string.IsNullOrWhiteSpace(client.Email)) ModelState.AddModelError("", "Email is required.");
    }

    private async Task ValidateUniqueEmailAsync(string email, int? currentClientId = null)
    {
        if (string.IsNullOrWhiteSpace(email)) return;

        var exists = await _db.Clients.AnyAsync(c =>
            c.AgentUserId == AgentId &&
            c.Email == email &&
            (!currentClientId.HasValue || c.Id != currentClientId.Value));
        if (exists)
        {
            ModelState.AddModelError(nameof(Client.Email), "A client with this email already exists.");
        }
    }

    private async Task<ContactLimitStatus> GetContactLimitStatusAsync()
    {
        var currentCount = await _db.Clients.CountAsync(c => c.AgentUserId == AgentId);
        var access = await _entitlements.GetAccessAsync(AgentId, PackageFeatureCodes.Contacts);

        if (!access.IsIncluded)
        {
            return new ContactLimitStatus
            {
                CurrentCount = currentCount,
                Limit = 0,
                PackageName = access.CurrentPackageName,
                CanAdd = false,
                Message = access.UpgradeMessage
            };
        }

        if (!access.LimitValue.HasValue || access.LimitValue.Value < 0)
        {
            return new ContactLimitStatus
            {
                CurrentCount = currentCount,
                Limit = null,
                PackageName = access.CurrentPackageName,
                CanAdd = true
            };
        }

        var limit = access.LimitValue.Value;
        var remaining = Math.Max(0, limit - currentCount);
        return new ContactLimitStatus
        {
            CurrentCount = currentCount,
            Limit = limit,
            PackageName = access.CurrentPackageName,
            CanAdd = remaining > 0,
            Message = remaining > 0
                ? string.Empty
                : $"Your {access.CurrentPackageName} package includes up to {limit:N0} contacts. You currently have {currentCount:N0}. Please upgrade your package to add more contacts."
        };
    }

    private void ClearOptionalClientModelState()
    {
        foreach (var key in new[]
        {
            nameof(Client.AgentUser),
            nameof(Client.Categories),
            nameof(Client.Comments),
            nameof(Client.AgentUserId),
            nameof(Client.DateOfBirth),
            nameof(Client.CompanyName),
            nameof(Client.Email2),
            nameof(Client.Phone),
            nameof(Client.HomePhone2),
            nameof(Client.BusinessPhone),
            nameof(Client.BusinessPhone2),
            nameof(Client.CellPhone),
            nameof(Client.CellPhone2),
            nameof(Client.Fax),
            nameof(Client.Fax2),
            nameof(Client.Address),
            nameof(Client.UnitNumber),
            nameof(Client.City),
            nameof(Client.Province),
            nameof(Client.PostalCode),
            nameof(Client.Country),
            nameof(Client.Notes)
        })
        {
            ModelState.Remove(key);
        }
    }

    private static List<ClientTimelineItem> BuildClientTimeline(Client client, IEnumerable<ClientComment> comments)
    {
        var items = new List<ClientTimelineItem>
        {
            new()
            {
                OccurredAt = client.CreatedAt,
                Kind = "Client",
                Icon = "fa-user-plus",
                ColorClass = "text-primary bg-primary-subtle",
                Title = "Client created",
                Details = $"{client.FirstName} {client.LastName} was added to the contact list."
            }
        };

        if (client.UpdatedAt > client.CreatedAt.AddMinutes(1))
        {
            items.Add(new ClientTimelineItem
            {
                OccurredAt = client.UpdatedAt,
                Kind = "Client",
                Icon = "fa-pen",
                ColorClass = "text-info bg-info-subtle",
                Title = "Client profile updated",
                Details = "Contact information was updated."
            });
        }

        foreach (var comment in comments)
        {
            items.Add(new ClientTimelineItem
            {
                OccurredAt = comment.CreatedAt,
                Kind = "Note",
                Icon = "fa-comment",
                ColorClass = "text-secondary bg-secondary-subtle",
                Title = "Note added",
                Details = comment.Comment
            });
        }

        foreach (var followUp in client.FollowUps)
        {
            items.Add(new ClientTimelineItem
            {
                OccurredAt = followUp.CreatedAt,
                Kind = "Follow-up",
                Icon = "fa-calendar-plus",
                ColorClass = "text-warning bg-warning-subtle",
                Title = $"Follow-up added: {followUp.Title}",
                Details = $"Due {followUp.DueAt:MMM d, yyyy}" + (string.IsNullOrWhiteSpace(followUp.Notes) ? "" : $" - {followUp.Notes}"),
                Url = $"/Clients/FollowUps/{client.Id}"
            });

            if (followUp.IsCompleted && followUp.CompletedAt.HasValue)
            {
                items.Add(new ClientTimelineItem
                {
                    OccurredAt = followUp.CompletedAt.Value,
                    Kind = "Follow-up",
                    Icon = "fa-check",
                    ColorClass = "text-success bg-success-subtle",
                    Title = $"Follow-up completed: {followUp.Title}",
                    Details = $"Completed on {followUp.CompletedAt.Value:MMM d, yyyy}",
                    Url = $"/Clients/FollowUps/{client.Id}?status=completed"
                });
            }
        }

        return items
            .OrderByDescending(i => i.OccurredAt)
            .ToList();
    }

    private async Task TryDeleteGoogleEventAsync(string googleEventId)
    {
        try
        {
            var connection = await _db.GoogleCalendarConnections.FirstOrDefaultAsync(c => c.AgentUserId == AgentId && c.IsActive);
            if (connection == null) return;

            var accessToken = _googleTokenProtector.Unprotect(connection.EncryptedAccessToken);
            if (connection.AccessTokenExpiresAt <= DateTime.UtcNow.AddMinutes(5))
            {
                var refreshToken = _googleTokenProtector.Unprotect(connection.EncryptedRefreshToken);
                var (newAccessToken, expiresAt) = await _googleCalendar.RefreshAccessTokenAsync(refreshToken);
                accessToken = newAccessToken;
                connection.EncryptedAccessToken = _googleTokenProtector.Protect(newAccessToken);
                connection.AccessTokenExpiresAt = expiresAt;
                await _db.SaveChangesAsync();
            }

            await _googleCalendar.DeleteEventAsync(accessToken, connection.GoogleCalendarId, googleEventId);
        }
        catch
        {
            // Best-effort - the follow-up delete in IPRO still proceeds even if Google is unreachable
            // or the connection has been revoked; the periodic sync job will reconcile eventually.
        }
    }
}
