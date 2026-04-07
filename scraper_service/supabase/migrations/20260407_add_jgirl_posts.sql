-- Migration: Create jgirl_posts table
-- Scrapes three categories from jgirl.co: upskirt, ksiroto, fc2
-- Media files stored in B2 under JGirls/{id}/thumbnail.jpg and JGirls/{id}/preview/{n}.jpg

CREATE TABLE IF NOT EXISTS public.jgirl_posts (
    id                      UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    source                  TEXT        NOT NULL CHECK (source IN ('upskirt', 'ksiroto', 'fc2')),
    source_url              TEXT        UNIQUE NOT NULL,
    title                   TEXT        NOT NULL,
    thumbnail_url           TEXT,
    images                  TEXT[]      DEFAULT '{}',
    tags                    TEXT[]      DEFAULT '{}',
    original_download_links TEXT[]      DEFAULT '{}',
    download_links          TEXT[]      DEFAULT '{}',
    download_status         TEXT        DEFAULT 'pending'
                            CHECK (download_status IN ('pending', 'processing', 'done', 'failed')),
    created_at              TIMESTAMPTZ DEFAULT NOW(),
    scraped_at              TIMESTAMPTZ
);

CREATE INDEX IF NOT EXISTS jgirl_posts_source_url_idx       ON public.jgirl_posts (source_url);
CREATE INDEX IF NOT EXISTS jgirl_posts_source_idx           ON public.jgirl_posts (source);
CREATE INDEX IF NOT EXISTS jgirl_posts_created_at_idx       ON public.jgirl_posts (created_at DESC);
CREATE INDEX IF NOT EXISTS jgirl_posts_download_status_idx  ON public.jgirl_posts (download_status);
