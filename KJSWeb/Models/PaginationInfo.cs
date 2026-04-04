namespace KJSWeb.Models;

public class PaginationInfo
{
    public int CurrentPage { get; set; }
    public int TotalPages { get; set; }
    public int PageSize { get; set; } = 24;
    public int TotalItems { get; set; }

    public bool HasPrevious => CurrentPage > 1;
    public bool HasNext => CurrentPage < TotalPages;

    public List<int?> GetPageNumbers()
    {
        var pages = new List<int?>();
        int delta = 2; // Number of pages to show around current page

        for (int i = 1; i <= TotalPages; i++)
        {
            if (i == 1 || i == TotalPages || (i >= CurrentPage - delta && i <= CurrentPage + delta))
            {
                // Add ellipsis if there's a gap
                if (pages.Count > 0 && pages.Last() != i - 1 && pages.Last() != null)
                {
                    pages.Add(null);
                }
                pages.Add(i);
            }
        }

        return pages;
    }
}
