-- migration: convert link columns to array types to support multi-part downloads
ALTER TABLE posts 
ALTER COLUMN original_rapidgator_url TYPE TEXT[] USING 
  CASE 
    WHEN original_rapidgator_url IS NULL THEN ARRAY[]::TEXT[]
    ELSE ARRAY[original_rapidgator_url]
  END;

ALTER TABLE posts 
ALTER COLUMN our_download_link TYPE TEXT[] USING 
  CASE 
    WHEN our_download_link IS NULL THEN ARRAY[]::TEXT[]
    ELSE ARRAY[our_download_link]
  END;

ALTER TABLE posts 
ALTER COLUMN download_status SET DEFAULT 'pending';
