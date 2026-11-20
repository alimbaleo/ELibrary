namespace ELibrary.Infrastructure.Doab
{
    /// <summary>One DOAB item (book or chapter) in the locally harvested catalogue.</summary>
    public sealed class DoabCatalogueEntry
    {
        /// <summary>DSpace item UUID, the same ID DOAB's REST API used, so existing client IDs keep working.</summary>
        public required string Uuid { get; init; }

        /// <summary>DSpace handle, e.g. <c>20.500.12854/62823</c>. Needed for OAI-PMH GetRecord lookups.</summary>
        public string? Handle { get; init; }

        public required string Title { get; init; }
        public IReadOnlyList<string> Authors { get; init; } = Array.Empty<string>();
        public string? Abstract { get; init; }

        /// <summary>DOAB item type, e.g. <c>book</c> or <c>chapter</c>.</summary>
        public string? Type { get; init; }

        /// <summary>Subject classifications and keywords, used for topic search.</summary>
        public IReadOnlyList<string> Subjects { get; init; } = Array.Empty<string>();

        public string? DownloadUrl { get; init; }

        /// <summary>DOAB's own thumbnail. DOAB now serves these only to browsers (Anubis), so they're not used by default.</summary>
        public string? ThumbnailUrl { get; init; }

        public DateTimeOffset? LastModified { get; init; }

        /// <summary>ISBNs (digits only), used to find a cover from Google Books or Open Library.</summary>
        public IReadOnlyList<string> Isbns { get; init; } = Array.Empty<string>();

        /// <summary>Cover image URL found from another source, cached once looked up.</summary>
        public string? CoverUrl { get; init; }

        /// <summary>When a cover was last looked up; with no <see cref="CoverUrl"/> this means none was found.</summary>
        public DateTimeOffset? CoverCheckedAt { get; init; }
    }
}
