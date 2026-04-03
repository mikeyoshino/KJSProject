import os
import requests
import logging
import io
import time
from PIL import Image
from supabase import create_client, Client
from dotenv import load_dotenv

load_dotenv()
url: str = os.getenv("SUPABASE_URL", "")
key: str = os.getenv("SUPABASE_KEY", "")

supabase: Client = create_client(url, key) if url and key else None

def download_image(img_url: str) -> bytes:
    retries = 3
    for attempt in range(retries):
        try:
            # Increased timeout to 30s to handle slow image hosts
            response = requests.get(img_url, timeout=30)
            response.raise_for_status()
            return response.content
        except Exception as e:
            logging.warning(f"Attempt {attempt+1}/{retries} failed for {img_url}: {e}")
            if attempt < retries - 1:
                time.sleep(1) # Short wait before retry
            else:
                logging.error(f"Failed to download image after {retries} attempts: {img_url}")
    return None

def optimize_image(content: bytes, max_width: int = 1400) -> bytes:
    if not content:
        return None
    try:
        img = Image.open(io.BytesIO(content))
        # Convert RGBA to RGB if necessary for JPEG
        if img.mode in ("RGBA", "P"):
            img = img.convert("RGB")
        
        # Only resize if larger than 1400px
        if img.width > max_width:
            w_percent = (max_width / float(img.width))
            h_size = int((float(img.height) * float(w_percent)))
            img = img.resize((max_width, h_size), Image.Resampling.LANCZOS)
        
        output = io.BytesIO()
        img.save(output, format="JPEG", quality=85, optimize=True)
        return output.getvalue()
    except Exception as e:
        logging.error(f"Failed to optimize image: {e}")
        return None

def upload_to_supabase(content: bytes, filename: str) -> str:
    if not supabase or not content:
        return None
    
    # Optimize before uploading
    optimized_content = optimize_image(content)
    if not optimized_content:
        return None
    
    bucket_name = "images"
    try:
        # Upload to Supabase Storage - path is just the filename
        supabase.storage.from_(bucket_name).upload(
            path=filename,
            file=optimized_content,
            file_options={"content-type": "image/jpeg"}
        )
        
        # Get public URL
        return supabase.storage.from_(bucket_name).get_public_url(filename)
    except Exception as e:
        # Check if file already exists
        if "already exists" in str(e).lower():
            return supabase.storage.from_(bucket_name).get_public_url(filename)
        logging.error(f"Failed to upload {filename} to Supabase: {e}")
        return None
