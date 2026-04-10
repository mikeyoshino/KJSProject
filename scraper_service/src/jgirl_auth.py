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
import tempfile
import shutil

import undetected_chromedriver as uc

STATE_FILE = os.path.join(os.path.dirname(__file__), "jgirl_state.json")

# URLs to visit during auth — cf_clearance is issued per-path-group on this site
AUTH_URLS = [
    "https://jgirl.co/upskirt",
    "https://jgirl.co/upskirt/vendor/Sifangclub",
]


def _get_chrome_version() -> int | None:
    """Auto-detect installed Chrome major version."""
    cmds = [
        ["google-chrome", "--version"],
        ["google-chrome-stable", "--version"],
        ["chromium-browser", "--version"],
        ["chromium", "--version"],
    ]
    # macOS: Chrome is a .app bundle — query version via the binary directly
    mac_paths = [
        "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
        "/Applications/Chromium.app/Contents/MacOS/Chromium",
    ]
    for path in mac_paths:
        if os.path.exists(path):
            cmds.insert(0, [path, "--version"])
    for cmd in cmds:
        try:
            out = subprocess.check_output(cmd, stderr=subprocess.DEVNULL).decode()
            version = int(out.strip().split()[-1].split(".")[0])
            return version
        except Exception:
            continue
    return None


# Use a fresh temp profile each run to avoid Chrome lock/crash on repeated runs
tmp_profile = tempfile.mkdtemp(prefix="jgirl_chrome_")

options = uc.ChromeOptions()
options.add_argument("--lang=ja-JP")
options.add_argument(f"--user-data-dir={tmp_profile}")

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
    # Visit each URL so cf_clearance covers all paths we scrape
    for url in AUTH_URLS:
        print(f"Opening {url} ...")
        driver.get(url)

        # Wait for Cloudflare to clear — poll until post cards appear
        print("Waiting for Cloudflare to pass...")
        for _ in range(30):
            if driver.find_elements("css selector", "div.post-item"):
                print(f"  Page loaded: {url}")
                break
            time.sleep(2)
        else:
            print(f"  Timed out — continuing with whatever cookies we have.")

        time.sleep(1)  # brief pause between navigations

    # Save cookies + user agent (collected across all visited URLs)
    cookies = {c["name"]: c["value"] for c in driver.get_cookies()}
    ua = driver.execute_script("return navigator.userAgent")
    state = {"cookies": cookies, "user_agent": ua}

    with open(STATE_FILE, "w") as f:
        json.dump(state, f, indent=2)

    print(f"\nSaved cookies to: {STATE_FILE}")
    print(f"Cookies: {list(cookies.keys())}")

finally:
    driver.quit()
    # Clean up temp profile
    shutil.rmtree(tmp_profile, ignore_errors=True)
