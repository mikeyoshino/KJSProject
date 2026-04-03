import pytest
from unittest.mock import MagicMock, patch
from src.storage import download_image, upload_to_supabase

@patch('requests.get')
def test_download_image(mock_get):
    mock_get.return_value.status_code = 200
    mock_get.return_value.content = b"fake-image-bytes"
    
    content = download_image("http://example.com/img.jpg")
    assert content == b"fake-image-bytes"

@patch('src.storage.supabase')
def test_upload_to_supabase(mock_supabase):
    # Mocking the storage client chain: supabase.storage.from_("bucket").upload("path", content)
    mock_bucket = MagicMock()
    mock_supabase.storage.from_.return_value = mock_bucket
    mock_bucket.upload.return_value = MagicMock()
    
    # Use a minimal valid 1x1 PNG byte sequence for the optimizer to work
    minimal_png = (
        b'\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR\x00\x00\x00\x01\x00\x00\x00\x01\x08\x06\x00\x00\x00\x1f\x15\xc4\x89'
        b'\x00\x00\x00\nIDATx\x9cc\x00\x01\x00\x00\x05\x00\x01\r\n-\xb4\x00\x00\x00\x00IEND\xaeB`\x82'
    )
    # Mock public URL response
    mock_bucket.get_public_url.return_value = "https://supabase.com/img.jpg"
    
    url = upload_to_supabase(minimal_png, "img.jpg")
    assert url == "https://supabase.com/img.jpg"
    mock_supabase.storage.from_.assert_called_with("images")
