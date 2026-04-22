# Plan: Replace exe.io Ads with 1-Day Free Trial

## Context

Free users currently see exe.io ad-gated download links. The goal is to eliminate all free-with-ads access and replace it with a single 1-day trial subscription granted automatically on new user registration. After the trial expires, users must purchase a paid subscription. No countdown banner is shown during the trial. Existing users are not given a trial retroactively.

---

## Design Decisions

- **Trial stored in `subscriptions` table** as `plan='trial'`, `status='active'`, `amount_usd=0`, `expires_at=UTC+1day`
- `GetActiveSubscriptionAsync()` already handles expiry — no extra logic needed
- **Disposable email blocking** on registration (bundled static blocklist)
- **No free download box** — removed entirely from all views
- **Expired/no-sub users** see locked premium box + Bootstrap modal (subscribe or register CTA)
- **Trial users** get identical full access to paid subscribers during the 1-day window

---

## Implementation Steps

### Step 1 — Disposable Email Blocklist
- Download `disposable_email_blocklist.conf` from `disposable-email-domains` GitHub project
- Save as `KJSWeb/Data/disposable_email_domains.txt`
- Create `DisposableEmailService.cs` in `KJSWeb/Services/`:
  - Loads the file once at startup into a `HashSet<string>`
  - Exposes `bool IsDisposable(string email)` — extracts domain, checks the set
- Register as singleton in `Program.cs`

### Step 2 — `CreateTrialSubscriptionAsync` in SupabaseService
File: `KJSWeb/Services/SupabaseService.cs`
- Add method `CreateTrialSubscriptionAsync(string userId)`
- Inserts row: `plan=trial`, `status=active`, `amount_usd=0`, `expires_at=now+1day`, `user_id=userId`
- Uses existing `HttpClient` POST pattern (raw REST, same as other insert methods)

### Step 3 — Auth Controller Registration Flow
File: `KJSWeb/Controllers/AuthController.cs`
- Inject `DisposableEmailService` and `SupabaseService`
- In `Signup` POST, before calling Supabase:
  - Call `_disposableEmail.IsDisposable(email)` → if true, return error view with message "Please use a permanent email address"
- After `SignInUserAsync()` succeeds:
  - Call `await _supabase.CreateTrialSubscriptionAsync(userId)` in a try/catch — log failure but do not fail registration

### Step 4 — Remove Free Download URL Generation from Controllers
Files: `KJSWeb/Controllers/HomeController.cs`, `AsianScandalController.cs`, `JGirlController.cs`
- In each `Details` action, remove the loop that builds `/download/public?postId=...&part=...` URLs
- **Before** the `if (!string.IsNullOrEmpty(userId))` block, set `ViewBag.ShowSubscribeModal = true` as the default
- Inside the block, after `GetActiveSubscriptionAsync`: if `activeSub != null`, set `ViewBag.ShowSubscribeModal = false`
- This ensures unauthenticated users AND authenticated users with no active sub both get the modal

### Step 5 — Delete exe.io Infrastructure
- Delete `KJSWeb/Services/ExeIoService.cs`
- Remove `ExeIoService` registration from `Program.cs`
- Delete `DownloadController.Public()` action
- Delete `DownloadController.Start()` action
- If `DownloadController` is now empty, delete the file
- Remove `ExeIoService` constructor injection from `DownloadController` (if file kept)

### Step 6 — Update `_PremiumDownload.cshtml`
File: `KJSWeb/Views/Shared/_PremiumDownload.cshtml`
- **Delete** the `else` branch (free download card, exe.io links, "advertising partners" text)
- When `IsSubscribed == false`:
  - Show premium download box UI with buttons rendered as greyed-out/locked (no `href`, disabled state)
  - Add Bootstrap modal triggered on page load (`show.bs.modal` on `DOMContentLoaded`)
  - Modal content branches on `User.Identity.IsAuthenticated`:
    - **Authenticated**: "Your access has expired" + "Subscribe from $5/month" → `/subscription/pricing` + dismiss button
    - **Unauthenticated**: "Get 1 free day — no card needed" + "Create Free Account" → `/auth/signup` + dismiss button
  - Dismiss closes modal; locked box remains visible, no download links shown

### Step 7 — Write Design Doc and Commit
- Save full spec to `docs/superpowers/specs/2026-04-22-free-trial-design.md`
- Commit all changes with message describing the feature

---

## Files Modified

| File | Change |
|------|--------|
| `KJSWeb/Services/SupabaseService.cs` | Add `CreateTrialSubscriptionAsync` |
| `KJSWeb/Services/DisposableEmailService.cs` | New — disposable email checker |
| `KJSWeb/Services/ExeIoService.cs` | **Delete** |
| `KJSWeb/Controllers/AuthController.cs` | Add disposable check + trial insert on signup |
| `KJSWeb/Controllers/HomeController.cs` | Remove free URL gen, add `ShowSubscribeModal` |
| `KJSWeb/Controllers/AsianScandalController.cs` | Same as Home |
| `KJSWeb/Controllers/JGirlController.cs` | Same as Home |
| `KJSWeb/Controllers/DownloadController.cs` | Delete `Public()` + `Start()`, likely delete file |
| `KJSWeb/Views/Shared/_PremiumDownload.cshtml` | Remove free card, add locked UI + modal |
| `KJSWeb/Data/disposable_email_domains.txt` | New — blocklist data file |
| `Program.cs` | Remove ExeIo DI, add DisposableEmailService singleton |

---

## Verification

1. Register a new account with a real email → subscription row with `plan=trial` appears in Supabase, user can download immediately
2. Register with a disposable email (e.g. `@mailinator.com`) → error shown, no account created
3. Let the trial expire (manually set `expires_at` to the past in Supabase) → revisit a post, modal appears, no download links visible
4. Dismiss modal → modal closes, locked premium box remains, still no download links
5. Unauthenticated visit to a post → modal shows "Get 1 free day — Create Free Account"
6. Existing user (no subscription, no trial) → same locked box + "subscribe" modal
7. Confirm `/download/public` route returns 404 (route removed)
8. Confirm no exe.io service registered (app starts without error)
