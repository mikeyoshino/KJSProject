import pytest
from unittest.mock import patch
from scraper import mirror_images_in_html

@patch('scraper.download_image')
@patch('scraper.upload_to_supabase')
def test_mirror_images_in_html(mock_upload, mock_download):
    # Mock download to return bytes
    mock_download.return_value = b"fake-bytes"
    # Mock upload to return a new URL
    mock_upload.side_effect = lambda content, name: f"https://supabase.com/{name}"
    
    html = (
        '<div>'
        '<span>Welcome to Buzz69.com</span>'
        '<a href="http://old-link.com"><img src="http://example.com/1.jpg" /></a>'
        '</div>'
    )
    new_html = mirror_images_in_html(html)
    
    assert '{{SiteName}}' in new_html
    assert '{{SiteUrl}}https://supabase.com/' in new_html
    assert 'http://old-link.com' not in new_html
    assert 'http://example.com/1.jpg' not in new_html

@patch('src.scraper.download_image')
def test_mirror_images_fails_gracefully(mock_download):
    # Mock download to fail
    mock_download.return_value = None
    
    html = '<div><img src="http://broken.com/image.jpg" /></div>'
    new_html = mirror_images_in_html(html)
    
    # Check that it returns None (failure)
    assert new_html is None
