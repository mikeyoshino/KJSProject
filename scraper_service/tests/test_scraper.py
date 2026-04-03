from src.scraper import parse_front_page, parse_post_page

def test_parse_front_page():
    html = '<article><a class="penci-image-holder" data-bgset="thumb.jpg?w=100"></a><h2 class="entry-title"><a href="http://buzz69.com/post1">Link</a></h2></article>'
    results = parse_front_page(html)
    assert results == [("http://buzz69.com/post1", "thumb.jpg")]

def test_parse_post_page():
    html = '''
    <h1>My Title</h1>
    <div class="entry">
        <img class="thumbnail" src="thumb.jpg" />
        <div class="content">raw html</div>
    </div>
    <a href="https://rapidgator.net/file/123">Download</a>
    '''
    data = parse_post_page("http://url", html)
    assert data["title"] == "My Title"
    assert data["thumbnail_url"] == "thumb.jpg"
    assert "https://rapidgator.net" in data["original_rapidgator_url"]
