# Design Specification: Scraper Data Pipeline

## Overview
This specification outlines the architecture and data schema for a Python-based web scraping service that continuously monitors `buzz69.com` for new posts. It extracts article content and Rapidgator links, saving them to a Supabase database. This service acts as the foundational data pipeline for a future ASP.NET web application and an automated downloader/uploader service.

## Architecture
The system consists of a containerized Python application:
- **Environment**: Deployed as a Docker container, but natively runnable as a standard Python script for easy local debugging on Windows using VS Code or standard terminal.
- **Language**: Python 3.11+
- **Key Libraries**: `requests`, `BeautifulSoup4` (for scraping), `supabase-py` (for DB operations), and `schedule` (for 24/7 background loop execution).
- **Execution Loop**: The service runs a continuous loop, waking up every 15 minutes to perform a scrape cycle.

## Scraping Logic
1. **Target Identification**: Fetches the front page(s) of `buzz69.com`.
2. **Duplicate Prevention**: Extracts post URLs and cross-references them against the `posts` table in Supabase via the `source_url` field.
3. **Data Extraction**: For any URL not found in the DB, it crawls inner post HTML to extract:
   - Post Title
   - Thumbnail Image URL
   - Raw HTML Content (preserving formatting and image tags)
   - Original Rapidgator download link(s)
4. **Termination Condition**: The scraping cycle terminates as soon as it encounters a post URL that already exists in the database, ensuring highly efficient polling.

## Data Schema (Supabase)
The Supabase database will feature a single primary table for this phase.

**Table**: `posts`
- `id` (uuid, primary key, auto-generated)
- `source_url` (text, unique): The original post URL from buzz69.com.
- `title` (text): Extracted article title.
- `thumbnail_url` (text): URL to the main thumbnail image.
- `content_html` (text): Raw inner HTML of the post.
- `original_rapidgator_url` (text): The scraped Rapidgator download link.
- `our_download_link` (text, nullable): To be populated later by a future automated uploader service.
- `download_status` (text): Defaults to `'pending'`. Used to track the state of file re-uploading tasks.
- `created_at` (timestamptz, auto-generated)

## Isolation and Future Integrations
- **Separation of Concerns**: This service ONLY scrapes text and original links. It explicitly DOES NOT handle file downloading or re-uploading to Rapidgator. That heavy network lifting is deferred to a future downstream service via the `download_status` flag.
- **Frontend Compatibility**: `content_html` is stored raw to allow the future ASP.NET application to trivially render the article exactly as it appeared originally.

## Testing & Error Handling
- **Resilience**: The script will implement try/except blocks to gracefully handle network timeouts or unexpected HTML structure changes on the target site. Failed cycles will log the error and retry on the next scheduled interval without crashing the Docker container.
