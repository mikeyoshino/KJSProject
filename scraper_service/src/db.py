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
#  ASIANSCANDAL_POSTS TABLE HELPERS
# ──────────────────────────────────────────────

def check_asianscandal_post_exists(source_url: str) -> bool:
    if not supabase: return False
    response = supabase.table("asianscandal_posts").select("id").eq("source_url", source_url).execute()
    return len(response.data) > 0

def insert_asianscandal_post(post_data: dict) -> dict:
    if not supabase: return {}
    response = supabase.table("asianscandal_posts").insert(post_data).execute()
    return response.data[0] if response.data else {}

def upsert_asianscandal_post(post_data: dict) -> dict:
    if not supabase: return {}
    # Supabase upsert requires an 'on_conflict' column (source_url is unique)
    response = supabase.table("asianscandal_posts").upsert(post_data, on_conflict="source_url").execute()
    return response.data[0] if response.data else {}

def fetch_asianscandal_posts_missing_content(limit: int = 100, offset: int = 0) -> list:
    """Fetch posts where content_html is NULL or empty, returning id + source_url + title."""
    if not supabase: return []
    response = (
        supabase.table("asianscandal_posts")
        .select("id, source_url, title")
        .or_("content_html.is.null,content_html.eq.")
        .order("created_at", desc=False)
        .range(offset, offset + limit - 1)
        .execute()
    )
    return response.data or []

def fetch_all_asianscandal_posts(limit: int = 100, offset: int = 0) -> list:
    """Fetch all posts (regardless of content_html), returning id + source_url + title + content_html."""
    if not supabase: return []
    response = (
        supabase.table("asianscandal_posts")
        .select("id, source_url, title, content_html")
        .order("created_at", desc=False)
        .range(offset, offset + limit - 1)
        .execute()
    )
    return response.data or []

def update_asianscandal_content_html(post_id: str, content_html: str) -> bool:
    """Update content_html for a single post by id. Returns True on success."""
    if not supabase: return False
    response = (
        supabase.table("asianscandal_posts")
        .update({"content_html": content_html})
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
