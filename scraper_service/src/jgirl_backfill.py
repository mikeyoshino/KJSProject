"""
jgirl_backfill.py
-----------------
Scrapes jgirl.co and downloads files via Real-Debrid → B2, all in one pass.

Pipeline per post:
  1. Scrape post page (title, tags, preview images)
  2. Upload thumbnail + preview images to B2
  3. Unrestrict download link via Real-Debrid
  4. Stream download directly into B2 (no temp file, no full-file buffer)
  5. Save everything to jgirl_posts in Supabase

On any failure after DB insert: delete entire JGirls/{postId}/ from B2, mark 'failed'.

Usage:
  python jgirl_backfill.py                                  # all categories
  python jgirl_backfill.py --source upskirt --limit 10
  python jgirl_backfill.py --mode backfill --spread-days 365
  python jgirl_backfill.py --source fc2 --limit 3 --dry-run
  python jgirl_backfill.py --no-images                      # skip all B2 uploads
  python jgirl_backfill.py --no-download                    # skip Real-Debrid download
"""

import sys
import os
import time
import random
import logging
import argparse
import mimetypes
import socket
import threading
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime, timedelta, timezone

sys.path.insert(0, os.path.dirname(__file__))

import requests
from dotenv import load_dotenv

# ── Force IPv4 for all outbound connections ───────────────────────────────────
# Real-Debrid blocks datacenter IPv6 (error_code 22). Patching getaddrinfo to
# return only AF_INET results forces requests/boto3 to connect over IPv4.
_orig_getaddrinfo = socket.getaddrinfo
def _ipv4_only_getaddrinfo(host, port, family=0, type=0, proto=0, flags=0):
    return _orig_getaddrinfo(host, port, socket.AF_INET, type, proto, flags)
socket.getaddrinfo = _ipv4_only_getaddrinfo
# ─────────────────────────────────────────────────────────────────────────────

load_dotenv(os.path.join(os.path.dirname(__file__), "..", ".env"))

from jgirl_scraper import (
    CATEGORY_URLS,
    collect_category_posts,
    fetch_html,
    parse_post_page,
    upload_thumbnail,
    upload_preview_images,
    upload_post_images,
    close_browser,
)
from db import (
    check_jgirl_post_exists, insert_jgirl_post, upsert_jgirl_post, update_jgirl_post,
    fetch_jgirl_posts_for_download, fetch_all_jgirl_source_urls, supabase,
)
from storage_b2 import stream_upload_to_b2, delete_b2_folder, _B2_PUBLIC_BASE, _B2_BUCKET

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S",
)

RD_API_BASE = "https://api.real-debrid.com/rest/1.0"
RD_API_KEY = os.getenv("RD_API_KEY", "")

# Set when Real-Debrid returns any non-200 — signals all workers to stop.
_rd_stop = threading.Event()


# Optional SOCKS5 proxy for Real-Debrid calls (bypasses VPS IP block).
# Set RD_PROXY=socks5://127.0.0.1:1080 in .env when using SSH tunnel.
_RD_PROXY = os.getenv("RD_PROXY", "")
_RD_PROXIES = {"https": _RD_PROXY, "http": _RD_PROXY} if _RD_PROXY else None

# Provider priority — highest first
_PROVIDER_PRIORITY = ["katfile.vip", "katfile.online", "ddownload.com", "rapidgator.net"]


# ─────────────────────────────────────────────────────────────────────────────
#  DATE HELPERS
# ─────────────────────────────────────────────────────────────────────────────

def _make_incremental_date(post_date: datetime | None) -> str:
    base = post_date if post_date else datetime.now(timezone.utc)
    natural = base.replace(
        hour=random.randint(8, 22),
        minute=random.randint(0, 59),
        second=random.randint(0, 59),
        microsecond=0,
        tzinfo=timezone.utc,
    )
    return natural.isoformat()


def _make_backfill_date(index: int, total: int, spread_days: int) -> str:
    now = datetime.now(timezone.utc)
    if total <= 1:
        return now.isoformat()
    hours_step = (spread_days * 24) / (total - 1)
    dt = now - timedelta(hours=(total - 1 - index) * hours_step)
    dt += timedelta(minutes=random.randint(-30, 30))
    return dt.isoformat()


# ─────────────────────────────────────────────────────────────────────────────
#  REAL-DEBRID HELPERS
# ─────────────────────────────────────────────────────────────────────────────

def _rd_headers() -> dict:
    return {"Authorization": f"Bearer {RD_API_KEY}"}


def unrestrict_link(url: str) -> dict | None:
    """
    Call Real-Debrid /unrestrict/link.
    Returns {download, filename, filesize, mimeType} or None on error.
    """
    if not RD_API_KEY:
        logging.error("RD_API_KEY not set in .env")
        return None
    try:
        resp = requests.post(
            f"{RD_API_BASE}/unrestrict/link",
            headers=_rd_headers(),
            data={"link": url},
            timeout=30,
            proxies=_RD_PROXIES,
        )
        if not resp.ok:
            try:
                err = resp.json()
            except Exception:
                err = resp.text
            error_code = err.get("error_code") if isinstance(err, dict) else None
            if error_code == 23:  # traffic exhausted — daily limit hit
                logging.error(f"Real-Debrid traffic exhausted — stopping all workers. Detail: {err}")
                _rd_stop.set()
            elif error_code == 19:  # hoster temporarily unavailable
                logging.warning(f"Real-Debrid hoster unavailable (error_code=19) — will retry later. Detail: {err}")
                return "hoster_unavailable"
            else:
                logging.warning(f"Real-Debrid returned {resp.status_code} (error_code={error_code}) — skipping link. Detail: {err}")
            return None
        data = resp.json()
        if "download" not in data:
            logging.warning(f"Real-Debrid: no download URL in response: {data}")
            return None
        return data
    except Exception as e:
        logging.warning(f"Real-Debrid unrestrict failed for {url}: {e}")
        return None


def _sort_links_by_priority(links: list[str]) -> list[str]:
    """Sort download links by provider priority."""
    def _priority(link: str) -> int:
        for i, host in enumerate(_PROVIDER_PRIORITY):
            if host in link:
                return i
        return len(_PROVIDER_PRIORITY)
    return sorted(links, key=_priority)


def _guess_content_type(filename: str) -> str:
    ct, _ = mimetypes.guess_type(filename)
    return ct or "application/octet-stream"


def download_and_upload_to_b2(post_id: str, rd_result: dict) -> str | None:
    """
    Stream file from Real-Debrid direct URL straight into B2.
    No temp file, no full-file buffer — safe for 1GB+ files.
    Idempotent: skips Real-Debrid download entirely if file already exists in B2.
    Returns B2 public URL or None on failure.
    """
    from storage_b2 import _get_client, _B2_BUCKET, _B2_PUBLIC_BASE
    filename = rd_result.get("filename", "file")
    content_type = rd_result.get("mimeType") or _guess_content_type(filename)
    b2_key = f"JGirls/{post_id}/{filename}"

    # Check B2 before hitting Real-Debrid
    try:
        client = _get_client()
        client.head_object(Bucket=_B2_BUCKET, Key=b2_key)
        logging.info(f"  B2 already exists, skipping download: {b2_key}")
        return f"{_B2_PUBLIC_BASE}/{b2_key}" if _B2_PUBLIC_BASE else b2_key
    except Exception as e:
        if "404" not in str(e) and "NoSuchKey" not in str(e):
            pass  # unexpected error — proceed with download attempt

    download_url = rd_result["download"]
    logging.info(f"  Streaming {filename} → B2 ({rd_result.get('filesize', '?')} bytes)")
    try:
        resp = requests.get(download_url, stream=True, timeout=(30, None), proxies=_RD_PROXIES)
        resp.raise_for_status()
        resp.raw.decode_content = True
        url = stream_upload_to_b2(resp.raw, b2_key, content_type)
        return url
    except Exception as e:
        logging.error(f"  Stream download/upload failed: {e}")
        return None


# ─────────────────────────────────────────────────────────────────────────────
#  PER-POST PIPELINE
# ─────────────────────────────────────────────────────────────────────────────

def process_post(
    post_info: dict,
    source: str,
    created_at: str,
    upload_images: bool,
    do_download: bool,
    dry_run: bool,
    retry_incomplete: bool = False,
) -> bool:
    """
    Full pipeline for one post. Returns True on success.
    If retry_incomplete=True, will upsert incomplete posts instead of inserting new ones.
    """
    source_url = post_info["url"]

    html = fetch_html(source_url)
    if not html:
        logging.warning(f"  Could not fetch post page: {source_url}")
        return False

    parsed = parse_post_page(source_url, html, source)
    parsed.pop("_post_date", None)

    if dry_run:
        links = _sort_links_by_priority(parsed["original_download_links"])
        rd = unrestrict_link(links[0]) if links and do_download else None
        logging.info(
            f"  [DRY RUN] title={parsed['title']!r} "
            f"tags={parsed['tags']} "
            f"previews={len(parsed['images'])} "
            f"dl_links={len(parsed['original_download_links'])} "
            f"rd={'OK: ' + rd['filename'] if rd else 'skipped'}"
        )
        return True

    # Insert stub row first to get UUID (or upsert if retrying incomplete)
    stub = {
        "source":                  parsed["source"],
        "source_url":              parsed["source_url"],
        "title":                   parsed["title"],
        "tags":                    parsed["tags"],
        "original_download_links": parsed["original_download_links"],
        "thumbnail_url":           parsed["thumbnail_url"],
        "post_images":             [],
        "images":                  [],
        "download_links":          [],
        "download_status":         "pending",
        "created_at":              created_at,
    }
    # Use upsert for incomplete posts, insert for new ones
    row = upsert_jgirl_post(stub) if retry_incomplete else insert_jgirl_post(stub)
    if not row or not row.get("id"):
        logging.error(f"  DB insert failed for {source_url}")
        return False

    post_id = row["id"]
    action = "Updating (incomplete)" if retry_incomplete else "Inserted"
    logging.info(f"  {action} id={post_id}")

    success = False
    try:
        # Upload thumbnail + images
        new_thumb = parsed["thumbnail_url"]
        new_post_images = parsed["post_images"]
        new_images = parsed["images"]
        if upload_images:
            new_thumb = upload_thumbnail(post_id, parsed["thumbnail_url"])
            if parsed["post_images"]:
                new_post_images = upload_post_images(post_id, parsed["post_images"])
                logging.info(f"  Uploaded thumbnail + {len(new_post_images)} post images")
            if parsed["images"]:
                new_images = upload_preview_images(post_id, parsed["images"])
                logging.info(f"  Uploaded {len(new_images)} preview frames")

        # Download file via Real-Debrid → B2
        b2_download_url = None
        if do_download and parsed["original_download_links"]:
            for link in _sort_links_by_priority(parsed["original_download_links"]):
                logging.info(f"  Trying Real-Debrid: {link}")
                rd_result = unrestrict_link(link)
                if not rd_result or rd_result == "hoster_unavailable":
                    continue
                b2_download_url = download_and_upload_to_b2(post_id, rd_result)
                if b2_download_url:
                    logging.info(f"  Download uploaded: {b2_download_url}")
                    break
                logging.warning(f"  Upload failed for link: {link}")

            if not b2_download_url:
                raise RuntimeError("All download links failed")

        # Save final state to DB — mark published only after full pipeline succeeds
        now_iso = datetime.now(timezone.utc).isoformat()
        update_jgirl_post(post_id, {
            "thumbnail_url":   new_thumb,
            "post_images":     new_post_images,
            "images":          new_images,
            "download_links":  [b2_download_url] if b2_download_url else [],
            "scraped_at":      now_iso,
            "download_status": "done",
            "status":          "published",
        })
        logging.info(f"  Done: {parsed['title']!r}")
        success = True

    except Exception as e:
        # Any download failure — keep images in B2, mark pending for retry next run
        logging.warning(f"  Download failed for {post_id} ({e}) — keeping images, marking pending")
        update_jgirl_post(post_id, {
            "thumbnail_url":   new_thumb,
            "post_images":     new_post_images,
            "images":          new_images,
            "download_status": "pending",
        })

    return success


# ─────────────────────────────────────────────────────────────────────────────
#  INCOMPLETE POST DETECTION
# ─────────────────────────────────────────────────────────────────────────────

def fetch_incomplete_jgirl_posts(source: str | None = None) -> set[str]:
    """
    Return source_urls of posts that are missing download_links.
    This includes:
    - Posts with images but no downloads (hoster_unavailable / pending retry)
    - Posts where a rollback cleared both images and downloads (failed with empty arrays)
    """
    if not supabase:
        return set()

    try:
        # Build query
        if source and source != "all":
            rows = supabase.table("jgirl_posts").select("source_url, download_links").eq("source", source).execute().data
        else:
            rows = supabase.table("jgirl_posts").select("source_url, download_links").execute().data

        # Flag any post missing download_links — regardless of images state
        incomplete = {
            row["source_url"]
            for row in rows
            if not row.get("download_links") or len(row.get("download_links", [])) == 0
        }
        return incomplete
    except Exception as e:
        logging.warning(f"Could not fetch incomplete posts: {e}")
        return set()


# ─────────────────────────────────────────────────────────────────────────────
#  CATEGORY BACKFILL
# ─────────────────────────────────────────────────────────────────────────────

def _process_one(
    idx: int,
    total: int,
    post_info: dict,
    category: str,
    mode: str,
    spread_days: int,
    delay: float,
    dry_run: bool,
    upload_images: bool,
    do_download: bool,
    force: bool = False,
) -> bool:
    """Process a single post. Returns True on success. Thread-safe."""
    if _rd_stop.is_set():
        return False

    source_url = post_info["url"]

    # Check if post exists and is complete (has download_links)
    is_incomplete = False
    if not dry_run and not force and check_jgirl_post_exists(source_url):
        # Check if it's incomplete (has images but no downloads)
        try:
            if not supabase:
                logging.warning(f"  Could not check post completeness: supabase not initialized, skipping")
                return False
            row = supabase.table("jgirl_posts").select("download_links").eq("source_url", source_url).single().execute().data
            has_downloads = row.get("download_links") and len(row.get("download_links", [])) > 0

            if not has_downloads:
                logging.info(f"  [{idx+1}/{total}] RETRY (incomplete): {source_url} - will fill downloads")
                is_incomplete = True
            else:
                logging.info(f"  [{idx+1}/{total}] SKIP (complete): {source_url}")
                return False  # Complete post, skip it
        except Exception as e:
            logging.warning(f"  Could not check post completeness: {e}, skipping")
            return False

    logging.info(f"  [{idx+1}/{total}] Processing: {source_url}")

    if mode == "incremental":
        html_for_date = fetch_html(source_url)
        if not html_for_date:
            logging.warning(f"  Could not fetch: {source_url}")
            return False
        temp_parsed = parse_post_page(source_url, html_for_date, category)
        if not temp_parsed.get("title") or temp_parsed["title"] == "Unknown":
            temp_parsed["title"] = post_info.get("title", "Unknown")
        created_at = _make_incremental_date(temp_parsed.get("_post_date"))
        temp_parsed.pop("_post_date", None)

        if dry_run:
            links = _sort_links_by_priority(temp_parsed["original_download_links"])
            rd = unrestrict_link(links[0]) if links and do_download else None
            logging.info(
                f"  [DRY RUN] title={temp_parsed['title']!r} "
                f"tags={temp_parsed['tags']} "
                f"previews={len(temp_parsed['images'])} "
                f"dl={len(temp_parsed['original_download_links'])} "
                f"rd={'OK: ' + rd['filename'] if rd else 'skipped'}"
            )
            return False

        # Use force=True for incomplete posts so _process_parsed uses upsert
        return _process_parsed(temp_parsed, category, created_at, upload_images, do_download, force or is_incomplete)
    else:
        created_at = _make_backfill_date(idx, total, spread_days)
        return process_post(post_info, category, created_at, upload_images, do_download, dry_run, retry_incomplete=is_incomplete)


def backfill_category(
    category: str,
    mode: str = "incremental",
    limit: int | None = None,
    max_pages: int = 0,
    spread_days: int = 365,
    delay: float = 1.5,
    dry_run: bool = False,
    upload_images: bool = True,
    do_download: bool = True,
    workers: int = 1,
    force: bool = False,
) -> int:
    logging.info(f"{'='*60}")
    logging.info(f"Category: {category} | mode={mode} | dry_run={dry_run} | download={do_download} | workers={workers} | force={force}")
    logging.info(f"{'='*60}")

    posts = collect_category_posts(category, max_pages=max_pages, limit=limit)
    if not posts:
        logging.info(f"  No posts found for {category}.")
        return 0

    logging.info(f"  Collected {len(posts)} post URLs for [{category}].")

    if not force and not dry_run:
        existing_urls = fetch_all_jgirl_source_urls(source=category if category != "all" else None)
        incomplete_urls = fetch_incomplete_jgirl_posts(source=category if category != "all" else None)

        # Only skip posts that are complete (exist AND have download_links)
        complete_urls = existing_urls - incomplete_urls

        logging.info(f"  {len(existing_urls)} posts in DB: {len(complete_urls)} complete, {len(incomplete_urls)} incomplete (missing downloads)")
        posts = [p for p in posts if p["url"] not in complete_urls]

        if incomplete_urls:
            logging.info(f"  Will retry {len(incomplete_urls)} incomplete posts to fill in missing downloads.")
        logging.info(f"  {len(posts)} new/incomplete posts to process.")

    inserted = 0
    total = len(posts)

    with ThreadPoolExecutor(max_workers=workers) as executor:
        futures = {
            executor.submit(
                _process_one,
                idx, total, post_info, category, mode,
                spread_days, delay, dry_run, upload_images, do_download, force,
            ): post_info
            for idx, post_info in enumerate(posts)
        }
        for future in as_completed(futures):
            post_info = futures[future]
            try:
                ok = future.result()
                if ok:
                    inserted += 1
            except Exception as e:
                logging.error(f"  Unexpected error for {post_info['url']}: {e}")

    if _rd_stop.is_set():
        logging.error(f"  [{category}] ABORTED: Real-Debrid daily traffic limit reached. {inserted} posts inserted before stop.")
    else:
        logging.info(f"  [{category}] finished: {inserted} new posts inserted.")
    return inserted


def _process_parsed(
    parsed: dict,
    source: str,
    created_at: str,
    upload_images: bool,
    do_download: bool,
    force: bool = False,
) -> bool:
    """Process an already-parsed post dict (avoids double-fetching in incremental mode)."""
    source_url = parsed["source_url"]
    stub = {
        "source":                  parsed["source"],
        "source_url":              source_url,
        "title":                   parsed["title"],
        "tags":                    parsed["tags"],
        "original_download_links": parsed["original_download_links"],
        "thumbnail_url":           parsed["thumbnail_url"],
        "post_images":             [],
        "images":                  [],
        "download_links":          [],
        "download_status":         "pending",
        "created_at":              created_at,
    }
    row = upsert_jgirl_post(stub) if force else insert_jgirl_post(stub)
    if not row or not row.get("id"):
        logging.error(f"  DB insert failed for {source_url}")
        return False

    post_id = row["id"]
    logging.info(f"  Inserted id={post_id}")

    try:
        new_thumb = parsed["thumbnail_url"]
        new_post_images = parsed["post_images"]
        new_images = parsed["images"]
        if upload_images:
            new_thumb = upload_thumbnail(post_id, parsed["thumbnail_url"])
            if parsed["post_images"]:
                new_post_images = upload_post_images(post_id, parsed["post_images"])
                logging.info(f"  Uploaded thumbnail + {len(new_post_images)} post images")
            if parsed["images"]:
                new_images = upload_preview_images(post_id, parsed["images"])
                logging.info(f"  Uploaded {len(new_images)} preview frames")

        b2_download_url = None
        if do_download and parsed["original_download_links"]:
            for link in _sort_links_by_priority(parsed["original_download_links"]):
                logging.info(f"  Trying Real-Debrid: {link}")
                rd_result = unrestrict_link(link)
                if not rd_result or rd_result == "hoster_unavailable":
                    continue
                b2_download_url = download_and_upload_to_b2(post_id, rd_result)
                if b2_download_url:
                    break
                logging.warning(f"  Upload failed for link: {link}")

            if not b2_download_url:
                raise RuntimeError("All download links failed")

        now_iso = datetime.now(timezone.utc).isoformat()
        update_jgirl_post(post_id, {
            "thumbnail_url":   new_thumb,
            "post_images":     new_post_images,
            "images":          new_images,
            "download_links":  [b2_download_url] if b2_download_url else [],
            "scraped_at":      now_iso,
            "download_status": "done",
            "status":          "published",
        })
        logging.info(f"  Done: {parsed['title']!r}")
        return True

    except Exception as e:
        # Any download failure — keep images in B2, mark pending for retry next run
        logging.warning(f"  Download failed for {post_id} ({e}) — keeping images, marking pending")
        update_jgirl_post(post_id, {
            "thumbnail_url":   new_thumb,
            "post_images":     new_post_images,
            "images":          new_images,
            "download_status": "pending",
        })
        return False


def backfill_all(sources: list[str], **kwargs) -> None:
    total = 0
    for source in sources:
        total += backfill_category(source, **kwargs)
    logging.info(f"{'='*60}")
    logging.info(f"All done. Total posts inserted: {total}")
    logging.info(f"{'='*60}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="JGirl Scraper + Downloader")
    parser.add_argument("--source", default="all",
                        choices=["upskirt", "ksiroto", "fc2", "bathroom", "all"])
    parser.add_argument("--mode", default="incremental",
                        choices=["incremental", "backfill"])
    parser.add_argument("--limit", type=int, default=None)
    parser.add_argument("--pages", type=int, default=0)
    parser.add_argument("--spread-days", type=int, default=365)
    parser.add_argument("--delay", type=float, default=1.5)
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--no-images", action="store_true",
                        help="Skip B2 image upload (thumbnail + previews)")
    parser.add_argument("--no-download", action="store_true",
                        help="Skip Real-Debrid download step")
    parser.add_argument("--workers", type=int, default=1,
                        help="Parallel workers per category (default 1)")
    parser.add_argument("--force", action="store_true",
                        help="Re-process posts that already exist (upsert instead of skip)")
    args = parser.parse_args()

    sources = ["upskirt", "ksiroto", "fc2", "bathroom"] if args.source == "all" else [args.source]
    try:
        backfill_all(
            sources=sources,
            mode=args.mode,
            limit=args.limit,
            max_pages=args.pages,
            spread_days=args.spread_days,
            delay=args.delay,
            dry_run=args.dry_run,
            upload_images=not args.no_images,
            do_download=not args.no_download,
            workers=args.workers,
            force=args.force,
        )
    finally:
        close_browser()
