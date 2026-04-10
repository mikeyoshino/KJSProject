# Daily Sync Guide

Run from inside the venv:
```bash
cd scraper_service
source .venv/bin/activate
cd src
```

---

## 1. JGirl

Scrapes new posts from all sources (upskirt, ksiroto, fc2, bathroom), uploads images to B2, downloads files via Real-Debrid.

```bash
python jgirl_backfill.py --source all --workers 2
```

- Skips posts that already exist in DB
- Use `--source bathroom` (or any single source) to sync one category only
- Keep `--workers` at 2 max — Real-Debrid enforces per-hoster daily limits
- If Real-Debrid hits fair_usage_limit (error 36) or hoster_limit_reached (error 18): stop and resume after midnight UTC
- Run `python jgirl_auth.py` first if you get 403 errors (cf_clearance expired)

---

## 2. AsianScandal

Scrapes new posts from asianscandal.net, uploads images to B2, rewrites content via Ollama.

```bash
python asianscandal_backfill.py --start-page 0 --pages 5
```

- `--start-page 0` starts from the newest posts (page 0 = most recent)
- `--pages 5` fetches 5 pages × 6 posts = ~30 posts
- Calculate start page if resuming from a known offset: `start_page = posts_already_scraped / 6`
- Add `--no-ai` to skip Ollama rewrite (faster, run rewrites later)
- Add `--with-downloads` to also download Rapidgator files inline

**Run pending downloads + rewrites after backfill:**
```bash
python asianscandal_download.py
```

---

## 3. Buzz69

Syncs new posts from buzz69.com, uploads images, AI rewrite, downloads Rapidgator files.

```bash
python backfill.py --sync --with-downloads
```

- Skips posts that already exist in DB (`source_url` deduplicated)
- Rapidgator has a daily traffic limit — if exceeded, affected posts reset to `pending` automatically. Re-run after midnight UTC.

**Retry failed downloads:**
```bash
python backfill.py --retry-failed
```

---

## Full Daily Run

```bash
python jgirl_backfill.py --source all --workers 2
python asianscandal_backfill.py --start-page 0 --pages 5
python asianscandal_download.py
python backfill.py --sync --with-downloads
```

---

## Notes

- All scripts must be run from inside `src/` with the venv activated
- Ollama must be running locally for AI rewrites: check with `curl http://localhost:11434`
- Real-Debrid and Rapidgator limits reset at **midnight UTC**
- New posts are inserted as `draft` and set to `published` only after the full pipeline succeeds (images + download complete)
