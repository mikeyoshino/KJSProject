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

# Mimic a browser to avoid bot-blocking/throttling
HEADERS = {'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36'}

def download_image(img_url: str) -> bytes:
    retries = 3
    for attempt in range(retries):
        try:
            # Use (connect_timeout, read_timeout) — fail fast on connection issues
            response = requests.get(img_url, headers=HEADERS, timeout=(10, 30))
            response.raise_for_status()
            return response.content
        except (requests.exceptions.ConnectionError, ConnectionResetError) as e:
            logging.warning(f"Attempt {attempt+1}/{retries} connection error for {img_url}: {e}")
            if attempt < retries - 1:
                time.sleep(2)  # Brief wait, don't hang forever
            else:
                logging.error(f"Connection permanently refused for: {img_url}")
        except Exception as e:
            logging.warning(f"Attempt {attempt+1}/{retries} failed for {img_url}: {e}")
            if attempt < retries - 1:
                time.sleep(1)
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
            img = img.resize((max_width, h_size), Image.Resampling.BICUBIC)
        
        output = io.BytesIO()
        img.save(output, format="JPEG", quality=85)
        res = output.getvalue()
        img.close()
        return res
    except Exception as e:
        logging.error(f"Failed to optimize image, using original content: {e}")
        return content

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
        err_msg = str(e).lower()
        if "already exists" in err_msg or "duplicate" in err_msg:
            logging.info(f"Image {filename} already exists in storage, skipping upload.")
            return supabase.storage.from_(bucket_name).get_public_url(filename)
        
        # Verbose logging for any other failure (like 400 Bad Request)
        logging.error("=" * 60)
        logging.error(f"SUPABASE UPLOAD FAILURE: {filename}")
        logging.error(f"Error Type: {type(e).__name__}")
        logging.error(f"Error Detail: {str(e)}")
        if hasattr(e, 'message'): logging.error(f"Error Message: {e.message}")
        if hasattr(e, 'response'): 
            try:
                logging.error(f"Response Status: {e.response.status_code}")
                logging.error(f"Response Text: {e.response.text}")
            except: pass
        logging.error("=" * 60)
        return None
