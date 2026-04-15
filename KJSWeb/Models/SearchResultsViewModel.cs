namespace KJSWeb.Models;

public class SearchResultsViewModel
{
    public string Query { get; set; } = "";
    public List<Post> Posts { get; set; } = new();
    public List<JGirlPost> JGirlPosts { get; set; } = new();
}
