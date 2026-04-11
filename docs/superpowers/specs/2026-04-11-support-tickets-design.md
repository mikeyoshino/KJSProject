# Support Tickets System — Design Spec
**Date:** 2026-04-11  
**Status:** Approved

---

## Overview

A support ticket system allowing users to report payment issues (and other problems) from the subscription payment page, with in-app conversation threads. Admins manage tickets in the CRM, reply to users, and track resolution status. Users receive email notifications when admins respond.

---

## 1. User-Facing Features

### Support Button & Ticket Creation

**Floating button** on `/Subscription/Payment` page:
- Small circular icon (chat bubble or question mark) in bottom-right corner
- Hoverable tooltip: "Need help?"
- Click → opens modal

**Ticket creation modal:**
- Auto-filled hidden title: `"Payment Issue — {BTC_ADDRESS}"`
- Auto-category: `TicketCategory.Payment`
- Text area: "Describe your issue..." (required, min 10 chars)
- Submit button: "Create Ticket"
- On submit → `POST /support/tickets/create` → closes modal, shows success toast

### User Ticket List

**Access:** Profile dropdown → "Support Tickets" link

**`GET /support/tickets`** — User ticket list:
- Shows all tickets for logged-in user
- Columns: Ticket ID, Title, Status (Open/In Progress/Resolved badge), Created, Last Reply
- Pagination: 20 per page, newest first
- Click row → `/support/tickets/{ticketId}`

### Ticket Detail View

**`GET /support/tickets/{ticketId}`** — Conversation:
- Header: ticket ID, title, status badge, created date
- Conversation thread: messages ordered chronologically
  - Each message shows: sender name (User or Admin), timestamp, message text
  - Admin messages have distinct styling (e.g., blue background)
- Reply form at bottom (only visible if status ≠ Resolved):
  - Text area: "Your reply..."
  - Submit button: "Send Reply"
- On submit → `POST /support/tickets/{ticketId}/reply` → message added, user gets email if admin replied

---

## 2. Admin-Facing (CRM)

### Tickets List

**`GET /crm/tickets`** — Admin ticket list:
- Table: Ticket ID, User Email (links to user detail), Title, Status, Created, Last Reply
- Status filter tabs: All / Open / In Progress / Resolved
- Pagination: 25 per page, newest first
- Click row → `/crm/tickets/{ticketId}`

### Tickets in User Detail

**`GET /crm/users/{userId}`** — New tab/section:
- "Support Tickets" card showing all tickets for that user
- Same table format as CRM tickets list
- Quick access when already viewing a user

### Admin Ticket Detail

**`GET /crm/tickets/{ticketId}`** — Conversation + controls:
- Header: ticket ID, user email (link to user detail), status dropdown (Open / In Progress / Resolved), created date
- Conversation thread (same format as user view)
- Reply form at bottom:
  - Text area: "Your reply..."
  - Submit button: "Send Reply"
- Status change:
  - Dropdown auto-sends email with status message
  - "In Progress" → "We're looking into your issue"
  - "Resolved" → "Your issue has been resolved"

---

## 3. Data Model

### Enums

```csharp
public enum TicketStatus
{
    Open,
    InProgress,
    Resolved
}

public enum TicketCategory
{
    Payment,
    Download,
    Account,
    Other
}
```

### C# Models

```csharp
public class SupportTicket
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string UserEmail { get; set; } = "";
    public string Title { get; set; } = "";
    public TicketCategory Category { get; set; }
    public TicketStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastReplyAt { get; set; }
}

public class TicketMessage
{
    public string Id { get; set; } = "";
    public string TicketId { get; set; } = "";
    public string SenderId { get; set; } = "";  // user_id or "admin"
    public bool IsAdmin { get; set; }
    public string Message { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
```

### Supabase Tables

```sql
-- Support tickets
create table support_tickets (
  id            text primary key,
  user_id       text not null,
  user_email    text not null,
  title         text not null,
  category      text not null default 'payment',  -- payment, download, account, other
  status        text not null default 'open',     -- open, in_progress, resolved
  created_at    timestamptz default now(),
  updated_at    timestamptz default now(),
  last_reply_at timestamptz
);

-- Ticket messages
create table ticket_messages (
  id         text primary key,
  ticket_id  text not null references support_tickets(id) on delete cascade,
  sender_id  text not null,
  is_admin   boolean default false,
  message    text not null,
  created_at timestamptz default now()
);
```

---

## 4. SupabaseService Methods

| Method | Description |
|---|---|
| `CreateTicketAsync(userId, email, title, category, description)` | Create ticket + first message |
| `GetUserTicketsAsync(userId, page, pageSize)` | User's tickets, paginated |
| `GetAllTicketsAsync(page, pageSize, status?)` | All tickets for CRM, filterable by status |
| `GetTicketByIdAsync(id)` | Single ticket |
| `GetTicketMessagesAsync(ticketId)` | All messages for ticket, ordered chronologically |
| `AddTicketMessageAsync(ticketId, senderId, message, isAdmin)` | Add message, update last_reply_at |
| `UpdateTicketStatusAsync(ticketId, newStatus)` | Change status, update updated_at |

---

## 5. Email Notifications

**On admin reply:**
```
Subject: Re: Your Support Ticket #{TicketId}

{AdminName} replied to your support ticket:

"{MessageText}"

View full conversation:
{BaseUrl}/support/tickets/{TicketId}
```

**On status change to "In Progress":**
```
Subject: Your Support Ticket #{TicketId} — In Progress

We're looking into your issue. You'll hear from us soon.

Ticket: {BaseUrl}/support/tickets/{TicketId}
```

**On status change to "Resolved":**
```
Subject: Your Support Ticket #{TicketId} — Resolved

Your issue has been resolved. Check the details below:

{LastAdminReplyText}

Ticket: {BaseUrl}/support/tickets/{TicketId}
```

---

## 6. Routes

### User Routes

| Method | Route | Action |
|---|---|---|
| GET | `/support/tickets` | List user's tickets |
| GET | `/support/tickets/{id}` | View ticket detail + conversation |
| POST | `/support/tickets/create` | Create new ticket |
| POST | `/support/tickets/{id}/reply` | Add message to ticket |

### CRM Routes

| Method | Route | Action |
|---|---|---|
| GET | `/crm/tickets` | List all tickets (with status filter) |
| GET | `/crm/tickets/{id}` | View ticket detail + reply form |
| POST | `/crm/tickets/{id}/reply` | Admin add message |
| PATCH | `/crm/tickets/{id}/status` | Change ticket status |

---

## 7. New Files

| File | Purpose |
|---|---|
| `Models/SupportTicket.cs` | Ticket model |
| `Models/TicketMessage.cs` | Message model |
| `Models/TicketStatus.cs` | Enum |
| `Models/TicketCategory.cs` | Enum |
| `Controllers/SupportController.cs` | User ticket routes |
| `Views/Support/Tickets.cshtml` | User ticket list |
| `Views/Support/TicketDetail.cshtml` | User ticket conversation |
| `Views/Crm/Tickets.cshtml` | Admin ticket list |
| `Views/Crm/TicketDetail.cshtml` | Admin ticket detail |
| `Views/Subscription/Payment.cshtml` | (update) Add support button |

---

## 8. UI Details

### Support Button Styling

- Small circular button, 56px diameter
- Icon: chat bubble or question mark (SVG)
- Position: fixed, bottom-right corner (20px from edges)
- Colors: `bg-orange-accent`, hover: `brightness-110`
- Z-index: 30 (below modals which are z-50)
- Only visible to authenticated users

### Ticket Status Badges

- Open: `bg-yellow-100 text-yellow-700`
- In Progress: `bg-blue-100 text-blue-700`
- Resolved: `bg-green-100 text-green-700`

### Conversation Styling

- User messages: left-aligned, light background
- Admin messages: right-aligned, orange/accent background, bold sender name
- Timestamps: small, muted color below each message
- Reply form: text area with clear "Send" button

---

## 9. Authentication & Permissions

- Support button visible only to authenticated users (checks `session["user_id"]`)
- User can only see/reply to their own tickets
- Admin can only access tickets via `/crm/tickets` (protected by `AdminAuthFilter`)
- Replies only allowed if:
  - User: ticket status ≠ Resolved
  - Admin: always allowed (can reopen if needed)

---

## 10. Out of Scope

- Ticket assignment to specific admins
- Priority levels for tickets
- Auto-closure after inactivity
- Ticket search/advanced filters
- Attachment uploads
- Canned responses/templates
