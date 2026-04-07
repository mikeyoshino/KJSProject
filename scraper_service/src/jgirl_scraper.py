"""
jgirl_scraper.py
----------------
Core parsing and B2 upload helpers for jgirl.co.

Three categories: upskirt, ksiroto, fc2
Category URL pattern: https://jgirl.co/{source}?pg=N
Post URL pattern:     https://jgirl.co/post/{slug}

Imported by jgirl_backfill.py — no CLI here.
Requires jgirl_state.json (run jgirl_auth.py once to generate it).
"""

import os
import json
import time
import random
import logging
from datetime import datetime

import requests
from bs4 import BeautifulSoup

BASE_URL = "https://jgirl.co"
_STATE_FILE = os.path.join(os.path.dirname(__file__), "jgirl_state.json")

CATEGORY_URLS = {
    "upskirt": f"{BASE_URL}/upskirt",
    "fc2":     f"{BASE_URL}/fc2",
    "ksiroto": f"{BASE_URL}/ksiroto",
}

_session: requests.Session | None = None


def _get_session() -> requests.Session:
    """Build a requests.Session loaded with saved Cloudflare cookies."""
    global _session
    if _session is not None:
        return _session

    _session = requests.Session()

    if not os.path.exists(_STATE_FILE):
        logging.warning("jgirl_state.json not found — run jgirl_auth.py first")
        return _session

    with open(_STATE_FILE) as f:
        state = json.load(f)

    ua = state.get("user_agent", (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36"
    ))
    _session.headers.update({
        "User-Agent": ua,
        "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
        "Accept-Language": "ja,en-US;q=0.9,en;q=0.8",
        "Referer": BASE_URL,
    })
    for name, value in state.get("cookies", {}).items():
        _session.cookies.set(name, value, domain="jgirl.co")

    logging.info(f"Loaded cookies: {list(state.get('cookies', {}).keys())}")
    return _session


def close_browser():
    """No-op — kept for compatibility with jgirl_backfill.py import."""
    pass


def fetch_html(url: str, retries: int = 3) -> str:
    """Fetch a URL using saved Cloudflare cookies via plain requests."""
    session = _get_session()
    for attempt in range(retries):
        try:
            resp = session.get(url, timeout=(15, 45))
            resp.raise_for_status()
            return resp.text
        except Exception as e:
            if attempt < retries - 1:
                wait = 2 ** attempt + random.uniform(0, 1)
                logging.warning(f"  fetch_html error ({e}), retrying in {wait:.1f}s: {url}")
                time.sleep(wait)
            else:
                logging.error(f"  fetch_html failed after {retries} attempts: {url}")
    return ""


# ─────────────────────────────────────────────────────────────────────────────
#  LISTING PAGE PARSING
# ─────────────────────────────────────────────────────────────────────────────

def parse_listing_html(html: str) -> list[dict]:
    """
    Parse a category listing page.
    Returns list of {url, thumbnail_url, title} dicts.
    """
    soup = BeautifulSoup(html, "html.parser")
    results = []

    for card in soup.find_all("div", class_="post-item"):
        # Post URL — <a href="/post/...">
        post_url = ""
        a_tag = card.find("a", href=lambda h: h and h.startswith("/post/"))
        if a_tag:
            post_url = BASE_URL + a_tag["href"]

        # Thumbnail — img.lazyload-poster[data-src]
        thumbnail_url = ""
        poster_img = card.find("img", class_="lazyload-poster")
        if poster_img:
            thumbnail_url = poster_img.get("data-src") or poster_img.get("src", "")

        # Title — h3.block-title
        title = ""
        h3 = card.find("h3", class_="block-title")
        if h3:
            title = h3.get_text(strip=True)

        if post_url:
            results.append({"url": post_url, "thumbnail_url": thumbnail_url, "title": title})

    soup.decompose()
    return results


def get_next_page_url(html: str) -> str | None:
    """
    Return the next-page URL if a '次へ' (next) link exists, else None.
    Pagination href format: /upskirt?pg=2
    """
    soup = BeautifulSoup(html, "html.parser")
    for a in soup.find_all("a", href=True):
        if "次へ" in a.get_text():
            href = a["href"]
            soup.decompose()
            return BASE_URL + href if not href.startswith("http") else href
    soup.decompose()
    return None


def collect_category_posts(
    category: str,
    max_pages: int = 0,
    limit: int | None = None,
) -> list[dict]:
    """
    Walk all listing pages for a category.
    Returns list of {url, thumbnail_url, title}.

    max_pages=0 means unlimited.
    limit caps the total number of posts returned.
    """
    base = CATEGORY_URLS[category]
    all_posts: list[dict] = []
    page = 1

    while True:
        url = base if page == 1 else f"{base}?pg={page}"
        logging.info(f"  [{category}] listing page {page}: {url}")

        html = fetch_html(url)
        if not html:
            logging.warning(f"  [{category}] empty response at page {page}, stopping.")
            break

        posts = parse_listing_html(html)
        if not posts:
            logging.info(f"  [{category}] no post cards found at page {page}, stopping.")
            break

        all_posts.extend(posts)
        logging.info(f"  [{category}] +{len(posts)} posts (total: {len(all_posts)})")

        if limit and len(all_posts) >= limit:
            all_posts = all_posts[:limit]
            break

        next_url = get_next_page_url(html)
        if not next_url:
            logging.info(f"  [{category}] no next page, done.")
            break

        if max_pages and page >= max_pages:
            logging.info(f"  [{category}] reached max_pages={max_pages}, stopping.")
            break

        page += 1
        time.sleep(random.uniform(1.0, 2.5))

    return all_posts


# ─────────────────────────────────────────────────────────────────────────────
#  POST PAGE PARSING
# ─────────────────────────────────────────────────────────────────────────────

def parse_post_page(source_url: str, html: str, source: str) -> dict:
    """
    Parse a jgirl.co post page.
    Returns a dict with all scraped fields.
    'thumbnail_url' and 'images' contain original pictstore.net URLs at this stage.
    """
    soup = BeautifulSoup(html, "html.parser")

    # Title — skip the logo h1 (contains only an <img>, no text)
    title = "Unknown"
    for h1 in soup.find_all("h1"):
        text = h1.get_text(strip=True)
        if text and not h1.find("img"):
            title = text
            break

    # Post date — first <h2 class="text-dark"> e.g. "2026/02/13"
    post_date = None
    h2_date = soup.find("h2", class_="text-dark")
    if h2_date:
        date_text = h2_date.get_text(strip=True)
        try:
            post_date = datetime.strptime(date_text, "%Y/%m/%d")
        except ValueError:
            pass

    # Tags — <a href="/{source}/tag/..."> <span class="badge">TAG</span> </a>
    tags = []
    for a in soup.find_all("a", href=lambda h: h and "/tag/" in (h or "")):
        span = a.find("span", class_="badge")
        if span:
            text = span.get_text(strip=True)
            if text and text not in tags:
                tags.append(text)

    thumbnail_url = ""
    post_images = []    # static photos (photo posts): 1.jpg, 2.jpg, 3.jpg...
    preview_images = [] # video slideshow frames: mov1001.jpg, mov1002.jpg...

    # ── Photo post layout ────────────────────────────────────────────────────
    # div.side-scroll contains: poster.jpg (thumbnail) + 1.jpg, 2.jpg... (photos)
    side_scroll = soup.find("div", class_=lambda c: c and "side-scroll" in c)
    if side_scroll:
        for img in side_scroll.find_all("img"):
            src = img.get("src") or img.get("data-src", "")
            if not src or src.startswith("/img/"):
                continue
            classes = img.get("class", [])
            if "ms-1" in classes:
                # Numbered photo (1.jpg, 2.jpg...)
                if src not in post_images:
                    post_images.append(src)
            elif not thumbnail_url:
                # First img without ms-1 is the poster/thumbnail
                thumbnail_url = src

    # ── Video post layout ────────────────────────────────────────────────────
    # img.img-fluid.mx-auto is the poster; lazyload-preview are slideshow frames
    if not thumbnail_url:
        poster = soup.find(
            "img",
            class_=lambda c: c and "img-fluid" in c and "mx-auto" in c,
        )
        if poster:
            thumbnail_url = poster.get("data-src") or poster.get("src", "")

    # Video preview slideshow — scoped to first div.bgg-dark.post-item
    main_player = soup.find("div", class_=lambda c: c and "bgg-dark" in c and "post-item" in c)
    img_scope = main_player if main_player else soup
    for img in img_scope.find_all("img", class_="lazyload-preview"):
        src = img.get("data-src") or img.get("src", "")
        if src and src not in preview_images and not src.startswith("/img/"):
            preview_images.append(src)

    # Download links — <a rel="nofollow" href="...external...">
    download_links = []
    for a in soup.find_all("a", rel="nofollow"):
        href = a.get("href", "")
        if href and not href.startswith("/") and href not in download_links:
            download_links.append(href)

    soup.decompose()

    return {
        "source": source,
        "source_url": source_url,
        "title": title,
        "thumbnail_url": thumbnail_url,
        "post_images": post_images,       # photo post: 1.jpg, 2.jpg, 3.jpg...
        "images": preview_images,         # video post: mov1001.jpg, mov1002.jpg...
        "tags": tags,
        "original_download_links": download_links,
        "download_links": [],
        "download_status": "pending",
        "_post_date": post_date,
    }


# ─────────────────────────────────────────────────────────────────────────────
#  B2 UPLOAD HELPERS
# ─────────────────────────────────────────────────────────────────────────────

def upload_thumbnail(post_id: str, thumb_url: str) -> str:
    """
    Download thumb_url and upload to B2 as JGirls/{post_id}/thumbnail.jpg.
    Returns B2 public URL, or original URL on failure.
    """
    if not thumb_url:
        logging.warning(f"  upload_thumbnail: no thumbnail URL for post {post_id}")
        return thumb_url
    try:
        from storage import download_image
        from storage_b2 import upload_file_to_b2
    except ImportError:
        logging.warning("storage/storage_b2 not importable, skipping thumbnail upload")
        return thumb_url

    content = download_image(thumb_url)
    if not content:
        logging.warning(f"  Could not download thumbnail: {thumb_url}")
        return thumb_url

    b2_key = f"JGirls/{post_id}/thumbnail.jpg"
    new_url = upload_file_to_b2(content, b2_key, content_type="image/jpeg", optimize_images=True)
    return new_url or thumb_url


def upload_preview_images(post_id: str, image_urls: list[str]) -> list[str]:
    """
    Download and upload preview images to B2 under JGirls/{post_id}/preview/{n:04d}.jpg.
    Returns list of B2 public URLs (original URL kept on failure).
    """
    if not image_urls:
        return []
    try:
        from storage import download_image
        from storage_b2 import upload_file_to_b2
    except ImportError:
        logging.warning("storage/storage_b2 not importable, skipping preview upload")
        return image_urls

    b2_urls = []
    for idx, url in enumerate(image_urls):
        b2_key = f"JGirls/{post_id}/preview/{idx + 1:04d}.jpg"
        content = download_image(url)
        if not content:
            logging.warning(f"  Could not download preview {idx + 1}: {url}")
            b2_urls.append(url)
            continue
        new_url = upload_file_to_b2(content, b2_key, content_type="image/jpeg", optimize_images=True)
        b2_urls.append(new_url or url)
        time.sleep(0.2)

    return b2_urls


def upload_post_images(post_id: str, image_urls: list[str]) -> list[str]:
    """
    Download and upload photo-post images to B2 under JGirls/{post_id}/images/{n:04d}.jpg.
    These are the static gallery photos (1.jpg, 2.jpg, 3.jpg...) from photo-type posts.
    Returns list of B2 public URLs (original URL kept on failure).
    """
    if not image_urls:
        return []
    try:
        from storage import download_image
        from storage_b2 import upload_file_to_b2
    except ImportError:
        logging.warning("storage/storage_b2 not importable, skipping post image upload")
        return image_urls

    b2_urls = []
    for idx, url in enumerate(image_urls):
        b2_key = f"JGirls/{post_id}/images/{idx + 1:04d}.jpg"
        content = download_image(url)
        if not content:
            logging.warning(f"  Could not download post image {idx + 1}: {url}")
            b2_urls.append(url)
            continue
        new_url = upload_file_to_b2(content, b2_key, content_type="image/jpeg", optimize_images=True)
        b2_urls.append(new_url or url)
        time.sleep(0.2)

    return b2_urls
