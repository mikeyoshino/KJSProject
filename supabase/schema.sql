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
