using System.Net;
using System.Net.Mail;

namespace KJSWeb.Services;

public class EmailService
{
    private readonly IConfiguration _config;
    private readonly ILogger<EmailService> _logger;

    // Config keys expected under "Email:" section:
    //   Email:Host, Email:Port, Email:Username, Email:Password,
    //   Email:FromAddress, Email:FromName, Email:SiteUrl
    private readonly string _host;
    private readonly int    _port;
    private readonly string _username;
    private readonly string _password;
    private readonly string _fromAddress;
    private readonly string _fromName;
    private readonly string _siteUrl;
    private readonly bool   _enabled;

    public EmailService(IConfiguration config, ILogger<EmailService> logger)
    {
        _config      = config;
        _logger      = logger;
        _host        = config["Email:Host"]        ?? "";
        _port        = int.TryParse(config["Email:Port"], out var p) ? p : 587;
        _username    = config["Email:Username"]    ?? "";
        _password    = config["Email:Password"]    ?? "";
        _fromAddress = config["Email:FromAddress"] ?? "";
        _fromName    = config["Email:FromName"]    ?? "Support";
        _siteUrl     = (config["Email:SiteUrl"]    ?? "").TrimEnd('/');
        _enabled     = !string.IsNullOrEmpty(_host) && !string.IsNullOrEmpty(_fromAddress);
    }

    // ── Public API ────────────────────────────────────────────────────

    /// <summary>
    /// Send an email to a user when an admin replies to their ticket.
    /// Subject: Re: Your Support Ticket #{id}
    /// </summary>
    public Task SendTicketReplyEmailAsync(
        string userEmail,
        string ticketId,
        string message,
        string adminName)
    {
        var subject = $"Re: Your Support Ticket #{ticketId}";
        var ticketUrl = $"{_siteUrl}/support/tickets/{ticketId}";

        var body = $@"<p>Hello,</p>
<p>You have a new reply on your support ticket.</p>
<blockquote style=""border-left:3px solid #ccc;padding-left:1em;color:#555;"">
{WebUtility.HtmlEncode(message)}
</blockquote>
<p>— {WebUtility.HtmlEncode(adminName)}</p>
<p><a href=""{ticketUrl}"">View your ticket</a></p>";

        return SendEmailAsync(userEmail, subject, body);
    }

    /// <summary>
    /// Send an email to a user when their ticket status changes to
    /// InProgress or Resolved. For Resolved, an optional last admin
    /// message is included in the body.
    /// </summary>
    public Task SendTicketStatusChangeEmailAsync(
        string userEmail,
        string ticketId,
        KJSWeb.Models.TicketStatus newStatus,
        string? message = null)
    {
        var ticketUrl = $"{_siteUrl}/support/tickets/{ticketId}";

        string subject;
        string body;

        if (newStatus == KJSWeb.Models.TicketStatus.InProgress)
        {
            subject = $"Your Support Ticket #{ticketId} — In Progress";
            body = $@"<p>Hello,</p>
<p>We're looking into your issue. We'll get back to you as soon as possible.</p>
<p><a href=""{ticketUrl}"">View your ticket</a></p>";
        }
        else if (newStatus == KJSWeb.Models.TicketStatus.Resolved)
        {
            subject = $"Your Support Ticket #{ticketId} — Resolved";

            var messageBlock = !string.IsNullOrWhiteSpace(message)
                ? $@"<blockquote style=""border-left:3px solid #ccc;padding-left:1em;color:#555;"">
{WebUtility.HtmlEncode(message)}
</blockquote>"
                : "";

            body = $@"<p>Hello,</p>
<p>Your support ticket has been resolved.</p>
{messageBlock}
<p><a href=""{ticketUrl}"">View your ticket</a></p>";
        }
        else
        {
            // No email for Open or unknown statuses
            return Task.CompletedTask;
        }

        return SendEmailAsync(userEmail, subject, body);
    }

    // ── Core send ─────────────────────────────────────────────────────

    /// <summary>
    /// Sends an HTML email. If SMTP is not configured, logs a warning and
    /// returns without throwing so the application continues normally.
    /// Configure SMTP via the "Email" section in appsettings.json /
    /// environment variables (see EmailService constructor).
    /// </summary>
    public async Task SendEmailAsync(string to, string subject, string htmlBody)
    {
        if (!_enabled)
        {
            _logger.LogWarning(
                "EmailService: SMTP not configured. Skipping email to {To} — Subject: {Subject}",
                to, subject);
            return;
        }

        try
        {
            using var smtp = new SmtpClient(_host, _port)
            {
                Credentials  = new NetworkCredential(_username, _password),
                EnableSsl    = true,
                DeliveryMethod = SmtpDeliveryMethod.Network
            };

            using var mail = new MailMessage
            {
                From       = new MailAddress(_fromAddress, _fromName),
                Subject    = subject,
                Body       = htmlBody,
                IsBodyHtml = true
            };
            mail.To.Add(to);

            await smtp.SendMailAsync(mail);
            _logger.LogInformation("Email sent to {To} — Subject: {Subject}", to, subject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EmailService: Failed to send email to {To} — Subject: {Subject}", to, subject);
            // Do not rethrow — email failure must never break the caller's flow
        }
    }
}
