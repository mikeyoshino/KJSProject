# Admin CRM — Design Spec
**Date:** 2026-04-11  
**Status:** Approved

---

## Overview

A protected admin CRM section at `/crm` for managing subscriptions and users. Primary use cases:

- Manually activate a subscription when a payment processed but the webhook failed
- Revoke or extend an active subscription
- Ban / unban a user
- Search users by email and see their full subscription history

---

## 1. Access Control & Security

### Admin Auth

- Config key: `"Admin": { "Emails": ["you@example.com"] }` in `appsettings.json`
- `AdminAuthFilter` — `IActionFilter` attribute applied to the entire `CrmController`
  - Not logged in → redirect to `/auth/login?returnUrl=/crm`
  - Logged in but `session["user_email"]` not in `Admin:Emails` → HTTP 403
  - Otherwise → allow

### Ban Enforcement

- `BanCheckMiddleware` — runs on every request before controllers
- Maintains an in-memory `HashSet<string>` of banned `user_id`s, refreshed every 5 minutes from Supabase
- If session `user_id` is in the banned set: clear session → redirect to `/auth/login?banned=1`
- Login view shows "Your account has been suspended." when `?banned=1` query param is present

---

## 2. Layout & Navigation

Dedicated CRM layout (`Views/Crm/_Layout.cshtml`) fully isolated from the public site layout.

### Sidebar

- Default state: **64px wide**, icon only
- Hover state: **240px wide**, smooth CSS transition, shows icon + title + subtitle
- Items:

| Icon | Title | Subtitle | Route |
|---|---|---|---|
| BarChart | Dashboard | Overview & activity | `/crm` |
| Users | Users | Search & manage | `/crm/users` |
| CreditCard | Subscriptions | All plans & payments | `/crm/subscriptions` |

Active item highlighted with `bg-orange-accent` accent consistent with public site style.

---

## 3. Pages

### `/crm` — Dashboard

**Stat cards (4):**
- Active Subscribers — count where `status=active AND expires_at > now`
- New This Month — count of subscriptions created this calendar month
- Revenue This Month — `SUM(amount_usd)` where `paid_at` is this month and `status=active`
- Banned Users — count of rows in `banned_users`

**Recent Subscriptions table (10 rows, newest first):**  
Columns: User Email · Plan · Status · Paid At · Expires At · Amount USD

**Recent Signups table (5 rows, newest first):**  
Columns: Email · Registered At  
Source: Supabase Admin Auth API `/auth/v1/admin/users`

---

### `/crm/users` — User Search

- Search box (email or partial match, case-insensitive)
- Results table: Email · Registered At · Last Sign-In · Sub Status badge · Ban badge
- Each row links to `/crm/users/{userId}`
- No default list — search-only to avoid loading all Supabase auth users

---

### `/crm/users/{userId}` — User Detail

**Info card:**
- Email, registered at, last sign-in
- Current active subscription: plan, expires at (or "No active subscription")

**Ban/Unban control:**
- If not banned: red **Ban** button → opens modal with optional reason textarea → POST `/crm/users/{userId}/ban`
- If banned: **Unban** button → POST `/crm/users/{userId}/unban` (no modal needed)
- Banned status shown as a red badge on the info card

**Grant Subscription form:**
- Plan dropdown: Monthly (30 days) / 3-Month (90 days) / Yearly (365 days)
- Optional "Custom days" number input — overrides the plan default when filled
- Submit → POST `/crm/subscriptions/grant` → creates subscription with `status=active`, `expires_at = now + days`
- If user already has an active subscription, new grant extends from current `expires_at` (same logic as `ActivateSubscriptionAsync`)

**Subscription History table (all rows for this user, newest first):**  
Columns: Plan · Status · Paid At · Expires At · Amount USD · TXID (truncated)

---

### `/crm/subscriptions` — Subscriptions List

**Status filter tabs:** All · Active · Pending · Expired · Revoked  
**Pagination:** 25 per page, newest first

**Table columns:** User Email · Plan · Status · Paid At · Expires At · Amount USD · Actions

**Inline actions:**
- **Revoke** — PATCH `status=revoked` on that subscription row
- **Extend** — opens small modal: "Add N days" number input → PATCH `expires_at = expires_at + N days`

---

## 4. Data Layer

### Email Denormalization

The `subscriptions` table does not store user email today. To avoid N+1 Admin API calls when rendering subscription lists, email is stored in the subscription row at grant/creation time. `GrantSubscriptionAdminAsync` accepts `email` as a parameter. The `CreateSubscriptionAsync` path (Blockonomics webhook) already has the email available from the subscription lookup. The `Subscription` model and DTO gain an `email` field.

### New Supabase Table: `banned_users`

```sql
create table banned_users (
  user_id   text primary key,
  email     text not null,
  reason    text,
  banned_at timestamptz default now(),
  banned_by text        -- admin email who performed the ban
);
```

### New Service: `AdminService`

Wraps Supabase Admin Auth API using service role key.

| Method | Endpoint | Purpose |
|---|---|---|
| `SearchUsersAsync(email)` | `GET /auth/v1/admin/users?email=...` | User search |
| `GetUserByIdAsync(userId)` | `GET /auth/v1/admin/users/{id}` | User detail |
| `ListRecentUsersAsync(limit)` | `GET /auth/v1/admin/users?page=1&per_page={limit}` | Recent signups on dashboard |

### New `SupabaseService` Methods

| Method | Description |
|---|---|
| `GetAllSubscriptionsAsync(page, pageSize, status?)` | Paginated subscriptions, optional status filter |
| `GetSubscriptionsByUserIdAsync(userId)` | Full history for one user |
| `GrantSubscriptionAdminAsync(userId, plan, days)` | Insert active subscription; if user has active sub, extend from its expiry |
| `RevokeSubscriptionAsync(id)` | PATCH `status=revoked` |
| `ExtendSubscriptionAsync(id, days)` | PATCH `expires_at = expires_at + interval '{days} days'` via RPC or computed value |
| `BanUserAsync(userId, email, reason, adminEmail)` | Insert into `banned_users` |
| `UnbanUserAsync(userId)` | Delete from `banned_users` by `user_id` |
| `GetBannedUsersAsync()` | Fetch all `user_id`s for middleware cache |
| `GetCrmStatsAsync()` | Returns active count, new-this-month count, revenue this month, banned count |
| `GetRecentSubscriptionsAsync(limit)` | Newest N subscriptions with email (stored at grant time in subscription row) |

---

## 5. Routes

| Method | Route | Action |
|---|---|---|
| GET | `/crm` | Dashboard |
| GET | `/crm/users` | User search page |
| GET | `/crm/users/{userId}` | User detail |
| POST | `/crm/users/{userId}/ban` | Ban user |
| POST | `/crm/users/{userId}/unban` | Unban user |
| GET | `/crm/subscriptions` | Subscriptions list |
| POST | `/crm/subscriptions/grant` | Grant subscription |
| POST | `/crm/subscriptions/{id}/revoke` | Revoke subscription |
| POST | `/crm/subscriptions/{id}/extend` | Extend subscription |

---

## 6. New Files

```
KJSWeb/
  Controllers/
    CrmController.cs
  Services/
    AdminService.cs
  Filters/
    AdminAuthFilter.cs
  Middleware/
    BanCheckMiddleware.cs
  Views/
    Crm/
      _Layout.cshtml
      Dashboard.cshtml
      Users.cshtml
      UserDetail.cshtml
      Subscriptions.cshtml
```

`SupabaseService.cs` — extended with new methods (not split, keeps existing pattern).

---

## 7. Out of Scope

- Email notifications to banned users
- Audit log of admin actions
- Multi-admin roles / permissions
- Temporary bans with auto-expiry
- Post/content management via CRM
