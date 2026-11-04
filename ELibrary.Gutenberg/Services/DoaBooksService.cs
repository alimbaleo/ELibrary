using AngleSharp.Html.Parser;
using ELibrary.Core.Enums;
using ELibrary.Core.Helpers;
using ELibrary.Core.Interfaces;
using ELibrary.Core.Models;
using ELibrary.Infrastructure.Doab;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace ELibrary.Infrastructure.Services
{
    /// <summary>
    /// DOAB (Directory of Open Access Books) provider.
    /// DOAB's REST API only serves allow-listed addresses, so search and lookups are served from a local
    /// copy of the catalogue (<see cref="IDoabCatalogueStore"/>) that <see cref="DoabCatalogueSyncService"/>
    /// harvests from DOAB's public OAI-PMH feed and keeps current.
    /// </summary>
    public class DoaBooksService : ILibraryService
    {
        private const string CatalogueLoadingMessage = "The DOAB catalogue is still loading; try again in a few minutes";

        private readonly IDoabCatalogueStore _catalogue;
        private readonly DoabClient _doab;
        private readonly ILogger<DoaBooksService> _logger;

        private readonly string _eLibraryBaseUrl;
        private readonly string _rewriteBaseUrl;
        private readonly int _maxResult;

        public BookSource BOOK_SOURCE => BookSource.DOA;


        public DoaBooksService(IDoabCatalogueStore catalogue, DoabClient doab, IConfiguration config, ILogger<DoaBooksService> logger)
        {
            _catalogue = catalogue;
            _doab = doab;
            _logger = logger;

            _maxResult = config.GetValue<int>("DoaBooks:MaxResults", 10);
            _eLibraryBaseUrl = config.GetValue<string>("ElibraryBaseUrl") ?? string.Empty;
            _rewriteBaseUrl = config.GetValue<string>("RewriteBaseUrl") ?? string.Empty;
        }



        public async Task<Response<string>> GetBookById(string id)
        {
            try
            {
                var book = await GetBookSummaryById(id);
                if (!book.Success)
                    return Response<string>.Failed(book);

                return string.IsNullOrEmpty(book.Data.viewLink)
                    ? Response<string>.NotFound($"Book content not found for book {id}")
                    : new Response<string>(book.Data.viewLink);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "DoaBooksService[GetBookById] failed for book {Id}", id);
                return Response<string>.FromProviderException(e, "DOAB", $"Book with ID {id} not found");
            }
        }



        public async Task<Response<BookSummary>> GetBookSummaryById(string id)
        {
            try
            {
                var lookup = FindInCatalogue(id);
                if (!lookup.Success)
                    return Response<BookSummary>.Failed(lookup);

                var book = lookup.Data;
                var pdfLink = book.DownloadUrl;
                if (string.IsNullOrEmpty(pdfLink))
                    return Response<BookSummary>.NotFound($"No download link available for book {id}");

                if (pdfLink.Contains("mdpi.com", StringComparison.OrdinalIgnoreCase) || pdfLink.Contains("https://doi.org", StringComparison.OrdinalIgnoreCase) || pdfLink.Contains("dx.doi.org", StringComparison.OrdinalIgnoreCase))
                {
                    pdfLink = await ResolvePdfLinkOrFallbackAsync(pdfLink, id);
                }

                var summary = new BookSummary
                {
                    Id = book.Uuid,
                    Title = book.Title,
                    Authors = book.Authors.ToList(),
                    Summary = SummaryText(book),
                    ImageUrl = _eLibraryBaseUrl + $"/Image/{book.Uuid}",
                    viewLink = ViewBook(pdfLink),
                };

                return new Response<BookSummary>(summary, "Book summary retrieved successfully", true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DoaBooksService[GetBookSummaryById] failed for book {Id}", id);
                return Response<BookSummary>.FromProviderException(ex, "DOAB", $"Book with ID {id} not found");
            }
        }



        public async Task<Response<FileContentResult>> GetImageById(string id)
        {
            try
            {
                var lookup = FindInCatalogue(id);
                if (!lookup.Success)
                    return Response<FileContentResult>.Failed(lookup);

                var book = lookup.Data;
                var thumbnailUrl = book.ThumbnailUrl;

                // A few harvested records come without a thumbnail URL; look it up once via OAI-PMH and cache it.
                if (string.IsNullOrEmpty(thumbnailUrl) && !string.IsNullOrEmpty(book.Handle))
                {
                    thumbnailUrl = (await _doab.GetRecordAsync(book.Handle, CancellationToken.None))?.ThumbnailUrl;
                    if (!string.IsNullOrEmpty(thumbnailUrl))
                        _catalogue.SetThumbnailUrl(book.Uuid, thumbnailUrl);
                }

                if (string.IsNullOrEmpty(thumbnailUrl))
                    return Response<FileContentResult>.NotFound($"Image not found for book {id}");

                var (bytes, contentType) = await _doab.DownloadAsync(thumbnailUrl, CancellationToken.None);
                var fileContent = new FileContentResult(bytes, contentType)
                {
                    FileDownloadName = $"book_{book.Uuid}{(contentType.Contains("png") ? ".png" : ".jpg")}"
                };

                return new Response<FileContentResult>(fileContent, "Image retrieved successfully", true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DoaBooksService[GetImageById] failed for book {Id}", id);
                return Response<FileContentResult>.FromProviderException(ex, "DOAB", $"Image not found for book {id}");
            }
        }



        public Task<Response<SearchBookResponse>> SearchBooks(int? page, string searchText) =>
            Search(page, searchText, DoabSearchField.TitleOrAuthor, "Search", "SearchText");



        public Task<Response<SearchBookResponse>> SearchBooksByTopic(int? page, string topic) =>
            Search(page, topic, DoabSearchField.Subject, "SearchTopic", "Topic");



        private Task<Response<SearchBookResponse>> Search(int? page, string text, DoabSearchField field, string action, string textParam)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text))
                    return Task.FromResult(new Response<SearchBookResponse>(null!, "Book search text not found", false));

                var currentPage = Math.Max(page.GetValueOrDefault(1), 1);
                var (books, total) = _catalogue.Search(text, field, _maxResult, (currentPage - 1) * _maxResult);

                if (total == 0 && !_catalogue.IsReady)
                    _logger.LogWarning("DOAB search for {Text} returned nothing because the catalogue is still loading", text);

                var encoded = Uri.EscapeDataString(text);
                var response = new SearchBookResponse
                {
                    Count = total,
                    NextPageUrl = currentPage * _maxResult < total
                        ? $"{_eLibraryBaseUrl}/{action}?Page={currentPage + 1}&{textParam}={encoded}"
                        : null,
                    PreviousPageUrl = currentPage > 1
                        ? $"{_eLibraryBaseUrl}/{action}?Page={currentPage - 1}&{textParam}={encoded}"
                        : null,
                    Data = books.Select(b => new Data
                    {
                        Id = b.Uuid,
                        Title = b.Title,
                        Authors = b.Authors.ToList(),
                        Summary = SummaryText(b),
                        ImageUrl = _eLibraryBaseUrl + $"/Image/{b.Uuid}",
                        Source = BOOK_SOURCE,
                    }).ToList()
                };

                return Task.FromResult(new Response<SearchBookResponse>(response, "Books retrieved successfully", true));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DoaBooksService[Search] failed for {Field} search: {Text}", field, text);
                return Task.FromResult(new Response<SearchBookResponse>(null!, "DOAB search failed", false, ErrorType.Upstream));
            }
        }



        private Response<DoabCatalogueEntry> FindInCatalogue(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out _))
                return Response<DoabCatalogueEntry>.NotFound($"Book with ID {id} not found");

            var book = _catalogue.Get(id);
            if (book != null)
                return new Response<DoabCatalogueEntry>(book);

            return _catalogue.IsReady
                ? Response<DoabCatalogueEntry>.NotFound($"Book with ID {id} not found")
                : Response<DoabCatalogueEntry>.UpstreamError(CatalogueLoadingMessage);
        }



        private static string SummaryText(DoabCatalogueEntry book) =>
            string.IsNullOrWhiteSpace(book.Abstract) ? "No Summary Available" : Util.TruncateText(book.Abstract, 200);



        private string ViewBook(string? pdfUrl)
        {
            var html = $@"
                <!DOCTYPE html>
                <html>
                <head>
                    <title>PDF Viewer</title>
                </head>
                <body style='margin:0;padding:0;'>
                    <iframe 
                        src='{pdfUrl}' 
                        style='width:100%;height:100vh;border:none;'>
                    </iframe>
                </body>
                </html>
            ";
            return html;
        }

        /// <summary>
        /// Tries to turn an mdpi/doi.org landing-page link into a direct PDF link. If that fails
        /// (scrape finds nothing, or the rewrite service is down), returns the original link so the
        /// reader still opens the publisher's page instead of the API reporting the book as not found.
        /// </summary>
        private async Task<string> ResolvePdfLinkOrFallbackAsync(string pdfLink, string id)
        {
            try
            {
                var resolved = await GetDownloadPdfLinkAsync(pdfLink);
                if (!string.IsNullOrEmpty(resolved))
                    return resolved;

                _logger.LogWarning("DoaBooksService: could not resolve a direct PDF link for book {Id} from {Link}; using the original link", id, pdfLink);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DoaBooksService: PDF link resolution failed for book {Id} from {Link}; using the original link", id, pdfLink);
            }

            return pdfLink;
        }

        public async Task<string?> GetDownloadPdfLinkAsync(string pdfUrl)
        {
            if (pdfUrl.Contains("mdpi.com", StringComparison.OrdinalIgnoreCase))
            {
                using var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = true,
                    AutomaticDecompression = System.Net.DecompressionMethods.All
                };

                using var client = new HttpClient(handler);

                // Required headers to avoid 403
                client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.5");
                client.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
                client.DefaultRequestHeaders.Add("Referer", "https://www.mdpi.com/");  // IMPORTANT for pdfview
                client.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "document");
                client.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "navigate");
                client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "none");
                client.DefaultRequestHeaders.Add("Sec-Fetch-User", "?1");

                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36"
                );

                var html = await client.GetStringAsync(pdfUrl);

                var parser = new HtmlParser();
                var doc = await parser.ParseDocumentAsync(html);

                // 🔍 Find <a> whose text contains "Free Download"
                var anchor = doc.QuerySelectorAll("a")
                                .FirstOrDefault(a =>
                                    a.TextContent.Contains("Free Download", StringComparison.OrdinalIgnoreCase));

                if (anchor == null)
                    return null;

                var href = anchor.GetAttribute("href");
                if (href == null)
                    return null;

                // Make href absolute if needed
                if (!href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    href = new Uri(new Uri(pdfUrl), href).ToString();
                }

                return href;
            }
            else
            {
                using var client = new HttpClient();

                var baseUrl = _rewriteBaseUrl;
                var requestUrl = baseUrl + Uri.EscapeDataString(pdfUrl);

                var json = await client.GetStringAsync(requestUrl);

                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("pdf", out var pdfArray))
                    return null;

                if (pdfArray.GetArrayLength() == 0)
                    return null;

                var pdfLink = pdfArray[0].GetProperty("link").GetString();

                return pdfLink;
            }
            
        }

        private async Task<string?> ResolveDoiWithRedirectAsync(string doiUrl)
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler);

            // Use a clean, single, up-to-date browser User-Agent
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36"
            );
            client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");

            var response = await client.GetAsync(doiUrl);

            if (response.Headers.Location != null)
            {
                return response.Headers.Location.ToString();
            }

            return response.StatusCode == System.Net.HttpStatusCode.OK ? doiUrl : null;
        }

        
    }
}
