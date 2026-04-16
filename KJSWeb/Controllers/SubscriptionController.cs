using Microsoft.AspNetCore.Mvc;
using KJSWeb.Models;
using KJSWeb.Services;
using System.Security.Claims;

namespace KJSWeb.Controllers;

public class SubscriptionController : Controller
{
    private readonly SupabaseService _supabase;
    private readonly BlockonomicsService _blockonomics;
    private readonly ILogger<SubscriptionController> _logger;

    public SubscriptionController(SupabaseService supabase, BlockonomicsService blockonomics, ILogger<SubscriptionController> logger)
    {
        _supabase = supabase;
        _blockonomics = blockonomics;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> Pricing()
    {
        var userId = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
        Subscription? activeSub = null;
        if (!string.IsNullOrEmpty(userId))
        {
            activeSub = await _supabase.GetActiveSubscriptionAsync(userId);
        }
        ViewBag.ActiveSubscription = activeSub;
        ViewBag.Plans = SubscriptionPlans.All;
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> Subscribe(string plan)
    {
        var userId = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return RedirectToAction("Login", "Auth");

        if (!SubscriptionPlans.All.TryGetValue(plan, out var planInfo))
        {
            TempData["Error"] = "Invalid plan selected.";
            return RedirectToAction("Pricing");
        }

        // Get a new BTC address from Blockonomics
        var btcAddress = await _blockonomics.GetNewAddressAsync();
        if (string.IsNullOrEmpty(btcAddress))
        {
            TempData["Error"] = "Failed to generate payment address. Please try again.";
            return RedirectToAction("Pricing");
        }

        // Get current BTC price
        var btcPrice = await _blockonomics.GetBtcPriceAsync();
        var btcAmount = _blockonomics.ConvertUsdToBtc(planInfo.PriceUsd, btcPrice);

        var subscription = new Subscription
        {
            UserId = userId,
            Plan = plan,
            AmountUsd = planInfo.PriceUsd,
            AmountBtc = btcAmount,
            BtcAddress = btcAddress,
            Status = "pending"
        };

        var created = await _supabase.CreateSubscriptionAsync(subscription);
        if (!created)
        {
            TempData["Error"] = "Failed to create subscription. Please try again.";
            return RedirectToAction("Pricing");
        }

        return RedirectToAction("Payment");
    }

    [HttpGet]
    public async Task<IActionResult> Payment()
    {
        var userId = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return RedirectToAction("Login", "Auth");

        var sub = await _supabase.GetPendingSubscriptionByUserIdAsync(userId);
        if (sub == null) return RedirectToAction("Pricing");

        ViewBag.BtcPrice = await _blockonomics.GetBtcPriceAsync();
        return View(sub);
    }

    [HttpGet]
    public async Task<IActionResult> MySubscription()
    {
        var userId = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return RedirectToAction("Login", "Auth");

        var activeSub = await _supabase.GetActiveSubscriptionAsync(userId);
        ViewBag.Plans = SubscriptionPlans.All;
        return View(activeSub);
    }

    [HttpGet]
    public IActionResult BitcoinGuide()
    {
        ViewData["Title"] = "How to Pay with Bitcoin";
        ViewData["OgTitle"] = "Bitcoin Payment Guide — SCANDAL69";
        ViewData["Description"] = "Step-by-step guide to buying and sending Bitcoin from Asia, Europe, and the Americas.";
        return View();
    }

    // API endpoint for checking payment status (polled by JS)
    [HttpGet]
    [Route("api/subscription/status/{address}")]
    public async Task<IActionResult> CheckStatus(string address)
    {
        var sub = await _supabase.GetSubscriptionByAddressAsync(address);
        if (sub == null) return NotFound();

        return Json(new { status = sub.Status, plan = sub.Plan, expiresAt = sub.ExpiresAt });
    }
}

// Separate controller for Blockonomics callback
public class BlockonomicsCallbackController : Controller
{
    private readonly SupabaseService _supabase;
    private readonly BlockonomicsService _blockonomics;
    private readonly ILogger<BlockonomicsCallbackController> _logger;

    public BlockonomicsCallbackController(SupabaseService supabase, BlockonomicsService blockonomics, ILogger<BlockonomicsCallbackController> logger)
    {
        _supabase = supabase;
        _blockonomics = blockonomics;
        _logger = logger;
    }

    /// <summary>
    /// Blockonomics sends: GET /api/blockonomics/callback?status={0|1|2}&addr={addr}&value={satoshi}&txid={txid}&secret={secret}
    /// Status: 0=Unconfirmed, 1=Partially confirmed, 2=Confirmed
    /// Value is in satoshi (1 BTC = 100,000,000 satoshi)
    /// </summary>
    [HttpGet]
    [Route("api/blockonomics/callback")]
    public async Task<IActionResult> Callback(int status, string addr, long value, string? txid, string? secret)
    {
        _logger.LogInformation("Blockonomics callback: status={Status}, addr={Addr}, value={Value}, txid={Txid}",
            status, addr, value, txid);

        // Validate secret
        if (!_blockonomics.ValidateSecret(secret))
        {
            _logger.LogWarning("Invalid callback secret");
            return Unauthorized();
        }

        // Find the subscription by BTC address
        var sub = await _supabase.GetSubscriptionByAddressAsync(addr);
        if (sub == null)
        {
            _logger.LogWarning("Subscription not found for address: {Addr}", addr);
            return NotFound();
        }

        switch (status)
        {
            case 0: // Unconfirmed
            case 1: // Partially confirmed
                await _supabase.UpdateSubscriptionStatusAsync(addr, "confirming");
                break;

            case 2: // Confirmed
                // Guard against replay attacks — only activate if still pending/confirming
                if (sub.Status != "pending" && sub.Status != "confirming")
                {
                    _logger.LogWarning("Callback replay blocked: subscription {Addr} already has status={Status}", addr, sub.Status);
                    return Ok();
                }
                if (SubscriptionPlans.All.TryGetValue(sub.Plan, out var planInfo))
                {
                    await _supabase.ActivateSubscriptionAsync(addr, txid ?? "", planInfo.DurationDays);
                    _logger.LogInformation("Subscription activated for address: {Addr}, plan: {Plan}, days: {Days}",
                        addr, sub.Plan, planInfo.DurationDays);
                }
                break;
        }

        return Ok(); // Return 200 to acknowledge
    }
}
