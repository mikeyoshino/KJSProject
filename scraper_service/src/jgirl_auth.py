"""
jgirl_auth.py
-------------
One-time script to save Cloudflare cookies for jgirl.co.

Run this once:
  python jgirl_auth.py

A real Chrome window opens, Cloudflare challenge solves automatically.
Once the page loads, cookies are saved to jgirl_state.json automatically.
All future scraper runs use those cookies with plain requests (no browser needed).
"""

import json
import time
import os
import sys
import subprocess

import undetected_chromedriver as uc

STATE_FILE = os.path.join(os.path.dirname(__file__), "jgirl_state.json")


def _get_chrome_version() -> int | None:
    """Auto-detect installed Chrome major version."""
    cmds = [
        ["google-chrome", "--version"],
        ["google-chrome-stable", "--version"],
        ["chromium-browser", "--version"],
        ["chromium", "--version"],
    ]
    for cmd in cmds:
        try:
            out = subprocess.check_output(cmd, stderr=subprocess.DEVNULL).decode()
            version = int(out.strip().split()[-1].split(".")[0])
            return version
        except Exception:
            continue
    return None


options = uc.ChromeOptions()
options.add_argument("--lang=ja-JP")

is_linux = sys.platform.startswith("linux")
if is_linux:
    options.add_argument("--no-sandbox")
    options.add_argument("--disable-dev-shm-usage")

chrome_version = _get_chrome_version()
driver = uc.Chrome(
    options=options,
    headless=False,
    version_main=chrome_version,
)

try:
    print("Opening https://jgirl.co/upskirt ...")
    driver.get("https://jgirl.co/upskirt")

    # Wait for Cloudflare to clear — poll until post cards appear
    print("Waiting for Cloudflare to pass...")
    for _ in range(30):
        if driver.find_elements("css selector", "div.post-item"):
            print("Page loaded successfully!")
            break
        time.sleep(2)
    else:
        print("Timed out waiting — saving whatever cookies we have.")

    # Save cookies + user agent
    cookies = {c["name"]: c["value"] for c in driver.get_cookies()}
    ua = driver.execute_script("return navigator.userAgent")
    state = {"cookies": cookies, "user_agent": ua}

    with open(STATE_FILE, "w") as f:
        json.dump(state, f, indent=2)

    print(f"Saved cookies to: {STATE_FILE}")
    print(f"Cookies: {list(cookies.keys())}")

finally:
    driver.quit()
