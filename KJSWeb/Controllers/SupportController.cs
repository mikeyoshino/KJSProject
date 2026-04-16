using Microsoft.AspNetCore.Mvc;
using KJSWeb.Models;
using KJSWeb.Services;
using System.Security.Claims;

namespace KJSWeb.Controllers;

[Route("support")]
public class SupportController : Controller
{
    private readonly SupabaseService _supabase;
    private readonly EmailService    _email;
    private readonly ILogger<SupportController> _logger;
    private const int PageSize = 20;

    public SupportController(SupabaseService supabase, EmailService email, ILogger<SupportController> logger)
    {
        _supabase = supabase;
        _email    = email;
        _logger   = logger;
    }

    // ── Auth helper ──────────────────────────────────────────────────

    private (string? UserId, string? UserEmail) GetSession()
    {
        var userId    = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var userEmail = User.FindFirstValue(ClaimTypes.Email);
        return (userId, userEmail);
    }

    // ── GET /support/tickets ─────────────────────────────────────────

    [HttpGet("tickets")]
    public async Task<IActionResult> Tickets(int page = 1)
    {
        var (userId, _) = GetSession();
        if (string.IsNullOrEmpty(userId))
            return RedirectToAction("Login", "Auth", new { returnUrl = "/support/tickets" });

        if (page < 1) page = 1;

        var (items, total) = await _supabase.GetUserTicketsAsync(userId, page, PageSize);

        ViewBag.Pagination = new PaginationInfo
        {
            CurrentPage = page,
            PageSize    = PageSize,
            TotalItems  = total,
            TotalPages  = (int)Math.Ceiling(total / (double)PageSize)
        };

        return View(items);
    }

    // ── GET /support/tickets/{id} ────────────────────────────────────

    [HttpGet("tickets/{id}")]
    public async Task<IActionResult> TicketDetail(string id)
    {
        var (userId, _) = GetSession();
        if (string.IsNullOrEmpty(userId))
            return RedirectToAction("Login", "Auth", new { returnUrl = $"/support/tickets/{id}" });

        var ticket = await _supabase.GetTicketByIdAsync(id);
        if (ticket == null)
            return NotFound();

        // Users may only view their own tickets
        if (!string.Equals(ticket.UserId, userId, StringComparison.Ordinal))
            return Forbid();

        var messages = await _supabase.GetTicketMessagesAsync(id);

        ViewBag.Messages = messages;
        return View(ticket);
    }

    // ── POST /support/tickets/create ─────────────────────────────────

    [HttpPost("tickets/create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        string title,
        string category,
        string description)
    {
        var (userId, userEmail) = GetSession();
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(userEmail))
            return Json(new { success = false, error = "Not authenticated." });

        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(description))
            return Json(new { success = false, error = "Title and description are required." });

        var parsedCategory = category?.ToLower() switch
        {
            "payment"  => TicketCategory.Payment,
            "download" => TicketCategory.Download,
            "account"  => TicketCategory.Account,
            _          => TicketCategory.Other
        };

        try
        {
            var ticket = await _supabase.CreateTicketAsync(
                userId, userEmail, title.Trim(), parsedCategory, description.Trim());

            if (ticket == null)
            {
                _logger.LogWarning("CreateTicketAsync returned null for user {UserId}", userId);
                return Json(new { success = false, error = "Failed to create ticket. Please try again." });
            }

            _logger.LogInformation("Ticket {TicketId} created by user {UserId}", ticket.Id, userId);
            return Json(new { success = true, ticketId = ticket.Id });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating ticket for user {UserId}", userId);
            return Json(new { success = false, error = "An unexpected error occurred." });
        }
    }

    // ── POST /support/tickets/{id}/reply ─────────────────────────────

    [HttpPost("tickets/{id}/reply")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reply(string id, string message)
    {
        var (userId, _) = GetSession();
        if (string.IsNullOrEmpty(userId))
            return RedirectToAction("Login", "Auth", new { returnUrl = $"/support/tickets/{id}" });

        if (string.IsNullOrWhiteSpace(message))
        {
            TempData["Error"] = "Message cannot be empty.";
            return RedirectToAction(nameof(TicketDetail), new { id });
        }

        var ticket = await _supabase.GetTicketByIdAsync(id);
        if (ticket == null)
            return NotFound();

        // Users may only reply to their own tickets
        if (!string.Equals(ticket.UserId, userId, StringComparison.Ordinal))
            return Forbid();

        if (ticket.Status == TicketStatus.Resolved)
        {
            TempData["Error"] = "This ticket is resolved and cannot receive new replies.";
            return RedirectToAction(nameof(TicketDetail), new { id });
        }

        try
        {
            var ok = await _supabase.AddTicketMessageAsync(id, userId, message.Trim(), isAdmin: false);
            if (!ok)
            {
                _logger.LogWarning("AddTicketMessageAsync failed for ticket {TicketId}, user {UserId}", id, userId);
                TempData["Error"] = "Failed to send reply. Please try again.";
                return RedirectToAction(nameof(TicketDetail), new { id });
            }

            _logger.LogInformation("User {UserId} replied to ticket {TicketId}", userId, id);
            TempData["Success"] = "Your reply has been sent.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding reply to ticket {TicketId} for user {UserId}", id, userId);
            TempData["Error"] = "An unexpected error occurred.";
        }

        return RedirectToAction(nameof(TicketDetail), new { id });
    }
}
