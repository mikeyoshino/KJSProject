"""
asianscandal_rewrite.py
-----------------------
Re-scrapes content for asianscandal_posts rows where content_html is NULL/empty.

Pipeline per post:
  1. Fetch post rows with missing content_html from Supabase
  2. Re-scrape original page via source_url
  3. Pre-clean HTML: remove Rapidgator/download wrapper blocks, replace site branding
  4. Upload all <img> tags to B2; rewrite src attributes to B2 URLs
  5. AI rewrite via Ollama (removes remaining references, rewrites RG promos as "direct download")
  6. UPDATE content_html in Supabase

Usage:
    cd scraper_service/src
    python asianscandal_rewrite.py                  # process posts with missing content_html
    python asianscandal_rewrite.py --all            # rewrite all posts, skip already-rewritten
    python asianscandal_rewrite.py --all --force    # rewrite everything, no skipping
    python asianscandal_rewrite.py --limit 50       # first 50
    python asianscandal_rewrite.py --dry-run        # scrape + clean, no DB write
    python asianscandal_rewrite.py --no-ai          # skip Ollama step
    python asianscandal_rewrite.py --delay 2.0      # seconds between posts
"""

import logging
import time
import sys
import os
import re
import hashlib
import argparse
import gc
import threading
from concurrent.futures import ThreadPoolExecutor, as_completed

sys.path.insert(0, os.path.dirname(__file__))

from bs4 import BeautifulSoup
from asianscandal_scraper import fetch_html, parse_post_page
from storage import download_image
from storage_b2 import upload_image_to_b2, b2_key_for_url
from ai_rewriter import rewrite_content, check_ollama_available
from db import fetch_asianscandal_posts_not_rewritten

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s - %(levelname)s - %(message)s",
    handlers=[logging.StreamHandler()],
)

# Ollama runs one inference at a time — serialise AI calls across threads
_ollama_sem = threading.Semaphore(1)

# ─── Block patterns to remove entirely ──────────────────────────────────────
_BLOCK_TAGS = {"div", "p", "figure", "section", "aside", "blockquote", "li"}
_REMOVE_PATTERNS = re.compile(
    r"rapidgator|download from|click (?:here )?to download|download link|download now"
    r"|get (?:the )?file|free download|grab (?:the )?file|mirror link"
    r"|important update:",
    re.IGNORECASE,
)
# Used for Pass 1 block removal — only Rapidgator links warrant removing the whole block.
# asianscandal.net internal links are handled in Pass 2 (unwrap) and Pass 3 (text replace).
_REMOVE_BLOCK_DOMAINS = re.compile(r"rapidgator\.net", re.IGNORECASE)
# Used for Pass 2 link unwrapping — strip both external hosts from <a> tags
_REMOVE_DOMAINS = re.compile(r"rapidgator\.net|asianscandal\.net", re.IGNORECASE)

# ─── Text replacement map ────────────────────────────────────────────────────
_TEXT_REPLACEMENTS = [
    (re.compile(r"asianscandal\.net", re.IGNORECASE), "scandal69.com"),
    (re.compile(r"Asian\s*Scandal", re.IGNORECASE), "Scandal69"),
]


def _should_remove_block(tag) -> bool:
    """Return True if this tag (or any descendant) matches removal criteria."""
    # Check for Rapidgator links — only these warrant removing the whole block
    for a in tag.find_all("a", href=True):
        if _REMOVE_BLOCK_DOMAINS.search(a["href"]):
            return True

    # Check visible text for download-promo patterns
    text = tag.get_text(" ", strip=True)
    if _REMOVE_PATTERNS.search(text):
        return True

    return False


def clean_html(html: str) -> str:
    """
    BeautifulSoup pre-clean:
    - Remove DIRECT CHILDREN of the content root that are Rapidgator/download promo blocks.
      (Never removes the root container itself, preventing total content wipe-out.)
    - Unwrap <a> tags pointing to asianscandal.net or rapidgator.net (keep inner text)
    - Replace site name text nodes
    """
    soup = BeautifulSoup(html, "html.parser")

    # Locate the outermost content wrapper so we only remove its direct children,
    # not the wrapper itself. This prevents nuking all content when the wrapper's
    # aggregate text happens to match a removal pattern.
    content_root = soup.find("div") or soup

    # Pass 1: Remove DIRECT CHILDREN of the content root that are promo blocks
    for tag in list(content_root.children):
        if not hasattr(tag, "name") or tag.name not in _BLOCK_TAGS:
            continue
        if _should_remove_block(tag):
            logging.debug(f"  Removing block: <{tag.name}> — {tag.get_text(' ', strip=True)[:80]}")
            tag.decompose()

    # Pass 2: Unwrap remaining bad <a> links (replace with link text)
    for a in list(soup.find_all("a", href=True)):
        if _REMOVE_DOMAINS.search(a.get("href", "")):
            a.replace_with(a.get_text())

    # Pass 3: Replace text in all text nodes
    for text_node in soup.find_all(string=True):
        new_text = text_node
        for pattern, replacement in _TEXT_REPLACEMENTS:
            new_text = pattern.sub(replacement, new_text)
        if new_text != text_node:
            text_node.replace_with(new_text)

    try:
        return str(soup)
    finally:
        soup.decompose()


def _detect_image_ext(content: bytes) -> str:
    """Detect image extension from bytes using Pillow. Falls back to 'jpg'."""
    try:
        from PIL import Image
        import io
        img = Image.open(io.BytesIO(content))
        fmt = img.format  # e.g. 'JPEG', 'PNG', 'GIF', 'WEBP'
        img.close()
        if fmt:
            return {"JPEG": "jpg"}.get(fmt, fmt.lower())
    except Exception:
        pass
    return "jpg"


def upload_images_to_b2(html: str, post_id: str = None) -> str:
    """
    Find all <img> tags, download each image, upload to B2, and rewrite src.
    If post_id is provided, images are stored at posts/{post_id}/images/{increment}.{ext}
    with increment = 1, 2, 3... (e.g., posts/ed019532-75ac-438a-b4ab-24ed1a9ad215/images/1.jpg)
    If post_id is None, images are stored at posts/images/{md5}.ext (legacy)
    Returns updated HTML string. soup.decompose() is called before returning
    so BeautifulSoup's internal tree is freed immediately.
    """
    b2_base = os.getenv("B2_PUBLIC_BASE_URL", "")
    soup = BeautifulSoup(html, "html.parser")

    try:
        img_counter = 1 if post_id else 0
        for img in soup.find_all("img"):
            src = (
                img.get("src")
                or img.get("data-src")
                or img.get("data-lazy-src", "")
            )
            if not src or src.startswith("data:"):
                continue

            # Skip if already a B2 URL
            if b2_base and b2_base in src:
                continue

            content = download_image(src)
            if not content:
                logging.warning(f"  Could not download image: {src}")
                continue

            # Construct B2 key with post_id if available
            if post_id:
                ext = _detect_image_ext(content)
                b2_key = f"posts/{post_id}/images/{img_counter}.{ext}"
                img_counter += 1
            else:
                b2_key = b2_key_for_url(src)

            new_url = upload_image_to_b2(content, b2_key)
            del content  # release image bytes immediately after upload

            if new_url:
                img["src"] = new_url
                if img.parent and img.parent.name == "a":
                    img.parent["href"] = new_url
                for attr in ("data-src", "data-lazy-src"):
                    if img.get(attr):
                        del img[attr]
            else:
                logging.warning(f"  B2 upload failed for: {src} — keeping original URL")

            time.sleep(0.3)

        return str(soup)
    finally:
        soup.decompose()


def is_already_rewritten(post: dict) -> bool:
    """
    A post is considered already rewritten if its content_html contains a B2 image URL.
    """
    content = post.get("content_html") or ""
    b2_base = os.getenv("B2_PUBLIC_BASE_URL", "")
    if b2_base and f"{b2_base}/scandal69/" in content:
        return True
    return "scandal69/" in content and "backblazeb2.com" in content


def process_post(post: dict, idx: int = 0, dry_run: bool = False, use_ai: bool = True) -> bool:
    """
    Full pipeline for a single post dict (must have 'id' and 'source_url').
    Returns True on success.  Thread-safe: Ollama calls are serialised via _ollama_sem.
    Memory-safe: large intermediate strings are released explicitly.
    """
    from db import update_asianscandal_content_html

    post_id    = post["id"]
    source_url = post["source_url"]
    title      = post.get("title", "")
    tag        = f"[{idx}]"

    logging.info(f"{tag} {source_url}")
    logging.info(f"{tag}   Fetching...")

    html = fetch_html(source_url)
    if not html:
        logging.warning(f"{tag}   -> FAIL (could not fetch page)")
        return False

    # Parse — hold only content_html, drop the full page HTML immediately
    data = parse_post_page(source_url, html)
    del html
    content_html = data.get("content_html", "")
    del data
    if not content_html:
        logging.warning(f"{tag}   -> FAIL (no content_html parsed)")
        return False
    logging.info(f"{tag}   Scraped: {len(content_html)} chars")

    # Step 1: Clean HTML (returns new string, old is released)
    content_html = clean_html(content_html)
    logging.info(f"{tag}   After clean: {len(content_html)} chars")
    if not content_html.strip():
        logging.warning(f"{tag}   -> FAIL (clean_html returned empty)")
        return False

    # Step 2: Upload images to B2 (returns new string)
    logging.info(f"{tag}   Uploading images to B2...")
    content_html = upload_images_to_b2(content_html)
    logging.info(f"{tag}   After B2: {len(content_html)} chars")

    # Step 3: AI rewrite — serialised so Ollama isn't flooded
    if use_ai:
        logging.info(f"{tag}   Waiting for Ollama slot...")
        with _ollama_sem:
            logging.info(f"{tag}   Rewriting via Ollama...")
            content_html = rewrite_content(title, content_html)
        logging.info(f"{tag}   After Ollama: {len(content_html)} chars")

    if dry_run:
        logging.info(f"{tag}   [DRY RUN] Would update id={post_id}")
        logging.info(f"{tag}   Preview: {content_html[:400]}")
        del content_html
        return True

    # Step 4: Save to Supabase
    result = update_asianscandal_content_html(post_id, content_html)
    del content_html
    if result:
        logging.info(f"{tag}   -> SUCCESS")
        return True
    else:
        logging.warning(f"{tag}   -> FAIL (DB update failed)")
        return False


def run(limit: int = None, dry_run: bool = False, use_ai: bool = True, workers: int = 4):
    fetch_fn = fetch_asianscandal_posts_not_rewritten

    logging.info("=" * 60)
    logging.info("AsianScandal Content Rewrite")
    logging.info(f"  Mode:    Posts not yet rewritten (rewritten_at IS NULL)")
    logging.info(f"  Workers: {workers}")
    if dry_run:
        logging.info("  DB writes: DRY RUN (disabled)")
    if not use_ai:
        logging.info("  AI rewrite: DISABLED")
    logging.info("=" * 60)

    if use_ai:
        if check_ollama_available():
            logging.info("Ollama is available.")
        else:
            logging.warning(
                "Ollama is NOT reachable. Will proceed with clean-only (no AI rewrite). "
                "Start Ollama on your Mac and re-run, or pass --no-ai to suppress this warning."
            )
            use_ai = False

    batch_size = 100
    offset = 0
    total_ok = 0
    total_fail = 0
    total_processed = 0

    with ThreadPoolExecutor(max_workers=workers) as executor:
        while True:
            posts = fetch_fn(limit=batch_size, offset=offset)
            if not posts:
                break

            # Trim to the remaining limit
            if limit:
                remaining = limit - total_processed
                posts = posts[:remaining]

            # Submit the whole batch concurrently
            futures = {
                executor.submit(
                    process_post, post,
                    total_processed + i + 1,
                    dry_run, use_ai
                ): post
                for i, post in enumerate(posts)
            }
            total_processed += len(posts)

            for future in as_completed(futures):
                post = futures[future]
                try:
                    ok = future.result()
                    if ok:
                        total_ok += 1
                    else:
                        total_fail += 1
                except Exception as e:
                    logging.error(f"  -> UNEXPECTED ERROR for {post['source_url']}: {e}")
                    total_fail += 1

            gc.collect()

            if limit and total_processed >= limit:
                break

            offset += batch_size

    logging.info("=" * 60)
    logging.info(f"Done. OK={total_ok}  Failed={total_fail}  Total={total_processed}")
    logging.info("=" * 60)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Rewrite AsianScandal post content")
    parser.add_argument("--limit", type=int, default=None, help="Max posts to process")
    parser.add_argument("--workers", type=int, default=4, help="Parallel workers (default 4)")
    parser.add_argument("--dry-run", action="store_true", help="Scrape and clean but do not write to DB")
    parser.add_argument("--no-ai", action="store_true", help="Skip Ollama AI rewrite step")
    args = parser.parse_args()

    run(
        limit=args.limit,
        dry_run=args.dry_run,
        use_ai=not args.no_ai,
        workers=args.workers,
    )
