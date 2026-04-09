"""
ai_rewriter.py
--------------
Rewrites post content via a local Ollama model.

Environment variables:
  OLLAMA_URL    - default http://localhost:11434
  OLLAMA_MODEL  - default llama3.2
"""

import os
import re
import json
import logging
import requests
from dotenv import load_dotenv

load_dotenv()

OLLAMA_URL   = os.getenv("OLLAMA_URL", "http://localhost:11434").rstrip("/")
OLLAMA_MODEL = os.getenv("OLLAMA_MODEL", "llama3.2")

PROMPT_TEMPLATE = """\
You are an HTML content editor. Your job is to make specific text replacements and removals in HTML.

Title: {title}

HTML to edit:
{html}

Instructions:
1. Keep the EXACT same HTML structure and tags — do not add or remove elements unless instructed below.
2. Replace every occurrence of "asianscandal.net" with "scandal69.com".
3. Replace every occurrence of "AsianScandal" or "Asian Scandal" with "Scandal69".
4. Replace every occurrence of "buzz69.com" with "scandal69.com".
5. Replace every occurrence of "Buzz69" with "Scandal69".
6. Any sentence promoting downloading from Rapidgator or any external file host: rewrite it to say the file is available as a "direct download" on this site.
7. Remove any HTML element (and its tags) that contains the text "IMPORTANT UPDATE:".
8. Keep all names, @handles, and social media references exactly as written.
9. Keep the tone short, punchy, and informal.
10. Output ONLY the edited HTML. No explanation, no markdown fences, no extra text.

Edited HTML:\
"""

# Patterns that indicate the model refused rather than returning HTML
_REFUSAL_PATTERNS = re.compile(
    r"i(?:'m| am) (?:sorry|unable|not able)|i can'?t (?:assist|help|provide|process)|"
    r"i (?:cannot|won't|will not)|(?:as an ai|as a language model)",
    re.IGNORECASE,
)


_BRAND_REPLACEMENTS = [
    ("asianscandal.net", "scandal69.com"),
    ("buzz69.com",       "scandal69.com"),
    ("AsianScandal",     "Scandal69"),
    ("Asian Scandal",    "Scandal69"),
    ("Buzz69",           "Scandal69"),
]


def _apply_brand_replacements(html: str) -> str:
    for old, new in _BRAND_REPLACEMENTS:
        html = html.replace(old, new)
    return html


def rewrite_content(title: str, html: str) -> str:
    """
    Send HTML to Ollama for rewriting. Returns rewritten HTML.
    Falls back to the original html if Ollama is unreachable.
    """
    if not html or not html.strip():
        return html

    # Always apply brand replacements — don't rely solely on the AI
    html = _apply_brand_replacements(html)

    prompt = PROMPT_TEMPLATE.format(title=title, html=html)

    try:
        resp = requests.post(
            f"{OLLAMA_URL}/api/generate",
            json={
                "model": OLLAMA_MODEL,
                "prompt": prompt,
                "stream": False,
                "options": {
                    "temperature": 0.3,
                    "top_p": 0.9,
                    "num_predict": 4096,
                },
            },
            timeout=180,
        )
        resp.raise_for_status()
        data = resp.json()
        result = data.get("response", "").strip()

        if not result:
            logging.warning("Ollama returned empty response, using cleaned HTML as-is.")
            return html

        if _REFUSAL_PATTERNS.search(result[:200]):
            logging.warning("Ollama refused the request, using cleaned HTML as-is.")
            return html

        # Strip any accidental markdown code fences the model may add
        result = re.sub(r"^```(?:html)?\s*", "", result, flags=re.IGNORECASE)
        result = re.sub(r"\s*```$", "", result)
        # Guarantee brand replacements even if the model ignored the instruction
        return _apply_brand_replacements(result.strip())

    except requests.exceptions.ConnectionError:
        logging.warning(f"Ollama not reachable at {OLLAMA_URL}. Using cleaned HTML as-is.")
        return html
    except Exception as e:
        logging.error(f"Ollama rewrite error: {e}")
        return html


def check_ollama_available() -> bool:
    """Quick ping to check if Ollama is running."""
    try:
        r = requests.get(f"{OLLAMA_URL}/api/tags", timeout=5)
        return r.status_code == 200
    except Exception:
        return False
