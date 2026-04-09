"""
backfill.py
-----------
All-in-one pipeline for buzz69.com posts.

Modes (can be combined):
  --sync     Scrape new posts from buzz69.com and insert them
  --migrate  Move Supabase images → B2 + AI rewrite + download RG files to B2

Pipeline per new post (--sync):
  1. Scrape post page
  2. clean_html (remove RG promo blocks, rebrand)
  3. upload content images → B2 at posts/{id}/images/N.ext
  4. Upload thumbnail → B2 at posts/{id}/thumbnail.ext
  5. AI rewrite via Ollama (optional)
  6. Upsert to Supabase posts table
  7. Download RG files → B2 (optional, --with-downloads)

Pipeline per existing post (--migrate-images):
  1. Fetch posts with supabase.co URLs in content_html / thumbnail_url
  2. Re-upload images to B2 (download_image handles any public URL)
  3. AI rewrite if use_ai
  4. update_post_content()

Pipeline per existing post (--migrate-downloads):
  1. Reset done+empty-link posts to pending
  2. For posts with empty original_rapidgator_url: re-scrape to extract RG links
  3. Download RG files → B2

Usage:
    cd scraper_service/src
    python backfill.py --sync
    python backfill.py --sync --pages 3 --with-downloads
    python backfill.py --migrate
    python backfill.py --migrate --limit 10
    python backfill.py --migrate --no-ai
    python backfill.py --sync --migrate
"""

import gc
import logging
import os
import re
import shutil
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime, timezone

sys.path.insert(0, os.path.dirname(__file__))

from dotenv import load_dotenv
load_dotenv()

from asianscandal_rewrite import clean_html, upload_images_to_b2, _detect_image_ext, _ollama_sem
from ai_rewriter import rewrite_content, check_ollama_available
from storage_b2 import upload_image_to_b2, upload_disk_file_to_b2
from storage import download_image
from scraper import scrape_home_page, fetch_html, parse_post_page
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
from db import (
    upsert_post,
    check_post_complete,
    fetch_posts_for_image_migration,
    fetch_posts_needing_rewrite,
    fetch_posts_pending_download,
    reset_failed_downloads,
    reset_empty_download_links_posts,
    update_post_content,
    update_post_download_status,
    update_post_rg_urls,
)

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(threadName)s] %(levelname)s - %(message)s",
    handlers=[logging.StreamHandler()],
)

_TEMP_FOLDER = os.getenv("MIGRATOR_TEMP_FOLDER", "/tmp/rg-migrator-buzz69")

# ── Thumbnail upload ──────────────────────────────────────────────────────────

def _upload_thumbnail(url: str, post_id: str) -> str | None:
    """Download thumbnail and upload to B2 at posts/{post_id}/thumbnail.{ext}.
    Returns the relative B2 key, or the original URL on failure, or None if no URL."""
    if not url:
        return None

    # Try the URL as-is, then fall back to base URL (no query params) if it fails
    candidates = [url]
    base = url.split('?')[0]
    if base != url:
        candidates.append(base)

    content = None
    for candidate in candidates:
        content = download_image(candidate)
        if content:
            break
        logging.warning(f"  Could not download thumbnail: {candidate}")

    if not content:
        return url

    ext = _detect_image_ext(content)
    b2_key = f"posts/{post_id}/thumbnail.{ext}"
    result = upload_image_to_b2(content, b2_key)
    del content
    return result if result else url


# ── RG URL extraction from HTML ───────────────────────────────────────────────

_RG_URL_RE = re.compile(
    r"https?://(?:rapidgator\.net|rg\.to)/(?:file|folder)/\S+",
    re.IGNORECASE,
)
_RG_SKIP_RE = re.compile(r"/(?:ref|article/premium)/", re.IGNORECASE)


def _extract_rg_urls_from_html(html: str) -> list[str]:
    """
    Extract all Rapidgator URLs from HTML including:
      - <a href> tags
      - .rg-box divs
      - plain-text URLs in any text node
    Excludes /ref/ and /article/premium/ links.
    """
    from bs4 import BeautifulSoup
    soup = BeautifulSoup(html, "html.parser")

    urls = set()

    # <a href> links
    for a in soup.find_all("a", href=True):
        href = a["href"]
        if re.search(r"rapidgator\.net|rg\.to", href, re.IGNORECASE):
            if not _RG_SKIP_RE.search(href):
                urls.add(href.strip())

    # Full-text regex scan (catches plain-text URLs too)
    for match in _RG_URL_RE.finditer(soup.get_text(" ")):
        url = match.group(0).rstrip(".,)")
        if not _RG_SKIP_RE.search(url):
            urls.add(url)

    soup.decompose()
    return list(urls)


# ── Download RG files to B2 ───────────────────────────────────────────────────

def _expand_rg_urls(client: RapidgatorClient, urls: list[str], post_id: str) -> list[str]:
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


def _is_non_first_rar_volume(path: str) -> bool:
    """
    Returns True for non-first volumes of a multi-part RAR:
    - New format: file.part2.rar, file.part3.rar, …
    - Old format: file.r00, file.r01, …
    These must be skipped during extraction — rarfile reads them automatically
    when opening the first volume (part1.rar / .rar).
    """
    name = os.path.basename(path).lower()
    m = re.search(r"\.part(\d+)\.rar$", name)
    if m and int(m.group(1)) > 1:
        return True
    if re.search(r"\.r\d+$", name):
        return True
    return False


def _download_one_rg_url(
    client: RapidgatorClient,
    url: str,
    post_temp: str,
    idx: int,
    total: int,
    max_retries: int = 3,
) -> str | None:
    """
    Download one RG URL to disk. Returns local file path, or None on failure.
    Extraction is NOT done here — caller handles it after all downloads complete.
    """
    delay = 5
    for attempt in range(1, max_retries + 1):
        try:
            logging.info(f"[dl] [{idx}/{total}] attempt {attempt}: {url}")
            dl_url, fname, fsize = client.get_download_link(url)
            logging.info(f"[dl] Downloading {fname} ({fsize / 1_048_576:.1f} MB)")

            archive_path = os.path.join(post_temp, fname)
            client.download_file(dl_url, archive_path)

            # Reject tiny HTML error pages (session expired, captcha, etc.)
            file_size = os.path.getsize(archive_path)
            if file_size < 10_240:
                with open(archive_path, "rb") as _f:
                    head = _f.read(512).lower()
                if b"<!doctype" in head or b"<html" in head or b"session" in head or b"login" in head:
                    raise RuntimeError(
                        f"Downloaded file looks like an HTML error page ({file_size} bytes) — "
                        f"Rapidgator session issue, will retry"
                    )

            # Strip .html suffix that Rapidgator appends (e.g. "file.rar.html")
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

            return archive_path

        except RapidgatorTrafficExceededException:
            raise
        except Exception as e:
            logging.warning(f"[dl] [{idx}/{total}] attempt {attempt} failed: {e}")
            if attempt == max_retries:
                logging.error(f"[dl] [{idx}/{total}] giving up: {url}")
                return None
            time.sleep(delay * attempt)

    return None


def _download_rg_files(
    client: RapidgatorClient,
    post_id: str,
    rg_urls: list[str],
    stop_event: threading.Event,
) -> str:
    """
    Full download pipeline for one post (posts table).
    Returns 'done' | 'failed' | 'traffic_exceeded' | 'stopped'.

    Two-phase approach:
    Phase 1 — download all files (with retry on network errors only)
    Phase 2 — extract: skip non-first RAR volumes; rarfile spans them automatically
    """
    post_temp = os.path.join(_TEMP_FOLDER, post_id)
    os.makedirs(post_temp, exist_ok=True)

    try:
        update_post_download_status(post_id, "processing")

        file_urls = _expand_rg_urls(client, rg_urls, post_id)
        if not file_urls:
            logging.warning(f"[dl] Post {post_id}: no RG file URLs — marking failed")
            update_post_download_status(post_id, "failed")
            return "failed"

        # ── Phase 1: download all files ───────────────────────────────────────
        downloaded_paths = []
        for i, url in enumerate(file_urls, 1):
            if stop_event.is_set():
                update_post_download_status(post_id, "pending")
                return "stopped"
            path = _download_one_rg_url(client, url, post_temp, i, len(file_urls))
            if path:
                downloaded_paths.append(path)

        # ── Phase 2: extract (skip non-first RAR volumes) ─────────────────────
        counter = FileCounter()
        text_counter: dict = {}
        all_files = []

        for path in sorted(downloaded_paths):
            if _is_non_first_rar_volume(path):
                logging.info(f"[dl] Skipping non-first RAR volume (read via part1): {os.path.basename(path)}")
                continue

            if is_archive(path):
                try:
                    extract_dir = extract_archive(path, post_temp)
                    files = process_extracted_files(extract_dir, counter, text_counter)
                    all_files.extend(files)
                    if not files:
                        logging.warning(f"[dl] Archive yielded 0 files: {os.path.basename(path)}")
                except Exception as e:
                    logging.error(f"[dl] Extraction failed (non-retryable): {os.path.basename(path)}: {e}")
                try:
                    os.remove(path)
                except OSError:
                    pass
            else:
                all_files.append(process_single_file(path, post_temp, counter))

        del downloaded_paths

        if not all_files:
            logging.warning(f"[dl] Post {post_id}: no files collected — marking failed")
            update_post_download_status(post_id, "failed")
            return "failed"

        zip_name = f"{post_id}.zip"
        zip_path = os.path.join(post_temp, zip_name)
        create_zip(all_files, zip_path)
        del all_files

        b2_key = f"posts/{post_id}/{zip_name}"
        result = upload_disk_file_to_b2(zip_path, b2_key)

        if not result:
            logging.error(f"[dl] Post {post_id}: B2 upload failed — marking failed")
            update_post_download_status(post_id, "failed")
            return "failed"

        update_post_download_status(post_id, "done", our_download_link=[b2_key])
        logging.info(f"[dl] Post {post_id}: done → {b2_key}")
        return "done"

    except RapidgatorTrafficExceededException:
        logging.warning(f"[dl] Post {post_id}: traffic exceeded — resetting to pending")
        stop_event.set()
        update_post_download_status(post_id, "pending")
        return "traffic_exceeded"

    except Exception as e:
        logging.error(f"[dl] Post {post_id}: error — {e}")
        try:
            update_post_download_status(post_id, "failed")
        except Exception:
            pass
        return "failed"

    finally:
        shutil.rmtree(post_temp, ignore_errors=True)
        gc.collect()


# ── New post sync pipeline ────────────────────────────────────────────────────

def process_new_post(
    post_url: str,
    thumb_url: str,
    use_ai: bool = True,
    rg_client: RapidgatorClient | None = None,
    stop_event: threading.Event | None = None,
) -> str:
    """
    Full pipeline for a single new post scraped from buzz69.com.
    Returns 'inserted' | 'skipped' | 'failed'.
    Thread-safe.
    """
    try:
        if check_post_complete(post_url):
            logging.info(f"  SKIP (exists): {post_url}")
            return "skipped"

        html = fetch_html(post_url)
        if not html:
            logging.warning(f"  FAIL (could not fetch): {post_url}")
            return "failed"

        data = parse_post_page(post_url, html, fallback_thumb=thumb_url)
        del html
        logging.info(f"  Scraped: {post_url} — {len(data['content_html'])} chars")

        content_html = clean_html(data["content_html"])
        if not content_html.strip():
            logging.warning(f"  FAIL (empty after clean): {post_url}")
            return "failed"

        # Insert placeholder to get DB-assigned UUID
        initial = {
            "source_url": data["source_url"],
            "title": data["title"],
            "thumbnail_url": None,
            "content_html": "",
            "categories": data["categories"],
            "original_rapidgator_url": data["original_rapidgator_url"],
            "our_download_link": [],
            "download_status": "pending" if data["original_rapidgator_url"] else "done",
            "created_at": datetime.now(timezone.utc).isoformat(),
        }
        row = upsert_post(initial)
        if not row:
            logging.warning(f"  FAIL (DB upsert failed): {post_url}")
            return "failed"

        post_id = row.get("id")
        if not post_id:
            logging.warning(f"  FAIL (no post_id from upsert): {post_url}")
            return "failed"

        # Upload content images to B2
        logging.info(f"  Uploading content images to B2 (post {post_id})...")
        content_html = upload_images_to_b2(content_html, post_id)

        # Upload thumbnail to B2
        mirrored_thumb = _upload_thumbnail(data["thumbnail_url"], post_id)

        # AI rewrite
        rewritten_at = None
        if use_ai:
            logging.info(f"  Waiting for Ollama slot...")
            with _ollama_sem:
                logging.info(f"  Rewriting via Ollama...")
                content_html = rewrite_content(data["title"], content_html)
            rewritten_at = datetime.now(timezone.utc).isoformat()

        update_post_content(
            post_id,
            content_html,
            thumbnail_url=mirrored_thumb or data["thumbnail_url"],
            rewritten_at=rewritten_at,
        )
        del content_html

        logging.info(f"  SUCCESS: {data['title']}")

        # Download RG files
        rg_urls = data.get("original_rapidgator_url") or []
        if rg_client and rg_urls and not (stop_event and stop_event.is_set()):
            logging.info(f"  Downloading {len(rg_urls)} RG file(s)...")
            stop_ev = stop_event or threading.Event()
            outcome = _download_rg_files(rg_client, post_id, rg_urls, stop_ev)
            if outcome == "traffic_exceeded":
                logging.warning("  RG traffic exceeded — stopping downloads for this run")
                if stop_event:
                    stop_event.set()
            else:
                logging.info(f"  Download outcome: {outcome}")

        return "inserted"

    except Exception as e:
        logging.error(f"  UNEXPECTED ERROR for {post_url}: {e}")
        return "failed"
    finally:
        gc.collect()


def sync_new_posts(
    pages: int = 1,
    start_page: int = 0,
    use_ai: bool = True,
    with_downloads: bool = False,
    workers: int = 4,
    limit: int | None = None,
):
    """Scrape new posts from buzz69.com and insert them."""
    logging.info("=" * 60)
    logging.info(f"Buzz69 Sync: pages {start_page+1}–{start_page+pages}, workers={workers}")
    logging.info("=" * 60)

    if use_ai and not check_ollama_available():
        logging.warning("Ollama not reachable — proceeding without AI rewrite.")
        use_ai = False

    rg_client = None
    stop_event = None
    if with_downloads:
        rg_client = RapidgatorClient()
        stop_event = threading.Event()

    all_posts: list[tuple[str, str]] = []
    for p in range(start_page + 1, start_page + pages + 1):
        posts = scrape_home_page(page_num=p)
        all_posts.extend(posts)
        if limit and len(all_posts) >= limit:
            all_posts = all_posts[:limit]
            break

    if not all_posts:
        logging.warning("No posts found.")
        return

    logging.info(f"Processing {len(all_posts)} post(s)...")
    inserted = skipped = failed = 0

    with ThreadPoolExecutor(max_workers=workers) as pool:
        futures = {
            pool.submit(process_new_post, url, thumb, use_ai, rg_client, stop_event): url
            for url, thumb in all_posts
        }
        for fut in as_completed(futures):
            try:
                outcome = fut.result()
                if outcome == "inserted":
                    inserted += 1
                elif outcome == "skipped":
                    skipped += 1
                else:
                    failed += 1
            except Exception as e:
                logging.error(f"Future error: {e}")
                failed += 1

    if rg_client:
        rg_client.close()

    logging.info("=" * 60)
    logging.info(f"Sync done. Inserted={inserted}  Skipped={skipped}  Failed={failed}")
    logging.info("=" * 60)


# ── Image migration pipeline ──────────────────────────────────────────────────

def _migrate_single_post_images(post: dict, use_ai: bool) -> bool:
    """Migrate images for one existing post from Supabase storage to B2."""
    post_id    = post["id"]
    source_url = post.get("source_url", "")
    title      = post.get("title", "")
    content_html = post.get("content_html", "") or ""
    thumb_url  = post.get("thumbnail_url", "") or ""

    logging.info(f"  Migrating images: {source_url or post_id}")

    # Migrate content images
    if "supabase.co" in content_html:
        content_html = upload_images_to_b2(content_html, post_id)

    # Migrate thumbnail
    new_thumb = thumb_url
    if thumb_url and "supabase.co" in thumb_url:
        new_thumb = _upload_thumbnail(thumb_url, post_id)

    # AI rewrite
    rewritten_at = None
    if use_ai and content_html.strip():
        logging.info(f"  Waiting for Ollama slot...")
        with _ollama_sem:
            logging.info(f"  Rewriting via Ollama...")
            content_html = rewrite_content(title, content_html)
        rewritten_at = datetime.now(timezone.utc).isoformat()

    ok = update_post_content(
        post_id,
        content_html,
        thumbnail_url=new_thumb if new_thumb != thumb_url else None,
        rewritten_at=rewritten_at,
    )

    del content_html
    gc.collect()
    return ok


def migrate_images(
    limit: int | None = None,
    use_ai: bool = True,
    workers: int = 4,
):
    """Migrate existing posts' images from Supabase storage to B2."""
    logging.info("=" * 60)
    logging.info(f"Image migration: Supabase → B2  workers={workers}")
    logging.info("=" * 60)

    if use_ai and not check_ollama_available():
        logging.warning("Ollama not reachable — proceeding without AI rewrite.")
        use_ai = False

    batch = 50
    offset = 0
    total_ok = total_fail = total_done = 0

    while True:
        posts = fetch_posts_for_image_migration(limit=batch, offset=offset)
        if not posts:
            break

        if limit:
            remaining = limit - total_done
            posts = posts[:remaining]

        with ThreadPoolExecutor(max_workers=workers) as pool:
            futures = {
                pool.submit(_migrate_single_post_images, post, use_ai): post
                for post in posts
            }
            for fut in as_completed(futures):
                try:
                    ok = fut.result()
                    if ok:
                        total_ok += 1
                    else:
                        total_fail += 1
                except Exception as e:
                    logging.error(f"  Migration error: {e}")
                    total_fail += 1

        total_done += len(posts)
        if limit and total_done >= limit:
            break
        if len(posts) < batch:
            break
        offset += batch

    logging.info("=" * 60)
    logging.info(f"Image migration done. OK={total_ok}  Failed={total_fail}")
    logging.info("=" * 60)


# ── Download migration pipeline ───────────────────────────────────────────────

def migrate_downloads(
    rg_client: RapidgatorClient,
    stop_event: threading.Event,
    limit: int | None = None,
):
    """Download Rapidgator files to B2 for pending posts."""
    logging.info("=" * 60)
    logging.info("Download migration: Rapidgator → B2")
    logging.info("=" * 60)

    os.makedirs(_TEMP_FOLDER, exist_ok=True)

    # Fix posts that are marked done but have empty our_download_link
    reset_count = reset_empty_download_links_posts()
    if reset_count:
        logging.info(f"Reset {reset_count} done+empty post(s) back to pending.")

    batch = 20
    offset = 0
    total_done = total_fail = total_processed = 0

    while True:
        if stop_event.is_set():
            break

        posts = fetch_posts_pending_download(limit=batch, offset=offset)
        if not posts:
            break

        if limit:
            remaining = limit - total_processed
            posts = posts[:remaining]

        for post in posts:
            if stop_event.is_set():
                break

            post_id = post["id"]
            rg_urls = post.get("original_rapidgator_url") or []

            # If RG URLs missing, re-scrape source page to find them
            if not rg_urls:
                source_url = post.get("source_url", "")
                logging.info(f"  Re-scraping for RG URLs: {source_url}")
                html = fetch_html(source_url)
                if html:
                    # Extract from raw HTML (before clean)
                    rg_urls = _extract_rg_urls_from_html(html)
                    del html
                    if rg_urls:
                        update_post_rg_urls(post_id, rg_urls)
                        logging.info(f"  Found {len(rg_urls)} RG URL(s) for {post_id}")
                    else:
                        logging.warning(f"  No RG URLs found for {source_url} — skipping")
                        continue

            outcome = _download_rg_files(rg_client, post_id, rg_urls, stop_event)
            if outcome == "done":
                total_done += 1
            elif outcome == "traffic_exceeded":
                logging.warning("  RG traffic exceeded — stopping download migration.")
                break
            else:
                total_fail += 1

        total_processed += len(posts)
        if limit and total_processed >= limit:
            break
        if len(posts) < batch:
            break
        offset += batch

    logging.info("=" * 60)
    logging.info(f"Download migration done. Done={total_done}  Failed={total_fail}")
    logging.info("=" * 60)


# ── Rewrite-only pipeline ─────────────────────────────────────────────────────

def rewrite_existing_posts(
    source_name: str = "Buzz69",
    limit: int | None = None,
    workers: int = 4,
):
    """Re-run AI rewrite on posts that still contain old brand names."""
    logging.info("=" * 60)
    logging.info(f"AI rewrite pass: source={source_name}  workers={workers}")
    logging.info("=" * 60)

    if not check_ollama_available():
        logging.error("Ollama not reachable — cannot rewrite. Aborting.")
        return

    batch = 50
    offset = 0
    total_ok = total_fail = total_done = 0

    while True:
        posts = fetch_posts_needing_rewrite(source_name=source_name, limit=batch, offset=offset)
        if not posts:
            break

        if limit:
            remaining = limit - total_done
            posts = posts[:remaining]

        def _rewrite_one(post: dict) -> bool:
            post_id = post["id"]
            title   = post.get("title", "")
            html    = post.get("content_html", "") or ""
            logging.info(f"  Rewriting: {post.get('source_url', post_id)}")
            with _ollama_sem:
                rewritten = rewrite_content(title, html)
            return update_post_content(post_id, rewritten)

        with ThreadPoolExecutor(max_workers=workers) as pool:
            futures = {pool.submit(_rewrite_one, p): p for p in posts}
            for fut in as_completed(futures):
                try:
                    ok = fut.result()
                    if ok:
                        total_ok += 1
                    else:
                        total_fail += 1
                except Exception as e:
                    logging.error(f"  Rewrite error: {e}")
                    total_fail += 1

        total_done += len(posts)
        if limit and total_done >= limit:
            break
        if len(posts) < batch:
            break
        offset += batch

    logging.info("=" * 60)
    logging.info(f"Rewrite done. OK={total_ok}  Failed={total_fail}")
    logging.info("=" * 60)


# ── Entry point ───────────────────────────────────────────────────────────────

if __name__ == "__main__":
    import argparse

    parser = argparse.ArgumentParser(description="Buzz69 backfill / sync / migration tool")
    parser.add_argument("--sync",           action="store_true", help="Scrape new posts from buzz69.com")
    parser.add_argument("--migrate",        action="store_true", help="Migrate existing posts: Supabase images → B2 + AI rewrite + RG downloads")
    parser.add_argument("--rewrite",        action="store_true", help="Re-run AI rewrite on posts still containing old brand names")
    parser.add_argument("--retry-failed",   action="store_true", help="Reset failed downloads back to pending then re-download")
    parser.add_argument("--source",         type=str, default="Buzz69", help="Source name for --rewrite (default: Buzz69)")
    parser.add_argument("--pages",          type=int, default=1,    help="Pages to scrape for --sync (default 1)")
    parser.add_argument("--start-page",     type=int, default=0,    help="Start page index for --sync (default 0)")
    parser.add_argument("--no-ai",          action="store_true",    help="Skip Ollama AI rewrite")
    parser.add_argument("--with-downloads", action="store_true",    help="Also download RG files when syncing new posts")
    parser.add_argument("--limit",          type=int, default=None, help="Max posts to process per mode")
    parser.add_argument("--workers",        type=int, default=4,    help="Parallel workers (default 4)")
    args = parser.parse_args()

    if not any([args.sync, args.migrate, args.rewrite, args.retry_failed]):
        parser.print_help()
        raise SystemExit(1)

    use_ai = not args.no_ai

    if args.retry_failed:
        count = reset_failed_downloads(source_name=args.source)
        logging.info(f"Reset {count} failed post(s) to pending.")
        client = RapidgatorClient()
        stop_ev = threading.Event()
        try:
            migrate_downloads(client, stop_ev, limit=args.limit)
        finally:
            client.close()

    if args.rewrite:
        rewrite_existing_posts(source_name=args.source, limit=args.limit, workers=args.workers)

    if args.migrate:
        migrate_images(limit=args.limit, use_ai=use_ai, workers=args.workers)
        client = RapidgatorClient()
        stop_ev = threading.Event()
        try:
            migrate_downloads(client, stop_ev, limit=args.limit)
        finally:
            client.close()

    if args.sync:
        sync_new_posts(
            pages=args.pages,
            start_page=args.start_page,
            use_ai=use_ai,
            with_downloads=args.with_downloads,
            workers=args.workers,
            limit=args.limit,
        )
