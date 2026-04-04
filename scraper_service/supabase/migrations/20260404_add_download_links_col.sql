-- Migration: Add alignment and tags columns to asianscandal_posts
-- Using original_rapidgator_url, our_download_link, and tags

ALTER TABLE public.asianscandal_posts 
ADD COLUMN IF NOT EXISTS original_rapidgator_url TEXT[] DEFAULT '{}',
ADD COLUMN IF NOT EXISTS our_download_link TEXT[] DEFAULT '{}',
ADD COLUMN IF NOT EXISTS tags TEXT[] DEFAULT '{}';

-- Index for tags if searching within them frequently
-- CREATE INDEX IF NOT EXISTS asianscandal_posts_tags_idx ON public.asianscandal_posts USING GIN (tags);
