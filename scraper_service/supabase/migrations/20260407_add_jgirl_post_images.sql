-- Add post_images column to jgirl_posts
-- post_images: static photo gallery images (photo-type posts: 1.jpg, 2.jpg, 3.jpg...)
-- images:      video preview slideshow frames (video-type posts: mov1001.jpg, mov1002.jpg...)

ALTER TABLE jgirl_posts
    ADD COLUMN IF NOT EXISTS post_images TEXT[] DEFAULT '{}';
