# Scraper Service

Scrapes, processes, and migrates AsianScandal.net posts into Supabase + Backblaze B2.

## Setup

```bash
cd scraper_service
python3 -m venv .venv
source .venv/bin/activate
pip install -r requirements.txt
```

> **Every new terminal session:** activate the venv before running any scripts:
> ```bash
> cd scraper_service
> source .venv/bin/activate
> cd src
> ```

Configure `.env` (copy from the example below):

```env
SUPABASE_URL=https://xxxx.supabase.co
SUPABASE_KEY=your_anon_key

B2_APPLICATION_KEY_ID=your_key_id
B2_APPLICATION_KEY=your_key
B2_BUCKET_NAME=KJSProject
B2_SERVICE_URL=https://s3.us-east-005.backblazeb2.com
B2_PUBLIC_BASE_URL=https://f005.backblazeb2.com/file/KJSProject

OLLAMA_URL=http://localhost:11434
OLLAMA_MODEL=dolphin-mistral

# Rapidgator credentials — wrap password in single quotes if it contains $ or special chars
RAPIDGATOR_USERNAME=your@email.com
RAPIDGATOR_PASSWORD='yourpassword'
RAPIDGATOR_API_URL=https://rapidgator.net/api/v2
RAPIDGATOR_DELAY_MS=3000
MIGRATOR_TEMP_FOLDER=/tmp/rg-migrator
```

> All scripts must be run from inside `src/`:
> ```bash
> cd scraper_service/src
> ```

---

## Scripts

### `backfill.py` — Buzz69 sync + migration

All-in-one pipeline for buzz69.com posts. Handles new post sync, image migration, AI rewrite, and RG downloads.

```bash
# Sync new posts (images + AI rewrite, no downloads)
python backfill.py --sync

# Sync new posts including RG file downloads to B2
python backfill.py --sync --with-downloads

# Scrape more pages (default is 1)
python backfill.py --sync --with-downloads --pages 5

# Retry all failed downloads (resets failed → pending then re-downloads)
python backfill.py --retry-failed

# Retry failed downloads in batches
python backfill.py --retry-failed --limit 10

# Migrate existing posts: images → B2 + AI rewrite + pending downloads
python backfill.py --migrate

# Re-run AI rewrite on posts still containing old brand names (buzz69.com, Buzz69)
python backfill.py --rewrite

# Re-run AI rewrite for AsianScandal posts
python backfill.py --rewrite --source AsianScandal

# Skip AI rewrite (faster)
python backfill.py --sync --no-ai
```

**Recommended workflows:**

```bash
# Daily sync — new posts with full pipeline
python backfill.py --sync --with-downloads

# After a failed run — retry all downloads that failed
python backfill.py --retry-failed

# Fix branding on old posts after updating AI prompt
python backfill.py --rewrite --limit 50
```

**Notes:**
- `--sync` skips posts that already exist in the DB (`source_url` deduplicated).
- `--retry-failed` resets all `download_status='failed'` Buzz69 posts back to `pending` then runs the download pipeline immediately.
- Rapidgator has a daily traffic limit. If exceeded, the run stops automatically and affected posts are reset to `pending`. Re-run after midnight UTC.
- Ollama calls are serialised (1 at a time) regardless of `--workers`.

---

### `asianscandal_backfill.py` — Scrape new posts

Collects posts from AsianScandal.net via AJAX pagination, uploads images + thumbnails to B2, rewrites content via Ollama, and saves to Supabase.

```bash
# Basic — scrape 50 pages (300 posts) starting from page 1
python asianscandal_backfill.py

# Resume from where you left off (800 posts already = page 133)
python asianscandal_backfill.py --start-page 133 --pages 50

# Test with 1 post only
python asianscandal_backfill.py --start-page 133 --pages 50 --limit 1

# Skip Ollama rewrite (faster, add rewrites later via asianscandal_download.py)
python asianscandal_backfill.py --start-page 133 --pages 50 --no-ai

# Also download Rapidgator files → B2 inline (slower per post)
python asianscandal_backfill.py --start-page 133 --pages 50 --with-downloads

# Full options
python asianscandal_backfill.py \
  --start-page 133 \   # AJAX page index to start from (0-based, 6 posts per page)
  --pages 50 \         # how many AJAX pages to fetch
  --spread-days 365 \  # spread post created_at timestamps across N days
  --workers 4 \        # parallel scraping workers
  --no-ai \            # skip Ollama rewrite
  --with-downloads \   # download RG files → B2 inline
  --limit 10           # max posts to process (stops collection early)
```

**How to calculate `--start-page`:**
`start_page = posts_already_scraped / 6`
Example: 800 posts → `800 / 6 = 133`

---

### `asianscandal_download.py` — Download + Rewrite pipeline (parallel)

Runs two concurrent pipelines in one command:
- **Download pipeline**: posts where `download_status='pending'` → Rapidgator → extract → zip → B2 → updates `our_download_link`
- **Rewrite pipeline**: posts where `rewritten_at IS NULL` → scrape → clean → Ollama → updates `content_html`

```bash
# Run both pipelines together
python asianscandal_download.py

# Cap each pipeline at N posts
python asianscandal_download.py --limit 20

# Download only (skip rewrite)
python asianscandal_download.py --no-rewrites

# Rewrite only (skip download)
python asianscandal_download.py --no-downloads

# Skip Ollama AI rewrite (clean HTML only)
python asianscandal_download.py --no-ai

# Custom concurrency (keep download-workers low — RG rate limits)
python asianscandal_download.py --download-workers 1 --rewrite-workers 3

# Full options
python asianscandal_download.py \
  --limit 50 \             # max posts per pipeline
  --download-workers 1 \   # RG download threads (default 1)
  --rewrite-workers 4 \    # rewrite threads (default 4)
  --no-ai \                # skip Ollama
  --no-downloads \         # rewrite pipeline only
  --no-rewrites            # download pipeline only
```

**Notes:**
- Rapidgator has a daily traffic limit. If exceeded, the download pipeline stops automatically and resets affected posts to `pending`. Re-run after midnight UTC.
- Ollama calls are always serialised (1 at a time) regardless of `--rewrite-workers`.

---

### `asianscandal_rewrite.py` — Rewrite existing posts

Re-scrapes and rewrites posts where `rewritten_at IS NULL`. Useful for posts that were inserted without AI rewrite (`--no-ai`).

```bash
# Rewrite all unprocessed posts
python asianscandal_rewrite.py

# Limit to N posts
python asianscandal_rewrite.py --limit 50

# Preview without writing to DB
python asianscandal_rewrite.py --dry-run

# Clean HTML only, skip Ollama
python asianscandal_rewrite.py --no-ai

# Full options
python asianscandal_rewrite.py \
  --limit 100 \    # max posts to process
  --workers 4 \    # parallel workers
  --dry-run \      # scrape + clean but do not save
  --no-ai          # skip Ollama step
```

---

### `jgirl_auth.py` — Authenticate to bypass Cloudflare

JGirl requires a valid session cookie to access pages. Run this **once before first use** and whenever you get 403 errors:

```bash
python jgirl_auth.py
```

This saves a `jgirl_state.json` session file in `src/`. Without it, `jgirl_backfill.py` will fail with 403 errors.

---

### `jgirl_backfill.py` — Scrape + download JGirl posts

Scrapes posts from JGirl sources (upskirt, ksiroto, fc2, bathroom), uploads images + thumbnails to B2, and downloads files via Real-Debrid.

```bash
# Scrape all sources incrementally (new posts only)
python jgirl_backfill.py

# Scrape a specific source
python jgirl_backfill.py --source bathroom

# Limit number of posts
python jgirl_backfill.py --source bathroom --limit 200

# Full backfill (all pages, not just new posts)
python jgirl_backfill.py --source bathroom --mode backfill

# Scrape N pages
python jgirl_backfill.py --source bathroom --pages 10

# Skip image upload (faster, thumbnails/previews not uploaded to B2)
python jgirl_backfill.py --source bathroom --no-images

# Skip Real-Debrid download step
python jgirl_backfill.py --source bathroom --no-download

# Dry run (scrape but do not write to DB or B2)
python jgirl_backfill.py --source bathroom --dry-run

# Run 2 posts in parallel (faster — keep at 2-3 max to avoid RD rate limits)
python jgirl_backfill.py --source bathroom --limit 200 --workers 2

# Full options
python jgirl_backfill.py \
  --source bathroom \       # upskirt | ksiroto | fc2 | bathroom | all
  --mode incremental \      # incremental (new only) | backfill (all pages)
  --limit 200 \             # max posts to process
  --pages 10 \              # max pages to scrape (0 = unlimited)
  --spread-days 365 \       # spread created_at timestamps across N days
  --delay 1.5 \             # delay between requests (seconds)
  --workers 2 \             # parallel workers per category (default 1, max 3)
  --no-images \             # skip B2 image upload
  --no-download \           # skip Real-Debrid download
  --dry-run                 # scrape only, no DB/B2 writes
```

**Sources:** `upskirt`, `ksiroto`, `fc2`, `bathroom`, `all`

---

## Recommended Workflows

### Initial backfill (fast, no downloads)

```bash
# 1. Scrape all posts quickly without AI or downloads
python asianscandal_backfill.py --start-page 133 --pages 100 --no-ai --workers 4

# 2. Then run rewrites + downloads together
python asianscandal_download.py --limit 100
```

### Sync new posts (daily)

```bash
# 1. Scrape latest posts from the site (page 1 = newest)
python asianscandal_scraper.py

# 2. Download + AI rewrite all pending posts
python asianscandal_download.py
```

### Ongoing (all-in-one per run)

```bash
python asianscandal_backfill.py --start-page 133 --pages 50 --with-downloads
```

### Just clear the download queue

```bash
python asianscandal_download.py --no-rewrites
```

### Just clear the rewrite queue

```bash
python asianscandal_download.py --no-downloads --limit 200
```

---

## Data Flow

```
asianscandal.net
    │
    ▼
asianscandal_backfill.py
    ├── scrape post HTML
    ├── upload images → B2 (scandal69/{md5}.jpg)
    ├── upload thumbnail → B2
    ├── AI rewrite via Ollama
    └── save to asianscandal_posts (download_status='pending')
              │
              ▼
asianscandal_download.py  [download pipeline]
    ├── fetch pending posts from Supabase
    ├── download from Rapidgator
    ├── extract archive (zip/rar/7z)
    ├── rename files → scandal69{N}.ext
    ├── create ZIP
    └── upload → B2 (posts/{id}/{id}.zip)
              │
              ▼
    UPDATE our_download_link = ['posts/{id}/{id}.zip']
    UPDATE download_status   = 'done'
```

## Supabase Table: `asianscandal_posts`

| Column | Description |
|---|---|
| `id` | UUID primary key |
| `source_url` | Original asianscandal.net URL (unique) |
| `title` | Post title |
| `thumbnail_url` | B2 URL of mirrored thumbnail |
| `content_html` | Cleaned + AI-rewritten HTML |
| `categories` | Array of category strings |
| `tags` | Array of tag strings |
| `original_rapidgator_url` | Array of original RG file URLs |
| `our_download_link` | Array of B2 object keys after migration |
| `download_status` | `pending` → `processing` → `done` / `failed` |
| `rewritten_at` | Timestamp when AI rewrite completed (NULL = not yet rewritten) |
| `created_at` | Synthetic timestamp spread across `--spread-days` |
