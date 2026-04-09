"""
storage_b2.py
-------------
Backblaze B2 image upload helper (S3-compatible via boto3).

Images are stored at: posts/images/{md5_hash}.jpg (legacy content images)
                    posts/{post_id}/images/{increment}.{ext} (backfill content images: 1.jpg, 2.jpg, ...)
                    posts/{post_id}/thumbnail.{ext} (backfill thumbnails)
Public URL:          {B2_PUBLIC_BASE_URL}/posts/...

B2 bucket is PRIVATE. Images are served through the Cloudflare Worker proxy
(cloudflare_b2_worker) which signs requests with B2 credentials at the edge.
Set B2_PUBLIC_BASE_URL to the worker's custom domain, not the direct B2 URL.

Environment variables (add to scraper_service/.env):
  B2_APPLICATION_KEY_ID   - Backblaze key ID
  B2_APPLICATION_KEY      - Backblaze application key
  B2_BUCKET_NAME          - e.g. KJSProject
  B2_SERVICE_URL          - e.g. https://s3.us-east-005.backblazeb2.com
  B2_PUBLIC_BASE_URL      - Cloudflare Worker domain, e.g. https://cdn.scandal69.com
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
    Upload image bytes to B2 under the given key.
    If filename is just a filename (no slashes), it's prefixed with posts/images/.
    Returns the public URL, or None on failure.
    Idempotent: skips upload if the object already exists.
    """
    if not content:
        return None

    # If filename has no path separator, prefix with posts/images/ (legacy behavior)
    if "/" not in filename:
        key = f"posts/images/{filename}"
    else:
        key = filename

    try:
        client = _get_client()

        # Idempotency check
        try:
            client.head_object(Bucket=_B2_BUCKET, Key=key)
            logging.info(f"  B2 already exists, skipping upload: {key}")
            return key
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

        logging.info(f"  B2 upload OK: {key}")
        return key

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
    Upload bytes to B2 at the given key (used for image uploads from scrapers).
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


def upload_disk_file_to_b2(file_path: str, b2_key: str) -> str | None:
    """
    Upload a large file from disk to B2 (used for zip/rar downloads).
    Uses multipart upload for files > 10 MB.
    Idempotent: skips upload if the object already exists.
    Returns b2_key on success, None on failure.
    """
    if not os.path.isfile(file_path):
        logging.error(f"upload_disk_file_to_b2: file not found: {file_path}")
        return None

    try:
        client = _get_client()

        try:
            client.head_object(Bucket=_B2_BUCKET, Key=b2_key)
            logging.info(f"  B2 already exists, skipping: {b2_key}")
            return b2_key
        except client.exceptions.ClientError as e:
            if e.response["Error"]["Code"] != "404":
                raise

        file_size = os.path.getsize(file_path)
        part_size = 10 * 1024 * 1024  # 10 MB parts for multipart

        if file_size > part_size:
            _multipart_upload(client, file_path, b2_key, part_size)
        else:
            with open(file_path, "rb") as f:
                client.put_object(
                    Bucket=_B2_BUCKET,
                    Key=b2_key,
                    Body=f,
                    ContentType="application/octet-stream",
                )

        logging.info(f"  B2 file upload OK: {b2_key} ({file_size / 1_048_576:.1f} MB)")
        return b2_key

    except Exception as e:
        logging.error(f"B2 file upload failed for {b2_key}: {e}")
        return None


def _multipart_upload(client, file_path: str, b2_key: str, part_size: int):
    """Multipart upload for large files."""
    mpu = client.create_multipart_upload(
        Bucket=_B2_BUCKET,
        Key=b2_key,
        ContentType="application/octet-stream",
    )
    upload_id = mpu["UploadId"]
    parts = []

    try:
        with open(file_path, "rb") as f:
            part_num = 1
            while True:
                data = f.read(part_size)
                if not data:
                    break
                resp = client.upload_part(
                    Bucket=_B2_BUCKET,
                    Key=b2_key,
                    PartNumber=part_num,
                    UploadId=upload_id,
                    Body=data,
                )
                parts.append({"PartNumber": part_num, "ETag": resp["ETag"]})
                logging.info(f"  Uploaded part {part_num} ({len(data) / 1_048_576:.1f} MB)")
                part_num += 1

        client.complete_multipart_upload(
            Bucket=_B2_BUCKET,
            Key=b2_key,
            UploadId=upload_id,
            MultipartUpload={"Parts": parts},
        )
    except Exception:
        client.abort_multipart_upload(Bucket=_B2_BUCKET, Key=b2_key, UploadId=upload_id)
        raise


def stream_upload_to_b2(
    response_raw,
    b2_key: str,
    content_type: str = "application/octet-stream",
) -> str | None:
    """
    Stream a file directly from an open HTTP response into B2 multipart upload.
    No full-file buffering — safe for 1GB+ files.
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
