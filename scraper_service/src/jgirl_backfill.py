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
from datetime import datetime, timedelta, timezone

sys.path.insert(0, os.path.dirname(__file__))

import requests
from dotenv import load_dotenv

load_dotenv(os.path.join(os.path.dirname(__file__), "..", ".env"))

from jgirl_scraper import (
    CATEGORY_URLS,
    collect_category_posts,
    fetch_html,
    parse_post_page,
    upload_thumbnail,
    upload_preview_images,
    close_browser,
)
from db import check_jgirl_post_exists, insert_jgirl_post, update_jgirl_post
from storage_b2 import stream_upload_to_b2, delete_b2_folder, _B2_PUBLIC_BASE, _B2_BUCKET

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S",
)

RD_API_BASE = "https://api.real-debrid.com/rest/1.0"
RD_API_KEY = os.getenv("RD_API_KEY", "")

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
        )
        if resp.status_code == 401:
            logging.error("Real-Debrid: invalid API key (401)")
            return None
        if resp.status_code == 503:
            logging.warning("Real-Debrid: service unavailable (503)")
            return None
        if not resp.ok:
            try:
                err = resp.json()
            except Exception:
                err = resp.text
            logging.warning(f"Real-Debrid error {resp.status_code} for {url}: {err}")
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
    Returns B2 public URL or None on failure.
    """
    download_url = rd_result["download"]
    filename = rd_result.get("filename", "file")
    content_type = rd_result.get("mimeType") or _guess_content_type(filename)
    b2_key = f"JGirls/{post_id}/{filename}"

    logging.info(f"  Streaming {filename} → B2 ({rd_result.get('filesize', '?')} bytes)")
    try:
        resp = requests.get(download_url, stream=True, timeout=(30, None))
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
) -> bool:
    """
    Full pipeline for one post. Returns True on success.
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

    # Insert stub row first to get UUID
    stub = {
        "source":                  parsed["source"],
        "source_url":              parsed["source_url"],
        "title":                   parsed["title"],
        "tags":                    parsed["tags"],
        "original_download_links": parsed["original_download_links"],
        "thumbnail_url":           parsed["thumbnail_url"],
        "images":                  [],
        "download_links":          [],
        "download_status":         "pending",
        "created_at":              created_at,
    }
    row = insert_jgirl_post(stub)
    if not row or not row.get("id"):
        logging.error(f"  DB insert failed for {source_url}")
        return False

    post_id = row["id"]
    logging.info(f"  Inserted id={post_id}")

    success = False
    try:
        # Upload thumbnail + preview images
        new_thumb = parsed["thumbnail_url"]
        new_images = parsed["images"]
        if upload_images:
            new_thumb = upload_thumbnail(post_id, parsed["thumbnail_url"])
            new_images = upload_preview_images(post_id, parsed["images"])
            logging.info(f"  Uploaded thumbnail + {len(new_images)} previews")

        # Download file via Real-Debrid → B2
        b2_download_url = None
        if do_download and parsed["original_download_links"]:
            sorted_links = _sort_links_by_priority(parsed["original_download_links"])
            for link in sorted_links:
                logging.info(f"  Trying Real-Debrid: {link}")
                rd_result = unrestrict_link(link)
                if not rd_result:
                    continue
                b2_download_url = download_and_upload_to_b2(post_id, rd_result)
                if b2_download_url:
                    logging.info(f"  Download uploaded: {b2_download_url}")
                    break
                logging.warning(f"  Upload failed for link: {link}")

            if not b2_download_url:
                logging.error(f"  All download links failed for post {post_id}")
                raise RuntimeError("All download links failed")

        # Save final state to DB
        now_iso = datetime.now(timezone.utc).isoformat()
        update_jgirl_post(post_id, {
            "thumbnail_url":   new_thumb,
            "images":          new_images,
            "download_links":  [b2_download_url] if b2_download_url else [],
            "scraped_at":      now_iso,
            "download_status": "done",
        })
        logging.info(f"  Done: {parsed['title']!r}")
        success = True

    except Exception as e:
        logging.error(f"  Pipeline failed for {post_id}: {e}")
        # Rollback: delete entire B2 folder for this post
        logging.info(f"  Rolling back B2 for JGirls/{post_id}/...")
        deleted = delete_b2_folder(f"JGirls/{post_id}/")
        logging.info(f"  Rolled back {deleted} B2 objects")
        update_jgirl_post(post_id, {"download_status": "failed"})

    return success


# ─────────────────────────────────────────────────────────────────────────────
#  CATEGORY BACKFILL
# ─────────────────────────────────────────────────────────────────────────────

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
) -> int:
    logging.info(f"{'='*60}")
    logging.info(f"Category: {category} | mode={mode} | dry_run={dry_run} | download={do_download}")
    logging.info(f"{'='*60}")

    posts = collect_category_posts(category, max_pages=max_pages, limit=limit)
    if not posts:
        logging.info(f"  No posts found for {category}.")
        return 0

    logging.info(f"  Collected {len(posts)} post URLs for [{category}].")
    inserted = 0

    for idx, post_info in enumerate(posts):
        source_url = post_info["url"]

        if not dry_run and check_jgirl_post_exists(source_url):
            logging.info(f"  [{idx+1}/{len(posts)}] SKIP (exists): {source_url}")
            continue

        logging.info(f"  [{idx+1}/{len(posts)}] Processing: {source_url}")

        # Compute created_at
        if mode == "incremental":
            # Parse date from post page later — use placeholder for now
            # We'll fetch and parse in process_post, but need date here
            # So fetch HTML first to get the date
            html_for_date = fetch_html(source_url)
            if not html_for_date:
                logging.warning(f"  Could not fetch for date: {source_url}")
                time.sleep(delay)
                continue
            temp_parsed = parse_post_page(source_url, html_for_date, category)
            # Fallback: use listing title if post page h1 was empty
            if not temp_parsed.get("title") or temp_parsed["title"] == "Unknown":
                temp_parsed["title"] = post_info.get("title", "Unknown")
            created_at = _make_incremental_date(temp_parsed.get("_post_date"))
            # Re-use this HTML — pass it directly to avoid double fetch
            # Inline process here since we already have HTML:
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
                time.sleep(delay)
                continue

            ok = _process_parsed(temp_parsed, category, created_at, upload_images, do_download)
        else:
            created_at = _make_backfill_date(idx, len(posts), spread_days)
            ok = process_post(post_info, category, created_at, upload_images, do_download, dry_run)

        if ok:
            inserted += 1
        time.sleep(delay)

    logging.info(f"  [{category}] finished: {inserted} new posts inserted.")
    return inserted


def _process_parsed(
    parsed: dict,
    source: str,
    created_at: str,
    upload_images: bool,
    do_download: bool,
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
        "images":                  [],
        "download_links":          [],
        "download_status":         "pending",
        "created_at":              created_at,
    }
    row = insert_jgirl_post(stub)
    if not row or not row.get("id"):
        logging.error(f"  DB insert failed for {source_url}")
        return False

    post_id = row["id"]
    logging.info(f"  Inserted id={post_id}")

    try:
        new_thumb = parsed["thumbnail_url"]
        new_images = parsed["images"]
        if upload_images:
            new_thumb = upload_thumbnail(post_id, parsed["thumbnail_url"])
            new_images = upload_preview_images(post_id, parsed["images"])
            logging.info(f"  Uploaded thumbnail + {len(new_images)} previews")

        b2_download_url = None
        if do_download and parsed["original_download_links"]:
            for link in _sort_links_by_priority(parsed["original_download_links"]):
                logging.info(f"  Trying Real-Debrid: {link}")
                rd_result = unrestrict_link(link)
                if not rd_result:
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
            "images":          new_images,
            "download_links":  [b2_download_url] if b2_download_url else [],
            "scraped_at":      now_iso,
            "download_status": "done",
        })
        logging.info(f"  Done: {parsed['title']!r}")
        return True

    except Exception as e:
        logging.error(f"  Pipeline failed for {post_id}: {e}")
        deleted = delete_b2_folder(f"JGirls/{post_id}/")
        logging.info(f"  Rolled back {deleted} B2 objects")
        update_jgirl_post(post_id, {"download_status": "failed"})
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
                        choices=["upskirt", "ksiroto", "fc2", "all"])
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
    args = parser.parse_args()

    sources = ["upskirt", "ksiroto", "fc2"] if args.source == "all" else [args.source]
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
        )
    finally:
        close_browser()
