namespace ELibrary.Models
{
    public class SearchBookRequest : PagedRequest
    {
        public string SearchText { get; set; } = string.Empty;
    }

    public class SearchBookByTopicRequest : PagedRequest
    {
        public string Topic { get; set; } = string.Empty;
    }
}
