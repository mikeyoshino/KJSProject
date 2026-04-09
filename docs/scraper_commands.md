# Scraper Service Command Reference

This document provides common commands and usage examples for the Buzz69 and AsianScandal scraping services.

## Environment Setup

Always run from the `scraper_service` directory with `src` on your `PYTHONPATH`.

**Mac/Linux:**
```bash
cd scraper_service
export PYTHONPATH=src
```

**PowerShell:**
```powershell
cd scraper_service
$env:PYTHONPATH="src"
```

---

## Buzz69 Scraper (`src/backfill.py`)

Handles historical backfilling and routine sync for Buzz69.com.

### Routine Sync (check for new posts)
```bash
python src/backfill.py --sync
```

### Historical Backfill
```bash
# Default: 29 pages spread over 365 days
python src/backfill.py

# Custom: 50 pages spread over 500 days
python src/backfill.py --pages 50 --spread-days 500

# Resume from a specific page
python src/backfill.py --pages 10 --start-page 30
```

---

## AsianScandal Backfill (`src/asianscandal_backfill.py`)

Scrapes AsianScandal.net, cleans HTML, uploads images to B2, AI rewrites, and saves to Supabase.
Skips posts already in the database (idempotent).

### Basic Backfill
```bash
# Default: 50 pages, 365-day spread, 4 workers, AI enabled
python src/asianscandal_backfill.py

# Custom page range
python src/asianscandal_backfill.py --pages 20 --start-page 50

# Limit total posts processed
python src/asianscandal_backfill.py --limit 100
```

### Backfill Without AI
```bash
python src/asianscandal_backfill.py --no-ai
```

### Backfill + Download Rapidgator Files to B2 Inline
Downloads each post's Rapidgator files immediately after scraping (one combined run).
```bash
python src/asianscandal_backfill.py --with-downloads

# With page + limit control
python src/asianscandal_backfill.py --with-downloads --pages 10 --limit 50
```

### All Options
| Flag | Default | Description |
|---|---|---|
| `--pages` | 50 | Number of AJAX pages to fetch |
| `--start-page` | 0 | AJAX page to start from |
| `--spread-days` | 365 | Days to spread post timestamps over |
| `--workers` | 4 | Parallel scrape workers |
| `--limit` | unlimited | Max posts to process |
| `--no-ai` | off | Skip Ollama AI rewrite |
| `--with-downloads` | off | Also download RG files → B2 inline |

---

## AsianScandal Download + Rewrite Pipeline (`src/asianscandal_download.py`)

Runs two concurrent pipelines on already-scraped posts:

- **Download pipeline** — picks `download_status=pending` posts → downloads from Rapidgator → extracts/renames → zips → uploads to B2 → marks `done`
- **Rewrite pipeline** — picks posts where `rewritten_at IS NULL` → re-scrapes → cleans HTML → uploads images to B2 → Ollama AI rewrite → updates `content_html`

### Run Both Pipelines (default)
```bash
python src/asianscandal_download.py

# With post limit per pipeline
python src/asianscandal_download.py --limit 20
```

### Download Only (no AI rewrite)
```bash
python src/asianscandal_download.py --no-rewrites

python src/asianscandal_download.py --no-rewrites --limit 60
```

### Rewrite Only (no Rapidgator downloads)
```bash
python src/asianscandal_download.py --no-downloads

# Skip AI, just clean HTML + re-upload images
python src/asianscandal_download.py --no-downloads --no-ai
```

### Fix Posts with Missing Rapidgator URLs
Some posts had RG links in plain text (not in `<a href>`). Re-scrapes those posts to find them.
```bash
python src/asianscandal_download.py --fix-missing-rg

python src/asianscandal_download.py --fix-missing-rg --limit 50
```

### Tune Worker Counts
```bash
# Default: 1 download worker, 4 rewrite workers
python src/asianscandal_download.py --download-workers 2 --rewrite-workers 6
```

> **Note:** Keep `--download-workers` at 1–2. Rapidgator rate-limits aggressively and will ban on too many concurrent requests.

### All Options
| Flag | Default | Description |
|---|---|---|
| `--limit` | unlimited | Max posts per pipeline |
| `--download-workers` | 1 | Rapidgator download workers |
| `--rewrite-workers` | 4 | Rewrite/AI workers |
| `--no-ai` | off | Skip Ollama AI rewrite step |
| `--no-downloads` | off | Rewrite pipeline only |
| `--no-rewrites` | off | Download pipeline only |
| `--fix-missing-rg` | off | Re-scrape posts with empty `original_rapidgator_url` |

---

## Scheduled Scraper (`src/main.py`)

Runs every 15 minutes automatically to check for new Buzz69 posts.

```bash
python src/main.py
```

---

## Common Workflows

### First-time AsianScandal setup (scrape everything)
```bash
# 1. Backfill all historical posts (no downloads yet, fast)
python src/asianscandal_backfill.py --pages 50 --no-ai

# 2. Fix any posts where RG links were in plain text
python src/asianscandal_download.py --fix-missing-rg

# 3. Run AI rewrites on all scraped posts
python src/asianscandal_download.py --no-downloads

# 4. Start downloading Rapidgator files to B2
python src/asianscandal_download.py --no-rewrites
```

### Daily sync (new posts only)
```bash
# Scrape latest 1-2 pages, download + rewrite immediately
python src/asianscandal_backfill.py --pages 2 --with-downloads --limit 20
```

### Catch up on pending downloads after Rapidgator traffic reset (midnight UTC)
```bash
python src/asianscandal_download.py --no-rewrites --limit 100
```

---

## Help

All scripts support `--help`:

```bash
python src/backfill.py --help
python src/asianscandal_backfill.py --help
python src/asianscandal_download.py --help
```

> **Tip:** If Rapidgator hits its daily traffic limit mid-run, the download pipeline stops automatically and resets affected posts to `pending`. Re-run after midnight UTC to continue.
