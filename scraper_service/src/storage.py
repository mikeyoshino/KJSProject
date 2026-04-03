import os
import requests
import logging
from supabase import create_client, Client
from dotenv import load_dotenv

load_dotenv()
url: str = os.getenv("SUPABASE_URL", "")
key: str = os.getenv("SUPABASE_KEY", "")

supabase: Client = create_client(url, key) if url and key else None

def download_image(img_url: str) -> bytes:
    try:
        response = requests.get(img_url, timeout=10)
        response.raise_for_status()
        return response.content
    except Exception as e:
        logging.error(f"Failed to download image {img_url}: {e}")
        return None

def upload_to_supabase(content: bytes, filename: str) -> str:
    if not supabase or not content:
        return None
    
    bucket_name = "images"
    try:
        # Upload to Supabase Storage - path is just the filename
        supabase.storage.from_(bucket_name).upload(
            path=filename,
            file=content,
            file_options={"content-type": "image/jpeg"} # Assuming JPEG for now
        )
        
        # Get public URL
        return supabase.storage.from_(bucket_name).get_public_url(filename)
    except Exception as e:
        # Check if file already exists
        if "already exists" in str(e).lower():
            return supabase.storage.from_(bucket_name).get_public_url(filename)
        logging.error(f"Failed to upload {filename} to Supabase: {e}")
        return None
