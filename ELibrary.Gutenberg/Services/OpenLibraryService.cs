using System.Net;
using System.Text.RegularExpressions;
using ELibrary.Core.Enums;
using ELibrary.Core.Helpers;
using ELibrary.Core.Interfaces;
using ELibrary.Core.Models;
using ELibrary.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace ELibrary.Infrastructure.Services
{
    /// <summary>
    /// Open Library (openlibrary.org) catalogue, read through the Internet Archive book reader.
    /// Only books with a public scan (readable without an account) are offered: "borrowable" and
    /// "printdisabled" books need an Internet Archive login, so they are left out of search results.
    /// Book IDs are Open Library work keys such as OL66554W; edition keys such as OL7173379M are also accepted.
    /// </summary>
    public partial class OpenLibraryService : ILibraryService
    {
        private const string ProviderName = "Open Library";
        private const string PublicAccess = "public";
        private const string SearchFields = "key,title,author_name,cover_i,first_sentence,ebook_access";
        private const string NoSummary = "No Summary Available";

        // archive.org rejects search queries longer than about 2,000 characters (HTTP 400), and popular works have
        // hundreds of scans, so only as many scan identifiers as fit in this length are checked.
        private const int MaxArchiveQueryLength = 1800;
        private const string ReadableScanFilter = " AND mediatype:texts AND NOT access-restricted-item:true";
        private static readonly TimeSpan CoverCacheDuration = TimeSpan.FromHours(6);

        // Cover downloads only; JSON calls go through IApiClient.
        private static readonly HttpClient CoverClient = new() { Timeout = TimeSpan.FromSeconds(45) };

        private readonly IApiClient _client;
        private readonly IConfiguration _config;
        private readonly IMemoryCache _cache;
        private readonly ILogger<OpenLibraryService> _logger;

        public BookSource BOOK_SOURCE => BookSource.OpenLibrary;

        public OpenLibraryService(IApiClient client, IConfiguration config, IMemoryCache cache, ILogger<OpenLibraryService> logger)
        {
            _client = client;
            _config = config;
            _cache = cache;
            _logger = logger;
        }

        private string BaseUrl => Setting("BaseUrl", "https://openlibrary.org");
        private string CoversUrl => Setting("CoversUrl", "https://covers.openlibrary.org");
        private string ArchiveSearchUrl => Setting("ArchiveSearchUrl", "https://archive.org/advancedsearch.php");
        private string ReaderEmbedUrl => Setting("ReaderEmbedUrl", "https://archive.org/embed");
        private string UserAgent => Setting("UserAgent", "ELibrary/1.0");
        private int MaxResults => Math.Max(_config.GetValue<int>("OpenLibrary:MaxResults", 10), 1);

        private string Setting(string name, string fallback) =>
            (_config.GetValue<string>($"OpenLibrary:{name}") ?? fallback).TrimEnd('/');


        public Task<Response<SearchBookResponse>> SearchBooks(int? page, string searchText)
        {
            return Search(page, ToPlainTerms(searchText), "Search", "SearchText", searchText);
        }

        public Task<Response<SearchBookResponse>> SearchBooksByTopic(int? page, string topic)
        {
            var terms = ToPlainTerms(topic);
            return Search(page, terms == null ? null : $"subject:\"{terms}\"", "SearchTopic", "Topic", topic);
        }

        public async Task<Response<BookSummary>> GetBookSummaryById(string id)
        {
            id = id?.Trim() ?? string.Empty;
            var notFound = $"Book with id {id} not found";
            try
            {
                if (!BookIdFormat.IsOpenLibraryId(id))
                    return Response<BookSummary>.NotFound(notFound);

                var doc = await FindBook(id, publicOnly: false,
                    SearchFields + ",editions,editions.key,editions.cover_i,editions.ebook_access");
                if (doc == null)
                    return Response<BookSummary>.NotFound(notFound);

                var edition = IsEditionId(id) ? FirstEdition(doc) : null;
                var description = await GetWorkDescription(doc.Key);
                RememberCover(id, CoverIdOf(id, doc));

                var summary = new BookSummary
                {
                    Id = id,
                    Title = doc.Title ?? "Unknown Title",
                    Authors = doc.AuthorName ?? new List<string>(),
                    Summary = ToSummary(description ?? doc.FirstSentence?.FirstOrDefault()),
                    ImageUrl = _config.GetValue<string>("ElibraryBaseUrl") + $"/Image/{id}",
                    Source = BOOK_SOURCE,
                    IsPartial = (edition ?? doc).EbookAccess != PublicAccess,
                };

                return new Response<BookSummary>(summary, "Book summary retrieved successfully", true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OpenLibraryService[GetBookSummaryById] failed for book {Id}", id);
                return Response<BookSummary>.FromProviderException(ex, ProviderName, notFound);
            }
        }

        public async Task<Response<string>> GetBookById(string id)
        {
            id = id?.Trim() ?? string.Empty;
            var notFound = $"No public scan found for book {id}";
            try
            {
                if (!BookIdFormat.IsOpenLibraryId(id))
                    return Response<string>.NotFound(notFound);

                var doc = await FindBook(id, publicOnly: true, "key,title,ia,editions,editions.key,editions.ia");
                if (doc == null)
                    return Response<string>.NotFound(notFound);

                var scanId = await FindPublicTextScan(doc);
                if (scanId == null)
                    return Response<string>.NotFound(notFound);

                return new Response<string>(GetReaderHtml(scanId, doc.Title), "Book content retrieved successfully", true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OpenLibraryService[GetBookById] failed for book {Id}", id);
                return Response<string>.FromProviderException(ex, ProviderName, notFound);
            }
        }

        public async Task<Response<FileContentResult>> GetImageById(string id)
        {
            id = id?.Trim() ?? string.Empty;
            var notFound = $"Image not found for book {id}";
            try
            {
                if (!BookIdFormat.IsOpenLibraryId(id))
                    return Response<FileContentResult>.NotFound(notFound);

                // Search results remember their cover IDs, so the usual search -> image flow needs no extra lookup.
                if (!_cache.TryGetValue(CoverCacheKey(id), out long? coverId))
                {
                    var doc = await FindBook(id, publicOnly: false, "key,cover_i,editions,editions.key,editions.cover_i");
                    if (doc == null)
                        return Response<FileContentResult>.NotFound(notFound);

                    coverId = CoverIdOf(id, doc);
                    RememberCover(id, coverId);
                }

                if (coverId == null)
                    return Response<FileContentResult>.NotFound(notFound);

                // default=false makes the covers service answer 404 instead of a blank placeholder image.
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{CoversUrl}/b/id/{coverId}-L.jpg?default=false");
                request.Headers.UserAgent.TryParseAdd(UserAgent);
                using var response = await CoverClient.SendAsync(request);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync();

                var fileContent = new FileContentResult(bytes, "image/jpeg")
                {
                    FileDownloadName = $"book_{id}.jpg"
                };

                return new Response<FileContentResult>(fileContent, "Image retrieved successfully", true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OpenLibraryService[GetImageById] failed for book {Id}", id);
                return Response<FileContentResult>.FromProviderException(ex, ProviderName, notFound);
            }
        }


        private async Task<Response<SearchBookResponse>> Search(int? page, string? query, string action, string queryParam, string? originalText)
        {
            try
            {
                if (query == null)
                    return new Response<SearchBookResponse>(new SearchBookResponse { Data = new List<Data>() }, "No books found", true);

                var eLibraryBaseUrl = _config.GetValue<string>("ElibraryBaseUrl");
                var maxResults = MaxResults;
                var currentPage = Math.Max(page ?? 1, 1);

                // The text is grouped so that an OR inside it cannot escape the public-only filter.
                var q = $"({query}) AND ebook_access:{PublicAccess}";
                var url = $"{BaseUrl}/search.json?q={Uri.EscapeDataString(q)}&fields={SearchFields}&page={currentPage}&limit={maxResults}";

                var result = await _client.GetAsync<OpenLibrarySearchResponse>(url);

                var books = new List<Data>();
                foreach (var doc in result?.Docs ?? new List<OpenLibrarySearchDoc>())
                {
                    var id = ToOpenLibraryId(doc.Key);
                    if (id == null || doc.EbookAccess != PublicAccess)
                        continue;

                    RememberCover(id, doc.CoverId);
                    books.Add(new Data
                    {
                        Id = id,
                        Title = doc.Title ?? "Unknown Title",
                        Authors = doc.AuthorName ?? new List<string>(),
                        Summary = ToSummary(doc.FirstSentence?.FirstOrDefault()),
                        ImageUrl = eLibraryBaseUrl + $"/Image/{id}",
                        Source = BOOK_SOURCE,
                        IsPartial = false
                    });
                }

                var total = result?.NumFound ?? 0;
                var totalPages = (int)Math.Ceiling((double)total / maxResults);
                var text = Uri.EscapeDataString(originalText ?? string.Empty);

                return new Response<SearchBookResponse>(
                    new SearchBookResponse
                    {
                        Count = total,
                        NextPageUrl = currentPage < totalPages ? eLibraryBaseUrl + $"/{action}?Page={currentPage + 1}&{queryParam}={text}" : null,
                        PreviousPageUrl = currentPage > 1 ? eLibraryBaseUrl + $"/{action}?Page={currentPage - 1}&{queryParam}={text}" : null,
                        Data = books
                    },
                    "Books retrieved successfully",
                    true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OpenLibraryService[{Action}] failed for {Query}", action, originalText);
                return Response<SearchBookResponse>.FromProviderException(ex, ProviderName, "No books found");
            }
        }

        /// <summary>
        /// Looks a work up through search.json, which (unlike /works/{id}.json) includes author names,
        /// the access level and the scan identifiers in one call. Edition IDs return their work, with
        /// that edition under <c>editions</c>.
        /// </summary>
        private async Task<OpenLibrarySearchDoc?> FindBook(string id, bool publicOnly, string fields)
        {
            var q = IsEditionId(id) ? $"edition_key:{id}" : $"key:/works/{id}";
            if (publicOnly)
                q += $" AND ebook_access:{PublicAccess}";

            var url = $"{BaseUrl}/search.json?q={Uri.EscapeDataString(q)}&fields={fields}&limit=1";
            var result = await _client.GetAsync<OpenLibrarySearchResponse>(url);
            return result?.Docs?.FirstOrDefault();
        }

        /// <summary>
        /// Picks a scan that can be read without logging in. Open Library's "public" flag is per work, and the
        /// work's scan list mixes public scans, lending-only scans and LibriVox audiobooks, so archive.org is
        /// asked which of them are texts with no access restriction. The edition Open Library ranked first is
        /// preferred, then the most downloaded scan.
        /// </summary>
        private async Task<string?> FindPublicTextScan(OpenLibrarySearchDoc doc)
        {
            var preferred = FirstEdition(doc)?.InternetArchiveIds ?? new List<string>();
            var candidates = preferred
                .Concat(doc.InternetArchiveIds ?? new List<string>())
                .Where(s => ArchiveIdentifier().IsMatch(s))
                .Distinct();

            var terms = new List<string>();
            var length = "identifier:()".Length + ReadableScanFilter.Length;
            foreach (var candidate in candidates)
            {
                var term = $"\"{candidate}\"";
                var added = term.Length + (terms.Count > 0 ? " OR ".Length : 0);
                if (length + added > MaxArchiveQueryLength)
                    break;

                terms.Add(term);
                length += added;
            }
            if (terms.Count == 0)
                return null;

            var q = $"identifier:({string.Join(" OR ", terms)}){ReadableScanFilter}";
            var url = $"{ArchiveSearchUrl}?q={Uri.EscapeDataString(q)}&fl%5B%5D=identifier&sort%5B%5D={Uri.EscapeDataString("downloads desc")}&rows={terms.Count}&output=json";

            var result = await _client.GetAsync<ArchiveSearchResponse>(url);
            var readable = result?.Response?.Docs?
                .Select(d => d.Identifier)
                .OfType<string>()
                .ToList() ?? new List<string>();

            return preferred.FirstOrDefault(readable.Contains) ?? readable.FirstOrDefault();
        }

        /// <summary>The work's description, or null. Best effort: a failure here should not hide the rest of the summary.</summary>
        private async Task<string?> GetWorkDescription(string? workKey)
        {
            var workId = ToOpenLibraryId(workKey);
            if (workId == null || IsEditionId(workId))
                return null;

            try
            {
                var work = await _client.GetAsync<OpenLibraryWork>($"{BaseUrl}/works/{workId}.json");
                var text = work?.Description switch
                {
                    JValue value => value.Value?.ToString(),
                    JObject obj => obj.Value<string>("value"),
                    _ => null
                };

                // Descriptions often end with a "----------" line followed by markdown link lists; keep the prose only.
                var separator = text?.IndexOf("----------", StringComparison.Ordinal) ?? -1;
                if (separator >= 0)
                    text = text![..separator];

                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "OpenLibraryService: could not load the description of work {WorkId}", workId);
                return null;
            }
        }

        private string GetReaderHtml(string scanId, string? title)
        {
            var src = $"{ReaderEmbedUrl}/{Uri.EscapeDataString(scanId)}";
            return $@"<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"" />
    <title>{WebUtility.HtmlEncode(title ?? "Book")}</title>
</head>
<body style=""margin:0;padding:0;"">
    <iframe src=""{WebUtility.HtmlEncode(src)}"" style=""width:100%;height:100vh;border:none;"" allowfullscreen></iframe>
</body>
</html>";
        }

        private void RememberCover(string id, long? coverId) =>
            _cache.Set(CoverCacheKey(id), coverId > 0 ? coverId : null,
                new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = CoverCacheDuration, Size = 1 });

        private static string CoverCacheKey(string id) => $"openlibrary:cover:{id}";

        private static long? CoverIdOf(string id, OpenLibrarySearchDoc doc)
        {
            var coverId = IsEditionId(id) ? FirstEdition(doc)?.CoverId ?? doc.CoverId : doc.CoverId;
            return coverId > 0 ? coverId : null;
        }

        private static OpenLibrarySearchDoc? FirstEdition(OpenLibrarySearchDoc doc) => doc.Editions?.Docs?.FirstOrDefault();

        private static bool IsEditionId(string id) => id.EndsWith('M');

        /// <summary>"/works/OL66554W" -> "OL66554W"; null when the key is not an Open Library work or edition key.</summary>
        private static string? ToOpenLibraryId(string? key)
        {
            var last = key?.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            return BookIdFormat.IsOpenLibraryId(last) ? last : null;
        }

        /// <summary>User text as plain words, or null when nothing searchable is left.</summary>
        private static string? ToPlainTerms(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var plain = Whitespace().Replace(QuerySyntax().Replace(text, " "), " ").Trim();
            return plain.Length == 0 ? null : plain;
        }

        private static string ToSummary(string? text) =>
            string.IsNullOrWhiteSpace(text) ? NoSummary : Util.TruncateText(Whitespace().Replace(text, " ").Trim(), 200);

        // Characters with a meaning in Solr query syntax. They become spaces so the user's text is searched as
        // plain words and cannot change the query (an unbalanced parenthesis, for example, returns no results).
        [GeneratedRegex(@"[+\-!(){}\[\]^""~*?:\\/&|]")]
        private static partial Regex QuerySyntax();

        [GeneratedRegex(@"\s+")]
        private static partial Regex Whitespace();

        // Internet Archive identifiers only use these characters; anything else is not sent to archive.org.
        [GeneratedRegex("^[A-Za-z0-9._-]+$")]
        private static partial Regex ArchiveIdentifier();
    }
}
