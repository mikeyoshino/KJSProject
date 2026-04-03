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
    
    html = '<div><img src="http://example.com/1.jpg" /><img src="http://example.com/2.png" /></div>'
    new_html = mirror_images_in_html(html)
    
    assert 'https://supabase.com/' in new_html
    assert 'http://example.com/1.jpg' not in new_html
    assert 'http://example.com/2.png' not in new_html
    assert mock_download.call_count == 2
    assert mock_upload.call_count == 2

@patch('src.scraper.download_image')
def test_mirror_images_fails_gracefully(mock_download):
    # Mock download to fail
    mock_download.return_value = None
    
    html = '<div><img src="http://broken.com/image.jpg" /></div>'
    new_html = mirror_images_in_html(html)
    
    # Check that it returns None (failure)
    assert new_html is None
