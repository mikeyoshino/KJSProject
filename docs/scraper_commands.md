# Scraper Service Command Reference

This document provides common commands and usage examples for the Buzz69 and AsianScandal scraping services.

## Environment Setup

Always ensure you are in the `scraper_service` directory and have the `src` directory in your `PYTHONPATH` when running scripts directly.

**In PowerShell:**
```powershell
$env:PYTHONPATH="src"
```

---

## Buzz69 Scraper (`src/backfill.py`)

This script handles both historical backfilling and routine synchronization for Buzz69.com.

### 1. Routine Sync (Check for new posts)
Use this command daily or once the initial backfill is done. It checks the first page only and uses the current date.
```powershell
python src/backfill.py --sync
```

### 2. Historical Backfill
Spreads posts across a specified number of days (useful for initial data population).
```powershell
# Default: 29 pages spread over 365 days
python src/backfill.py

# Custom: 50 pages spread over 500 days
python src/backfill.py --pages 50 --spread-days 500
```

### 3. Progressive Backfill
Start from a specific page to resume or target a specific historical range.
```powershell
# Start from page 30 and scan 10 pages
python src/backfill.py --pages 10 --start-page 30
```

---

## AsianScandal Scraper (`src/asianscandal_backfill.py`)

This script is dedicated to AsianScandal.net and supports AJAX pagination.

### 1. Historical Backfill
```powershell
# Default: 50 pages spread over 365 days
python src/asianscandal_backfill.py

# Custom segment: Pages 51-100
python src/asianscandal_backfill.py --pages 50 --start-page 50
```

### 2. Fast Sync (Manual)
To check just the latest 1-2 pages of AsianScandal.
```powershell
python src/asianscandal_backfill.py --pages 1 --spread-days 0
```

---

## Scheduled Scraper (`src/main.py`)

This is the main service that runs every 15 minutes to automatically check for new posts (Buzz69).

```powershell
python src/main.py
```

---

## Help and Troubleshooting

All scripts support the `--help` flag to see available options:

```powershell
python src/backfill.py --help
python src/asianscandal_backfill.py --help
```

> [!TIP]
> If the script hangs or freezes, ensure your computer has a stable internet connection and that the target site (Buzz69 or AsianScandal) is accessible in your browser.
