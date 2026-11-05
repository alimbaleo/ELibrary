using ELibrary.Core.Enums;

namespace ELibrary.Core.Models
{
    public class BookSummary
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public List<string> Authors { get; set; } = new();
        public string Summary { get; set; } = string.Empty;
        public string ImageUrl { get; set; } = string.Empty;
        public BookSource Source { get; set; }
        public bool IsPartial { get; set; }
        public string? ViewLink { get; set; } = string.Empty;
    }
}
