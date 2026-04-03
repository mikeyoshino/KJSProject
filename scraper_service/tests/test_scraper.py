from src.scraper import parse_front_page, parse_post_page

def test_parse_front_page():
    html = '<article><a class="penci-image-holder" data-bgset="thumb.jpg?w=100"></a><h2 class="entry-title"><a href="http://buzz69.com/post1">Link</a></h2></article>'
    results = parse_front_page(html)
    assert results == [("http://buzz69.com/post1", "thumb.jpg")]

def test_parse_post_page():
    html = '<html><h1 class="entry-title">Test Title</h1><div class="entry-content">Content</div><a rel="category tag">Category 1</a><a href="https://rapidgator.net/file/1">Link</a></html>'
    data = parse_post_page("http://example.com", html)
    
    assert data['title'] == "Test Title"
    assert data['original_rapidgator_url'] == ["https://rapidgator.net/file/1"]
    assert data['categories'] == ["Category 1"]
    # Verify link is removed from HTML but remains in rg_links
    assert "https://rapidgator.net" not in data['content_html']
    assert isinstance(data["original_rapidgator_url"], list)
