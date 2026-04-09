"""
file_processor.py
-----------------
Archive extraction, magic-byte extension detection, file renaming,
and ZIP creation — Python port of the C# FileProcessingService.

Renamed files follow the scandal69 convention:
  - Media files  → scandal69{N}.ext   (N is a shared counter across all archives in a post)
  - Text files   → scandal69.txt      (replaced with site promo text)
"""

import io
import os
import re
import struct
import zipfile
import logging
import threading
from dataclasses import dataclass

# Optional archive libraries — only imported if needed
_HAS_RARFILE = False
_HAS_PY7ZR   = False

try:
    import rarfile
    _HAS_RARFILE = True
except ImportError:
    pass

try:
    import py7zr
    _HAS_PY7ZR = True
except ImportError:
    pass

# ── Constants ─────────────────────────────────────────────────────────────────

_TEXT_EXTENSIONS = {".txt", ".nfo", ".url", ".html", ".htm", ".rtf", ".log", ".diz"}
_ARCHIVE_EXTENSIONS = {".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".tgz", ".tbz2"}

_SCANDAL_TEXT_CONTENT = (
    "Amateyr Leaks US UK CA AU and SEX Scandal Collection\r\nhttps://www.scandal69.com"
)

# Magic bytes → extension mapping
_MAGIC_MAP = [
    (b"\xff\xd8\xff",                    ".jpg"),
    (b"\x89PNG\r\n\x1a\n",              ".png"),
    (b"GIF87a",                          ".gif"),
    (b"GIF89a",                          ".gif"),
    (b"RIFF",                            ".webp"),   # checked further below
    (b"\x00\x00\x00\x18ftyp",            ".mp4"),
    (b"\x00\x00\x00\x20ftyp",            ".mp4"),
    (b"ftyp",                            ".mp4"),    # 4-byte offset variant
    (b"AVI ",                            ".avi"),
    (b"\x1a\x45\xdf\xa3",               ".mkv"),
    (b"ftypqt  ",                        ".mov"),
    (b"\x30\x26\xb2\x75",               ".wmv"),
    (b"PK\x03\x04",                      ".zip"),
    (b"Rar!\x1a\x07",                    ".rar"),
    (b"7z\xbc\xaf\x27\x1c",             ".7z"),
    (b"\x1f\x8b",                        ".gz"),
]


# ── Thread-safe counter ───────────────────────────────────────────────────────

class FileCounter:
    """Shared, thread-safe counter for sequential file renaming across archives."""
    def __init__(self):
        self._n = 0
        self._lock = threading.Lock()

    def next(self) -> int:
        with self._lock:
            self._n += 1
            return self._n


# ── Processed file record ─────────────────────────────────────────────────────

@dataclass
class ProcessedFile:
    path: str       # absolute path on disk
    rel:  str       # relative path to include inside the final ZIP


# ── Public API ────────────────────────────────────────────────────────────────

def is_archive(path: str) -> bool:
    ext = os.path.splitext(path)[1].lower()
    return ext in _ARCHIVE_EXTENSIONS


def detect_extension(path: str) -> str:
    """Sniff magic bytes and return extension like '.jpg', or '' if unknown."""
    try:
        with open(path, "rb") as f:
            header = f.read(32)
        for magic, ext in _MAGIC_MAP:
            if header[:len(magic)] == magic:
                # Distinguish WebP from generic RIFF
                if magic == b"RIFF" and header[8:12] != b"WEBP":
                    continue
                return ext
    except OSError:
        pass
    return ""


def extract_archive(archive_path: str, dest_folder: str) -> str:
    """
    Extract an archive into a unique sub-folder inside dest_folder.
    Returns the path to the extraction folder.
    """
    base = os.path.splitext(os.path.basename(archive_path))[0]
    extract_dir = os.path.join(dest_folder, f"extracted_{base}")
    os.makedirs(extract_dir, exist_ok=True)

    ext = os.path.splitext(archive_path)[1].lower()

    if ext == ".zip":
        with zipfile.ZipFile(archive_path, "r") as zf:
            zf.extractall(extract_dir)

    elif ext == ".rar":
        if not _HAS_RARFILE:
            raise RuntimeError("rarfile is not installed — run: pip install rarfile")
        with rarfile.RarFile(archive_path) as rf:
            rf.extractall(extract_dir)

    elif ext == ".7z":
        if not _HAS_PY7ZR:
            raise RuntimeError("py7zr is not installed — run: pip install py7zr")
        with py7zr.SevenZipFile(archive_path, mode="r") as sz:
            sz.extractall(path=extract_dir)

    elif ext in (".gz", ".tgz"):
        import tarfile
        with tarfile.open(archive_path, "r:gz") as tf:
            tf.extractall(extract_dir)

    elif ext in (".bz2", ".tbz2"):
        import tarfile
        with tarfile.open(archive_path, "r:bz2") as tf:
            tf.extractall(extract_dir)

    elif ext == ".tar":
        import tarfile
        with tarfile.open(archive_path, "r") as tf:
            tf.extractall(extract_dir)

    else:
        raise ValueError(f"Unsupported archive format: {ext}")

    logging.info(f"  Extracted {os.path.basename(archive_path)} → {extract_dir}")
    return extract_dir


def process_extracted_files(folder: str, counter: FileCounter, text_counter: dict | None = None) -> list[ProcessedFile]:
    """
    Walk `folder`, rename every file to scandal69{N}.ext (or scandal69.txt),
    and return a list of ProcessedFile records.
    Pass text_counter from the caller to deduplicate scandal69.txt across multiple archives.
    """
    results = []
    text_count: dict[str, int] = text_counter if text_counter is not None else {}  # track text file collisions

    for root, _dirs, files in os.walk(folder):
        rel_dir = os.path.relpath(root, folder)
        if rel_dir == ".":
            rel_dir = ""

        for fname in sorted(files):
            src = os.path.join(root, fname)
            ext = os.path.splitext(fname)[1].lower()

            if not ext:
                detected = detect_extension(src)
                if detected:
                    ext = detected

            if ext in _TEXT_EXTENSIONS:
                # Replace content with site promo text
                with open(src, "w", encoding="utf-8") as f:
                    f.write(_SCANDAL_TEXT_CONTENT)

                base_name = "scandal69.txt"
                count = text_count.get(base_name, 0)
                text_count[base_name] = count + 1
                if count > 0:
                    new_fname = f"scandal69({count + 1}).txt"
                else:
                    new_fname = "scandal69.txt"
            else:
                n = counter.next()
                new_fname = f"scandal69{n}{ext}"

            new_src = os.path.join(root, new_fname)
            if src != new_src:
                os.rename(src, new_src)

            rel = os.path.join(rel_dir, new_fname) if rel_dir else new_fname
            results.append(ProcessedFile(path=new_src, rel=rel))

    logging.info(f"  Processed {len(results)} file(s) from {folder}")
    return results


def process_single_file(src: str, dest_folder: str, counter: FileCounter) -> ProcessedFile:
    """
    Process a single (non-archive) file: detect extension if missing, rename.
    """
    ext = os.path.splitext(src)[1].lower()
    if not ext:
        ext = detect_extension(src) or ""

    if ext in _TEXT_EXTENSIONS:
        with open(src, "w", encoding="utf-8") as f:
            f.write(_SCANDAL_TEXT_CONTENT)
        new_name = "scandal69.txt"
    else:
        n = counter.next()
        new_name = f"scandal69{n}{ext}"

    dest = os.path.join(dest_folder, new_name)
    if src != dest:
        os.rename(src, dest)

    return ProcessedFile(path=dest, rel=new_name)


def create_zip(files: list[ProcessedFile], zip_path: str):
    """Create a ZIP at zip_path containing all ProcessedFiles at their rel paths."""
    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED) as zf:
        for pf in files:
            if os.path.isfile(pf.path):
                zf.write(pf.path, pf.rel)
    logging.info(f"  Created ZIP: {os.path.basename(zip_path)} ({len(files)} file(s))")
