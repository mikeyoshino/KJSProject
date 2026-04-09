-- Add status column: new rows default to 'draft', existing rows backfilled to 'published'
-- Note: asianscandal posts are stored in the posts table (source_name='AsianScandal'), no separate table.

ALTER TABLE public.posts
  ADD COLUMN IF NOT EXISTS status TEXT NOT NULL DEFAULT 'draft';
UPDATE public.posts SET status = 'published';
CREATE INDEX IF NOT EXISTS posts_status_idx ON public.posts (status);

ALTER TABLE public.jgirl_posts
  ADD COLUMN IF NOT EXISTS status TEXT NOT NULL DEFAULT 'draft';
UPDATE public.jgirl_posts SET status = 'published';
CREATE INDEX IF NOT EXISTS jgirl_posts_status_idx ON public.jgirl_posts (status);
