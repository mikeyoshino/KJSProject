import time
import schedule
import logging
from scraper import fetch_html, parse_front_page, parse_post_page, mirror_single_image, mirror_images_in_html
from db import check_post_exists, insert_post

logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(message)s')

def job():
    logging.info("Starting scrape job...")
    try:
        page = 1
        found_existing = False
        
        while not found_existing and page <= 2:
            url = f"https://www.buzz69.com/page/{page}/" if page > 1 else "https://www.buzz69.com/"
            logging.info(f"Fetching {url}...")
            
            html = fetch_html(url)
            results = parse_front_page(html)
            
            if not results:
                logging.info("No more posts found on this page. Stopping.")
                break
                
            for link, thumb_url in results:
                if check_post_exists(link):
                    logging.info(f"Post already exists, skipping: {link}")
                    continue
                
                logging.info(f"Scraping new post: {link}")
                post_html = fetch_html(link)
                data = parse_post_page(link, post_html, fallback_thumb=thumb_url)
                
                # Mirror images before insertion
                logging.info(f"Mirroring images for: {data['title']}...")
                mirrored_thumb = mirror_single_image(data['thumbnail_url'])
                mirrored_content = mirror_images_in_html(data['content_html'])
                
                if not mirrored_thumb or not mirrored_content:
                    logging.warning(f"Skipping post due to mirroring failure: {data['title']}")
                    continue
                
                data['thumbnail_url'] = mirrored_thumb
                data['content_html'] = mirrored_content
                
                insert_post(data)
                logging.info(f"Successfully inserted with mirrored images: {data['title']}")
                time.sleep(2) # Polite delay
                
            page += 1
            if not found_existing and page <= 2:
                time.sleep(2) # Polite delay between pagination
        
        if page > 2:
            logging.info("Testing Limit: Reached 2 page maximum. Stopping job.")
                
    except Exception as e:
        logging.error(f"Error during scrape job: {e}")

if __name__ == "__main__":
    logging.info("Scraper service started.")
    job() # Run once on startup
    schedule.every(15).minutes.do(job)
    
    while True:
        schedule.run_pending()
        time.sleep(1)
