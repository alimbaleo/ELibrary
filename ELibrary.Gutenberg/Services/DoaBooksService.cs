using AngleSharp.Html.Parser;
using ELibrary.Core.Constants;
using ELibrary.Core.Enums;
using ELibrary.Core.Helpers;
using ELibrary.Core.Interfaces;
using ELibrary.Core.Models;
using ELibrary.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Net;
using System.Text.Json;

namespace ELibrary.Infrastructure.Services
{
    public class DoaBooksService : ILibraryService
    {
        private readonly IApiClient _client;
        private readonly IConfiguration _config;
        private readonly ILogger<DoaBooksService> _logger;
        private readonly IMemoryCache _cache;

        private readonly string _doaBaseUrl = string.Empty;
        private readonly string _eLibraryBaseUrl = string.Empty;
        private readonly string _imageUrl = string.Empty;
        private readonly string _rewriteBaseUrl = string.Empty;
        private readonly int _maxResult = 0;

        public BookSource BOOK_SOURCE => BookSource.DOA;


        public DoaBooksService(IApiClient client, IConfiguration config, ILogger<DoaBooksService> logger, IMemoryCache cache)
        {
            _config = config;
            _client = client;
            _logger = logger;
             _cache = cache;

            _doaBaseUrl = _config.GetValue<string>("DoaBooks:BaseUrl") ?? string.Empty;
            _imageUrl = _config.GetValue<string>("DoaBooks:ImageUrl") ?? string.Empty;
            _maxResult = _config.GetValue<int>("DoaBooks:MaxResults", 10);
            _eLibraryBaseUrl = _config.GetValue<string>("ElibraryBaseUrl") ?? string.Empty;
            _rewriteBaseUrl = _config.GetValue<string>("RewriteBaseUrl") ?? string.Empty;
        }



        public async Task<Response<string>> GetBookById(string id)
        {
            try
            {
                if(string.IsNullOrEmpty(id))
                    return new Response<string>(null, $"Book with ID {id} not found", false);
                
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
                if(string.IsNullOrEmpty(id))
                    return new Response<BookSummary>(null, $"Book with ID {id} not found", false);
                
                _logger.LogInformation($"DoaBooksService[GetBookSummaryById] : Getting book content for book {id}");
                
                var url = $"{_doaBaseUrl}/rest/items/{id}?expand=all";
                var book = await _client.GetAsync<DoaBookResponseModel>(url);

                _logger.LogInformation(
                    $"DoaBooksService[GetBookSummaryById] : Response from api : {JsonConvert.SerializeObject(book)}");
                
                var pdfLink = string.Empty;
                
                foreach (var bit in book.Bitstreams.Where(bit => bit.Metadata.Any(x => x.Key == Constants.DownloadKey)))
                {
                    pdfLink = bit.Metadata.Where(x => x.Key == Constants.DownloadKey)?.FirstOrDefault()?.Value.ToString();
                    break;
                }

                if (string.IsNullOrEmpty(pdfLink))
                    return Response<BookSummary>.NotFound($"No download link available for book {id}");

                if (pdfLink.Contains("mdpi.com", StringComparison.OrdinalIgnoreCase) || pdfLink.Contains("https://doi.org", StringComparison.OrdinalIgnoreCase) || pdfLink.Contains("dx.doi.org", StringComparison.OrdinalIgnoreCase))
                {
                    pdfLink = await ResolvePdfLinkOrFallbackAsync(pdfLink, id);
                }
                
                var summary = new BookSummary
                {
                    Id = id,
                    Title = book.Name ?? "Unknown Title",
                    Authors = book.Metadata
                        .Where(m => m.Key == Constants.AuthorKey || (m.Key == Constants.EditorKey && !book.Metadata.Any(x => x.Key == Constants.AuthorKey)))
                        .Where(m => m.Key == Constants.AuthorKey || m.Key == Constants.EditorKey)
                        .Select(m => m.Value?.ToString() ?? string.Empty)
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToList(),
                    Summary = Util.TruncateText(book.Metadata.FirstOrDefault(x => x.Key == Constants.DescriptionKey)?.Value?.ToString(), 200) ?? "No Summary Available",
                    ImageUrl = _eLibraryBaseUrl + $"/Image/{book.Bitstreams.FirstOrDefault(x => x.BundleName == Constants.ThumbnailBundleName)?.Uuid}",
                    viewLink = ViewBook(pdfLink),
                };

                _logger.LogInformation(
                    $"DoaBooksService[GetBookSummaryById] : Response from api : {JsonConvert.SerializeObject(summary)}");
                
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
                if(string.IsNullOrEmpty(id))
                    return new Response<FileContentResult>(null, $"Book with ID {id} not found", false);
                
                _logger.LogInformation(
                    $"DoaBooksService[GetImageById] : Getting image content for book {id}");
                
                var imageUrl = $"{_imageUrl}/{id}/retrieve";
                using var client = new HttpClient();
                var bytes = await client.GetByteArrayAsync(imageUrl);

                var fileContent = new FileContentResult(bytes, "image/jpeg")
                {
                    FileDownloadName = $"book_{id}.jpg"
                };

                return new Response<FileContentResult>(fileContent, "Image retrieved successfully", true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DoaBooksService[GetImageById] failed for image {Id}", id);
                return Response<FileContentResult>.FromProviderException(ex, "DOAB", $"Image {id} not found");
            }
        }




        public async Task<Response<SearchBookResponse>> SearchBooks(int? page, string searchText)
        {
            try
            {
                if(string.IsNullOrEmpty(searchText))
                    return new Response<SearchBookResponse>(null, $"Book search text not found", false);
                
                var currentPage = page.GetValueOrDefault(1);
                if (currentPage < 1) currentPage = 1;

                var limit = _maxResult;
                var offset = (currentPage - 1) * limit;
                
                var split = searchText.Split(' ');
                var query = string.Join("+", split);
                
                var url = $"{_doaBaseUrl}/rest/search?query=dc.title:{query}&limit={limit}&offset={offset}&expand=metadata,bitstreams";

                _logger.LogInformation(
                    $"DoaBooksService[SearchBooks] : About to get book search result with url: {url}");
                
                var books = await _client.GetAsync<List<DoaBookResults>>(url) ?? new List<DoaBookResults>();
                
                var nextPageUrl = books.Count == limit
                    ? $"{_eLibraryBaseUrl}/books/search?searchText={searchText}&page={currentPage + 1}"
                    : null;

                var previousPageUrl = currentPage > 1
                    ? $"{_eLibraryBaseUrl}/books/search?searchText={searchText}&page={currentPage - 1}"
                    : null;
                
                
                var response = new Response<SearchBookResponse>(
                    new SearchBookResponse
                    {
                        Count = books.Count,
                        NextPageUrl = nextPageUrl,
                        PreviousPageUrl = previousPageUrl,
                        Data = books.Select(b => new Data
                        {
                            Id = b.Uuid,
                            Title = b.Name,
                            Authors = b.Metadata
                                .Where(m => m.Key == Constants.AuthorKey || (m.Key == Constants.EditorKey &&
                                                                             !b.Metadata.Any(x =>
                                                                                 x.Key == Constants.AuthorKey)))
                                .Where(m => m.Key == Constants.AuthorKey || m.Key == Constants.EditorKey)
                                .Select(m => m.Value?.ToString() ?? string.Empty)
                                .Where(s => !string.IsNullOrEmpty(s))
                                .ToList(),

                            Summary = b.Metadata.FirstOrDefault(x => x.Key == Constants.DescriptionKey)?.Value
                                ?.ToString() ?? string.Empty,
                            ImageUrl = _eLibraryBaseUrl + $"/Image/{b.Bitstreams.FirstOrDefault(x => x.BundleName == Constants.ThumbnailBundleName)?.Uuid}",
                            Source = BOOK_SOURCE,
                        }).ToList() ?? new List<Data>()
                    },
                    "Books retrieved successfully",
                    true
                );

                _logger.LogInformation(
                    $"DoaBooksService[SearchBooks] : Books retrieved successfully with search text: {searchText} and count {response.Data.Count}");
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DoaBooksService[SearchBooks] failed for search: {SearchText}", searchText);
                return Response<SearchBookResponse>.FromProviderException(ex, "DOAB", "No books found");
            }
        }



        public async Task<Response<SearchBookResponse>> SearchBooksByTopic(int? page, string topic)
        {
            return await SearchBooks(page, topic);
        }



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
