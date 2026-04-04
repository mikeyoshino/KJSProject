import requests
from bs4 import BeautifulSoup
import re

headers = {
    'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:149.0) Gecko/20100101 Firefox/149.0',
    'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8',
    'Accept-Language': 'en-US,en;q=0.9',
}

print("=== FETCHING HOME PAGE ===")
r = requests.get('https://asianscandal.net/', headers=headers, timeout=30)
print(f"Status: {r.status_code}")
soup = BeautifulSoup(r.text, 'html.parser')

articles = soup.find_all('article')
print(f"Found {len(articles)} articles on home page\n")

print("=== FIRST ARTICLE RAW HTML ===")
if articles:
    print(str(articles[0])[:3000])

print("\n=== ARTICLE STRUCTURE ===")
for i, a in enumerate(articles[:3]):
    classes = a.get('class', [])
    print(f"\nArticle {i+1}: class={classes}")
    # Links
    link = a.find('a', href=True)
    print(f"  First link: {link['href'] if link else None}")
    # Title
    for tag in ['h1','h2','h3','h4']:
        t = a.find(tag)
        if t:
            cls = t.get('class', [])
            print(f"  Title <{tag} class={cls}>: {t.get_text(strip=True)[:60]}")
            break
    # Images
    img = a.find('img')
    if img:
        print(f"  IMG class={img.get('class')}")
        print(f"  IMG src={str(img.get('src',''))[:100]}")
        print(f"  IMG data-src={str(img.get('data-src',''))[:100]}")
        print(f"  IMG data-bgset={str(img.get('data-bgset',''))[:100]}")
    # BGSet attributes
    for el in a.find_all(True, attrs={'data-bgset': True}):
        print(f"  <{el.name} class={el.get('class')} data-bgset={str(el['data-bgset'])[:100]}>")

print("\n=== NONCE SEARCH ===")
nonce_matches = re.findall(r'"nonce"\s*:\s*"([^"]+)"', r.text)
print(f"nonce from JSON: {nonce_matches}")
# Try JS variable patterns
for pat in [r'nonce["\'\s:=]+([a-f0-9]{8,12})', r'penci_ajax_nonce["\'\s:=]+([a-f0-9]{8,12})']:
    found = re.findall(pat, r.text)
    if found:
        print(f"Pattern '{pat}': {found[:5]}")

print("\n=== AJAX REQUEST (offset=0) ===")
ajax_headers = {
    'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:149.0) Gecko/20100101 Firefox/149.0',
    'Accept': 'text/html, */*; q=0.01',
    'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8',
    'X-Requested-With': 'XMLHttpRequest',
    'Origin': 'https://asianscandal.net',
    'Referer': 'https://asianscandal.net/',
}
nonce = nonce_matches[0] if nonce_matches else '4c8c040d61'
payload = {
    'action': 'penci_more_post_ajax',
    'offset': '6',
    'layout': 'magazine-2',
    'from': 'customize',
    'datafilter': '',
    'template': 'sidebar',
    'ppp': '6',
    'number': '6',
    'order': 'desc',
    'nonce': nonce,
}
r2 = requests.post('https://asianscandal.net/wp-admin/admin-ajax.php',
                   headers=ajax_headers, data=payload, timeout=30)
print(f"AJAX Status: {r2.status_code}")
print("AJAX Response (first 3000 chars):")
print(r2.text[:3000])

print("\n=== FETCH ONE POST PAGE ===")
# Get first post URL from articles
first_url = None
if articles:
    link = articles[0].find('a', href=True)
    if link:
        first_url = link['href']
if first_url:
    print(f"Fetching: {first_url}")
    rp = requests.get(first_url, headers=headers, timeout=30)
    sp = BeautifulSoup(rp.text, 'html.parser')
    h1 = sp.find('h1')
    print(f"H1: class={h1.get('class') if h1 else None}, text={h1.get_text(strip=True)[:80] if h1 else None}")
    # Featured image
    for cls in ['wp-post-image', 'attachment-full', 'size-full']:
        fi = sp.find('img', class_=cls)
        if fi:
            print(f"Featured img class={fi.get('class')} src={str(fi.get('src',''))[:100]}")
            break
    # Content div
    for cls in ['entry-content', 'post-content', 'the-content']:
        cd = sp.find('div', class_=cls)
        if cd:
            print(f"Content div class={cd.get('class')}, len={len(str(cd))}")
            print(f"Content snippet: {str(cd)[:500]}")
            break
    # Categories
    cats = sp.select('a[rel="category tag"]')
    print(f"Categories (rel=category tag): {[c.get_text(strip=True) for c in cats]}")
