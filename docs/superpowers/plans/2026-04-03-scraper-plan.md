# Python Scraper Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a robust, containerized Python scraper that monitors buzz69.com and saves posts and Rapidgator links to Supabase.

**Architecture:** Python application running a continuous `schedule` loop inside a Docker container. Uses `BeautifulSoup4` for parsing and `supabase-py` for persistence.

**Tech Stack:** Python 3.11, Docker, BeautifulSoup4, Requests, Supabase

---

### Task 1: Project Skeleton and Database Setup

**Files:**
- Create: `requirements.txt`
- Create: `Dockerfile`
- Create: `.env.example`
- Create: `supabase/schema.sql` (for reference)

- [ ] **Step 1: Define Python Dependencies**
Create `requirements.txt`:
```txt
requests==2.31.0
beautifulsoup4==4.12.3
supabase==2.3.4
schedule==1.2.1
python-dotenv==1.0.1
pytest==8.0.0
```

- [ ] **Step 2: Create Docker configuration**
Create `Dockerfile`:
```dockerfile
FROM python:3.11-slim
WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
COPY src/ src/
CMD ["python", "src/main.py"]
```

- [ ] **Step 3: Define Supabase Schema Reference**
Create `supabase/schema.sql` (just for the database admin to run manually):
```sql
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";

CREATE TABLE IF NOT EXISTS posts (
    id UUID PRIMARY KEY DEFAULT uuid_generate_v4(),
    source_url TEXT UNIQUE NOT NULL,
    title TEXT NOT NULL,
    thumbnail_url TEXT,
    content_html TEXT,
    original_rapidgator_url TEXT,
    our_download_link TEXT,
    download_status TEXT DEFAULT 'pending',
    created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
);
```

- [ ] **Step 4: Commit**
```bash
git add requirements.txt Dockerfile supabase/schema.sql
git commit -m "chore: setup project skeleton and db schema"
```

### Task 2: Database Layer

**Files:**
- Create: `src/db.py`
- Create: `tests/test_db.py`

- [ ] **Step 1: Write DB layer tests**
Create `tests/test_db.py`:
```python
import os
from unittest.mock import MagicMock, patch
from src.db import check_post_exists, insert_post

@patch('src.db.supabase')
def test_check_post_exists(mock_supabase):
    mock_supabase.table().select().eq().execute.return_value = MagicMock(data=[{"id": "1"}])
    assert check_post_exists("http://url.com") == True

@patch('src.db.supabase')
def test_insert_post(mock_supabase):
    mock_supabase.table().insert().execute.return_value = MagicMock(data=[{"id": "1"}])
    post_data = {"source_url": "1", "title": "A", "original_rapidgator_url": "B"}
    assert insert_post(post_data)["id"] == "1"
```

- [ ] **Step 2: Implement DB logic**
Create `src/db.py`:
```python
import os
from supabase import create_client, Client
from dotenv import load_dotenv

load_dotenv()
url: str = os.getenv("SUPABASE_URL", "")
key: str = os.getenv("SUPABASE_KEY", "")

supabase: Client = create_client(url, key) if url and key else None

def check_post_exists(source_url: str) -> bool:
    if not supabase: return False
    response = supabase.table("posts").select("id").eq("source_url", source_url).execute()
    return len(response.data) > 0

def insert_post(post_data: dict) -> dict:
    if not supabase: return {}
    response = supabase.table("posts").insert(post_data).execute()
    return response.data[0] if response.data else {}
```

- [ ] **Step 3: Run tests & Commit**
```bash
pytest tests/test_db.py
git add src/db.py tests/test_db.py
git commit -m "feat: database interface layer"
```

### Task 3: HTML Scraper Logic

**Files:**
- Create: `src/scraper.py`
- Create: `tests/test_scraper.py`

- [ ] **Step 1: Write scraper logic tests**
Create `tests/test_scraper.py`:
```python
from src.scraper import parse_front_page, parse_post_page

def test_parse_front_page():
    html = '<div class="post"><a href="http://buzz69.com/post1">Link</a></div>'
    links = parse_front_page(html)
    assert links == ["http://buzz69.com/post1"]

def test_parse_post_page():
    html = '''
    <h1>My Title</h1>
    <div class="entry">
        <img class="thumbnail" src="thumb.jpg" />
        <div class="content">raw html</div>
    </div>
    <a href="https://rapidgator.net/file/123">Download</a>
    '''
    data = parse_post_page("http://url", html)
    assert data["title"] == "My Title"
    assert data["thumbnail_url"] == "thumb.jpg"
    assert "https://rapidgator.net" in data["original_rapidgator_url"]
```

- [ ] **Step 2: Implement scraper logic**
Create `src/scraper.py`:
```python
import requests
from bs4 import BeautifulSoup

HEADERS = {'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)'}

def fetch_html(url: str) -> str:
    response = requests.get(url, headers=HEADERS, timeout=10)
    response.raise_for_status()
    return response.text

def parse_front_page(html: str) -> list[str]:
    soup = BeautifulSoup(html, 'html.parser')
    links = []
    for h2 in soup.find_all(['h2', 'h1', 'div'], class_=lambda c: c and ('post' in c.lower() or 'article' in c.lower())):
        a_tag = h2.find('a')
        if a_tag and a_tag.get('href'):
            links.append(a_tag['href'])
    return links

def parse_post_page(source_url: str, html: str) -> dict:
    soup = BeautifulSoup(html, 'html.parser')
    title = soup.find('h1').text.strip() if soup.find('h1') else 'Unknown Title'
    
    img = soup.find('img')
    thumbnail_url = img['src'] if img else ''
    
    content_div = soup.find('div', class_='entry-content') or soup.find('div', class_='content')
    content_html = str(content_div) if content_div else ''
    
    rg_link = ''
    for a in soup.find_all('a', href=True):
        if 'rapidgator.net' in a['href'] or 'rg.to' in a['href']:
            rg_link = a['href']
            break
            
    return {
        "source_url": source_url,
        "title": title,
        "thumbnail_url": thumbnail_url,
        "content_html": content_html,
        "original_rapidgator_url": rg_link
    }
```

- [ ] **Step 3: Run tests & Commit**
```bash
pytest tests/test_scraper.py
git add src/scraper.py tests/test_scraper.py
git commit -m "feat: parser extraction logic"
```

### Task 4: Main Loop Application

**Files:**
- Create: `src/main.py`

- [ ] **Step 1: Implement Main execution loop**
Create `src/main.py`:
```python
import time
import schedule
import logging
from src.scraper import fetch_html, parse_front_page, parse_post_page
from src.db import check_post_exists, insert_post

logging.basicConfig(level=logging.INFO, format='%(asctime)s - %(message)s')

def job():
    logging.info("Starting scrape job...")
    try:
        html = fetch_html("https://www.buzz69.com/")
        post_links = parse_front_page(html)
        
        for link in post_links:
            if check_post_exists(link):
                logging.info(f"Stopping execution. Reached existing post: {link}")
                break
            
            logging.info(f"Scraping new post: {link}")
            post_html = fetch_html(link)
            data = parse_post_page(link, post_html)
            
            insert_post(data)
            logging.info(f"Successfully inserted: {data['title']}")
            time.sleep(2) # Polite delay
    except Exception as e:
        logging.error(f"Error during scrape job: {e}")

if __name__ == "__main__":
    logging.info("Scraper service started.")
    job() # Run once on startup
    schedule.every(15).minutes.do(job)
    
    while True:
        schedule.run_pending()
        time.sleep(1)
```

- [ ] **Step 2: Commit**
```bash
git add src/main.py
git commit -m "feat: main daemon application loop"
```
