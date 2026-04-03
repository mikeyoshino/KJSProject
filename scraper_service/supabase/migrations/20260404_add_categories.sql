-- Add categories column as an array of strings
ALTER TABLE posts ADD COLUMN IF NOT EXISTS categories TEXT[];

-- Update index for search optimization later
CREATE INDEX IF NOT EXISTS idx_posts_categories ON posts USING GIN (categories);
