using System.Security.Claims;
using IPRO.Business.Interfaces;
using IPRO.DataAccess.Repositories;
using IPRO.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Admin.Controllers;

[Authorize(Policy = "SuperAdmin")]
public class AdminUsersController : Controller
{
    private readonly IUnitOfWork _uow;
    private readonly IPasswordHasher<AdminUser> _hasher;
    private readonly IAdminAuditLogService _auditLog;

    public AdminUsersController(IUnitOfWork uow, IPasswordHasher<AdminUser> hasher, IAdminAuditLogService auditLog)
    {
        _uow = uow;
        _hasher = hasher;
        _auditLog = auditLog;
    }

    private string CurrentAdminUsername => User.Identity?.Name ?? "unknown";

    private int CurrentAdminId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");

    public async Task<IActionResult> Index()
    {
        var users = await _uow.AdminUsers.GetAllAsync();
        return View(users.OrderBy(u => u.Username));
    }

    public IActionResult Create() => View(new AdminUser { Role = string.Empty, IsActive = true });

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(AdminUser model, string password)
    {
        model.Username = model.Username?.Trim() ?? string.Empty;
        model.FullName = model.FullName?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(model.Username))
        {
            ModelState.AddModelError(nameof(model.Username), "Username is required.");
        }
        else if (await _uow.AdminUsers.ExistsAsync(u => u.Username == model.Username))
        {
            ModelState.AddModelError(nameof(model.Username), "That username is already taken.");
        }

        if (string.IsNullOrWhiteSpace(model.FullName))
        {
            ModelState.AddModelError(nameof(model.FullName), "Full name is required.");
        }

        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
        {
            ModelState.AddModelError(nameof(password), "Password must be at least 8 characters.");
        }

        if (model.Role != AdminRoles.SuperAdmin && model.Role != AdminRoles.Support)
        {
            ModelState.AddModelError(nameof(model.Role), "Choose a valid role.");
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var newUser = new AdminUser
        {
            Username = model.Username,
            FullName = model.FullName,
            Role = model.Role,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        newUser.PasswordHash = _hasher.HashPassword(newUser, password);
        await _uow.AdminUsers.AddAsync(newUser);
        await _uow.SaveChangesAsync();
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "AdminUserCreated", $"Created admin account '{newUser.Username}' with role {newUser.Role}.");

        TempData["Success"] = $"{newUser.Username} was created.";
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(int id)
    {
        var user = await _uow.AdminUsers.GetByIdAsync(id);
        if (user == null) return NotFound();
        return View(user);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(int id, string fullName, string role, bool isActive)
    {
        var user = await _uow.AdminUsers.GetByIdAsync(id);
        if (user == null) return NotFound();

        fullName = fullName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(fullName))
        {
            ModelState.AddModelError("FullName", "Full name is required.");
        }
        if (role != AdminRoles.SuperAdmin && role != AdminRoles.Support)
        {
            ModelState.AddModelError("Role", "Choose a valid role.");
        }
        if (id == CurrentAdminId && !isActive)
        {
            ModelState.AddModelError("IsActive", "You cannot deactivate your own account.");
        }

        if (!ModelState.IsValid)
        {
            user.FullName = fullName;
            user.Role = role;
            user.IsActive = isActive;
            return View(user);
        }

        var changes = new List<string>();
        if (user.FullName != fullName) changes.Add("full name");
        if (user.Role != role) changes.Add($"role to {role}");
        if (user.IsActive != isActive) changes.Add(isActive ? "reactivated" : "deactivated");

        user.FullName = fullName;
        user.Role = role;
        user.IsActive = isActive;
        _uow.AdminUsers.Update(user);

        await _uow.SaveChangesAsync();
        if (changes.Any())
        {
            await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "AdminUserUpdated", $"Updated '{user.Username}': {string.Join(", ", changes)}.");
        }

        TempData["Success"] = $"{user.Username} was updated.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(int id, string newPassword)
    {
        var user = await _uow.AdminUsers.GetByIdAsync(id);
        if (user == null) return NotFound();

        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
        {
            TempData["Error"] = "Password must be at least 8 characters.";
            return RedirectToAction(nameof(Edit), new { id });
        }

        user.PasswordHash = _hasher.HashPassword(user, newPassword);
        _uow.AdminUsers.Update(user);
        await _uow.SaveChangesAsync();
        await _auditLog.LogAsync(CurrentAdminId, CurrentAdminUsername, "AdminUserPasswordReset", $"Reset password for '{user.Username}'.");

        TempData["Success"] = $"Password reset for {user.Username}.";
        return RedirectToAction(nameof(Edit), new { id });
    }

    public async Task<IActionResult> AuditLog(int page = 1, int? adminUserId = null, string? action = null, DateTime? from = null, DateTime? to = null)
    {
        const int pageSize = 50;
        page = Math.Max(1, page);
        action = action?.Trim();

        var entries = (await _uow.AdminAuditLogEntries.GetAllAsync()).AsEnumerable();

        if (adminUserId.HasValue)
        {
            entries = entries.Where(e => e.AdminUserId == adminUserId.Value);
        }
        if (!string.IsNullOrWhiteSpace(action))
        {
            entries = entries.Where(e =>
                e.Action.Contains(action, StringComparison.OrdinalIgnoreCase) ||
                e.Details.Contains(action, StringComparison.OrdinalIgnoreCase));
        }
        if (from.HasValue)
        {
            entries = entries.Where(e => e.CreatedAt >= from.Value.Date);
        }
        if (to.HasValue)
        {
            entries = entries.Where(e => e.CreatedAt < to.Value.Date.AddDays(1));
        }

        var ordered = entries.OrderByDescending(e => e.CreatedAt).ToList();
        var totalCount = ordered.Count;
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)pageSize));
        page = Math.Min(page, totalPages);

        ViewBag.AdminUsers = (await _uow.AdminUsers.GetAllAsync()).OrderBy(u => u.Username).ToList();
        ViewBag.AdminUserId = adminUserId;
        ViewBag.Action = action;
        ViewBag.From = from?.ToString("yyyy-MM-dd");
        ViewBag.To = to?.ToString("yyyy-MM-dd");
        ViewBag.Page = page;
        ViewBag.TotalPages = totalPages;
        ViewBag.TotalCount = totalCount;

        return View(ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList());
    }
}
