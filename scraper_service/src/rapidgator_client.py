"""
rapidgator_client.py
--------------------
Rapidgator API v2 client — Python port of the C# RapidgatorDownloadService.

Uses a persistent requests.Session per instance for connection reuse
(avoids socket leaks and saves TCP handshake overhead on repeated calls).

Environment variables (.env):
  RAPIDGATOR_USERNAME  - Rapidgator account email
  RAPIDGATOR_PASSWORD  - Rapidgator account password
  RAPIDGATOR_API_URL   - default https://rapidgator.net/api/v2
  RAPIDGATOR_DELAY_MS  - ms to wait between API calls (default 3000)
"""

import os
import re
import time
import logging
import threading
import requests
from requests.adapters import HTTPAdapter
from urllib3.util.retry import Retry
from dotenv import load_dotenv

load_dotenv()

_API_BASE  = os.getenv("RAPIDGATOR_API_URL", "https://rapidgator.net/api/v2")
_USERNAME  = os.getenv("RAPIDGATOR_USERNAME", "")
_PASSWORD  = os.getenv("RAPIDGATOR_PASSWORD", "")
_DELAY_MS  = int(os.getenv("RAPIDGATOR_DELAY_MS", "3000"))

_FOLDER_URL_RE = re.compile(r"rapidgator\.net/folder/", re.IGNORECASE)
_FILE_URL_RE   = re.compile(r"rapidgator\.net/file/",   re.IGNORECASE)


class RapidgatorTrafficExceededException(Exception):
    pass


def _make_session() -> requests.Session:
    """Create a Session with a conservative retry adapter (network errors only)."""
    session = requests.Session()
    retry = Retry(
        total=3,
        backoff_factor=1,
        status_forcelist=[500, 502, 503, 504],
        allowed_methods=["GET", "POST"],
        raise_on_status=False,
    )
    adapter = HTTPAdapter(
        max_retries=retry,
        pool_connections=4,
        pool_maxsize=4,
    )
    session.mount("https://", adapter)
    session.mount("http://",  adapter)
    return session


class RapidgatorClient:
    """
    Thread-safe Rapidgator API client.
    One instance can be shared across threads. The auth lock serialises
    re-authentication; API calls are otherwise parallel-safe.
    Call .close() (or use as a context manager) to release the HTTP session.
    """

    def __init__(self):
        self._session_id: str | None = None
        self._lock = threading.Lock()
        self._session = _make_session()

    def close(self):
        """Release the underlying HTTP connection pool."""
        self._session.close()

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()

    # ── Authentication ────────────────────────────────────────────

    def _login(self) -> str:
        resp = self._session.get(
            f"{_API_BASE}/user/login",
            params={"login": _USERNAME, "password": _PASSWORD},
            timeout=30,
        )
        data = resp.json()
        sid = _extract_session_id(data)
        if not sid:
            raise RuntimeError(f"Rapidgator login failed: {data}")
        logging.info("Rapidgator authenticated OK")
        return sid

    def _get_session_id(self) -> str:
        with self._lock:
            if not self._session_id:
                self._session_id = self._login()
            return self._session_id

    def _invalidate_session(self):
        with self._lock:
            self._session_id = None

    # ── Public API ────────────────────────────────────────────────

    def get_download_link(self, rapidgator_url: str) -> tuple[str, str, int]:
        """
        Resolve a Rapidgator file URL to a direct download link.
        Returns (download_url, filename, file_size_bytes).
        Raises RapidgatorTrafficExceededException if daily traffic is exhausted.
        """
        for attempt in range(3):
            sid = self._get_session_id()
            time.sleep(_DELAY_MS / 1000)

            resp = self._session.get(
                f"{_API_BASE}/file/download",
                params={"token": sid, "url": rapidgator_url},
                timeout=30,
            )

            # HTTP-level 401
            if resp.status_code == 401:
                logging.warning("Rapidgator HTTP 401 — re-authenticating")
                self._invalidate_session()
                if attempt < 2:
                    continue
                raise RuntimeError("Rapidgator auth failed after re-login")

            if resp.status_code == 406:
                raise RapidgatorTrafficExceededException("Daily traffic limit exceeded")

            data = resp.json()
            _check_traffic_error(data)

            # JSON-level 401 — session expired mid-run (HTTP 200 but status=401 in body)
            json_status = data.get("status")
            if json_status == 401:
                logging.warning(f"Rapidgator JSON 401 — session expired, re-authenticating (attempt {attempt + 1})")
                self._invalidate_session()
                if attempt < 2:
                    continue
                raise RuntimeError(f"Rapidgator session invalid after re-login: {data}")

            dl_url = _dig(data, "response", "download_url") or ""
            fname  = _dig(data, "response", "filename") or _url_filename(rapidgator_url)
            fsize  = int(_dig(data, "response", "size") or 0)

            if not dl_url:
                raise RuntimeError(f"No download_url in response: {data}")

            return dl_url, fname, fsize

        raise RuntimeError("get_download_link failed after retry")

    def expand_folder_url(self, folder_url: str) -> list[str]:
        """
        Expand a Rapidgator folder URL to individual file URLs.
        Tries the API first; falls back to scraping the HTML page if the API
        returns 404 or an empty file list (common for non-premium accounts).
        """
        folder_id = _extract_folder_id(folder_url)
        if not folder_id:
            logging.warning(f"Could not extract folder ID from: {folder_url}")
            return []

        # ── Try API first ──────────────────────────────────────────
        try:
            sid = self._get_session_id()
            time.sleep(_DELAY_MS / 1000)

            resp = self._session.get(
                f"{_API_BASE}/folder/content",
                params={"token": sid, "folder_id": folder_id},
                timeout=30,
            )
            data = resp.json()

            # JSON-level 401 in folder API — re-authenticate and fall through to HTML scrape
            if data.get("status") == 401:
                logging.warning("Rapidgator folder API JSON 401 — re-authenticating")
                self._invalidate_session()

            if resp.status_code == 200 and data.get("status") == 200:
                files = (
                    _dig(data, "response", "files")
                    or _dig(data, "response", "folder", "files")
                    or _dig(data, "response", "items")
                    or []
                )
                urls = [
                    f.get("url") or f.get("link") or f.get("download_url", "")
                    for f in files if isinstance(f, dict)
                ]
                urls = [u for u in urls if u]
                if urls:
                    logging.info(f"Folder {folder_id} (API) → {len(urls)} file(s)")
                    return urls

            logging.info(f"Folder {folder_id} API returned no files (status={data.get('status')}) — falling back to HTML scrape")
        except Exception as e:
            logging.warning(f"Folder {folder_id} API error: {e} — falling back to HTML scrape")

        # ── Fallback: scrape the folder HTML page ──────────────────
        return self._expand_folder_html(folder_url, folder_id)

    def _expand_folder_html(self, folder_url: str, folder_id: str) -> list[str]:
        """Scrape the Rapidgator folder HTML page to extract /file/ links."""
        from bs4 import BeautifulSoup

        # Use the original URL (with folder name) — Rapidgator requires it
        # Ensure it's a full URL
        if folder_url.startswith("/"):
            page_url = f"https://rapidgator.net{folder_url}"
        elif not folder_url.startswith("http"):
            page_url = f"https://rapidgator.net/folder/{folder_id}/"
        else:
            page_url = folder_url

        headers = {
            "User-Agent": "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36",
            "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
            "Accept-Language": "en-US,en;q=0.9",
            "Referer": "https://rapidgator.net/",
        }

        try:
            resp = self._session.get(page_url, headers=headers, timeout=30)
            if resp.status_code == 404:
                logging.warning(f"Folder page 404 — content likely deleted: {page_url}")
                return []
            resp.raise_for_status()
        except Exception as e:
            logging.error(f"Folder HTML fetch failed for {page_url}: {e}")
            return []

        soup = BeautifulSoup(resp.text, "html.parser")
        try:
            urls = []
            for a in soup.select("table.items a[href]"):
                href = a["href"]
                if "/file/" in href:
                    full = f"https://rapidgator.net{href}" if href.startswith("/") else href
                    urls.append(full)

            if not urls:
                # Fallback: scan all links on the page
                for a in soup.find_all("a", href=True):
                    href = a["href"]
                    if "/file/" in href and "rapidgator.net" in href:
                        urls.append(href)

            logging.info(f"Folder {folder_id} (HTML scrape) → {len(urls)} file(s)")
            return urls
        finally:
            soup.decompose()

    def download_file(self, download_url: str, dest_path: str, chunk_size: int = 1024 * 1024):
        """
        Stream a file from download_url to dest_path (1 MB chunks).
        Uses atomic temp-file rename. No full file is held in memory.
        """
        tmp_path = dest_path + ".tmp"
        downloaded = 0
        last_log = time.time()

        # Use a fresh session for large streaming downloads so the connection
        # is fully released when the context manager exits.
        with _make_session() as dl_session:
            with dl_session.get(download_url, stream=True, timeout=None) as resp:
                resp.raise_for_status()
                with open(tmp_path, "wb") as f:
                    for chunk in resp.iter_content(chunk_size=chunk_size):
                        if chunk:
                            f.write(chunk)
                            downloaded += len(chunk)
                            now = time.time()
                            if now - last_log >= 10:
                                logging.info(
                                    f"  Downloaded {downloaded / 1_048_576:.1f} MB"
                                    f" → {os.path.basename(dest_path)}"
                                )
                                last_log = now

        os.replace(tmp_path, dest_path)
        logging.info(
            f"  Download complete: {os.path.basename(dest_path)}"
            f" ({downloaded / 1_048_576:.1f} MB)"
        )


# ── Helpers ───────────────────────────────────────────────────────────────────

def is_folder_url(url: str) -> bool:
    return bool(_FOLDER_URL_RE.search(url))


def is_file_url(url: str) -> bool:
    return bool(_FILE_URL_RE.search(url))


def _extract_session_id(data: dict) -> str | None:
    return (
        _dig(data, "response", "token")
        or _dig(data, "response", "session_id")
        or _dig(data, "response", "user", "session_id")
        or _dig(data, "response", "user", "token")
    )


def _check_traffic_error(data: dict):
    details = str(_dig(data, "details") or "")
    if re.search(r"traffic|bandwidth", details, re.IGNORECASE):
        raise RapidgatorTrafficExceededException(f"Traffic exceeded: {details}")


def _dig(obj, *keys):
    for key in keys:
        if not isinstance(obj, dict):
            return None
        obj = obj.get(key)
    return obj


def _extract_folder_id(url: str) -> str | None:
    m = re.search(r"/folder/([a-zA-Z0-9]+)", url)
    return m.group(1) if m else None


def _url_filename(url: str) -> str:
    return url.rstrip("/").split("/")[-1] or "file"
