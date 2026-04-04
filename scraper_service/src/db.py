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
