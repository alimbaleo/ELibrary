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
        public string? ThumbnailUrl { get; init; }
        public DateTimeOffset? LastModified { get; init; }
    }
}
