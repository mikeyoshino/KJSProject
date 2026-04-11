# Support Tickets Implementation Plan
**Date:** 2026-04-11  
**Based on:** `docs/superpowers/specs/2026-04-11-support-tickets-design.md`

---

## Overview
Implement a user-facing support ticket system with floating button, in-app conversations, and admin CRM management. Users report payment issues → admins reply via CRM → users get email notifications.

---

## Task 1: Create C# Models & Enums

**Description:** Add `TicketStatus`, `TicketCategory` enums and `SupportTicket`, `TicketMessage` models to the codebase.

**Files to create:**
- `KJSWeb/Models/TicketStatus.cs`
- `KJSWeb/Models/TicketCategory.cs`
- `KJSWeb/Models/SupportTicket.cs`
- `KJSWeb/Models/TicketMessage.cs`

**Acceptance criteria:**
- All enums and models compile without errors
- Models include all fields from design spec
- JSON serialization uses proper attributes for Supabase mapping

---

## Task 2: Create Supabase Tables (SQL Migration)

**Description:** Add SQL migration for `support_tickets` and `ticket_messages` tables.

**Files to create:**
- `docs/sql/2026-04-11-support-tickets.sql`

**Acceptance criteria:**
- SQL creates both tables with correct schema
- Foreign key constraint on ticket_messages → support_tickets
- Document includes migration instructions for user to run in Supabase

---

## Task 3: Extend SupabaseService with Ticket Methods

**Description:** Add 8 new methods to `SupabaseService` for ticket CRUD and querying.

**Files to modify:**
- `KJSWeb/Services/SupabaseService.cs`

**Methods to add:**
1. `CreateTicketAsync(userId, email, title, category, description)`
2. `GetUserTicketsAsync(userId, page, pageSize)`
3. `GetAllTicketsAsync(page, pageSize, status?)`
4. `GetTicketByIdAsync(id)`
5. `GetTicketMessagesAsync(ticketId)`
6. `AddTicketMessageAsync(ticketId, senderId, message, isAdmin)`
7. `UpdateTicketStatusAsync(ticketId, newStatus)`

**Acceptance criteria:**
- All methods use Supabase REST API with service key
- Proper error handling and null checks
- Methods use enums (TicketStatus, TicketCategory) not strings

---

## Task 4: Create SupportController (User Routes)

**Description:** New controller for user ticket operations at `/support` routes.

**Files to create:**
- `KJSWeb/Controllers/SupportController.cs`

**Routes to implement:**
- `GET /support/tickets` — list user's tickets (paginated, auth required)
- `GET /support/tickets/{id}` — view ticket detail + messages (auth required)
- `POST /support/tickets/create` — create new ticket (auth required)
- `POST /support/tickets/{id}/reply` — add message (auth required)

**Acceptance criteria:**
- All routes require authenticated session
- User can only see/reply to their own tickets
- Create endpoint returns JSON response with ticket ID on success
- Reply endpoint updates last_reply_at and triggers email notification

---

## Task 5: Create Support Views (User-Facing)

**Description:** Razor views for user ticket list and conversation detail.

**Files to create:**
- `KJSWeb/Views/Support/Tickets.cshtml` — ticket list
- `KJSWeb/Views/Support/TicketDetail.cshtml` — conversation view with reply form

**Acceptance criteria:**
- Tickets.cshtml shows: ID, Title, Status badge, Created, Last Reply
- TicketDetail.cshtml shows conversation thread with timestamps
- Admin messages styled differently from user messages
- Reply form only shown if status ≠ Resolved
- Pagination on list view (20 per page)

---

## Task 6: Add Support Button to Payment Page

**Description:** Update Payment.cshtml to include floating support button in bottom-right corner.

**Files to modify:**
- `KJSWeb/Views/Subscription/Payment.cshtml`

**Details:**
- Floating button: 56px circle, orange-accent color, chat bubble icon
- Fixed position: bottom-right (20px from edges), z-index: 30
- Only visible if authenticated (check session)
- Click → modal with ticket creation form

**Acceptance criteria:**
- Button visible and styled correctly
- Modal opens on click with form (description textarea + submit button)
- Form submission creates ticket and closes modal
- Success message shown to user

---

## Task 7: Extend CRM with Tickets Management

**Description:** Add `/crm/tickets` routes and pages, plus tickets section in user detail.

**Files to create:**
- `KJSWeb/Controllers/CrmTicketsController.cs` (or routes in existing CrmController)
- `KJSWeb/Views/Crm/Tickets.cshtml` — admin ticket list
- `KJSWeb/Views/Crm/TicketDetail.cshtml` — admin ticket conversation + reply form

**Routes to implement:**
- `GET /crm/tickets` — list all tickets (status filter tabs, paginated)
- `GET /crm/tickets/{id}` — ticket detail with reply form
- `POST /crm/tickets/{id}/reply` — admin add message
- `PATCH /crm/tickets/{id}/status` — change status + send email

**Files to modify:**
- `KJSWeb/Views/Crm/UserDetail.cshtml` — add tickets tab/section

**Acceptance criteria:**
- All routes protected by AdminAuthFilter
- Ticket list shows: ID, User Email, Title, Status, Created, Last Reply
- Status filter tabs (All/Open/In Progress/Resolved) work correctly
- User detail shows user's tickets in a tab
- Admin can change status with dropdown (triggers email)
- Reply form submits and sends email to user

---

## Task 8: Implement Email Notifications

**Description:** Add email notification logic for ticket replies and status changes.

**Files to create or modify:**
- `KJSWeb/Services/EmailService.cs` (create if needed, or extend BlockonomicsService pattern)

**Email templates:**
1. Admin reply: "Re: Your Support Ticket #{ID}"
2. Status → In Progress: "We're looking into your issue"
3. Status → Resolved: "Your issue has been resolved"

**Acceptance criteria:**
- Email sent when admin replies to a ticket
- Email sent when ticket status changes (In Progress, Resolved only)
- Email includes relevant message text + link to ticket
- Email address pulled from `SupportTicket.UserEmail`

---

## Task 9: Wire Up Program.cs, Config, Auth

**Description:** Register services, add middleware, configure authentication.

**Files to modify:**
- `KJSWeb/Program.cs`
- `KJSWeb/appsettings.Example.json`

**Changes:**
- Register `EmailService` (if needed)
- Add `[ServiceFilter(typeof(AdminAuthFilter))]` to CRM ticket routes (or create filter)
- Ensure `BanCheckMiddleware` runs before routing (already done in CRM)

**Acceptance criteria:**
- Application builds without errors
- SupportController routes accessible at `/support/tickets`
- CRM ticket routes accessible at `/crm/tickets` with admin auth
- Config example includes any new required settings

---

## Summary

**Total tasks:** 9
**Dependencies:** 
- Task 2 must be done before Task 3 (models needed before service methods)
- Task 3 must be done before Tasks 4, 7 (both controllers depend on SupabaseService methods)
- Task 4 can proceed in parallel with Task 5, 6
- Task 7 can proceed in parallel with Tasks 4, 5, 6
- Task 8 can be done after any ticket-creation logic is in place
- Task 9 (wiring) must be done last

**Sequence:**
1. Task 1 (models) - foundation
2. Task 2 (SQL) - database schema
3. Task 3 (SupabaseService) - data access
4. Task 4, 5, 6, 7, 8 (controllers, views, email) - can be done in parallel
5. Task 9 (wiring) - integration
