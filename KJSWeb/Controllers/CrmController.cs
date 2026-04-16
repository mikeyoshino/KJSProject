using Microsoft.AspNetCore.Mvc;
using KJSWeb.Filters;
using KJSWeb.Services;
using System.Security.Claims;

namespace KJSWeb.Controllers;

[Route("crm")]
[ServiceFilter(typeof(AdminAuthFilter))]
public class CrmController : Controller
{
    private readonly SupabaseService _supabase;
    private readonly AdminService    _admin;
    private readonly EmailService    _email;
    private const int PageSize = 25;

    public CrmController(SupabaseService supabase, AdminService admin, EmailService email)
    {
        _supabase = supabase;
        _admin    = admin;
        _email    = email;
    }

    // ── Dashboard ─────────────────────────────────────────────────

    [Route("")]
    public async Task<IActionResult> Dashboard()
    {
        var stats       = await _supabase.GetCrmStatsAsync();
        var recentSubs  = await _supabase.GetRecentSubscriptionsAsync(10);
        var recentUsers = await _admin.ListRecentUsersAsync(5);

        // Enrich missing emails on recent subscriptions
        var missingIds = recentSubs
            .Where(s => string.IsNullOrEmpty(s.Email))
            .Select(s => s.UserId)
            .Distinct()
            .ToList();

        if (missingIds.Any())
        {
            var emailMap = new Dictionary<string, string>(StringComparer.Ordinal);
            await Task.WhenAll(missingIds.Select(async uid =>
            {
                var u = await _admin.GetUserByIdAsync(uid);
                if (u != null) lock (emailMap) { emailMap[uid] = u.Email; }
            }));

            foreach (var s in recentSubs.Where(s => string.IsNullOrEmpty(s.Email)))
                if (emailMap.TryGetValue(s.UserId, out var email))
                    s.Email = email;
        }

        ViewBag.Stats       = stats;
        ViewBag.RecentSubs  = recentSubs;
        ViewBag.RecentUsers = recentUsers;
        return View();
    }

    // ── Users ─────────────────────────────────────────────────────

    [Route("users")]
    public IActionResult Users(string? q)
    {
        ViewBag.Query = q;
        return View();
    }

    [Route("users/search")]
    [HttpGet]
    public async Task<IActionResult> UserSearch(string q)
    {
        var users = string.IsNullOrWhiteSpace(q)
            ? new List<AdminUser>()
            : await _admin.SearchUsersAsync(q);

        // Enrich with subscription + ban status
        var results = new List<UserSearchResult>();
        foreach (var u in users)
        {
            var activeSub = await _supabase.GetActiveSubscriptionAsync(u.Id);
            var isBanned  = await _supabase.IsUserBannedAsync(u.Id);
            results.Add(new UserSearchResult
            {
                User      = u,
                SubStatus = activeSub != null ? $"Active until {activeSub.ExpiresAt:MMM dd, yyyy}" : "None",
                IsBanned  = isBanned
            });
        }

        ViewBag.Query   = q;
        ViewBag.Results = results;
        return View("Users");
    }

    [Route("users/{userId}")]
    [HttpGet]
    public async Task<IActionResult> UserDetail(string userId)
    {
        var user = await _admin.GetUserByIdAsync(userId);
        if (user == null) return NotFound();

        var activeSub          = await _supabase.GetActiveSubscriptionAsync(userId);
        var history            = await _supabase.GetSubscriptionsByUserIdAsync(userId);
        var isBanned           = await _supabase.IsUserBannedAsync(userId);
        var (tickets, _)       = await _supabase.GetUserTicketsAsync(userId, page: 1, pageSize: 10);

        ViewBag.User      = user;
        ViewBag.ActiveSub = activeSub;
        ViewBag.History   = history;
        ViewBag.IsBanned  = isBanned;
        ViewBag.Tickets   = tickets;
        return View();
    }

    [Route("users/{userId}/ban")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> BanUser(string userId, string? reason)
    {
        var user = await _admin.GetUserByIdAsync(userId);
        if (user == null) return NotFound();

        var adminEmail = User.FindFirstValue(ClaimTypes.Email) ?? "";
        await _supabase.BanUserAsync(userId, user.Email, reason, adminEmail);
        TempData["Success"] = $"User {user.Email} has been banned.";
        return RedirectToAction(nameof(UserDetail), new { userId });
    }

    [Route("users/{userId}/unban")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnbanUser(string userId)
    {
        var user = await _admin.GetUserByIdAsync(userId);
        await _supabase.UnbanUserAsync(userId);
        TempData["Success"] = $"User {user?.Email ?? userId} has been unbanned.";
        return RedirectToAction(nameof(UserDetail), new { userId });
    }

    // ── Subscriptions ─────────────────────────────────────────────

    [Route("subscriptions")]
    [HttpGet]
    public async Task<IActionResult> Subscriptions(int page = 1, string? status = null)
    {
        if (page < 1) page = 1;
        var (items, total) = await _supabase.GetAllSubscriptionsAsync(page, PageSize, status);

        // Enrich rows that were created before the email column existed
        var missingIds = items
            .Where(s => string.IsNullOrEmpty(s.Email))
            .Select(s => s.UserId)
            .Distinct()
            .ToList();

        if (missingIds.Any())
        {
            var emailMap = new Dictionary<string, string>(StringComparer.Ordinal);
            await Task.WhenAll(missingIds.Select(async uid =>
            {
                var u = await _admin.GetUserByIdAsync(uid);
                if (u != null) lock (emailMap) { emailMap[uid] = u.Email; }
            }));

            foreach (var s in items.Where(s => string.IsNullOrEmpty(s.Email)))
                if (emailMap.TryGetValue(s.UserId, out var email))
                    s.Email = email;
        }

        ViewBag.Pagination = new KJSWeb.Models.PaginationInfo
        {
            CurrentPage = page,
            PageSize    = PageSize,
            TotalItems  = total,
            TotalPages  = (int)Math.Ceiling(total / (double)PageSize)
        };
        ViewBag.ActiveStatus = status;
        return View(items);
    }

    [Route("subscriptions/grant")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GrantSubscription(string userId, string email, string plan, int? customDays)
    {
        var days = customDays ?? plan switch
        {
            "3month" => 90,
            "yearly" => 365,
            _        => 30   // monthly
        };

        var ok = await _supabase.GrantSubscriptionAdminAsync(userId, email, plan, days);
        TempData[ok ? "Success" : "Error"] = ok
            ? $"Subscription granted: {plan} ({days} days)."
            : "Failed to grant subscription. Check logs.";

        return RedirectToAction(nameof(UserDetail), new { userId });
    }

    [Route("subscriptions/{id}/revoke")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeSubscription(string id, string? returnUserId)
    {
        await _supabase.RevokeSubscriptionAsync(id);
        TempData["Success"] = "Subscription revoked.";

        if (!string.IsNullOrEmpty(returnUserId))
            return RedirectToAction(nameof(UserDetail), new { userId = returnUserId });
        return RedirectToAction(nameof(Subscriptions));
    }

    [Route("subscriptions/{id}/extend")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ExtendSubscription(string id, int days, string? returnUserId)
    {
        await _supabase.ExtendSubscriptionAsync(id, days);
        TempData["Success"] = $"Subscription extended by {days} day(s).";

        if (!string.IsNullOrEmpty(returnUserId))
            return RedirectToAction(nameof(UserDetail), new { userId = returnUserId });
        return RedirectToAction(nameof(Subscriptions));
    }

    // ── Tickets ───────────────────────────────────────────────────

    [Route("tickets")]
    [HttpGet]
    public async Task<IActionResult> Tickets(int page = 1, string? status = null)
    {
        if (page < 1) page = 1;

        KJSWeb.Models.TicketStatus? statusFilter = status?.ToLower() switch
        {
            "open"        => KJSWeb.Models.TicketStatus.Open,
            "in_progress" => KJSWeb.Models.TicketStatus.InProgress,
            "resolved"    => KJSWeb.Models.TicketStatus.Resolved,
            _             => null
        };

        var (items, total) = await _supabase.GetAllTicketsAsync(page, PageSize, statusFilter);

        ViewBag.Pagination = new KJSWeb.Models.PaginationInfo
        {
            CurrentPage = page,
            PageSize    = PageSize,
            TotalItems  = total,
            TotalPages  = (int)Math.Ceiling(total / (double)PageSize)
        };
        ViewBag.ActiveStatus = status ?? "";
        return View(items);
    }

    [Route("tickets/{id}")]
    [HttpGet]
    public async Task<IActionResult> TicketDetail(string id)
    {
        var ticket = await _supabase.GetTicketByIdAsync(id);
        if (ticket == null) return NotFound();

        var messages = await _supabase.GetTicketMessagesAsync(id);

        ViewBag.Ticket   = ticket;
        ViewBag.Messages = messages;
        return View();
    }

    [Route("tickets/{id}/reply")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReplyTicket(string id, string message)
    {
        var ticket = await _supabase.GetTicketByIdAsync(id);
        if (ticket == null) return NotFound();

        if (string.IsNullOrWhiteSpace(message))
        {
            TempData["Error"] = "Reply message cannot be empty.";
            return RedirectToAction(nameof(TicketDetail), new { id });
        }

        var adminId    = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "admin";
        var adminEmail = User.FindFirstValue(ClaimTypes.Email) ?? "Support Team";
        var ok = await _supabase.AddTicketMessageAsync(id, adminId, message, isAdmin: true);

        if (ok && !string.IsNullOrEmpty(ticket.UserEmail))
            await _email.SendTicketReplyEmailAsync(ticket.UserEmail, id, message, adminEmail);

        TempData[ok ? "Success" : "Error"] = ok
            ? "Reply sent."
            : "Failed to send reply. Check logs.";

        return RedirectToAction(nameof(TicketDetail), new { id });
    }

    [Route("tickets/{id}/status")]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateTicketStatus(string id, string newStatus)
    {
        var ticket = await _supabase.GetTicketByIdAsync(id);
        if (ticket == null) return NotFound();

        var parsedStatus = newStatus.ToLower() switch
        {
            "open"        => KJSWeb.Models.TicketStatus.Open,
            "in_progress" => KJSWeb.Models.TicketStatus.InProgress,
            "resolved"    => KJSWeb.Models.TicketStatus.Resolved,
            _             => KJSWeb.Models.TicketStatus.Open
        };

        var ok = await _supabase.UpdateTicketStatusAsync(id, parsedStatus);

        if (ok)
        {
            if (!string.IsNullOrEmpty(ticket.UserEmail) &&
                (parsedStatus == KJSWeb.Models.TicketStatus.InProgress ||
                 parsedStatus == KJSWeb.Models.TicketStatus.Resolved))
            {
                string? lastAdminMessage = null;
                if (parsedStatus == KJSWeb.Models.TicketStatus.Resolved)
                {
                    var messages = await _supabase.GetTicketMessagesAsync(id);
                    lastAdminMessage = messages
                        .Where(m => m.IsAdmin)
                        .OrderByDescending(m => m.CreatedAt)
                        .Select(m => m.Message)
                        .FirstOrDefault();
                }
                await _email.SendTicketStatusChangeEmailAsync(
                    ticket.UserEmail, id, parsedStatus, lastAdminMessage);
            }
            TempData["Success"] = $"Ticket status updated to {newStatus}.";
        }
        else
        {
            TempData["Error"] = "Failed to update ticket status.";
        }

        return RedirectToAction(nameof(TicketDetail), new { id });
    }
}

public class UserSearchResult
{
    public AdminUser User      { get; set; } = new();
    public string    SubStatus { get; set; } = "None";
    public bool      IsBanned  { get; set; }
}
