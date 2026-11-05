using System.Text.RegularExpressions;
using ELibrary.Core.Enums;

namespace ELibrary.Core.Helpers
{
    // Each provider's ids have a recognisable shape:
    //   Gutenberg    numeric                          1342
    //   DOA          UUID                             e94670b8-6958-40d9-93b0-262a5929c6c4
    //   Google       12 chars of [A-Za-z0-9_-]        Ih4pAAAAYAAJ
    //   OpenLibrary  Internet Archive identifier      canadianbanking00brecrich
    //                or Open Library work/edition key OL66554W, OL7173379M
    public static partial class BookIdFormat
    {
        [GeneratedRegex("^[0-9]+$")]
        private static partial Regex GutenbergId();

        [GeneratedRegex("^OL[0-9]+[WM]$")]
        private static partial Regex OpenLibraryKey();

        [GeneratedRegex("^[A-Za-z0-9_-]{12}$")]
        private static partial Regex GoogleId();

        [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{2,99}$")]
        private static partial Regex ArchiveIdentifier();

        public static bool IsOpenLibraryKey(string? id) => id != null && OpenLibraryKey().IsMatch(id);

        public static bool IsArchiveIdentifier(string? id) => id != null && ArchiveIdentifier().IsMatch(id);

        // The provider whose id format unambiguously matches, or null.
        public static BookSource? DetectSource(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            id = id.Trim();

            if (GutenbergId().IsMatch(id)) return BookSource.Gutenberg;
            if (Guid.TryParseExact(id, "D", out _)) return BookSource.DOA;
            if (IsOpenLibraryKey(id)) return BookSource.OpenLibrary;
            if (GoogleId().IsMatch(id)) return BookSource.Google;
            return null;
        }

        // Whether the id could plausibly belong to the given source. Archive identifiers are free-form,
        // so almost anything could be an Open Library id; this stops a 12-char archive id being
        // mistaken for a Google volume.
        public static bool CouldBelongTo(BookSource source, string id) => source switch
        {
            BookSource.Gutenberg => GutenbergId().IsMatch(id),
            BookSource.DOA => Guid.TryParseExact(id, "D", out _),
            BookSource.Google => GoogleId().IsMatch(id) && !IsOpenLibraryKey(id),
            BookSource.OpenLibrary => IsOpenLibraryKey(id) || IsArchiveIdentifier(id),
            _ => false
        };
    }
}
