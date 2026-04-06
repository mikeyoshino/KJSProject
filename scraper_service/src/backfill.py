import logging
import time
import argparse
import sys
from datetime import datetime, timedelta
from scraper import scrape_home_page, fetch_html, parse_post_page, mirror_images_in_html, mirror_single_image
from db import check_post_exists, insert_post

logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(levelname)s - %(message)s')

def run_backfill(total_pages=29, start_page=1, spread_days=365, is_sync=False):
    logging.info("=" * 60)
    mode_str = "SYNC MODE (1 page, now)" if is_sync else f"BACKFILL MODE ({total_pages} pages, {spread_days} days)"
    logging.info(f"Starting Buzz69 {mode_str}")
    logging.info("=" * 60)
    
    now = datetime.now()
    all_post_links = []
    
    # 1. Collect post links
    logging.info(f"Collecting post links from {total_pages} pages starting at {start_page}...")
    for page in range(start_page, start_page + total_pages):
        logging.info(f"Scanning page {page}...")
        results = scrape_home_page(page)
        
        page_added = 0
        for link, thumb_url in results:
            if check_post_exists(link):
                logging.info(f"  -> Post already exists, skipping collection for this link: {link}")
                continue
            all_post_links.append((link, thumb_url))
            page_added += 1
            
        else: # Only runs if loop didn't break
            if not results:
                logging.info("No more posts found. Stopping collection.")
                break
            time.sleep(1) # Be polite
            continue
        break # Break outer loop if inner loop broke
    
    total_posts = len(all_post_links)
    if total_posts == 0:
        logging.info("No new posts found.")
        return

    logging.info(f"Found {total_posts} posts to process.")
    
    # Calculate artificial date spread if NOT in sync mode
    hours_step = 0
    if not is_sync and total_posts > 0:
        total_hours = spread_days * 24
        hours_step = total_hours / max(total_posts, 1)
        logging.info(f"Spreading posts over {spread_days} days ({hours_step:.1f} hours/post)...")

    # 2. Process posts
    for i, (link, thumb_url) in enumerate(all_post_links):
        if check_post_exists(link):
            logging.info(f"Post already exists in database, skipping: {link}")
            continue
            
        logging.info(f"Processing post [{i+1}/{total_posts}]: {link}")
        
        # Calculate date
        # In sync mode, just use current time
        # In backfill mode, spread backwards from now
        if is_sync:
            post_date = now
        else:
            post_date = now - timedelta(hours=(i * hours_step))
        
        html = fetch_html(link)
        if not html: continue
        
        data = parse_post_page(link, html, thumb_url)
        data['source_url'] = link
        data['thumbnail_url'] = thumb_url
        
        # Mirror images
        logging.info(f"Mirroring images for: {data['title']}...")
        mirrored_thumb = mirror_single_image(data['thumbnail_url'])
        mirrored_content = mirror_images_in_html(data['content_html'])
        
        if mirrored_thumb is None or mirrored_content is None:
            logging.warning(f"Skipping post due to critical mirroring failure (returned None): {data['title']}")
            continue
            
        data['thumbnail_url'] = mirrored_thumb
        data['content_html'] = mirrored_content
        data['created_at'] = post_date.isoformat()
        
        insert_post(data)
        logging.info(f"Successfully inserted: {data['title']} (Simulated Date: {post_date.strftime('%Y-%m-%d %H:%M')})")
        
        # Small delay to keep Supabase happy
        time.sleep(1.5)

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Buzz69 Backfill & Sync Script")
    parser.add_argument("--pages", type=int, default=29, help="Number of pages to scan (default: 29)")
    parser.add_argument("--start-page", type=int, default=1, help="Page to start from (default: 1)")
    parser.add_argument("--spread-days", type=int, default=365, help="Days to spread posts over (default: 365)")
    parser.add_argument("--sync", action="store_true", help="Sync mode: check 1st page only and use current date")
    
    args = parser.parse_args()
    
    if args.sync:
        run_backfill(total_pages=1, start_page=1, spread_days=0, is_sync=True)
    else:
        run_backfill(total_pages=args.pages, start_page=args.start_page, spread_days=args.spread_days, is_sync=False)

