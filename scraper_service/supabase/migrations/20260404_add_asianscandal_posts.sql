-- Migration: Create asianscandal_posts table
CREATE TABLE IF NOT EXISTS public.asianscandal_posts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    source_url TEXT UNIQUE NOT NULL,
    title TEXT NOT NULL,
    thumbnail_url TEXT,
    content_html TEXT,
    categories TEXT[] DEFAULT '{}',
    created_at TIMESTAMPTZ DEFAULT NOW()
);

-- Index on source_url for efficient duplicate checks
CREATE INDEX IF NOT EXISTS asianscandal_posts_source_url_idx ON public.asianscandal_posts(source_url);

-- Index on created_at for latest posts ordering
CREATE INDEX IF NOT EXISTS asianscandal_posts_created_at_idx ON public.asianscandal_posts(created_at DESC);
