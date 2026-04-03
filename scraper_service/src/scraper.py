import requests
import uuid
import hashlib
from bs4 import BeautifulSoup
from storage import download_image, upload_to_supabase

HEADERS = {'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)'}

def fetch_html(url: str) -> str:
    response = requests.get(url, headers=HEADERS, timeout=10)
    response.raise_for_status()
    return response.text

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
    content_html = str(content_div) if content_div else ''
    
    rg_links = []
    for a in soup.find_all('a', href=True):
        href = a['href']
        if href and ('rapidgator.net/file' in href or 'rg.to/file' in href):
            # Only add unique links
            if href not in rg_links:
                rg_links.append(href)
            
    return {
        "source_url": source_url,
        "title": title,
        "thumbnail_url": thumbnail_url,
        "content_html": content_html,
        "original_rapidgator_url": rg_links
    }

def mirror_single_image(img_url: str) -> str:
    if not img_url:
        return ""
    # Create unique filename based on the URL hash
    parsed_ext = img_url.split('.')[-1].split('?')[0].lower()
    ext = parsed_ext if parsed_ext in ['jpg', 'jpeg', 'png', 'gif', 'webp'] else 'jpg'
    filename = f"{hashlib.md5(img_url.encode()).hexdigest()}.{ext}"
    
    content = download_image(img_url)
    if content:
        new_url = upload_to_supabase(content, filename)
        return new_url if new_url else img_url
    return img_url

def mirror_images_in_html(html: str) -> str:
    if not html:
        return ""
    soup = BeautifulSoup(html, 'html.parser')
    imgs = soup.find_all('img')
    
    for img in imgs:
        src = img.get('src')
        if not src:
            continue
            
        # Create unique filename
        parsed_ext = src.split('.')[-1].split('?')[0].lower()
        ext = parsed_ext if parsed_ext in ['jpg', 'jpeg', 'png', 'gif', 'webp'] else 'jpg'
        filename = f"{hashlib.md5(src.encode()).hexdigest()}.{ext}"
        
        content = download_image(src)
        if content:
            new_url = upload_to_supabase(content, filename)
            if new_url:
                img['src'] = new_url
                
    return str(soup)
