"""
asianscandal_backfill.py
------------------------
Backfill script for https://asianscandal.net/

Strategy:
  - Collect all posts via AJAX pagination (newest first, page 1 → last page)
  - Spread posts across 365 days so the latest post = today, oldest = ~1 year ago
  - For each post: scrape details, clean HTML, upload images to B2, AI rewrite, save to DB
  - Optionally download Rapidgator files → B2 inline (--with-downloads)
  - Skips posts already in the database (idempotent)
  - Sets rewritten_at so the rewrite script never re-processes these posts

Usage:
    cd scraper_service/src
    python asianscandal_backfill.py
    python asianscandal_backfill.py --pages 10 --workers 4
    python asianscandal_backfill.py --no-ai --workers 2
    python asianscandal_backfill.py --with-downloads --pages 20
"""

import logging
import sys
import os
import gc
import threading
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime, timedelta, timezone

# Ensure src/ is on the path when running directly
sys.path.insert(0, os.path.dirname(__file__))

from asianscandal_scraper import (
    fetch_nonce_from_home,
    collect_all_posts,
    fetch_html,
    parse_post_page,
)
from asianscandal_rewrite import clean_html, upload_images_to_b2, _ollama_sem
from ai_rewriter import rewrite_content, check_ollama_available
from storage_b2 import upload_image_to_b2
from storage import download_image
from db import check_asianscandal_post_exists, upsert_asianscandal_post

logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(levelname)s - %(message)s',
    handlers=[logging.StreamHandler()],
)


def _upload_thumbnail_to_b2(url: str, post_id: str) -> str | None:
    """Download a thumbnail and upload to B2 to posts/{post_id}/thumbnail.{ext}. Falls back to original URL on failure."""
    from asianscandal_rewrite import _detect_image_ext
    if not url:
        return None
    content = download_image(url)
    if not content:
        logging.warning(f"  Could not download thumbnail: {url}")
        return url
    ext = _detect_image_ext(content)
    b2_key = f"posts/{post_id}/thumbnail.{ext}"
    new_url = upload_image_to_b2(content, b2_key)
    del content
    return new_url if new_url else url


def process_single_post(
    post_url: str,
    thumb_url: str,
    post_date: datetime,
    idx: int,
    total: int,
    use_ai: bool = True,
    rg_client=None,          # RapidgatorClient instance, or None to skip downloads
    stop_event: threading.Event | None = None,
) -> str:
    """
    Full pipeline for one post. Thread-safe.
    Returns "inserted", "skipped", or "failed".
    """
    from asianscandal_download import download_post

    tag = f"[{idx}/{total}]"
    try:
        logging.info(f"{tag} {post_url} ({post_date.strftime('%Y-%m-%d %H:%M')})")

        # Skip if already in DB
        if check_asianscandal_post_exists(post_url):
            logging.info(f"{tag}   -> SKIP (exists)")
            return "skipped"

        # Fetch and parse
        html = fetch_html(post_url)
        if not html:
            logging.warning(f"{tag}   -> FAIL (could not fetch page)")
            return "failed"

        data = parse_post_page(post_url, html, fallback_thumb=thumb_url)
        del html
        logging.info(f"{tag}   Scraped: {len(data['content_html'])} chars")

        # Clean HTML
        content_html = clean_html(data["content_html"])
        logging.info(f"{tag}   After clean: {len(content_html)} chars")
        if not content_html.strip():
            logging.warning(f"{tag}   -> FAIL (clean_html returned empty)")
            return "failed"

        # Insert post first to get ID (with placeholder content)
        initial_record = {
            "source_url": data["source_url"],
            "title": data["title"],
            "thumbnail_url": None,  # Will be updated after upload
            "content_html": "",  # Will be updated after B2 upload
            "categories": data["categories"],
            "tags": data["tags"],
            "original_rapidgator_url": data["original_rapidgator_url"],
            "our_download_link": [],
            "created_at": post_date.isoformat(),
            "source_name": "AsianScandal",
        }

        result = upsert_asianscandal_post(initial_record)
        if not result:
            logging.warning(f"{tag}   -> FAIL (DB upsert failed)")
            return "failed"

        post_id = result.get("id")
        if not post_id:
            logging.warning(f"{tag}   -> FAIL (could not get post_id from upsert)")
            return "failed"

        # Now upload content images to B2 with post_id
        logging.info(f"{tag}   Uploading content images to B2...")
        content_html = upload_images_to_b2(content_html, post_id)
        logging.info(f"{tag}   After B2: {len(content_html)} chars")

        # Upload thumbnail to B2 with post_id
        mirrored_thumb = None
        if data["thumbnail_url"]:
            logging.info(f"{tag}   Uploading thumbnail to B2...")
            mirrored_thumb = _upload_thumbnail_to_b2(data["thumbnail_url"], post_id)

        # AI rewrite — serialised via shared semaphore
        rewritten_at = None
        if use_ai:
            logging.info(f"{tag}   Waiting for Ollama slot...")
            with _ollama_sem:
                logging.info(f"{tag}   Rewriting via Ollama...")
                content_html = rewrite_content(data["title"], content_html)
            logging.info(f"{tag}   After Ollama: {len(content_html)} chars")
            rewritten_at = datetime.now(timezone.utc).isoformat()

        # Update post with final content_html and thumbnail
        update_payload = {
            "content_html": content_html,
            "thumbnail_url": mirrored_thumb or data["thumbnail_url"],
        }
        if rewritten_at:
            update_payload["rewritten_at"] = rewritten_at

        from db import supabase
        supabase.table("posts").update(update_payload).eq("id", post_id).execute()

        logging.info(f"{tag}   -> SUCCESS: {data['title']}")

        # Download Rapidgator files → B2 inline if client was provided
        rg_urls = data.get("original_rapidgator_url") or []
        if rg_client and rg_urls and not (stop_event and stop_event.is_set()):
            if post_id:
                logging.info(f"{tag}   Downloading {len(rg_urls)} Rapidgator file(s)...")
                stop_ev = stop_event or threading.Event()
                outcome = download_post(
                    rg_client,
                    {"id": post_id, "original_rapidgator_url": rg_urls},
                    stop_ev,
                )
                if outcome == "traffic_exceeded":
                    logging.warning(f"{tag}   RG traffic exceeded — downloads stopped for this run")
                    if stop_event:
                        stop_event.set()
                else:
                    logging.info(f"{tag}   Download outcome: {outcome}")
            else:
                logging.warning(f"{tag}   Could not get post ID from upsert result — skipping download")

        return "inserted"

    except Exception as e:
        logging.error(f"{tag}   -> UNEXPECTED ERROR: {e}")
        return "failed"
    finally:
        gc.collect()


def backfill_asianscandal(
    spread_days: int = 365,
    delay_between_posts: float = 0.0,  # kept for CLI compat, unused in parallel mode
    max_pages: int = 50,
    start_page: int = 0,
    workers: int = 4,
    use_ai: bool = True,
    with_downloads: bool = False,
    limit: int | None = None,
):
    logging.info("=" * 60)
    logging.info(f"AsianScandal.net Backfill: Pages {start_page+1}-{start_page+max_pages} "
                 f"({spread_days} days, {workers} workers)"
                 + (" + RG downloads" if with_downloads else ""))
    logging.info("=" * 60)

    if use_ai:
        if check_ollama_available():
            logging.info("Ollama is available.")
        else:
            logging.warning("Ollama is NOT reachable. Proceeding without AI rewrite.")
            use_ai = False

    # One shared RapidgatorClient for the whole run (handles auth + session reuse)
    rg_client = None
    stop_event = None
    if with_downloads:
        from asianscandal_download import download_post  # noqa: ensure import is valid
        from rapidgator_client import RapidgatorClient
        rg_client = RapidgatorClient()
        stop_event = threading.Event()
        logging.info("RapidgatorClient initialised for inline downloads.")

    # Collect post URLs — stop early if limit is set
    nonce = fetch_nonce_from_home()
    logging.info(f"Collecting posts from AJAX pages {start_page+1}–{start_page+max_pages}...")
    all_posts = collect_all_posts(nonce=nonce, max_pages=max_pages, start_page=start_page, limit=limit)

    total_posts = len(all_posts)
    if total_posts == 0:
        logging.error("No posts collected. Check nonce and connection.")
        return

    logging.info(f"Processing {total_posts} posts{f' (limit={limit})' if limit else ''}...")

    # All posts get current timestamp
    now = datetime.now(timezone.utc)
    post_dates = [now for _ in range(total_posts)]

    inserted = skipped = failed = 0

    with ThreadPoolExecutor(max_workers=workers) as executor:
        futures = {
            executor.submit(
                process_single_post,
                post_url, thumb_url, post_dates[i],
                i + 1, total_posts, use_ai, rg_client, stop_event,
            ): post_url
            for i, (post_url, thumb_url) in enumerate(all_posts)
        }

        for future in as_completed(futures):
            post_url = futures[future]
            try:
                outcome = future.result()
                if outcome == "inserted":
                    inserted += 1
                elif outcome == "skipped":
                    skipped += 1
                else:
                    failed += 1
            except Exception as e:
                logging.error(f"  -> Unhandled future error for {post_url}: {e}")
                failed += 1

    if rg_client:
        rg_client.close()

    logging.info("=" * 60)
    logging.info(f"Backfill complete! Pages {start_page+1}–{start_page+max_pages}")
    logging.info(f"  Inserted : {inserted} | Skipped : {skipped} | Failed : {failed}")
    logging.info("=" * 60)


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser(description="AsianScandal Segmented Backfill Script")
    parser.add_argument("--pages",          type=int,   default=50,  help="Number of AJAX pages to fetch (default: 50)")
    parser.add_argument("--start-page",     type=int,   default=0,   help="AJAX page to start from (default: 0)")
    parser.add_argument("--spread-days",    type=int,   default=365, help="Days to spread posts over (default: 365)")
    parser.add_argument("--delay",          type=float, default=0.0, help="Unused in parallel mode (kept for compat)")
    parser.add_argument("--workers",        type=int,   default=4,   help="Parallel workers (default: 4)")
    parser.add_argument("--no-ai",          action="store_true",     help="Skip Ollama AI rewrite step")
    parser.add_argument("--with-downloads", action="store_true",     help="Also download Rapidgator files → B2 for each post")
    parser.add_argument("--limit",          type=int,   default=None, help="Max posts to process")
    args = parser.parse_args()

    backfill_asianscandal(
        spread_days=args.spread_days,
        delay_between_posts=args.delay,
        max_pages=args.pages,
        start_page=args.start_page,
        workers=args.workers,
        use_ai=not args.no_ai,
        with_downloads=args.with_downloads,
        limit=args.limit,
    )
