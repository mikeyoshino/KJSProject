-- Migration: Add download_status column to asianscandal_posts
-- This mirrors the download_status column already present on the posts table.

ALTER TABLE public.asianscandal_posts
ADD COLUMN IF NOT EXISTS download_status TEXT DEFAULT 'pending';

-- Update any existing rows that have rapidgator URLs but no status
-- (rows inserted before this migration already have our_download_link empty)
UPDATE public.asianscandal_posts
SET download_status = 'pending'
WHERE download_status IS NULL;

-- Index for efficient polling of pending rows
CREATE INDEX IF NOT EXISTS asianscandal_posts_download_status_idx
ON public.asianscandal_posts (download_status);
