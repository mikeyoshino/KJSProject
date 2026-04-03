import time
import schedule
import logging
from src.scraper import fetch_html, parse_front_page, parse_post_page
from src.db import check_post_exists, insert_post

logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(message)s')

def job():
    logging.info("Starting scrape job...")
    try:
        html = fetch_html("https://www.buzz69.com/")
        post_links = parse_front_page(html)
        
        for link in post_links:
            if check_post_exists(link):
                logging.info(f"Stopping execution. Reached existing post: {link}")
                break
            
            logging.info(f"Scraping new post: {link}")
            post_html = fetch_html(link)
            data = parse_post_page(link, post_html)
            
            insert_post(data)
            logging.info(f"Successfully inserted: {data['title']}")
            time.sleep(2) # Polite delay
    except Exception as e:
        logging.error(f"Error during scrape job: {e}")

if __name__ == "__main__":
    logging.info("Scraper service started.")
    job() # Run once on startup
    schedule.every(15).minutes.do(job)
    
    while True:
        schedule.run_pending()
        time.sleep(1)
