import os
from unittest.mock import MagicMock, patch
from src.db import check_post_exists, insert_post

@patch('src.db.supabase')
def test_check_post_exists(mock_supabase):
    mock_supabase.table().select().eq().execute.return_value = MagicMock(data=[{"id": "1"}])
    assert check_post_exists("http://url.com") == True

@patch('src.db.supabase')
def test_insert_post(mock_supabase):
    mock_supabase.table().insert().execute.return_value = MagicMock(data=[{"id": "1"}])
    post_data = {"source_url": "1", "title": "A", "original_rapidgator_url": "B"}
    assert insert_post(post_data)["id"] == "1"
