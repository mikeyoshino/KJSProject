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
You are editing HTML content for an adult website called Scandal69 (scandal69.com).

Title: {title}

Original HTML content (already pre-cleaned):
{html}

Rules:
1. Keep the EXACT same HTML structure and tags — do not add or remove elements.
2. Replace every occurrence of "asianscandal.net" with "scandal69.com".
3. Replace every occurrence of "AsianScandal" or "Asian Scandal" with "Scandal69".
4. Any sentence or phrase that promotes downloading from Rapidgator or any external file host must be rewritten to say the content is available as a "direct download" on this site instead.
5. Keep the person's name and any @instagram / social media handle exactly as written.
6. Keep the social media aesthetic — short punchy sentences, informal tone.
7. Output ONLY the rewritten HTML. No commentary, no markdown fences, no extra text before or after.

Rewritten HTML:\
"""


def rewrite_content(title: str, html: str) -> str:
    """
    Send HTML to Ollama for rewriting. Returns rewritten HTML.
    Falls back to the original html if Ollama is unreachable.
    """
    if not html or not html.strip():
        return html

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

        # Strip any accidental markdown code fences the model may add
        result = re.sub(r"^```(?:html)?\s*", "", result, flags=re.IGNORECASE)
        result = re.sub(r"\s*```$", "", result)
        return result.strip()

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
