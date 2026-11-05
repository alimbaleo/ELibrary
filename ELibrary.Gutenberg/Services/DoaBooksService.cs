using System.Text.Json;
using AngleSharp.Html.Parser;
using ELibrary.Core.Configuration;
using ELibrary.Core.Enums;
using ELibrary.Core.Helpers;
using ELibrary.Core.Models;
using ELibrary.Core.Services;
using ELibrary.Infrastructure.Configuration;
using ELibrary.Infrastructure.Doab;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ELibrary.Infrastructure.Services
{
    // Directory of Open Access Books. DOAB's REST API only serves allow-listed addresses, so search
    // and lookups come from the local catalogue that DoabCatalogueSyncService harvests over OAI-PMH.
    public class DoaBooksService : LibraryServiceBase
    {
        private const string ProviderName = "DOAB";
        private const string CatalogueLoading = "The DOAB catalogue is still loading; try again in a few minutes";

        private readonly IDoabCatalogueStore _catalogue;
        private readonly DoabClient _doab;
        private readonly DoaBooksSettings _settings;

        public override BookSource Source => BookSource.DOA;

        public DoaBooksService(
            IDoabCatalogueStore catalogue,
            DoabClient doab,
            IApiClient client,
            IOptions<DoaBooksSettings> settings,
            IOptions<ELibrarySettings> apiSettings,
            ILogger<DoaBooksService> logger)
            : base(client, logger, apiSettings.Value)
        {
            _catalogue = catalogue;
            _doab = doab;
            _settings = settings.Value;
        }

        public override async Task<Response<string>> GetBookById(string id)
        {
            var summary = await GetBookSummaryById(id);
            if (!summary.Success)
                return Response<string>.Failed(summary);

            return string.IsNullOrEmpty(summary.Data.ViewLink)
                ? Response<string>.NotFound($"Book content not found for book {id}")
                : Response<string>.Ok(summary.Data.ViewLink, "Book content retrieved successfully");
        }

        public override async Task<Response<BookSummary>> GetBookSummaryById(string id)
        {
            try
            {
                var lookup = FindInCatalogue(id);
                if (!lookup.Success)
                    return Response<BookSummary>.Failed(lookup);

                var book = lookup.Data;
                if (string.IsNullOrEmpty(book.DownloadUrl))
                {
                    Logger.LogWarning("DOAB book {Id} has no download link", id);
                    return Response<BookSummary>.NotFound($"No download link available for book {id}");
                }

                var pdfLink = NeedsResolving(book.DownloadUrl)
                    ? await ResolvePdfLinkOrFallback(book.DownloadUrl, id)
                    : book.DownloadUrl;

                var summary = new BookSummary
                {
                    Id = book.Uuid,
                    Title = book.Title,
                    Authors = book.Authors.ToList(),
                    Summary = SummaryOf(book),
                    ImageUrl = ImageUrlFor(book.Uuid),
                    Source = Source,
                    ViewLink = Util.IframeViewerHtml(pdfLink, book.Title)
                };

                Logger.LogInformation("DOAB summary for {Id} retrieved: '{Title}'", id, summary.Title);
                return Response<BookSummary>.Ok(summary, "Book summary retrieved successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "DOAB summary for {Id} could not be retrieved", id);
                return Response<BookSummary>.FromProviderException(ex, ProviderName, $"Book with id {id} not found");
            }
        }

        public override async Task<Response<FileContentResult>> GetImageById(string id)
        {
            try
            {
                var lookup = FindInCatalogue(id);
                if (!lookup.Success)
                    return Response<FileContentResult>.Failed(lookup);

                var book = lookup.Data;
                var thumbnailUrl = book.ThumbnailUrl;

                // A few harvested records lack a thumbnail URL; fetch it once over OAI-PMH and remember it
                if (string.IsNullOrEmpty(thumbnailUrl) && !string.IsNullOrEmpty(book.Handle))
                {
                    thumbnailUrl = (await _doab.GetRecordAsync(book.Handle, CancellationToken.None))?.ThumbnailUrl;
                    if (!string.IsNullOrEmpty(thumbnailUrl))
                        _catalogue.SetThumbnailUrl(book.Uuid, thumbnailUrl);
                }

                if (string.IsNullOrEmpty(thumbnailUrl))
                {
                    Logger.LogWarning("DOAB book {Id} has no thumbnail", id);
                    return Response<FileContentResult>.NotFound($"Image not found for book {id}");
                }

                var (bytes, contentType) = await _doab.DownloadAsync(thumbnailUrl, CancellationToken.None);
                Logger.LogInformation("DOAB image for {Id} downloaded ({Bytes} bytes, {ContentType})", id, bytes.Length, contentType);

                var extension = contentType.Contains("png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpg";
                var file = new FileContentResult(bytes, contentType) { FileDownloadName = $"book_{book.Uuid}.{extension}" };
                return Response<FileContentResult>.Ok(file, "Image retrieved successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "DOAB image for {Id} could not be retrieved", id);
                return Response<FileContentResult>.FromProviderException(ex, ProviderName, $"Image not found for book {id}");
            }
        }

        public override Task<Response<SearchBookResponse>> SearchBooks(int? page, string searchText)
            => Search(page, searchText, DoabSearchField.TitleOrAuthor, SearchRoute, SearchParameter);

        public override Task<Response<SearchBookResponse>> SearchBooksByTopic(int? page, string topic)
            => Search(page, topic, DoabSearchField.Subject, TopicRoute, TopicParameter);

        private Task<Response<SearchBookResponse>> Search(int? page, string term, DoabSearchField field, string route, string parameter)
        {
            if (string.IsNullOrWhiteSpace(term))
                return Task.FromResult(Response<SearchBookResponse>.Fail("Book search text not found"));

            var currentPage = NormalizePage(page);
            var limit = _settings.MaxResults;

            try
            {
                var (items, total) = _catalogue.Search(term, field, limit, (currentPage - 1) * limit);

                if (total == 0 && !_catalogue.IsReady)
                    Logger.LogWarning("DOAB search for '{Term}' found nothing: catalogue still loading ({Count} items so far)", term, _catalogue.Count());
                else
                    Logger.LogInformation("DOAB returned {Count} of {Total} books for {Field} '{Term}' page {Page}", items.Count, total, field, term, currentPage);

                return Task.FromResult(Response<SearchBookResponse>.Ok(new SearchBookResponse
                {
                    Count = total,
                    NextPageUrl = currentPage * limit < total ? PageLink(route, parameter, term, currentPage + 1) : null,
                    PreviousPageUrl = currentPage > 1 ? PageLink(route, parameter, term, currentPage - 1) : null,
                    Data = items.Select(ToBook).ToList()
                }, "Books retrieved successfully"));
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "DOAB {Field} search failed for '{Term}' page {Page}", field, term, currentPage);
                return Task.FromResult(Response<SearchBookResponse>.UpstreamError("DOAB search failed"));
            }
        }

        private Response<DoabCatalogueEntry> FindInCatalogue(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out _))
                return Response<DoabCatalogueEntry>.NotFound($"Book with id {id} not found");

            var book = _catalogue.Get(id);
            if (book != null)
                return Response<DoabCatalogueEntry>.Ok(book);

            if (_catalogue.IsReady)
                return Response<DoabCatalogueEntry>.NotFound($"Book with id {id} not found");

            Logger.LogWarning("DOAB book {Id} not in catalogue yet: still loading ({Count} items so far)", id, _catalogue.Count());
            return Response<DoabCatalogueEntry>.UpstreamError(CatalogueLoading);
        }

        private Data ToBook(DoabCatalogueEntry book) => new()
        {
            Id = book.Uuid,
            Title = book.Title,
            Authors = book.Authors.ToList(),
            Summary = SummaryOf(book),
            ImageUrl = ImageUrlFor(book.Uuid),
            Source = Source
        };

        private static string SummaryOf(DoabCatalogueEntry book)
            => string.IsNullOrWhiteSpace(book.Abstract) ? NoSummary : Util.TruncateText(book.Abstract, SummaryLength);

        private static bool NeedsResolving(string link)
            => link.Contains("mdpi.com", StringComparison.OrdinalIgnoreCase)
               || link.Contains("doi.org", StringComparison.OrdinalIgnoreCase);

        // Falls back to the original link so the reader at least opens the publisher's page
        private async Task<string> ResolvePdfLinkOrFallback(string link, string id)
        {
            try
            {
                var resolved = link.Contains("mdpi.com", StringComparison.OrdinalIgnoreCase)
                    ? await ScrapeMdpiDownloadLink(link)
                    : await ResolveThroughRewriteService(link);

                if (!string.IsNullOrEmpty(resolved))
                    return resolved;

                Logger.LogWarning("DOAB book {Id}: no direct PDF found behind {Link}; using the original link", id, link);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "DOAB book {Id}: resolving {Link} failed; using the original link", id, link);
            }

            return link;
        }

        // mdpi.com serves a landing page whose "Free Download" anchor holds the PDF, and only
        // renders it for browser-like requests.
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
            if (string.IsNullOrEmpty(_settings.RewriteBaseUrl)) return null;

            var json = await Client.GetStringAsync(_settings.RewriteBaseUrl + Uri.EscapeDataString(link));
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("pdf", out var pdfs) || pdfs.GetArrayLength() == 0)
                return null;

            return pdfs[0].GetProperty("link").GetString();
        }
    }
}
