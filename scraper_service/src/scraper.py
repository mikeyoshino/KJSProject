import requests
import uuid
import hashlib
import logging
import random
import time
from bs4 import BeautifulSoup
from concurrent.futures import ThreadPoolExecutor, as_completed
from storage import download_image, upload_to_supabase

HEADERS = {'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)'}
BASE_URL = "https://buzz69.com"

def scrape_home_page(page_num: int = 1) -> list:
    url = BASE_URL if page_num == 1 else f"{BASE_URL}/page/{page_num}/"
    logging.info(f"Fetching {url}...")
    try:
        # Increased timeout to 30s
        response = requests.get(url, headers=HEADERS, timeout=30)
        response.raise_for_status()
    except Exception as e:
        logging.error(f"Failed to fetch home page: {e}")
        return []
    return parse_front_page(response.text)

def fetch_html(url: str) -> str:
    retries = 3
    for attempt in range(retries):
        try:
            # Increased timeout and added retry logic
            response = requests.get(url, headers=HEADERS, timeout=30)
            response.raise_for_status()
            return response.text
        except Exception as e:
            if attempt < retries - 1:
                logging.warning(f"Retry {attempt + 1}/{retries} for {url} due to: {e}")
                time.sleep(2) # Wait 2s before retry
            else:
                logging.error(f"Failed to fetch {url} after {retries} attempts: {e}")
    return ""

def parse_front_page(html: str) -> list[tuple[str, str]]:
    soup = BeautifulSoup(html, 'html.parser')
    results = []
    for article in soup.find_all('article'):
        # Correctly extract thumbnail from the front page
        thumb_tag = article.find('a', class_='penci-image-holder')
        thumb_url = ''
        if thumb_tag and thumb_tag.get('data-bgset'):
            # Extract the URL part from data-bgset attribute
            thumb_url = thumb_tag['data-bgset'].split('?')[0]
            
        h2 = article.find(['h2', 'h1'], class_=lambda c: c and 'entry-title' in c)
        if h2:
            a_tag = h2.find('a')
            if a_tag and a_tag.get('href'):
                results.append((a_tag['href'], thumb_url))
    return results

def parse_post_page(source_url: str, html: str, fallback_thumb: str = '') -> dict:
    soup = BeautifulSoup(html, 'html.parser')
    title = soup.find('h1').text.strip() if soup.find('h1') else 'Unknown Title'
    
    # Identify the featured image within the post area specifically
    featured_img = soup.find('img', class_='attachment-penci-full-thumb')
    thumbnail_url = fallback_thumb
    if featured_img:
        thumbnail_url = featured_img.get('src', fallback_thumb)
        # If it's a base64/SVG lazy wrapper, try to find the real one in data-src
        if thumbnail_url.startswith('data:') and featured_img.get('data-src'):
             thumbnail_url = featured_img['data-src']
    
    if not thumbnail_url:
        img = soup.find('img')
        thumbnail_url = img['src'] if img else ''
    
    content_div = soup.find('div', class_='entry-content') or soup.find('div', class_='content')
    
    # Extract original links BEFORE decomposing them
    rg_links = [a['href'] for a in soup.find_all('a', href=True) if 'rapidgator.net' in a['href'] or 'rg.to' in a['href']]
    categories = [a.get_text(strip=True) for a in soup.select('a[rel="category tag"]')]
    
    if content_div:
        # Remove Rapidgator/RG links from content_html as they are stored separately
        for a in content_div.find_all('a', href=True):
            href = a['href']
            if 'rapidgator.net' in href or 'rg.to' in href:
                a.decompose()
    
    content_html = str(content_div) if content_div else ''
    
    return {
        "source_url": source_url,
        "title": title,
        "thumbnail_url": thumbnail_url,
        "content_html": content_html,
        "original_rapidgator_url": list(set(rg_links)),
        "categories": list(set(categories))
    }

def mirror_single_image(img_url: str) -> str:
    if not img_url:
        return None
    # Create unique filename based on the URL hash
    parsed_ext = img_url.split('.')[-1].split('?')[0].lower()
    ext = parsed_ext if parsed_ext in ['jpg', 'jpeg', 'png', 'gif', 'webp'] else 'jpg'
    filename = f"{hashlib.md5(img_url.encode()).hexdigest()}.{ext}"
    
    content = download_image(img_url)
    if not content:
        return None
        
    new_url = upload_to_supabase(content, filename)
    return new_url

def mirror_images_in_html(html: str) -> str:
    if not html:
        return None
    
    # Text replacement for branding cleanup
    html = html.replace("Buzz69.com", "{{SiteName}}")
    html = html.replace("buzz69.com", "{{SiteName}}")
    
    soup = BeautifulSoup(html, 'html.parser')
    imgs = soup.find_all('img')
    
    if not imgs:
        return str(soup)

    def process_image(img):
        # Increased delay for maximum stability on slow hosts
        time.sleep(random.uniform(1.0, 3.0))
        
        src = img.get('src')
        if not src:
            return True # Not a failure, just nothing to do
            
        # Create unique filename
        parsed_ext = src.split('.')[-1].split('?')[0].lower()
        ext = parsed_ext if parsed_ext in ['jpg', 'jpeg', 'png', 'gif', 'webp'] else 'jpg'
        filename = f"{hashlib.md5(src.encode()).hexdigest()}.{ext}"
        
        content = download_image(src)
        if not content:
            return False # Failure
            
        new_url = upload_to_supabase(content, filename)
        if not new_url:
            return False # Failure
            
        # Update image src
        img['src'] = new_url
        
        # Update parent link if it exists to also use our mirrored link
        parent = img.parent
        if parent and parent.name == 'a':
            parent['href'] = f"{{{{SiteUrl}}}}{new_url}"
        
        return True # Success

    # Reduced workers to 3 for 'gentle' mirroring to avoid timeouts
    with ThreadPoolExecutor(max_workers=3) as executor:
        future_to_img = {executor.submit(process_image, img): img for img in imgs}
        for future in as_completed(future_to_img):
            try:
                success = future.result()
                if not success:
                    return None # Atomic failure: stop and fail the whole post
            except Exception as e:
                logging.error(f"Thread execution error during mirroring: {e}")
                return None # Fail safe
                
    return str(soup)
