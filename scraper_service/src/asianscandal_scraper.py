"""
asianscandal_scraper.py
-----------------------
Scraper for https://asianscandal.net/

Both asianscandal.net and buzz69.com use the Penci/Soledad WordPress theme,
so the HTML structure (article containers, lazy-load patterns, content divs)
is identical. This scraper reuses the same selectors proven in scraper.py.

The site uses WordPress AJAX (admin-ajax.php) with action=penci_more_post_ajax
to return paginated HTML fragments instead of full page pagination.
"""

import requests
import hashlib
import logging
import random
import time
import re
import sys
import os
from bs4 import BeautifulSoup
from concurrent.futures import ThreadPoolExecutor, as_completed

# Allow running standalone from src/ dir
sys.path.insert(0, os.path.dirname(__file__))
from storage import download_image, upload_to_supabase

BASE_URL = "https://asianscandal.net"
AJAX_URL = f"{BASE_URL}/wp-admin/admin-ajax.php"

def get_random_ua():
    uas = [
        'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36',
        'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36',
        'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36',
        'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:124.0) Gecko/20100101 Firefox/124.0',
        'Mozilla/5.0 (AppleWebKit/537.36; KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36',
    ]
    return random.choice(uas)

def get_headers(is_ajax=False):
    h = {
        'User-Agent': get_random_ua(),
        'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7',
        'Accept-Language': 'en-US,en;q=0.9',
        'Cache-Control': 'max-age=0',
        'Connection': 'keep-alive',
    }
    if is_ajax:
        h.update({
            'Accept': 'text/html, */*; q=0.01',
            'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8',
            'X-Requested-With': 'XMLHttpRequest',
            'Origin': BASE_URL,
            'Referer': f'{BASE_URL}/',
        })
    return h

# Number of posts per AJAX page (matches the site's ppp setting)
POSTS_PER_PAGE = 6


def fetch_nonce_from_home(html_content: str = None) -> str:
    """
    Extract the WordPress AJAX nonce from the home page JS.
    Scans multiple JS objects for the 'nonce' key.
    """
    FALLBACK_NONCE = "4c8c040d61"
    
    if not html_content:
        try:
            logging.info(f"Fetching home page to extract nonce: {BASE_URL}")
            r = requests.get(BASE_URL, headers=get_headers(), timeout=60)
            r.raise_for_status()
            html_content = r.text
        except Exception as e:
            logging.warning(f"Failed to fetch home page for nonce: {e}")
            return FALLBACK_NONCE

    # Check specific JS objects first for accuracy
    # Patterns: 
    # var penci_ajax_more_posts = {"nonce":"..."};
    # var ajax_var_more = {"url":"...","nonce":"..."};
    # var PENCI = {"ajaxUrl":"...","nonce":"..."};
    
    objects_to_check = ['penci_ajax_more_posts', 'ajax_var_more', 'PENCI', 'penci_widgets_ajax']
    
    for obj in objects_to_check:
        # Match "obj_name" = { ... "nonce" : "VALUE" ... }
        # The value is usually 10-12 hex chars
        pattern = rf'{obj}\s*=\s*\{{.*?"nonce"\s*:\s*"([a-f0-9]{{10,}})"'
        m = re.search(pattern, html_content, re.DOTALL)
        if m:
            nonce = m.group(1)
            logging.info(f"Detected nonce from '{obj}': {nonce}")
            return nonce

    # Broad fallback pattern if object-specific fails
    broad_pattern = r'"nonce"\s*:\s*"([a-f0-9]{10,})"'
    m = re.search(broad_pattern, html_content)
    if m:
        logging.info(f"Detected nonce via broad pattern: {m.group(1)}")
        return m.group(1)

    logging.warning(f"Nonce detection failed. Using fallback: {FALLBACK_NONCE}")
    return FALLBACK_NONCE


def fetch_html(url: str, retries: int = 3) -> str:
    """Fetch a URL with retries and backoff."""
    for attempt in range(retries):
        try:
            r = requests.get(url, headers=get_headers(), timeout=60)
            r.raise_for_status()
            return r.text
        except Exception as e:
            if attempt < retries - 1:
                logging.warning(f"Retry {attempt+1}/{retries} for {url}: {e}")
                time.sleep(2 ** attempt)  # exponential backoff
            else:
                logging.error(f"Failed after {retries} attempts: {url} — {e}")
    return ""


def fetch_ajax_page(offset: int, nonce: str) -> list:
    """
    POST to admin-ajax.php to get an HTML fragment of posts.
    Returns list of (post_url, thumbnail_url) tuples.
    """
    payload = {
        'action': 'penci_more_post_ajax',
        'offset': str(offset),
        'layout': 'magazine-2',
        'from': 'customize',
        'datafilter': '',
        'template': 'sidebar',
        'ppp': str(POSTS_PER_PAGE),
        'number': str(POSTS_PER_PAGE),
        'order': 'desc',
        'nonce': nonce,
    }
    
    try:
        logging.info(f"  Requesting AJAX offset {offset}...")
        r = requests.post(AJAX_URL, headers=get_headers(is_ajax=True), data=payload, timeout=60)
        r.raise_for_status()
    except Exception as e:
        logging.error(f"  AJAX error at offset={offset}: {e}")
        return []

    text = r.text.strip()
    if text in ('', '0', '-1', 'false'):
        logging.info(f"  No more posts at offset={offset} (Server returned '{text}')")
        return []

    return parse_listing_html(text)


def parse_listing_html(html: str) -> list:
    """
    Parse the HTML fragment returned by Admin AJAX (or a full page).
    Uses Penci/Soledad theme selectors — same as buzz69.com.

    Returns list of (post_url, thumbnail_url) tuples.
    """
    soup = BeautifulSoup(html, 'html.parser')
    results = []

    for article in soup.find_all('article'):
        # --- Thumbnail ---
        # Penci theme uses <a class="penci-image-holder" data-bgset="URL">
        thumb_url = ''
        thumb_tag = article.find('a', class_='penci-image-holder')
        if thumb_tag and thumb_tag.get('data-bgset'):
            thumb_url = thumb_tag['data-bgset'].split('?')[0]

        # Fallback: regular <img>
        if not thumb_url:
            img = article.find('img')
            if img:
                thumb_url = (
                    img.get('data-src')
                    or img.get('data-lazy-src')
                    or img.get('src', '')
                )
                if thumb_url.startswith('data:'):
                    thumb_url = img.get('data-src', '')

        # --- Post URL ---
        # Penci theme puts the link inside h2.entry-title or h1.entry-title
        post_url = ''
        h_tag = article.find(['h2', 'h1'], class_=lambda c: c and 'entry-title' in c)
        if h_tag:
            a = h_tag.find('a')
            if a and a.get('href'):
                post_url = a['href']

        # Fallback: first <a> with href
        if not post_url:
            a = article.find('a', href=True)
            if a:
                post_url = a['href']

        if post_url:
            results.append((post_url, thumb_url))

    return results


def parse_post_page(source_url: str, html: str, fallback_thumb: str = '') -> dict:
    """
    Parse a full individual post page.
    Uses Penci/Soledad selectors — same theme as buzz69.com.
    Returns dict matching asianscandal_posts schema.
    """
    soup = BeautifulSoup(html, 'html.parser')

    # --- Title ---
    h1 = soup.find('h1')
    title = h1.get_text(strip=True) if h1 else 'Unknown Title'

    # --- Thumbnail ---
    # Penci theme: <img class="attachment-penci-full-thumb">
    thumbnail_url = fallback_thumb
    featured = soup.find('img', class_='attachment-penci-full-thumb')
    if featured:
        src = featured.get('src', '')
        if src.startswith('data:') and featured.get('data-src'):
            src = featured['data-src']
        if src and not src.startswith('data:'):
            thumbnail_url = src

    # Fallback: any img
    if not thumbnail_url:
        img = soup.find('img')
        if img:
            thumbnail_url = img.get('data-src') or img.get('src', '')

    # --- Content ---
    content_div = soup.find('div', class_='entry-content') or soup.find('div', class_='post-content')
    content_html = str(content_div) if content_div else ''

    # --- Categories ---
    # Standard WP category indicators
    categories = [a.get_text(strip=True) for a in soup.select('a[rel="category tag"]')]
    
    # --- Tags ---
    # User requested tags specifically from div.post-tags
    tags = [a.get_text(strip=True) for a in soup.select('div.post-tags a')]

    # --- Download Links (Rapidgator) ---
    # Capture both /file/ and /folder/ links, from <a href> and plain text
    _RG_URL_RE = re.compile(
        r'https?://(?:www\.)?rapidgator\.net/(?:file|folder)/[^\s"\'<>]+',
        re.IGNORECASE,
    )
    _RG_EXCLUDE_RE = re.compile(r'/ref/|/article/premium/', re.IGNORECASE)

    rg_links = []
    if content_div:
        # Pass 1: <a href> tags
        for a in content_div.find_all('a', href=True):
            href = a['href']
            if _RG_URL_RE.match(href) and not _RG_EXCLUDE_RE.search(href):
                rg_links.append(href)
        # Pass 2: plain-text URLs (e.g. <p>https://rapidgator.net/file/...</p>)
        for match in _RG_URL_RE.findall(content_div.get_text(" ")):
            if not _RG_EXCLUDE_RE.search(match):
                rg_links.append(match)

    # deduplicate, preserving order
    seen = set()
    rg_links = [u for u in rg_links if not (u in seen or seen.add(u))]

    return {
        "source_url": source_url,
        "title": title,
        "thumbnail_url": thumbnail_url,
        "content_html": content_html,
        "categories": list(set(categories)),
        "tags": list(set(tags)),
        "original_rapidgator_url": rg_links,
        "our_download_link": [],  # For future automated uploader service
    }


def mirror_single_image(img_url: str) -> str | None:
    """Download and re-upload one image to Supabase storage."""
    if not img_url:
        return ""
    ext_raw = img_url.split('.')[-1].split('?')[0].lower()
    ext = ext_raw if ext_raw in ('jpg', 'jpeg', 'png', 'gif', 'webp') else 'jpg'
    filename = f"{hashlib.md5(img_url.encode()).hexdigest()}.{ext}"
    content = download_image(img_url)
    new_url = upload_to_supabase(content, filename)
    if not new_url:
        logging.warning(f"  -> Failed to upload thumbnail, falling back to original: {img_url}")
        return img_url
    return new_url


def mirror_images_in_html(html: str) -> str | None:
    """Re-host all images in an HTML string to Supabase storage."""
    if not html:
        return ""

    soup = BeautifulSoup(html, 'html.parser')
    imgs = soup.find_all('img')
    if not imgs:
        return str(soup)

    def process_image(img):
        time.sleep(random.uniform(0.5, 1.5))
        src = img.get('src') or img.get('data-src') or img.get('data-lazy-src', '')
        if not src or src.startswith('data:'):
            return True
        
        # SKIP if already mirrored in Supabase
        if "supabase.co" in src:
            return True

        ext_raw = src.split('.')[-1].split('?')[0].lower()

        ext = ext_raw if ext_raw in ('jpg', 'jpeg', 'png', 'gif', 'webp') else 'jpg'
        filename = f"{hashlib.md5(src.encode()).hexdigest()}.{ext}"
        data = download_image(src)
        if not data:
            logging.warning(f"  -> Failed to download content image, keeping original: {src}")
            return True
        new_url = upload_to_supabase(data, filename)
        if not new_url:
            logging.warning(f"  -> Failed to upload content image, keeping original: {src}")
            return True
        img['src'] = new_url
        # Update parent <a> href if it wraps the image
        if img.parent and img.parent.name == 'a':
            img.parent['href'] = new_url
        return True

    with ThreadPoolExecutor(max_workers=2) as executor:
        futures = {executor.submit(process_image, img): img for img in imgs}
        for future in as_completed(futures):
            try:
                # No longer returning None on single image failure (keeps post partially intact)
                if not future.result():
                    logging.warning(f"  -> Failed to mirror one image in HTML content.")
            except Exception as e:
                logging.error(f"Image mirroring thread error: {e}")

    result_html = str(soup)
    soup.decompose() # Explicitly free memory
    return result_html


def collect_all_posts(nonce: str, max_pages: int = 100, start_page: int = 0, limit: int | None = None) -> list:
    """
    Iterate through AJAX offsets and collect (url, thumb) pairs.
    Stops early if limit posts have been collected.
    """
    all_posts = []
    end_page = start_page + max_pages
    consecutive_errors = 0

    for page_idx in range(start_page, end_page):
        if limit and len(all_posts) >= limit:
            break

        offset = page_idx * POSTS_PER_PAGE
        logging.info(f"  AJAX page {page_idx + 1} (offset={offset})...")

        try:
            posts = fetch_ajax_page(offset, nonce)
            if not posts:
                logging.info(f"No more posts found at offset={offset}.")
                break
            all_posts.extend(posts)
            logging.info(f"  -> +{len(posts)} posts (total: {len(all_posts)})")
            consecutive_errors = 0
        except Exception as e:
            consecutive_errors += 1
            logging.error(f"  -> Error on page {page_idx + 1}: {e}")
            if consecutive_errors >= 3:
                logging.error("Too many consecutive errors. Stopping collection.")
                break

        time.sleep(1)

    if limit:
        all_posts = all_posts[:limit]
    return all_posts


def process_posts_batch(posts: list, limit: int = None):
    """
    Process a list of (url, thumb) tuples.
    Parses each post, mirrors images, and saves to database.
    """
    from db import upsert_asianscandal_post, check_asianscandal_post_exists
    
    count = 0
    total = len(posts)
    if limit:
        posts = posts[:limit]
        total = len(posts)

    for i, (url, thumb) in enumerate(posts):
        logging.info(f"[{i+1}/{total}] Processing: {url}")
        
        # 1. Skip if exists
        try:
            if check_asianscandal_post_exists(url):
                logging.info(f"  -> Skipping (already exists)")
                continue
        except Exception as e:
            logging.error(f"  -> Error checking DB: {e}")
            continue

        # 2. Fetch HTML
        html = fetch_html(url)
        if not html:
            continue
            
        # 3. Parse content
        try:
            data = parse_post_page(url, html, fallback_thumb=thumb)
        except Exception as e:
            logging.error(f"  -> Parsing error: {e}")
            continue
            
        # 4. Mirror images
        logging.info(f"  -> Mirroring images...")
        mirrored_html = mirror_images_in_html(data['content_html'])
        if mirrored_html:
            data['content_html'] = mirrored_html
        
        # Also mirror thumbnail if it's external
        if data['thumbnail_url'] and BASE_URL not in data['thumbnail_url']:
            new_thumb = mirror_single_image(data['thumbnail_url'])
            if new_thumb:
                data['thumbnail_url'] = new_thumb
        
        # 5. Save to Supabase
        try:
            upsert_asianscandal_post(data)
            logging.info(f"  -> SUCCESS")
            count += 1
        except Exception as e:
            logging.error(f"  -> Database error: {e}")
            
        # polite delay
        time.sleep(random.uniform(1, 3))
    
    return count

if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser(description="AsianScandal Scraper")
    parser.add_argument("--test-file", help="Local HTML file to parse for testing")
    parser.add_argument("--type", choices=["home", "post"], default="home", help="Type of file for --test-file")
    parser.add_argument("--limit", type=int, help="Limit number of posts to process")
    parser.add_argument("--url", help="URL for the post being tested (if using --test-file --type post)")
    args = parser.parse_args()

    logging.basicConfig(level=logging.INFO, format='%(asctime)s %(levelname)s %(message)s')

    if args.test_file:
        with open(args.test_file, 'r', encoding='utf-8') as f:
            content = f.read()
        
        if args.type == "home":
            print("--- Nonce Detection Test ---")
            nonce = fetch_nonce_from_home(content)
            print(f"Detected Nonce: {nonce}")
            
            print("\n--- Listing Extraction Test ---")
            posts = parse_listing_html(content)
            print(f"Found {len(posts)} posts:")
            for u, t in posts:
                print(f"  URL: {u}")
                print(f"  Thumb: {t}\n")
        else:
            print(f"--- Post Parsing Test ({args.url}) ---")
            data = parse_post_page(args.url or "https://test.com", content)
            print(f"Title: {data['title']}")
            print(f"Categories: {data['categories']}")
            print(f"Tags: {data['tags']}")
            print(f"Thumbnail: {data['thumbnail_url']}")
            print(f"Original Rapidgator: {data['original_rapidgator_url']}")
            print(f"Our Link: {data['our_download_link']}")
            print(f"Content Length: {len(data['content_html'])}")
            # print(data['content_html'][:500] + "...")
    else:
        nonce = fetch_nonce_from_home()
        logging.info(f"Final Nonce: {nonce}")
        
        # Demo mode: process first page
        posts = fetch_ajax_page(0, nonce)
        if posts:
            processed = process_posts_batch(posts, limit=args.limit)
            logging.info(f"Finished. Processed {processed} new posts.")
        else:
            logging.error("Failed to fetch first AJAX page.")
