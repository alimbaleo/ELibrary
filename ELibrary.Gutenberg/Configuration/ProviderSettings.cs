namespace ELibrary.Infrastructure.Configuration
{
    public class GutenbergSettings
    {
        public const string Section = "Gutenberg";

        public string OpdsSearchUrl { get; set; } = "https://www.gutenberg.org/ebooks/search.opds/";
        public string OpdsBookUrl { get; set; } = "https://www.gutenberg.org/ebooks/";
        public string ContentUrl { get; set; } = "https://www.gutenberg.org/cache/epub/";
    }

    public class GoogleBooksSettings
    {
        public const string Section = "GoogleBooks";

        public string BaseUrl { get; set; } = "https://www.googleapis.com/books/v1";
        public string? ApiKey { get; set; }
        public int MaxResults { get; set; } = 10;
    }

    public class OpenLibrarySettings
    {
        public const string Section = "OpenLibrary";

        public string SearchUrl { get; set; } = "https://openlibrary.org/search.json";
        public string ArchiveUrl { get; set; } = "https://archive.org";
        public int MaxResults { get; set; } = 10;
    }

    // Harvest settings (OaiUrl, CatalogueDbPath, Sync*) under the same section are read by the Doab classes.
    public class DoaBooksSettings
    {
        public const string Section = "DoaBooks";

        public string RewriteBaseUrl { get; set; } = string.Empty;
        public int MaxResults { get; set; } = 10;

        // DOAB's thumbnails only load in browsers (Anubis bot challenge); turn this on if DOAB
        // allow-lists the server, to prefer them over covers looked up by ISBN.
        public bool UseDoabThumbnails { get; set; }

        // How long a "no cover found" result is remembered before looking again.
        public int CoverRecheckDays { get; set; } = 7;
    }
}
