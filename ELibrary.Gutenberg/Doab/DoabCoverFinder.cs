using ELibrary.Core.Helpers;
using ELibrary.Infrastructure.Configuration;
using ELibrary.Infrastructure.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ELibrary.Infrastructure.Doab
{
    /// <summary>
    /// Finds a cover image for a DOAB book by ISBN. DOAB's own thumbnails sit behind an anti-bot
    /// challenge (Anubis) that only browsers can pass, so covers come from Google Books (when an API
    /// key is configured), then Open Library.
    /// </summary>
    public sealed class DoabCoverFinder
    {
        private const int MaxIsbnsToTry = 3;

        private readonly IApiClient _client;
        private readonly DoabClient _doab;
        private readonly GoogleBooksSettings _google;
        private readonly ILogger<DoabCoverFinder> _logger;

        public DoabCoverFinder(IApiClient client, DoabClient doab, IOptions<GoogleBooksSettings> google, ILogger<DoabCoverFinder> logger)
        {
            _client = client;
            _doab = doab;
            _google = google.Value;
            _logger = logger;
        }

        /// <summary>The first cover URL found for any of the ISBNs, or null when no source has one.</summary>
        public async Task<string?> FindCoverUrlAsync(IReadOnlyList<string> isbns, CancellationToken ct)
        {
            var candidates = isbns.Take(MaxIsbnsToTry).ToList();

            if (!string.IsNullOrEmpty(_google.ApiKey))
            {
                foreach (var isbn in candidates)
                {
                    var url = await FromGoogleBooksAsync(isbn);
                    if (url != null)
                        return url;
                }
            }

            foreach (var isbn in candidates)
            {
                var url = await FromOpenLibraryAsync(isbn, ct);
                if (url != null)
                    return url;
            }

            return null;
        }

        private async Task<string?> FromGoogleBooksAsync(string isbn)
        {
            try
            {
                var url = $"{_google.BaseUrl}/volumes?q=isbn:{isbn}&maxResults=5" +
                          $"&fields=items(volumeInfo(imageLinks,industryIdentifiers))&key={_google.ApiKey}";
                var response = await _client.GetAsync<GoogleBooksResponse>(url);

                // Google answers isbn: queries with its closest match when nothing matches exactly, so only
                // accept a volume that really carries this ISBN; otherwise we'd show another book's cover.
                var wanted = ToIsbn13(isbn);
                var volume = response?.Items?.FirstOrDefault(item =>
                    item.VolumeInfo.ImageLinks != null &&
                    (item.VolumeInfo.IndustryIdentifiers ?? new List<GoogleIndustryIdentifier>())
                        .Any(id => ToIsbn13(DoabXoaiParser.NormalizeIsbn(id.Identifier)) == wanted));

                var links = volume?.VolumeInfo.ImageLinks;
                var image = links?.Thumbnail ?? links?.Small ?? links?.Medium ?? links?.Large;
                return image?.Replace("http://", "https://");
            }
            catch (Exception ex)
            {
                // e.g. quota exceeded; Open Library is tried next
                _logger.LogWarning(ex, "Google Books cover lookup failed for ISBN {Isbn}", isbn);
                return null;
            }
        }

        /// <summary>ISBN-10s are converted to their ISBN-13 form so the two can be compared.</summary>
        internal static string? ToIsbn13(string? isbn)
        {
            if (isbn == null)
                return null;
            if (isbn.Length == 13)
                return isbn;
            if (isbn.Length != 10)
                return null;

            var core = "978" + isbn[..9];
            var sum = core.Select((c, i) => (c - '0') * (i % 2 == 0 ? 1 : 3)).Sum();
            return core + ((10 - sum % 10) % 10);
        }

        private async Task<string?> FromOpenLibraryAsync(string isbn, CancellationToken ct)
        {
            // default=false makes Open Library answer 404 instead of serving a blank placeholder.
            var url = $"https://covers.openlibrary.org/b/isbn/{isbn}-M.jpg?default=false";
            try
            {
                return await _doab.TryDownloadAsync(url, ct) != null ? url : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Open Library cover lookup failed for ISBN {Isbn}", isbn);
                return null;
            }
        }
    }
}
