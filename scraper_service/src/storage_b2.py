"""
storage_b2.py
-------------
Backblaze B2 image upload helper (S3-compatible via boto3).

Images are stored at: scandal69/{md5_hash}.jpg
Public URL:           {B2_PUBLIC_BASE_URL}/scandal69/{md5_hash}.jpg

Environment variables (add to scraper_service/.env):
  B2_APPLICATION_KEY_ID   - Backblaze key ID
  B2_APPLICATION_KEY      - Backblaze application key
  B2_BUCKET_NAME          - e.g. KJSProject
  B2_SERVICE_URL          - e.g. https://s3.us-east-005.backblazeb2.com
  B2_PUBLIC_BASE_URL      - e.g. https://f005.backblazeb2.com/file/KJSProject
"""

import os
import io
import logging
import hashlib
from PIL import Image
from dotenv import load_dotenv

load_dotenv()

_B2_KEY_ID      = os.getenv("B2_APPLICATION_KEY_ID", "")
_B2_KEY         = os.getenv("B2_APPLICATION_KEY", "")
_B2_BUCKET      = os.getenv("B2_BUCKET_NAME", "KJSProject")
_B2_SERVICE_URL = os.getenv("B2_SERVICE_URL", "https://s3.us-east-005.backblazeb2.com")
_B2_PUBLIC_BASE = os.getenv("B2_PUBLIC_BASE_URL", "").rstrip("/")

_s3 = None


def _get_client():
    global _s3
    if _s3 is not None:
        return _s3
    if not _B2_KEY_ID or not _B2_KEY:
        raise RuntimeError("B2_APPLICATION_KEY_ID / B2_APPLICATION_KEY not set in .env")
    import boto3
    _s3 = boto3.client(
        "s3",
        endpoint_url=_B2_SERVICE_URL,
        aws_access_key_id=_B2_KEY_ID,
        aws_secret_access_key=_B2_KEY,
    )
    return _s3


def _optimize(content: bytes, max_width: int = 1400) -> bytes:
    try:
        img = Image.open(io.BytesIO(content))
        if img.mode in ("RGBA", "P"):
            img = img.convert("RGB")
        if img.width > max_width:
            ratio = max_width / float(img.width)
            img = img.resize((max_width, int(img.height * ratio)), Image.Resampling.BICUBIC)
        buf = io.BytesIO()
        img.save(buf, format="JPEG", quality=85)
        img.close()
        return buf.getvalue()
    except Exception as e:
        logging.warning(f"Image optimize failed, using raw bytes: {e}")
        return content


def upload_image_to_b2(content: bytes, filename: str) -> str | None:
    """
    Upload image bytes to B2 under scandal69/{filename}.
    Returns the public URL, or None on failure.
    Idempotent: skips upload if the object already exists.
    """
    if not content:
        return None

    key = f"scandal69/{filename}"

    try:
        client = _get_client()

        # Idempotency check
        try:
            client.head_object(Bucket=_B2_BUCKET, Key=key)
            logging.info(f"  B2 already exists, skipping upload: {key}")
            return f"{_B2_PUBLIC_BASE}/{key}" if _B2_PUBLIC_BASE else None
        except client.exceptions.ClientError as e:
            if e.response["Error"]["Code"] != "404":
                raise

        optimized = _optimize(content)
        client.put_object(
            Bucket=_B2_BUCKET,
            Key=key,
            Body=optimized,
            ContentType="image/jpeg",
        )

        url = f"{_B2_PUBLIC_BASE}/{key}" if _B2_PUBLIC_BASE else None
        logging.info(f"  B2 upload OK: {key}")
        return url

    except Exception as e:
        logging.error(f"B2 upload failed for {key}: {e}")
        return None


def b2_key_for_url(img_url: str) -> str:
    """Derive the B2 filename (md5 hash + .jpg) from a source URL."""
    return f"{hashlib.md5(img_url.encode()).hexdigest()}.jpg"
