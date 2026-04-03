import requests
from bs4 import BeautifulSoup

HEADERS = {'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)'}

def fetch_html(url: str) -> str:
    response = requests.get(url, headers=HEADERS, timeout=10)
    response.raise_for_status()
    return response.text

def parse_front_page(html: str) -> list[str]:
    soup = BeautifulSoup(html, 'html.parser')
    links = []
    for h2 in soup.find_all(['h2', 'h1', 'div'], class_=lambda c: c and ('post' in c.lower() or 'article' in c.lower())):
        a_tag = h2.find('a')
        if a_tag and a_tag.get('href'):
            links.append(a_tag['href'])
    return links

def parse_post_page(source_url: str, html: str) -> dict:
    soup = BeautifulSoup(html, 'html.parser')
    title = soup.find('h1').text.strip() if soup.find('h1') else 'Unknown Title'
    
    img = soup.find('img')
    thumbnail_url = img['src'] if img else ''
    
    content_div = soup.find('div', class_='entry-content') or soup.find('div', class_='content')
    content_html = str(content_div) if content_div else ''
    
    rg_link = ''
    for a in soup.find_all('a', href=True):
        if hasattr(a, 'href') and a['href'] is not None and ('rapidgator.net' in a['href'] or 'rg.to' in a['href']):
            rg_link = a['href']
            break
            
    return {
        "source_url": source_url,
        "title": title,
        "thumbnail_url": thumbnail_url,
        "content_html": content_html,
        "original_rapidgator_url": rg_link
    }
