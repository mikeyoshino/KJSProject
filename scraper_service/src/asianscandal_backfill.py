"""
asianscandal_backfill.py
------------------------
Backfill script for https://asianscandal.net/

Strategy:
  - Collect all posts via AJAX pagination (newest first, page 1 → last page)
  - Spread posts across 365 days so the latest post = today, oldest = ~1 year ago
  - For each post: scrape details, mirror images, save to asianscandal_posts table
  - Skips posts already in the database (idempotent)

Usage:
    cd scraper_service/src
    python asianscandal_backfill.py
"""

import logging
import time
import sys
import os
from datetime import datetime, timedelta

# Ensure src/ is on the path when running directly
sys.path.insert(0, os.path.dirname(__file__))

from asianscandal_scraper import (
    fetch_nonce_from_home,
    collect_all_posts,
    fetch_html,
    parse_post_page,
    mirror_single_image,
    mirror_images_in_html,
)
from db import check_asianscandal_post_exists, upsert_asianscandal_post

logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(levelname)s - %(message)s',
    handlers=[
        logging.StreamHandler(),
    ]
)


def backfill_asianscandal(
    spread_days: int = 365,
    delay_between_posts: float = 1.5,
    max_pages: int = 50,
    start_page: int = 0,
):
    """
    Main backfill routine.
    """
    logging.info("=" * 60)
    logging.info(f"AsianScandal.net Backfill: Pages {start_page+1}-{start_page+max_pages} ({spread_days} days)")
    logging.info("=" * 60)

    # Step 1: Get a fresh nonce
    nonce = fetch_nonce_from_home()

    # Step 2: Collect post links
    logging.info(f"Collecting posts from AJAX page {start_page+1} to {start_page+max_pages}...")
    all_posts = collect_all_posts(nonce=nonce, max_pages=max_pages, start_page=start_page)

    total_posts = len(all_posts)
    if total_posts == 0:
        logging.error("No posts collected. Check nonce and connection.")
        return

    logging.info(f"Collected {total_posts} posts total.")

    # Step 3: Calculate date spread
    now = datetime.utcnow()
    total_hours = spread_days * 24
    hours_step = total_hours / max(total_posts - 1, 1)

    import gc
    # Step 4: Process each post
    inserted = 0
    skipped = 0
    failed = 0

    for i, (post_url, thumb_url) in enumerate(all_posts):
        try: # Added per-post try-except for stability
            post_date = now - timedelta(hours=(i * hours_step))
            logging.info(
                f"[{i + 1}/{total_posts}] {post_url} ({post_date.strftime('%Y-%m-%d %H:%M')})"
            )

            # Skip if already in DB (Optional, upsert also handles this)
            if check_asianscandal_post_exists(post_url):
                logging.info(f"  -> SKIP (exists)")
                skipped += 1
                continue

            # Fetch and Parse
            html = fetch_html(post_url)
            if not html:
                failed += 1
                continue
            data = parse_post_page(post_url, html, fallback_thumb=thumb_url)

            # Mirroring
            logging.info(f"  -> Mirroring thumbnail...")
            mirrored_thumb = mirror_single_image(data['thumbnail_url'])
            
            logging.info(f"  -> Mirroring content images...")
            mirrored_content = mirror_images_in_html(data['content_html'])
            
            # Note: We no longer strictly fail if mirrored_content is a bit wonky, 
            # but we still check mirrored_thumb as it's the main visual.
            if not mirrored_thumb:
                logging.warning(f"  -> FAIL (Thumbnail mirroring failed)")
                failed += 1
                continue

            # Build Record
            record = {
                "source_url": data["source_url"],
                "title": data["title"],
                "thumbnail_url": mirrored_thumb,
                "content_html": mirrored_content or data['content_html'], # Fallback to original if mirroring fails partially
                "categories": data["categories"],
                "tags": data["tags"],
                "original_rapidgator_url": data["original_rapidgator_url"],
                "our_download_link": data["our_download_link"],
                "created_at": post_date.isoformat(),
            }

            # Save
            result = upsert_asianscandal_post(record)
            if result:
                logging.info(f"  -> SUCCESS: {data['title']}")
                inserted += 1
            else:
                logging.warning(f"  -> FAIL (DB upsert failed)")
                failed += 1

            # Memory management
            del data
            del html
            gc.collect()

        except Exception as e:
            logging.error(f"  -> UNEXPECTED ERROR processing post {post_url}: {e}")
            failed += 1
            gc.collect()

        time.sleep(delay_between_posts)

    logging.info("=" * 60)
    logging.info(f"Backfill complete! Segment: Page {start_page+1}-{start_page+max_pages}")
    logging.info(f"  Inserted : {inserted} | Skipped : {skipped} | Failed : {failed}")
    logging.info("=" * 60)


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser(description="AsianScandal Segmented Backfill Script")
    parser.add_argument("--pages", type=int, default=50, help="Number of AJAX pages to fetch (default: 50)")
    parser.add_argument("--start-page", type=int, default=0, help="AJAX page to start from (default: 0)")
    parser.add_argument("--spread-days", type=int, default=365, help="Days to spread posts over (default: 365)")
    parser.add_argument("--delay", type=float, default=1.5, help="Delay between posts in seconds (default: 1.5)")
    args = parser.parse_args()

    backfill_asianscandal(
        spread_days=args.spread_days,
        delay_between_posts=args.delay,
        max_pages=args.pages,
        start_page=args.start_page,
    )
