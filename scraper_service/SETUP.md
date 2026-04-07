# Scraper Service — Setup & Usage Guide

Covers Windows and Linux. Python 3.11+ required.

---

## Table of Contents

1. [Prerequisites](#prerequisites)
2. [Installation](#installation)
3. [Configuration](#configuration)
4. [Database Setup](#database-setup)
5. [JGirl Pipeline](#jgirl-pipeline)
6. [AsianScandal Pipeline](#asianscandal-pipeline)
7. [CLI Reference](#cli-reference)
8. [Troubleshooting](#troubleshooting)

---

## Prerequisites

| Requirement | Windows | Linux |
|-------------|---------|-------|
| Python 3.11+ | [python.org](https://python.org/downloads) | `sudo apt install python3.11 python3.11-venv` |
| Google Chrome | Install normally | `sudo apt install google-chrome-stable` |
| pip | Included with Python | `sudo apt install python3-pip` |

> Chrome is required only once for the Cloudflare bypass auth step (`jgirl_auth.py`).

---

## Installation

### Windows

```powershell
cd scraper_service

# Create and activate virtual environment
python -m venv .venv
.venv\Scripts\activate

# Install dependencies
pip install -r requirements.txt
pip install undetected-chromedriver
```

### Linux

```bash
cd scraper_service

# Create and activate virtual environment
python3.11 -m venv .venv
source .venv/bin/activate

# Install dependencies
pip install -r requirements.txt
pip install undetected-chromedriver

# Linux: Chrome requires these system packages
sudo apt install -y libglib2.0-0 libnss3 libgconf-2-4 libfontconfig1
```

---

## Configuration

Copy and fill in `.env` in the `scraper_service/` directory:

```env
# Supabase
SUPABASE_URL=https://your-project.supabase.co
SUPABASE_KEY=your-anon-key

# Backblaze B2
B2_APPLICATION_KEY_ID=your-key-id
B2_APPLICATION_KEY=your-application-key
B2_BUCKET_NAME=YourBucket
B2_SERVICE_URL=https://s3.us-east-005.backblazeb2.com
B2_PUBLIC_BASE_URL=https://f005.backblazeb2.com/file/YourBucket

# Real-Debrid (required for JGirl file downloads)
RD_API_KEY=your-real-debrid-api-key
```

Get your Real-Debrid API key from: https://real-debrid.com/apitoken

---

## Database Setup

Run all migration files in order in the Supabase SQL editor:

```
supabase/migrations/20260403_multi_links.sql
supabase/migrations/20260404_add_asianscandal_posts.sql
supabase/migrations/20260404_add_categories.sql
supabase/migrations/20260404_add_download_links_col.sql
supabase/migrations/20260404_add_subscriptions.sql
supabase/migrations/20260406_add_download_status_asianscandal.sql
supabase/migrations/20260407_add_jgirl_posts.sql
```

---

## JGirl Pipeline

jgirl.co uses Cloudflare protection. Authentication is a **one-time** step that saves cookies for all future runs.

### Step 1 — Cloudflare Auth (one-time only)

This opens a real Chrome window, solves the Cloudflare challenge, and saves cookies to `src/jgirl_state.json`.

**Windows:**
```powershell
cd scraper_service/src
python jgirl_auth.py
```

**Linux:**
```bash
cd scraper_service/src
python jgirl_auth.py
```

> On a headless Linux server (no display), you need a virtual display:
> ```bash
> sudo apt install -y xvfb
> Xvfb :99 -screen 0 1280x720x24 &
> export DISPLAY=:99
> python jgirl_auth.py
> ```
>
> Or use SSH with X11 forwarding: `ssh -X user@server`

The script waits for the page to fully load, then saves cookies automatically. The Chrome window will close when done. You should see:
```
Saved cookies to: .../jgirl_state.json
Cookies: ['_pk_id.1.f29f', 'cf_clearance', '_pk_ses.1.f29f']
```

### Step 2 — Run the Scraper

All commands run from `scraper_service/src/`.

**Windows:**
```powershell
cd scraper_service\src
python jgirl_backfill.py --source upskirt --limit 5
```

**Linux:**
```bash
cd scraper_service/src
python jgirl_backfill.py --source upskirt --limit 5
```

#### Recommended run order:

```bash
# 1. Dry run first — scrapes and resolves Real-Debrid links, no DB writes, no uploads
python jgirl_backfill.py --source upskirt --limit 2 --dry-run

# 2. Images only test — scrape + B2 uploads, skip download
python jgirl_backfill.py --source upskirt --limit 2 --no-download

# 3. Full pipeline — scrape + images + Real-Debrid download → B2
python jgirl_backfill.py --source upskirt --limit 2

# 4. Run all categories
python jgirl_backfill.py --source all

# 5. Backfill mode — spread posts across 365 days instead of using original dates
python jgirl_backfill.py --source all --mode backfill --spread-days 365
```

#### Re-authenticating (cookies expired)

Cloudflare cookies last ~24 hours. If you start getting 403 errors, re-run auth:

```bash
python jgirl_auth.py
```

---

## AsianScandal Pipeline

No Cloudflare — plain requests, no auth step needed.

```bash
cd scraper_service/src

# Scrape new posts
python asianscandal_backfill.py

# Rewrite content with AI (requires Ollama running locally)
python asianscandal_rewrite.py

# Rewrite all posts (including already-written ones)
python asianscandal_rewrite.py --all
```

---

## CLI Reference

### `jgirl_backfill.py`

| Flag | Default | Description |
|------|---------|-------------|
| `--source` | `all` | `upskirt`, `ksiroto`, `fc2`, or `all` |
| `--mode` | `incremental` | `incremental` uses original post date; `backfill` spreads evenly over N days |
| `--limit` | unlimited | Max number of posts to process per run |
| `--pages` | unlimited | Max listing pages to crawl |
| `--spread-days` | `365` | Used with `--mode backfill` — date range to spread posts over |
| `--delay` | `1.5` | Seconds to wait between posts |
| `--dry-run` | off | Resolve RD links and log results, no DB writes or B2 uploads |
| `--no-images` | off | Skip thumbnail and preview image uploads to B2 |
| `--no-download` | off | Skip Real-Debrid download step |

### `asianscandal_backfill.py`

```bash
python asianscandal_backfill.py [--limit N] [--delay SECONDS]
```

### `asianscandal_rewrite.py`

```bash
python asianscandal_rewrite.py [--all] [--limit N]
```

---

## B2 Storage Layout

```
KJSProject/
└── JGirls/
    └── {post-uuid}/
        ├── thumbnail.jpg
        ├── preview/
        │   ├── 0001.jpg
        │   ├── 0002.jpg
        │   └── ...
        └── {filename}.mp4   ← downloaded via Real-Debrid
└── scandal69/
    └── {md5hash}.jpg        ← AsianScandal images
```

---

## Troubleshooting

### 403 Forbidden when scraping jgirl.co

Cloudflare cookies have expired. Re-run:
```bash
python jgirl_auth.py
```

### `jgirl_state.json not found`

Run `jgirl_auth.py` first to generate the cookies file.

### `ChromeDriver version mismatch`

`jgirl_auth.py` auto-detects your Chrome version on Linux. On Windows, it defaults to version 146. If you have a different Chrome version:
```python
# In jgirl_auth.py, change:
driver = uc.Chrome(options=options, headless=False, version_main=146)
# To your actual Chrome version, e.g.:
driver = uc.Chrome(options=options, headless=False, version_main=135)
```

Check your Chrome version: open Chrome → `chrome://settings/help`

### `RD_API_KEY not set`

Add `RD_API_KEY=your_key` to `scraper_service/.env`.

### Real-Debrid returns 404 for a link

The file host link is dead or not supported by Real-Debrid. The scraper automatically tries all available links in priority order (katfile → ddownload → rapidgator). If all fail, the post is marked `download_status = 'failed'` and B2 is cleaned up.

### Linux: Chrome won't open (no display)

Use a virtual display:
```bash
sudo apt install -y xvfb
Xvfb :99 -screen 0 1280x720x24 &
export DISPLAY=:99
python jgirl_auth.py
```

### `No module named 'undetected_chromedriver'`

```bash
pip install undetected-chromedriver
```

### `No module named 'boto3'`

```bash
pip install boto3
```
