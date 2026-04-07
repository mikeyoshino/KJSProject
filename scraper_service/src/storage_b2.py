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


def upload_file_to_b2(
    content: bytes,
    b2_key: str,
    content_type: str = "application/octet-stream",
    optimize_images: bool = False,
) -> str | None:
    """
    Upload with a caller-supplied full key, e.g. 'JGirls/{postId}/preview/0001.jpg'.
    Unlike upload_image_to_b2() which always writes under scandal69/, this accepts any path.
    Idempotent: skips upload if the object already exists.
    Returns public URL or None on failure.
    """
    if not content:
        return None
    try:
        client = _get_client()
        try:
            client.head_object(Bucket=_B2_BUCKET, Key=b2_key)
            logging.info(f"  B2 already exists, skipping: {b2_key}")
            return f"{_B2_PUBLIC_BASE}/{b2_key}" if _B2_PUBLIC_BASE else None
        except client.exceptions.ClientError as e:
            if e.response["Error"]["Code"] != "404":
                raise

        body = content
        if optimize_images and content_type.startswith("image/"):
            body = _optimize(content)
            content_type = "image/jpeg"

        client.put_object(Bucket=_B2_BUCKET, Key=b2_key, Body=body, ContentType=content_type)
        url = f"{_B2_PUBLIC_BASE}/{b2_key}" if _B2_PUBLIC_BASE else None
        logging.info(f"  B2 upload OK: {b2_key}")
        return url
    except Exception as e:
        logging.error(f"B2 upload failed for {b2_key}: {e}")
        return None


def stream_upload_to_b2(
    response_raw,
    b2_key: str,
    content_type: str = "application/octet-stream",
) -> str | None:
    """
    Stream a file directly from an open HTTP response into B2 multipart upload.
    No full-file buffering — safe for 1GB+ files.

    Args:
        response_raw: requests response.raw (file-like object, decode_content=True)
        b2_key:       Full B2 object key, e.g. 'JGirls/{postId}/file.mp4'
        content_type: MIME type of the file

    Returns: public URL or None on failure.
    """
    try:
        from boto3.s3.transfer import TransferConfig
        client = _get_client()
        config = TransferConfig(
            multipart_threshold=100 * 1024 * 1024,
            multipart_chunksize=100 * 1024 * 1024,
            max_concurrency=4,
        )
        client.upload_fileobj(
            response_raw,
            Bucket=_B2_BUCKET,
            Key=b2_key,
            Config=config,
            ExtraArgs={"ContentType": content_type},
        )
        url = f"{_B2_PUBLIC_BASE}/{b2_key}" if _B2_PUBLIC_BASE else None
        logging.info(f"  B2 stream upload OK: {b2_key}")
        return url
    except Exception as e:
        logging.error(f"B2 stream upload failed for {b2_key}: {e}")
        return None


def delete_b2_folder(prefix: str) -> int:
    """
    Delete all B2 objects whose key starts with prefix.
    e.g. 'JGirls/abc-123/' deletes thumbnail, previews, and downloaded files.
    Returns count of deleted objects.
    """
    try:
        client = _get_client()
        deleted = 0
        paginator = client.get_paginator("list_objects_v2")
        for page in paginator.paginate(Bucket=_B2_BUCKET, Prefix=prefix):
            for obj in page.get("Contents", []):
                client.delete_object(Bucket=_B2_BUCKET, Key=obj["Key"])
                logging.info(f"  B2 deleted: {obj['Key']}")
                deleted += 1
        if deleted:
            logging.info(f"  B2 folder cleanup: {deleted} objects deleted under '{prefix}'")
        return deleted
    except Exception as e:
        logging.error(f"B2 folder delete failed for '{prefix}': {e}")
        return 0
