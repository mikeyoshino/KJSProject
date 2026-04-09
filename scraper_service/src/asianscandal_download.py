"""
asianscandal_download.py
------------------------
Runs BOTH pipelines concurrently in a single command:

  ┌─ Download pipeline ─────────────────────────────────────────────────────┐
  │  Picks posts where download_status='pending' + original_rapidgator_url  │
  │  → Download from Rapidgator → extract → rename → zip → upload B2        │
  │  → UPDATE our_download_link, download_status='done'                     │
  └─────────────────────────────────────────────────────────────────────────┘
  ┌─ Rewrite pipeline ──────────────────────────────────────────────────────┐
  │  Picks posts where rewritten_at IS NULL                                 │
  │  → Re-scrape → clean HTML → upload images to B2 → Ollama AI rewrite    │
  │  → UPDATE content_html, rewritten_at                                   │
  └─────────────────────────────────────────────────────────────────────────┘

Both pipelines share a ThreadPoolExecutor. The Ollama semaphore in
asianscandal_rewrite already serialises AI calls across threads.
Rapidgator rate limits are respected via RAPIDGATOR_DELAY_MS.

Usage:
    cd scraper_service/src
    python asianscandal_download.py                          # both pipelines
    python asianscandal_download.py --limit 20               # cap each pipeline at 20
    python asianscandal_download.py --no-downloads           # rewrite only
    python asianscandal_download.py --no-rewrites            # download only
    python asianscandal_download.py --no-ai                  # skip Ollama
    python asianscandal_download.py --download-workers 2 --rewrite-workers 3

Environment variables (.env):
    RAPIDGATOR_USERNAME, RAPIDGATOR_PASSWORD
    B2_APPLICATION_KEY_ID, B2_APPLICATION_KEY, B2_BUCKET_NAME, B2_SERVICE_URL
    SUPABASE_URL, SUPABASE_KEY
    MIGRATOR_TEMP_FOLDER   - default /tmp/rg-migrator
    RAPIDGATOR_DELAY_MS    - ms between API calls (default 3000)
"""

import gc
import os
import sys
import time
import shutil
import logging
import argparse
import threading
from concurrent.futures import ThreadPoolExecutor, as_completed, Future
from dataclasses import dataclass, field

sys.path.insert(0, os.path.dirname(__file__))

from dotenv import load_dotenv
load_dotenv()

from rapidgator_client import (
    RapidgatorClient,
    RapidgatorTrafficExceededException,
    is_folder_url,
    is_file_url,
)
from file_processor import (
    FileCounter,
    is_archive,
    detect_extension,
    extract_archive,
    process_extracted_files,
    process_single_file,
    create_zip,
)
from storage_b2 import upload_disk_file_to_b2
from db import (
    fetch_pending_download_posts_asianscandal,
    fetch_asianscandal_posts_not_rewritten,
    update_download_status_asianscandal,
    reset_failed_downloads_asianscandal,
    fetch_asianscandal_posts_missing_rg_urls,
    update_asianscandal_rg_urls,
)
from asianscandal_rewrite import process_post as rewrite_post, _ollama_sem
from ai_rewriter import check_ollama_available

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(threadName)s] %(levelname)s - %(message)s",
    handlers=[logging.StreamHandler()],
)

_TEMP_FOLDER = os.getenv("MIGRATOR_TEMP_FOLDER", "/tmp/rg-migrator")

# ── Thread-safe stats counter ─────────────────────────────────────────────────

@dataclass
class _Stats:
    _lock: threading.Lock = field(default_factory=threading.Lock, repr=False)
    dl_done: int = 0
    dl_failed: int = 0
    rw_done: int = 0
    rw_failed: int = 0

    def inc(self, attr: str):
        with self._lock:
            setattr(self, attr, getattr(self, attr) + 1)


# ── Download pipeline ─────────────────────────────────────────────────────────

def _expand_urls(client: RapidgatorClient, urls: list[str], post_id: str) -> list[str]:
    result = []
    for url in urls:
        if is_folder_url(url):
            logging.info(f"[dl] Expanding folder: {url}")
            try:
                result.extend(client.expand_folder_url(url))
            except Exception as e:
                logging.error(f"[dl] Folder expand failed: {e}")
        elif is_file_url(url):
            result.append(url)
        else:
            logging.debug(f"[dl] Skipping non-RG URL: {url}")
    logging.info(f"[dl] Post {post_id}: {len(result)} file URL(s)")
    return result


def _download_one_url(
    client: RapidgatorClient,
    url: str,
    post_temp: str,
    counter: FileCounter,
    text_counter: dict,
    idx: int,
    total: int,
    max_retries: int = 3,
) -> list:
    """Download + extract + rename one RG URL. Returns list of ProcessedFile."""
    delay = 5
    for attempt in range(1, max_retries + 1):
        try:
            logging.info(f"[dl] [{idx}/{total}] attempt {attempt}: {url}")
            dl_url, fname, fsize = client.get_download_link(url)
            logging.info(f"[dl] Downloading {fname} ({fsize / 1_048_576:.1f} MB)")

            archive_path = os.path.join(post_temp, fname)
            client.download_file(dl_url, archive_path)

            # Reject tiny HTML error pages returned by Rapidgator (session expired,
            # captcha wall, etc.).  Real archives are at least several KB.
            file_size = os.path.getsize(archive_path)
            if file_size < 10_240:
                with open(archive_path, "rb") as _f:
                    head = _f.read(512).lower()
                if b"<!doctype" in head or b"<html" in head or b"session" in head or b"login" in head:
                    raise RuntimeError(
                        f"Downloaded file looks like an HTML error page ({file_size} bytes) — "
                        f"Rapidgator session issue, will retry"
                    )

            # Rapidgator appends .html to filenames (e.g. "Myka.rar.html").
            # Strip it so the real extension is visible for archive detection.
            stem, ext = os.path.splitext(archive_path)
            if ext.lower() == ".html" and os.path.splitext(stem)[1]:
                os.rename(archive_path, stem)
                archive_path = stem
                logging.info(f"[dl] Stripped .html suffix → {os.path.basename(archive_path)}")

            # Fix missing extension via magic bytes
            if not os.path.splitext(archive_path)[1]:
                detected = detect_extension(archive_path)
                if detected:
                    new_path = archive_path + detected
                    os.rename(archive_path, new_path)
                    archive_path = new_path

            if is_archive(archive_path):
                extract_dir = extract_archive(archive_path, post_temp)
                files = process_extracted_files(extract_dir, counter, text_counter)
                try:
                    os.remove(archive_path)
                except OSError:
                    pass
            else:
                files = [process_single_file(archive_path, post_temp, counter)]

            return files

        except RapidgatorTrafficExceededException:
            raise
        except Exception as e:
            logging.warning(f"[dl] [{idx}/{total}] attempt {attempt} failed: {e}")
            if attempt == max_retries:
                logging.error(f"[dl] [{idx}/{total}] giving up: {url}")
                return []
            time.sleep(delay * attempt)

    return []


def download_post(
    client: RapidgatorClient,
    post: dict,
    stop_event: threading.Event,
) -> str:
    """
    Full download pipeline for one post.
    Returns 'done' | 'failed' | 'traffic_exceeded' | 'stopped'.
    """
    post_id  = post["id"]
    rg_urls  = post.get("original_rapidgator_url") or []
    post_temp = os.path.join(_TEMP_FOLDER, post_id)

    os.makedirs(post_temp, exist_ok=True)

    try:
        update_download_status_asianscandal(post_id, "processing")

        file_urls = _expand_urls(client, rg_urls, post_id)
        if not file_urls:
            logging.warning(f"[dl] Post {post_id}: no RG URLs — marking failed")
            update_download_status_asianscandal(post_id, "failed")
            return "failed"

        counter = FileCounter()
        text_counter: dict = {}   # shared across all archives so scandal69.txt names are unique
        all_files = []

        for i, url in enumerate(file_urls, 1):
            if stop_event.is_set():
                update_download_status_asianscandal(post_id, "pending")
                return "stopped"
            files = _download_one_url(client, url, post_temp, counter, text_counter, i, len(file_urls))
            all_files.extend(files)
            # Free per-URL references immediately
            del files

        if not all_files:
            logging.warning(f"[dl] Post {post_id}: no files collected — marking failed")
            update_download_status_asianscandal(post_id, "failed")
            return "failed"

        # Create ZIP, upload, clean up processed file list
        zip_name = f"{post_id}.zip"
        zip_path = os.path.join(post_temp, zip_name)
        create_zip(all_files, zip_path)
        del all_files

        b2_key = f"posts/{post_id}/{zip_name}"
        result = upload_disk_file_to_b2(zip_path, b2_key)
        # zip_path will be deleted with the whole temp folder below

        if not result:
            logging.error(f"[dl] Post {post_id}: B2 upload failed — marking failed")
            update_download_status_asianscandal(post_id, "failed")
            return "failed"

        update_download_status_asianscandal(post_id, "done", our_download_link=[b2_key])
        logging.info(f"[dl] Post {post_id}: done → {b2_key}")
        return "done"

    except RapidgatorTrafficExceededException:
        logging.warning(f"[dl] Post {post_id}: traffic exceeded — resetting to pending")
        stop_event.set()
        update_download_status_asianscandal(post_id, "pending")
        return "traffic_exceeded"

    except Exception as e:
        logging.error(f"[dl] Post {post_id}: error — {e}")
        try:
            update_download_status_asianscandal(post_id, "failed")
        except Exception:
            pass
        return "failed"

    finally:
        # Always free disk space, even on exception
        shutil.rmtree(post_temp, ignore_errors=True)
        gc.collect()


# ── Rewrite pipeline wrapper ──────────────────────────────────────────────────

def _rewrite_post_safe(post: dict, idx: int, use_ai: bool) -> bool:
    """Thin wrapper around asianscandal_rewrite.process_post with gc."""
    try:
        ok = rewrite_post(post, idx=idx, dry_run=False, use_ai=use_ai)
        return ok
    except Exception as e:
        logging.error(f"[rw] Post {post.get('id')}: unexpected error — {e}")
        return False
    finally:
        gc.collect()


# ── Fix posts with missing RG URLs (plain-text links) ────────────────────────

def fix_missing_rg_urls(limit: int | None = None):
    """
    Re-scrape posts where original_rapidgator_url is empty.
    The original scraper only looked at <a href> tags; some posts have plain-text URLs.
    """
    from asianscandal_scraper import fetch_html, parse_post_page

    logging.info("Scanning for posts with missing Rapidgator URLs...")
    offset = 0
    batch = 50
    total_fixed = 0

    while True:
        posts = fetch_asianscandal_posts_missing_rg_urls(limit=batch, offset=offset)
        if not posts:
            break
        if limit and total_fixed >= limit:
            break

        for post in posts:
            if limit and total_fixed >= limit:
                break
            post_id    = post["id"]
            source_url = post["source_url"]

            html = fetch_html(source_url)
            if not html:
                logging.warning(f"  Could not fetch {source_url}")
                continue

            data = parse_post_page(source_url, html)
            del html
            rg_urls = data.get("original_rapidgator_url") or []

            if rg_urls:
                update_asianscandal_rg_urls(post_id, rg_urls)
                logging.info(f"  Fixed {post_id}: {len(rg_urls)} RG URL(s) found")
                total_fixed += 1
            else:
                logging.info(f"  No RG URLs found for {source_url} — skipping")

        offset += batch

    logging.info(f"Fixed {total_fixed} post(s) with missing RG URLs.")


# ── Batch iterators ───────────────────────────────────────────────────────────

def _iter_download_posts(limit: int | None, batch: int = 20):
    """Yield individual pending-download post dicts, batch by batch."""
    offset = 0
    emitted = 0
    while True:
        rows = fetch_pending_download_posts_asianscandal(limit=batch, offset=offset)
        if not rows:
            break
        for row in rows:
            if limit and emitted >= limit:
                return
            yield row
            emitted += 1
        if len(rows) < batch:
            break
        offset += batch


def _iter_rewrite_posts(limit: int | None, batch: int = 20):
    """Yield individual not-rewritten post dicts, batch by batch."""
    offset = 0
    emitted = 0
    while True:
        rows = fetch_asianscandal_posts_not_rewritten(limit=batch, offset=offset)
        if not rows:
            break
        for row in rows:
            if limit and emitted >= limit:
                return
            yield row
            emitted += 1
        if len(rows) < batch:
            break
        offset += batch


# ── Main parallel runner ──────────────────────────────────────────────────────

def run(
    limit: int | None = None,
    download_workers: int = 1,
    rewrite_workers: int = 4,
    use_ai: bool = True,
    no_downloads: bool = False,
    no_rewrites: bool = False,
    fix_missing_rg: bool = False,
):
    """
    Run download and rewrite pipelines concurrently.

    download_workers: keep at 1–2 (Rapidgator rate-limits aggressively)
    rewrite_workers:  4 is a good default; Ollama semaphore handles serialisation
    """
    logging.info("=" * 60)
    logging.info("AsianScandal Parallel Pipeline")
    logging.info(f"  Download workers : {download_workers if not no_downloads else 'DISABLED'}")
    logging.info(f"  Rewrite workers  : {rewrite_workers if not no_rewrites else 'DISABLED'}")
    logging.info(f"  AI rewrite       : {'yes' if use_ai else 'no'}")
    logging.info(f"  Post limit       : {limit or 'unlimited'} per pipeline")
    logging.info("=" * 60)

    if use_ai and not no_rewrites:
        if not check_ollama_available():
            logging.warning("Ollama not reachable — proceeding with clean-only (no AI).")
            use_ai = False

    os.makedirs(_TEMP_FOLDER, exist_ok=True)

    # Re-scrape posts whose RG URLs were missed (plain-text links)
    if fix_missing_rg:
        fix_missing_rg_urls(limit=limit)

    # Reset previously failed downloads so they are retried this run
    # (only resets posts that failed due to auth/credential issues, not 404s)
    if not no_downloads and fix_missing_rg is False:
        reset_count = reset_failed_downloads_asianscandal()
        if reset_count:
            logging.info(f"Reset {reset_count} failed post(s) back to pending for retry.")

    stats = _Stats()
    stop_event = threading.Event()  # set on RG traffic exceeded or KeyboardInterrupt
    client = RapidgatorClient() if not no_downloads else None

    total_workers = (0 if no_downloads else download_workers) + \
                    (0 if no_rewrites  else rewrite_workers)

    if total_workers == 0:
        logging.warning("Both pipelines disabled — nothing to do.")
        return

    future_to_kind: dict[Future, str] = {}

    try:
        with ThreadPoolExecutor(
            max_workers=total_workers,
            thread_name_prefix="pipeline",
        ) as pool:

            # ── Submit download tasks ──────────────────────────────────────
            if not no_downloads:
                dl_sem = threading.Semaphore(download_workers)

                def _submit_download(post):
                    dl_sem.acquire()
                    fut = pool.submit(download_post, client, post, stop_event)
                    future_to_kind[fut] = "dl"

                    def _release(_):
                        dl_sem.release()

                    fut.add_done_callback(_release)
                    return fut

                for post in _iter_download_posts(limit):
                    if stop_event.is_set():
                        break
                    _submit_download(post)

            # ── Submit rewrite tasks ───────────────────────────────────────
            if not no_rewrites:
                rw_sem = threading.Semaphore(rewrite_workers)
                rw_idx = 0

                def _submit_rewrite(post, idx):
                    rw_sem.acquire()
                    fut = pool.submit(_rewrite_post_safe, post, idx, use_ai)
                    future_to_kind[fut] = "rw"

                    def _release(_):
                        rw_sem.release()

                    fut.add_done_callback(_release)
                    return fut

                for post in _iter_rewrite_posts(limit):
                    rw_idx += 1
                    _submit_rewrite(post, rw_idx)

            # ── Collect results ────────────────────────────────────────────
            for fut in as_completed(future_to_kind):
                kind = future_to_kind.pop(fut, "?")
                try:
                    result = fut.result()
                    if kind == "dl":
                        if result == "done":
                            stats.inc("dl_done")
                        elif result == "traffic_exceeded":
                            logging.warning("[dl] Traffic exceeded — download pipeline stopped.")
                            stop_event.set()
                        else:
                            stats.inc("dl_failed")
                    else:  # rw
                        if result:
                            stats.inc("rw_done")
                        else:
                            stats.inc("rw_failed")
                except Exception as e:
                    logging.error(f"Future error ({kind}): {e}")
                    if kind == "dl":
                        stats.inc("dl_failed")
                    else:
                        stats.inc("rw_failed")

    except KeyboardInterrupt:
        logging.info("Interrupted — stopping pipelines gracefully...")
        stop_event.set()

    finally:
        logging.info("=" * 60)
        logging.info("Pipeline complete.")
        if not no_downloads:
            logging.info(f"  Downloads : done={stats.dl_done}  failed={stats.dl_failed}")
        if not no_rewrites:
            logging.info(f"  Rewrites  : done={stats.rw_done}  failed={stats.rw_failed}")
        logging.info("=" * 60)

        if stop_event.is_set() and not no_downloads:
            logging.warning("Rapidgator traffic limit hit. Re-run after midnight UTC to continue.")

        gc.collect()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(
        description="Run AsianScandal download + rewrite pipelines in parallel"
    )
    parser.add_argument("--limit",            type=int, default=None, help="Max posts per pipeline")
    parser.add_argument("--download-workers", type=int, default=1,    help="RG download workers (default 1)")
    parser.add_argument("--rewrite-workers",  type=int, default=4,    help="Rewrite workers (default 4)")
    parser.add_argument("--no-ai",            action="store_true",    help="Skip Ollama AI rewrite")
    parser.add_argument("--no-downloads",     action="store_true",    help="Rewrite pipeline only")
    parser.add_argument("--no-rewrites",      action="store_true",    help="Download pipeline only")
    parser.add_argument("--fix-missing-rg",   action="store_true",    help="Re-scrape posts with empty original_rapidgator_url to find plain-text links")
    args = parser.parse_args()

    run(
        limit=args.limit,
        download_workers=args.download_workers,
        rewrite_workers=args.rewrite_workers,
        use_ai=not args.no_ai,
        no_downloads=args.no_downloads,
        no_rewrites=args.no_rewrites,
        fix_missing_rg=args.fix_missing_rg,
    )
