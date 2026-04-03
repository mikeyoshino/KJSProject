import time
import schedule
import logging
from src.scraper import fetch_html, parse_front_page, parse_post_page
from src.db import check_post_exists, insert_post

logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(message)s')

def job():
    logging.info("Starting scrape job...")
    try:
        page = 1
        found_existing = False
        
        while not found_existing:
            url = f"https://www.buzz69.com/page/{page}/" if page > 1 else "https://www.buzz69.com/"
            logging.info(f"Fetching {url}...")
            
            html = fetch_html(url)
            post_links = parse_front_page(html)
            
            if not post_links:
                logging.info("No more posts found on this page. Stopping.")
                break
                
            for link in post_links:
                if check_post_exists(link):
                    logging.info(f"Stopping execution. Reached existing post: {link}")
                    found_existing = True
                    break
                
                logging.info(f"Scraping new post: {link}")
                post_html = fetch_html(link)
                data = parse_post_page(link, post_html)
                
                insert_post(data)
                logging.info(f"Successfully inserted: {data['title']}")
                time.sleep(2) # Polite delay
                
            page += 1
            if not found_existing:
                time.sleep(2) # Polite delay between pagination
                
    except Exception as e:
        logging.error(f"Error during scrape job: {e}")

if __name__ == "__main__":
    logging.info("Scraper service started.")
    job() # Run once on startup
    schedule.every(15).minutes.do(job)
    
    while True:
        schedule.run_pending()
        time.sleep(1)
