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

sys.path.insert(0, os.path.dirname(__file__))

from bs4 import BeautifulSoup
from asianscandal_scraper import fetch_html, parse_post_page
from storage import download_image
from storage_b2 import upload_image_to_b2, b2_key_for_url
from ai_rewriter import rewrite_content, check_ollama_available
from db import fetch_asianscandal_posts_missing_content, fetch_all_asianscandal_posts

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s - %(levelname)s - %(message)s",
    handlers=[logging.StreamHandler()],
)

# ─── Block patterns to remove entirely ──────────────────────────────────────
_BLOCK_TAGS = {"div", "p", "figure", "section", "aside", "blockquote", "li"}
_REMOVE_PATTERNS = re.compile(
    r"rapidgator|download from|click (?:here )?to download|download link|download now"
    r"|get (?:the )?file|free download|grab (?:the )?file|mirror link",
    re.IGNORECASE,
)
_REMOVE_DOMAINS = re.compile(r"rapidgator\.net|asianscandal\.net", re.IGNORECASE)

# ─── Text replacement map ────────────────────────────────────────────────────
_TEXT_REPLACEMENTS = [
    (re.compile(r"asianscandal\.net", re.IGNORECASE), "scandal69.com"),
    (re.compile(r"Asian\s*Scandal", re.IGNORECASE), "Scandal69"),
]


def _should_remove_block(tag) -> bool:
    """Return True if this tag (or any descendant) matches removal criteria."""
    # Check for Rapidgator / download-domain links
    for a in tag.find_all("a", href=True):
        if _REMOVE_DOMAINS.search(a["href"]):
            return True

    # Check visible text for download-promo patterns
    text = tag.get_text(" ", strip=True)
    if _REMOVE_PATTERNS.search(text):
        return True

    return False


def clean_html(html: str) -> str:
    """
    BeautifulSoup pre-clean:
    - Remove entire block-level wrappers that contain Rapidgator links or download promos
    - Unwrap <a> tags pointing to asianscandal.net or rapidgator.net (keep inner text)
    - Replace site name text nodes
    """
    soup = BeautifulSoup(html, "html.parser")

    # Pass 1: Remove entire block wrappers that match removal criteria
    # Walk top-level blocks inside the content div
    for tag in list(soup.find_all(_BLOCK_TAGS)):
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

    result = str(soup)
    soup.decompose()
    return result


def upload_images_to_b2(html: str) -> str:
    """
    Find all <img> tags, download each image, upload to B2, and rewrite src.
    Returns updated HTML string.
    """
    soup = BeautifulSoup(html, "html.parser")
    imgs = soup.find_all("img")

    for img in imgs:
        src = (
            img.get("src")
            or img.get("data-src")
            or img.get("data-lazy-src", "")
        )
        if not src or src.startswith("data:"):
            continue

        # Skip if already a B2 URL
        b2_base = os.getenv("B2_PUBLIC_BASE_URL", "")
        if b2_base and b2_base in src:
            continue

        filename = b2_key_for_url(src)
        content = download_image(src)
        if not content:
            logging.warning(f"  Could not download image: {src}")
            continue

        new_url = upload_image_to_b2(content, filename)
        if new_url:
            img["src"] = new_url
            # Update parent <a> href if it wraps only this image
            if img.parent and img.parent.name == "a":
                img.parent["href"] = new_url
            # Clear lazy-load attributes to avoid duplicate loads
            for attr in ("data-src", "data-lazy-src"):
                if img.get(attr):
                    del img[attr]
        else:
            logging.warning(f"  B2 upload failed for: {src} — keeping original URL")

        time.sleep(0.3)

    result = str(soup)
    soup.decompose()
    return result


def is_already_rewritten(post: dict) -> bool:
    """
    A post is considered already rewritten if its content_html contains a B2 image URL
    (i.e. the scandal69/ path prefix we use for uploaded images).
    """
    content = post.get("content_html") or ""
    b2_base = os.getenv("B2_PUBLIC_BASE_URL", "")
    if b2_base and f"{b2_base}/scandal69/" in content:
        return True
    # Fallback: check for the path pattern even without the full base URL
    return "scandal69/" in content and "backblazeb2.com" in content


def process_post(post: dict, dry_run: bool = False, use_ai: bool = True) -> bool:
    """
    Full pipeline for a single post dict (must have 'id' and 'source_url').
    Returns True on success.
    """
    from db import update_asianscandal_content_html

    post_id = post["id"]
    source_url = post["source_url"]
    title = post.get("title", "")

    logging.info(f"  Fetching: {source_url}")
    html = fetch_html(source_url)
    if not html:
        logging.warning("  -> FAIL (could not fetch page)")
        return False

    # Parse content div
    data = parse_post_page(source_url, html)
    content_html = data.get("content_html", "")
    if not content_html:
        logging.warning("  -> FAIL (no content_html parsed from page)")
        return False

    # Step 1: Clean HTML (remove blocks, fix branding)
    logging.info("  Cleaning HTML...")
    content_html = clean_html(content_html)

    # Step 2: Upload images to B2
    logging.info("  Uploading images to B2...")
    content_html = upload_images_to_b2(content_html)

    # Step 3: AI rewrite
    if use_ai:
        logging.info("  Rewriting via Ollama...")
        content_html = rewrite_content(title, content_html)

    if dry_run:
        logging.info(f"  [DRY RUN] Would update id={post_id}")
        logging.info(f"  Preview (first 400 chars): {content_html[:400]}")
        return True

    # Step 4: Save to Supabase
    result = update_asianscandal_content_html(post_id, content_html)
    if result:
        logging.info("  -> SUCCESS")
        return True
    else:
        logging.warning("  -> FAIL (DB update failed)")
        return False


def run(limit: int = None, delay: float = 1.5, dry_run: bool = False, use_ai: bool = True, all_posts: bool = False, force: bool = False):
    fetch_fn = fetch_all_asianscandal_posts if all_posts else fetch_asianscandal_posts_missing_content

    logging.info("=" * 60)
    logging.info("AsianScandal Content Rewrite")
    logging.info(f"  Mode: {'ALL posts' if all_posts else 'Missing content_html only'}")
    logging.info(f"  Skip already-rewritten: {'NO (--force)' if force else 'YES'}")
    if dry_run:
        logging.info("  DB writes: DRY RUN (disabled)")
    if not use_ai:
        logging.info("  AI rewrite: DISABLED")
    logging.info("=" * 60)

    if use_ai:
        if check_ollama_available():
            logging.info(f"Ollama is available.")
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

    while True:
        posts = fetch_fn(limit=batch_size, offset=offset)
        if not posts:
            break

        for post in posts:
            if limit and total_processed >= limit:
                break

            # Skip already-rewritten posts unless --force
            if not force and is_already_rewritten(post):
                logging.info(f"  SKIP (already rewritten): {post['source_url']}")
                continue

            total_processed += 1
            logging.info(f"[{total_processed}] {post['source_url']}")

            try:
                ok = process_post(post, dry_run=dry_run, use_ai=use_ai)
                if ok:
                    total_ok += 1
                else:
                    total_fail += 1
            except Exception as e:
                logging.error(f"  -> UNEXPECTED ERROR: {e}")
                total_fail += 1

            gc.collect()
            time.sleep(delay)

        if limit and total_processed >= limit:
            break

        offset += batch_size

    logging.info("=" * 60)
    logging.info(f"Done. OK={total_ok}  Failed={total_fail}  Total={total_processed}")
    logging.info("=" * 60)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Rewrite AsianScandal post content")
    parser.add_argument("--limit", type=int, default=None, help="Max posts to process")
    parser.add_argument("--delay", type=float, default=1.5, help="Seconds between posts (default 1.5)")
    parser.add_argument("--dry-run", action="store_true", help="Scrape and clean but do not write to DB")
    parser.add_argument("--no-ai", action="store_true", help="Skip Ollama AI rewrite step")
    parser.add_argument("--all", dest="all_posts", action="store_true", help="Rewrite ALL posts, not just missing content")
    parser.add_argument("--force", action="store_true", help="Re-process even already-rewritten posts (skips the B2 URL check)")
    args = parser.parse_args()

    run(
        limit=args.limit,
        delay=args.delay,
        dry_run=args.dry_run,
        use_ai=not args.no_ai,
        all_posts=args.all_posts,
        force=args.force,
    )
