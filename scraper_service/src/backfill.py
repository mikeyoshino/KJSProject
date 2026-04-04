import logging
import time
from datetime import datetime, timedelta
from scraper import scrape_home_page, fetch_html, parse_post_page, mirror_images_in_html, mirror_single_image
from db import check_post_exists, insert_post

logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(levelname)s - %(message)s')

def backfill_history():
    logging.info("Starting historical backfill (29 pages)...")
    
    total_pages = 29
    # Total days to spread over: 365
    # Posts per page: ~10 (Estimated)
    # Total posts: ~290
    # Average time between posts: ~30 hours
    hours_per_post = (365 * 24) / (total_pages * 10) # ~30 hours approx
    
    current_post_count = 0
    now = datetime.now()

    # Iterate from oldest page to newest (29 down to 1)
    # But wait, to make page 1 latest, we should calculate backwards from today
    # Total posts found so far will determine the date
    
    # Actually, iterate 29 down to 1 (Backwards in time)
    # Post count starts from oldest possible index
    
    all_post_links = []
    
    # 1. Collect all post links first so we know total count for perfect dating
    logging.info("Collecting all post links from 29 pages...")
    for page in range(1, total_pages + 1):
        logging.info(f"Scanning page {page}...")
        results = scrape_home_page(page)
        for link, thumb_url in results:
            all_post_links.append((link, thumb_url))
        time.sleep(1) # Be polite
    
    total_posts = len(all_post_links)
    logging.info(f"Found {total_posts} total posts. Spreading over 365 days...")
    
    # Total hours in a year
    total_hours = 365 * 24
    hours_step = total_hours / max(total_posts, 1)
    
    # 2. Process posts in REVERSE (oldest to newest) to match their site's order?
    # No, all_post_links[0] is latest. all_post_links[-1] is oldest.
    # index 0 should get now()
    # index total_posts-1 should get now() - 365 days
    
    for i, (link, thumb_url) in enumerate(all_post_links):
        if check_post_exists(link):
            logging.info(f"Post already exists, skipping: {link}")
            continue
            
        logging.info(f"Processing historical post [{i+1}/{total_posts}]: {link}")
        
        # Calculate artificial date
        # index 0 -> now
        # index N -> now - N*hours_step
        post_date = now - timedelta(hours=(i * hours_step))
        
        html = fetch_html(link)
        if not html: continue
        
        data = parse_post_page(link, html, thumb_url)
        data['source_url'] = link
        data['thumbnail_url'] = thumb_url # Ensure we use the best thumb
        
        # Mirror images (Parallel)
        logging.info(f"Mirroring images for: {data['title']}...")
        mirrored_thumb = mirror_single_image(data['thumbnail_url'])
        mirrored_content = mirror_images_in_html(data['content_html'])
        
        if not mirrored_thumb or not mirrored_content:
            logging.warning(f"Skipping post due to mirroring failure: {data['title']}")
            continue
            
        data['thumbnail_url'] = mirrored_thumb
        data['content_html'] = mirrored_content
        data['created_at'] = post_date.isoformat()
        
        insert_post(data)
        logging.info(f"Inserted: {data['title']} with date {post_date.strftime('%Y-%m-%d %H:%M')}")
        
        # Small delay to keep Supabase happy
        time.sleep(1.5)

if __name__ == "__main__":
    backfill_history()
