using System.Text.RegularExpressions;
using ELibrary.Core.Enums;

namespace ELibrary.Core.Helpers
{
    /// <summary>
    /// Each provider uses a distinctive ID format, so the provider can be recognised from the ID alone:
    ///   Gutenberg: numeric                         e.g. 1342
    ///   DOA:       UUID (item or bitstream)        e.g. e94670b8-6958-40d9-93b0-262a5929c6c4
    ///   Google:    12 chars of [A-Za-z0-9_-]       e.g. Ih4pAAAAYAAJ
    ///   OpenLibrary: work or edition key           e.g. OL66554W, OL7173379M
    /// </summary>
    public static partial class BookIdFormat
    {
        [GeneratedRegex("^[0-9]+$")]
        private static partial Regex GutenbergId();

        [GeneratedRegex("^OL[0-9]+[WM]$")]
        private static partial Regex OpenLibraryIdPattern();

        /// <summary>True for an Open Library work key (OL…W) or edition key (OL…M).</summary>
        public static bool IsOpenLibraryId(string? id) => id != null && OpenLibraryIdPattern().IsMatch(id);

        [GeneratedRegex("^[A-Za-z0-9_-]{12}$")]
        private static partial Regex GoogleId();

        /// <summary>Returns the provider whose ID format matches, or null when the ID matches none.</summary>
        public static BookSource? DetectSource(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;

            id = id.Trim();

            if (GutenbergId().IsMatch(id))
                return BookSource.Gutenberg;

            if (Guid.TryParseExact(id, "D", out _))
                return BookSource.DOA;

            // Must run before the Google check: a 12-char key such as OL123456789W also fits the Google pattern.
            if (IsOpenLibraryId(id))
                return BookSource.OpenLibrary;

            if (GoogleId().IsMatch(id))
                return BookSource.Google;

            return null;
        }
    }
}
