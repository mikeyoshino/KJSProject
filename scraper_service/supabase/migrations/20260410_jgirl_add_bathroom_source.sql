-- Add 'bathroom' as a valid source for jgirl_posts

ALTER TABLE public.jgirl_posts DROP CONSTRAINT jgirl_posts_source_check;
ALTER TABLE public.jgirl_posts ADD CONSTRAINT jgirl_posts_source_check
  CHECK (source IN ('upskirt', 'fc2', 'ksiroto', 'bathroom'));
