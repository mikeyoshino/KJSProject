# Asian Scandal Web Integration Design Specification

This document specifies the integration of the "Asian Scandal" content into the main KJSWeb application, pulling from the dedicated `asianscandal_posts` table.

## 1. Overview
The goal is to provide a dedicated section for Asian Scandal content with its own navigation entry, paginated listing, and enhanced post detail pages that leverage unique metadata like tags and direct download mirrors.

## 2. Architecture & Data Models

### 2.1. Models (KJSWeb.Models)
#### `AsianScandalPost`
A new model inheriting from `BaseModel` and mapped to the `asianscandal_posts` table.
- `Id`: `Guid`
- `Title`: `string`
- `ThumbnailUrl`: `string`
- `ContentHtml`: `string`
- `SourceUrl`: `string`
- `Categories`: `List<string>`
- `Tags`: `List<string>`
- `OriginalRapidgatorUrls`: `List<string>` (Column: `original_rapidgator_url`)
- `OurDownloadLink`: `string` (Column: `our_download_link`)
- `CreatedAt`: `DateTime`

### 2.2. Services (KJSWeb.Services.SupabaseService)
Add new asynchronous methods to handle the `asianscandal_posts` table:
- `GetLatestAsianScandalPostsAsync(int page, int pageSize)`: Returns a tuple of `(List<AsianScandalPost> Posts, int TotalCount)`.
- `GetAsianScandalPostByIdAsync(string id)`: Returns a single `AsianScandalPost` or null.
- `GetAsianScandalTotalCountAsync()`: Private helper for pagination.

## 3. Controllers & Routes

### 3.1. `AsianScandalController`
A new controller to manage routes:
- `GET /AsianScandal?page=1`: Index listing.
- `GET /AsianScandal/post/{id}`: Post details.
- Route: `[Route("asian-scandal")]` for clean URLs.

## 4. User Interface (UI)

### 4.1. Navigation
- **Desktop**: Add "Asian Scandal" link next to "Home" in the main navigation.
- **Mobile**: Add "Asian Scandal" link below "Home" in the hammer menu.

### 4.2. Index View (Views/AsianScandal/Index.cshtml)
- Reuses the 3-column grid design from `Home/Index.cshtml`.
- Supports pagination using `PaginationInfo`.
- Shows a "New" badge for posts created within the last 24 hours.

### 4.3. Details View (Views/AsianScandal/Details.cshtml)
- Reuses the `Home/Details.cshtml` structure with enhancements:
- **Fast Mirror Button**: A primary download button if `OurDownloadLink` is non-empty.
- **Rapidgator Links**: Secondary mirrors listed below.
- **Tags Cloud**: Displays tags as small, rounded badges listed below the main content.

## 5. Security & Authentication
- Access follows the existing site policy (Details require subscription if download gating is active).
- JWT validation and subscription checks remain consistent with the `HomeController` implementation.

## 6. Verification
- Build and run the project.
- Verify navigation links correctly target the `/asian-scandal` route.
- Confirm total post counts and pagination links work as expected.
