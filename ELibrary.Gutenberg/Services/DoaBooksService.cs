using System.Text.Json;
using AngleSharp.Html.Parser;
using ELibrary.Core.Configuration;
using ELibrary.Core.Enums;
using ELibrary.Core.Helpers;
using ELibrary.Core.Models;
using ELibrary.Core.Services;
using ELibrary.Infrastructure.Configuration;
using ELibrary.Infrastructure.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ELibrary.Infrastructure.Services
{
    // Directory of Open Access Books (DSpace REST API). Currently not registered:
    // directory.doabooks.org rejects the server's IP with 403.
    public class DoaBooksService : LibraryServiceBase
    {
        private const string AuthorKey = "dc.contributor.author";
        private const string EditorKey = "dc.contributor.editor";
        private const string DescriptionKey = "dc.description.abstract";
        private const string DownloadKey = "oapen.identifier.downloadUrl";
        private const string ThumbnailBundle = "THUMBNAIL";

        private readonly DoaBooksSettings _settings;

        public override BookSource Source => BookSource.DOA;

        public DoaBooksService(
            IApiClient client,
            IOptions<DoaBooksSettings> settings,
            IOptions<ELibrarySettings> apiSettings,
            ILogger<DoaBooksService> logger)
            : base(client, logger, apiSettings.Value)
        {
            _settings = settings.Value;
        }

        public override async Task<Response<string>> GetBookById(string id)
        {
            var summary = await GetBookSummaryById(id);
            if (!summary.Success)
                return Response<string>.Fail(summary.Message);

            return string.IsNullOrEmpty(summary.Data.ViewLink)
                ? Response<string>.Fail($"Book content not found for book {id}")
                : Response<string>.Ok(summary.Data.ViewLink, "Book content retrieved successfully");
        }

        public override async Task<Response<BookSummary>> GetBookSummaryById(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return Response<BookSummary>.Fail("Book id is required");

            try
            {
                var book = await Client.GetAsync<DoaBookResponseModel>($"{_settings.BaseUrl}/rest/items/{id}?expand=all");
                if (book == null)
                    return Response<BookSummary>.Fail($"Book with id {id} not found");

                var pdfLink = book.Bitstreams
                    .SelectMany(b => b.Metadata)
                    .FirstOrDefault(m => m.Key == DownloadKey)?.Value?.ToString();

                if (string.IsNullOrEmpty(pdfLink))
                {
                    Logger.LogWarning("DOA book {Id} has no download link", id);
                    return Response<BookSummary>.Fail($"Book with id {id} not found");
                }

                if (NeedsResolving(pdfLink))
                {
                    var resolved = await ResolvePdfLink(pdfLink);
                    if (string.IsNullOrEmpty(resolved))
                    {
                        Logger.LogWarning("DOA book {Id} download link {Link} could not be resolved to a PDF", id, pdfLink);
                        return Response<BookSummary>.Fail($"Book with id {id} not found");
                    }
                    pdfLink = resolved;
                }

                var metadata = book.Metadata.Select(m => (m.Key, (object?)m.Value)).ToList();
                var summary = new BookSummary
                {
                    Id = id,
                    Title = book.Name ?? UnknownTitle,
                    Authors = AuthorsOf(metadata),
                    Summary = DescriptionOf(metadata),
                    ImageUrl = ImageUrlFor(ThumbnailId(book.Bitstreams.Select(b => (b.BundleName, b.Uuid)))),
                    Source = Source,
                    ViewLink = Util.IframeViewerHtml(pdfLink, book.Name ?? "PDF Viewer")
                };

                Logger.LogInformation("DOA summary for {Id} retrieved: '{Title}'", id, summary.Title);
                return Response<BookSummary>.Ok(summary, "Book summary retrieved successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "DOA summary for {Id} could not be retrieved", id);
                return Response<BookSummary>.Fail("An error occurred getting book summary");
            }
        }

        public override Task<Response<FileContentResult>> GetImageById(string id)
            => DownloadImage($"{_settings.ImageUrl}/{id}/retrieve", id);

        public override Task<Response<SearchBookResponse>> SearchBooks(int? page, string searchText)
            => Search(page, searchText, SearchRoute, SearchParameter);

        public override Task<Response<SearchBookResponse>> SearchBooksByTopic(int? page, string topic)
            => Search(page, topic, TopicRoute, TopicParameter);

        private async Task<Response<SearchBookResponse>> Search(int? page, string term, string route, string parameter)
        {
            if (string.IsNullOrWhiteSpace(term))
                return Response<SearchBookResponse>.Fail("Book search text not found");

            var currentPage = NormalizePage(page);
            var limit = _settings.MaxResults;
            var offset = (currentPage - 1) * limit;

            try
            {
                var query = string.Join("+", term.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                var url = $"{_settings.BaseUrl}/rest/search?query=dc.title:{query}&limit={limit}&offset={offset}&expand=metadata,bitstreams";

                var results = await Client.GetAsync<List<DoaBookResults>>(url) ?? new List<DoaBookResults>();
                var books = results.Select(ToBook).ToList();

                Logger.LogInformation("DOA returned {Count} books for '{Term}' page {Page}", books.Count, term, currentPage);

                return Response<SearchBookResponse>.Ok(new SearchBookResponse
                {
                    Count = books.Count,
                    NextPageUrl = books.Count == limit ? PageLink(route, parameter, term, currentPage + 1) : null,
                    PreviousPageUrl = currentPage > 1 ? PageLink(route, parameter, term, currentPage - 1) : null,
                    Data = books
                }, "Books retrieved successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "DOA search failed for '{Term}' page {Page}", term, currentPage);
                return Response<SearchBookResponse>.Fail("An error occurred getting books");
            }
        }

        private Data ToBook(DoaBookResults result)
        {
            var metadata = (result.Metadata ?? new List<DoaMetaData>()).Select(m => (m.Key, (object?)m.Value)).ToList();
            return new Data
            {
                Id = result.Uuid,
                Title = result.Name ?? UnknownTitle,
                Authors = AuthorsOf(metadata),
                Summary = DescriptionOf(metadata),
                ImageUrl = ImageUrlFor(ThumbnailId((result.Bitstreams ?? new List<BitstreamData>()).Select(b => (b.BundleName, b.Uuid)))),
                Source = Source
            };
        }

        // Editors stand in for authors only when no author is listed
        private static List<string> AuthorsOf(List<(string Key, object? Value)> metadata)
        {
            var key = metadata.Any(m => m.Key == AuthorKey) ? AuthorKey : EditorKey;
            return metadata
                .Where(m => m.Key == key)
                .Select(m => m.Value?.ToString() ?? string.Empty)
                .Where(v => !string.IsNullOrEmpty(v))
                .ToList();
        }

        private static string DescriptionOf(List<(string Key, object? Value)> metadata)
        {
            var text = Util.TruncateText(metadata.FirstOrDefault(m => m.Key == DescriptionKey).Value?.ToString(), SummaryLength);
            return string.IsNullOrEmpty(text) ? NoSummary : text;
        }

        private static string ThumbnailId(IEnumerable<(string BundleName, string Uuid)> bitstreams)
            => bitstreams.FirstOrDefault(b => b.BundleName == ThumbnailBundle).Uuid ?? string.Empty;

        private static bool NeedsResolving(string link)
            => link.Contains("mdpi.com", StringComparison.OrdinalIgnoreCase)
               || link.Contains("doi.org", StringComparison.OrdinalIgnoreCase);

        private async Task<string?> ResolvePdfLink(string link)
        {
            return link.Contains("mdpi.com", StringComparison.OrdinalIgnoreCase)
                ? await ScrapeMdpiDownloadLink(link)
                : await ResolveThroughRewriteService(link);
        }

        // mdpi.com serves a landing page; the PDF is behind its "Free Download" anchor
        // and the page only renders for browser-like requests.
        private static async Task<string?> ScrapeMdpiDownloadLink(string pageUrl)
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = System.Net.DecompressionMethods.All };
            using var browser = new HttpClient(handler);
            browser.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            browser.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.5");
            browser.DefaultRequestHeaders.Add("Referer", "https://www.mdpi.com/");
            browser.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36");

            var html = await browser.GetStringAsync(pageUrl);
            var document = await new HtmlParser().ParseDocumentAsync(html);

            var href = document.QuerySelectorAll("a")
                .FirstOrDefault(a => a.TextContent.Contains("Free Download", StringComparison.OrdinalIgnoreCase))
                ?.GetAttribute("href");

            if (string.IsNullOrEmpty(href)) return null;
            return href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : new Uri(new Uri(pageUrl), href).ToString();
        }

        private async Task<string?> ResolveThroughRewriteService(string link)
        {
            var json = await Client.GetStringAsync(_settings.RewriteBaseUrl + Uri.EscapeDataString(link));
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("pdf", out var pdfs) || pdfs.GetArrayLength() == 0)
                return null;

            return pdfs[0].GetProperty("link").GetString();
        }
    }
}
