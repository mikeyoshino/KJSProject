import os
from supabase import create_client, Client
from dotenv import load_dotenv

load_dotenv()
url: str = os.getenv("SUPABASE_URL", "")
key: str = os.getenv("SUPABASE_KEY", "")

supabase: Client = create_client(url, key) if url and key else None

def check_post_exists(source_url: str) -> bool:
    if not supabase: return False
    response = supabase.table("posts").select("id").eq("source_url", source_url).execute()
    return len(response.data) > 0

def insert_post(post_data: dict) -> dict:
    if not supabase: return {}
    # Use the provided created_at if it exists, otherwise Supabase defaults to NOW()
    response = supabase.table("posts").insert(post_data).execute()
    return response.data[0] if response.data else {}

# ──────────────────────────────────────────────
#  ASIANSCANDAL HELPERS — now query `posts` table
#  with source_name='AsianScandal' filter.
#  Function signatures preserved so callers need no changes.
# ──────────────────────────────────────────────

_AS = "AsianScandal"

def check_asianscandal_post_exists(source_url: str) -> bool:
    """Return False for missing or incomplete placeholder posts (empty content_html)."""
    if not supabase: return False
    response = (supabase.table("posts")
        .select("id, content_html")
        .eq("source_url", source_url)
        .eq("source_name", _AS)
        .execute())
    if not response.data:
        return False
    return bool(response.data[0].get("content_html"))

def insert_asianscandal_post(post_data: dict) -> dict:
    post_data = {**post_data, "source_name": _AS}
    if not supabase: return {}
    response = supabase.table("posts").insert(post_data).execute()
    return response.data[0] if response.data else {}

def upsert_asianscandal_post(post_data: dict) -> dict:
    post_data = {**post_data, "source_name": _AS}
    if not supabase: return {}
    response = supabase.table("posts").upsert(post_data, on_conflict="source_url").execute()
    return response.data[0] if response.data else {}

def fetch_asianscandal_posts_missing_content(limit: int = 100, offset: int = 0) -> list:
    """Fetch AsianScandal posts where content_html is NULL or empty."""
    if not supabase: return []
    response = (supabase.table("posts")
        .select("id, source_url, title")
        .eq("source_name", _AS)
        .or_("content_html.is.null,content_html.eq.")
        .order("created_at", desc=False)
        .range(offset, offset + limit - 1)
        .execute())
    return response.data or []

def fetch_asianscandal_posts_not_rewritten(limit: int = 100, offset: int = 0) -> list:
    """Fetch AsianScandal posts where rewritten_at IS NULL."""
    if not supabase: return []
    response = (supabase.table("posts")
        .select("id, source_url, title")
        .eq("source_name", _AS)
        .is_("rewritten_at", "null")
        .order("created_at", desc=False)
        .range(offset, offset + limit - 1)
        .execute())
    return response.data or []

def fetch_asianscandal_posts_missing_rg_urls(limit: int = 100, offset: int = 0) -> list:
    """Fetch AsianScandal posts where original_rapidgator_url is empty."""
    if not supabase: return []
    response = (supabase.table("posts")
        .select("id, source_url")
        .eq("source_name", _AS)
        .eq("download_status", "pending")
        .eq("original_rapidgator_url", "{}")
        .order("created_at", desc=False)
        .range(offset, offset + limit - 1)
        .execute())
    return response.data or []

def update_asianscandal_rg_urls(post_id: str, rg_urls: list[str]) -> bool:
    return update_post_rg_urls(post_id, rg_urls)

def reset_failed_downloads_asianscandal() -> int:
    """Reset AsianScandal download_status='failed' rows back to 'pending'."""
    if not supabase: return 0
    response = (supabase.table("posts")
        .update({"download_status": "pending"})
        .eq("source_name", _AS)
        .eq("download_status", "failed")
        .execute())
    return len(response.data) if response.data else 0

def fetch_pending_download_posts_asianscandal(limit: int = 50, offset: int = 0) -> list:
    """Fetch AsianScandal posts where download_status='pending'."""
    if not supabase: return []
    response = (supabase.table("posts")
        .select("id, source_url, original_rapidgator_url")
        .eq("source_name", _AS)
        .eq("download_status", "pending")
        .order("created_at", desc=False)
        .range(offset, offset + limit - 1)
        .execute())
    return response.data or []

def update_download_status_asianscandal(
    post_id: str,
    status: str,
    our_download_link: list[str] | None = None,
) -> bool:
    return update_post_download_status(post_id, status, our_download_link)

# ──────────────────────────────────────────────
#  POSTS TABLE HELPERS (buzz69)
# ──────────────────────────────────────────────

def upsert_post(post_data: dict) -> dict:
    """Upsert a post into the posts table. Returns the upserted row dict."""
    if not supabase: return {}
    response = supabase.table("posts").upsert(post_data, on_conflict="source_url").execute()
    return response.data[0] if response.data else {}

def check_post_complete(source_url: str) -> bool:
    """Return True only if a post exists AND has non-empty content_html (not a broken placeholder)."""
    if not supabase: return False
    response = supabase.table("posts").select("id, content_html").eq("source_url", source_url).execute()
    if not response.data:
        return False
    row = response.data[0]
    return bool(row.get("content_html"))

def fetch_posts_for_image_migration(limit: int = 100, offset: int = 0) -> list:
    """
    Fetch posts whose content_html or thumbnail_url still contain Supabase storage URLs.
    Returns list of dicts with id, source_url, content_html, thumbnail_url.
    """
    if not supabase: return []
    response = (
        supabase.table("posts")
        .select("id, source_url, content_html, thumbnail_url, title")
        .or_("content_html.ilike.%supabase.co%,thumbnail_url.ilike.%supabase.co%")
        .order("created_at", desc=False)
        .range(offset, offset + limit - 1)
        .execute()
    )
    return response.data or []

def fetch_posts_pending_download(limit: int = 50, offset: int = 0) -> list:
    """Fetch posts where download_status='pending' (with or without RG URLs).
    Posts with empty original_rapidgator_url will be re-scraped by migrate_downloads."""
    if not supabase: return []
    response = (
        supabase.table("posts")
        .select("id, source_url, original_rapidgator_url")
        .eq("download_status", "pending")
        .order("created_at", desc=False)
        .range(offset, offset + limit - 1)
        .execute()
    )
    return response.data or []

def reset_failed_downloads(source_name: str = "Buzz69") -> int:
    """Reset failed posts that have RG URLs but no our_download_link back to pending."""
    if not supabase: return 0
    response = (
        supabase.table("posts")
        .update({"download_status": "pending"})
        .eq("download_status", "failed")
        .eq("source_name", source_name)
        .execute()
    )
    return len(response.data) if response.data else 0


def reset_empty_download_links_posts() -> int:
    """
    Reset posts where download_status='done' but our_download_link is empty/null/[""]
    back to 'pending' so they are retried.
    """
    if not supabase: return 0
    # Reset done rows with null our_download_link
    r1 = (
        supabase.table("posts")
        .update({"download_status": "pending"})
        .eq("download_status", "done")
        .is_("our_download_link", "null")
        .execute()
    )
    # Reset done rows with empty-array our_download_link ([""])
    r2 = (
        supabase.table("posts")
        .update({"download_status": "pending"})
        .eq("download_status", "done")
        .eq("our_download_link", '{""}')
        .execute()
    )
    count = (len(r1.data) if r1.data else 0) + (len(r2.data) if r2.data else 0)
    return count

def fetch_posts_needing_rewrite(source_name: str = "Buzz69", limit: int = 100, offset: int = 0) -> list:
    """
    Fetch posts whose content_html still contains old brand names that need rewriting.
    Matches buzz69.com, Buzz69, asianscandal.net, AsianScandal, Asian Scandal.
    """
    if not supabase: return []
    response = (
        supabase.table("posts")
        .select("id, source_url, title, content_html")
        .eq("source_name", source_name)
        .or_(
            "content_html.ilike.%buzz69.com%,"
            "content_html.ilike.%Buzz69%,"
            "content_html.ilike.%asianscandal.net%,"
            "content_html.ilike.%AsianScandal%,"
            "content_html.ilike.%Asian Scandal%"
        )
        .order("created_at", desc=False)
        .range(offset, offset + limit - 1)
        .execute()
    )
    return response.data or []


def update_post_content(
    post_id: str,
    content_html: str,
    thumbnail_url: str | None = None,
    rewritten_at: str | None = None,
) -> bool:
    """Update content_html (and optionally thumbnail_url) for a post.
    rewritten_at is accepted for API compatibility but ignored — posts table has no such column."""
    if not supabase: return False
    payload: dict = {"content_html": content_html}
    if thumbnail_url is not None:
        payload["thumbnail_url"] = thumbnail_url
    response = supabase.table("posts").update(payload).eq("id", post_id).execute()
    return bool(response.data)

def update_post_download_status(
    post_id: str,
    status: str,
    our_download_link: list[str] | None = None,
) -> bool:
    """Update download_status (and optionally our_download_link) for a post."""
    if not supabase: return False
    payload: dict = {"download_status": status}
    if our_download_link is not None:
        payload["our_download_link"] = our_download_link
    response = supabase.table("posts").update(payload).eq("id", post_id).execute()
    return bool(response.data)

def update_post_rg_urls(post_id: str, rg_urls: list[str]) -> bool:
    """Update original_rapidgator_url for a post."""
    if not supabase: return False
    response = (
        supabase.table("posts")
        .update({"original_rapidgator_url": rg_urls})
        .eq("id", post_id)
        .execute()
    )
    return bool(response.data)

# ──────────────────────────────────────────────
#  JGIRL_POSTS TABLE HELPERS
# ──────────────────────────────────────────────

def check_jgirl_post_exists(source_url: str) -> bool:
    if not supabase: return False
    response = supabase.table("jgirl_posts").select("id").eq("source_url", source_url).execute()
    return len(response.data) > 0

def insert_jgirl_post(post_data: dict) -> dict:
    """Insert stub row; returns dict with auto-generated 'id' UUID."""
    if not supabase: return {}
    response = supabase.table("jgirl_posts").insert(post_data).execute()
    return response.data[0] if response.data else {}

def update_jgirl_post(post_id: str, updates: dict) -> bool:
    if not supabase: return False
    response = supabase.table("jgirl_posts").update(updates).eq("id", post_id).execute()
    return bool(response.data)

def fetch_jgirl_posts_for_download(limit: int = 50, offset: int = 0) -> list:
    """Fetch posts where download_status='pending' and original_download_links is not empty."""
    if not supabase: return []
    response = (
        supabase.table("jgirl_posts")
        .select("id, source_url, title, original_download_links")
        .eq("download_status", "pending")
        .neq("original_download_links", "{}")
        .order("created_at", desc=False)
        .range(offset, offset + limit - 1)
        .execute()
    )
    return response.data or []

def fetch_jgirl_posts_pending(limit: int = 100, offset: int = 0) -> list:
    if not supabase: return []
    response = (
        supabase.table("jgirl_posts")
        .select("id, source_url, title, original_download_links")
        .eq("download_status", "pending")
        .order("created_at", desc=False)
        .range(offset, offset + limit - 1)
        .execute()
    )
    return response.data or []


def update_asianscandal_content_html(post_id: str, content_html: str) -> bool:
    """Update content_html and stamp rewritten_at for a single AsianScandal post."""
    from datetime import datetime, timezone
    return update_post_content(
        post_id,
        content_html,
        rewritten_at=datetime.now(timezone.utc).isoformat(),
    )
