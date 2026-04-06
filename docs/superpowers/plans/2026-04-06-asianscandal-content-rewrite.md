# AsianScandal Content Rewrite & B2 Image Migration

**Date:** 2026-04-06

## Context

All `content_html` was cleared from `asianscandal_posts`. This pipeline re-scrapes each post's content using its `source_url` (the original asianscandal.net URL), cleans it, re-hosts all images on Backblaze B2, and rewrites text via a local AI to produce a clean standalone post that retains only the person's name/social media handle.

---

## New Files

| File | Purpose |
|------|---------|
| `scraper_service/src/storage_b2.py` | B2 image upload via boto3. Stores images at `scandal69/{md5}.jpg` |
| `scraper_service/src/ai_rewriter.py` | Ollama HTTP client — rewrites HTML content via local LLM |
| `scraper_service/src/asianscandal_rewrite.py` | Main orchestration script |

## Modified Files

| File | Change |
|------|--------|
| `scraper_service/src/db.py` | Added `fetch_asianscandal_posts_missing_content()`, `fetch_all_asianscandal_posts()`, `update_asianscandal_content_html()` |
| `scraper_service/requirements.txt` | Added `boto3>=1.34.0` |
| `scraper_service/.env` | Added B2 and Ollama config stubs |

---

## Pipeline (per post)

1. Fetch posts from Supabase (`--all` = every post, default = only null/empty `content_html`)
2. Re-scrape original page via `source_url` using existing `fetch_html()` + `parse_post_page()`
3. **Pre-clean HTML** with BeautifulSoup:
   - Remove entire block-level wrapper (`div`, `p`, `figure`, etc.) if it contains a Rapidgator link or download-promo text
   - Unwrap `<a>` tags pointing to `asianscandal.net` or `rapidgator.net` (keep inner text)
   - Replace all text nodes: `asianscandal.net` → `scandal69.com`, `AsianScandal` → `Scandal69`
4. **Upload images to B2**: download each `<img>` src, upload to `scandal69/{md5}.jpg`, rewrite `src` attribute to B2 public URL
5. **AI rewrite** via Ollama: keep structure, replace remaining branding, rewrite RG promos as "direct download"
6. `UPDATE asianscandal_posts SET content_html = ? WHERE id = ?`

---

## AI Model

**Local Ollama on Mac M4** — chosen because OpenAI/Claude API refuse adult content rewrites.

- Default model: `llama3.2` (configurable via `OLLAMA_MODEL` env var)
- Endpoint: `http://localhost:11434` (configurable via `OLLAMA_URL`)
- Falls back to cleaned-but-unrewritten HTML if Ollama is unreachable

### Rewrite Prompt

```
You are editing HTML content for an adult website called Scandal69 (scandal69.com).

Title: {title}

Original HTML content (already pre-cleaned):
{html}

Rules:
1. Keep the EXACT same HTML structure and tags — do not add or remove elements.
2. Replace every occurrence of "asianscandal.net" with "scandal69.com".
3. Replace every occurrence of "AsianScandal" or "Asian Scandal" with "Scandal69".
4. Any sentence or phrase that promotes downloading from Rapidgator or any external
   file host must be rewritten to say the content is available as a "direct download"
   on this site instead.
5. Keep the person's name and any @instagram / social media handle exactly as written.
6. Keep the social media aesthetic — short punchy sentences, informal tone.
7. Output ONLY the rewritten HTML. No commentary, no markdown fences.
```

---

## B2 Image Storage

- **Path:** `scandal69/{md5_hash}.jpg`
- **Public URL:** `{B2_PUBLIC_BASE_URL}/scandal69/{md5_hash}.jpg`
- Idempotent: uses `HeadObject` check before uploading
- Thumbnail URLs are **not** changed (already in Supabase Storage from original backfill)

### Required `.env` variables

```
B2_APPLICATION_KEY_ID=
B2_APPLICATION_KEY=
B2_BUCKET_NAME=KJSProject
B2_SERVICE_URL=https://s3.us-east-005.backblazeb2.com
B2_PUBLIC_BASE_URL=https://f005.backblazeb2.com/file/KJSProject
OLLAMA_URL=http://localhost:11434
OLLAMA_MODEL=llama3.2
```

---

## CLI Usage

```bash
cd scraper_service/src

# Rewrite ALL posts (overwrite existing content_html)
python asianscandal_rewrite.py --all

# Rewrite only posts with missing content_html
python asianscandal_rewrite.py

# Dry run — scrape + clean, no DB writes
python asianscandal_rewrite.py --all --dry-run --limit 3

# Skip AI step (clean + B2 images only)
python asianscandal_rewrite.py --all --no-ai

# Limit and custom delay
python asianscandal_rewrite.py --all --limit 50 --delay 2.0
```

---

## Verification

1. `--dry-run --limit 3` — confirm correct posts selected, cleaned HTML looks right
2. `--limit 5 --no-ai` — verify B2 uploads succeed and URLs appear in output
3. `--limit 5` — verify full pipeline including Ollama rewrite
4. Check Supabase: `content_html` populated, `<img>` src values are B2 URLs containing `scandal69/`
5. Open a post in KJSWeb (`/asian-scandal/{id}`): images load, no asianscandal.net references visible
