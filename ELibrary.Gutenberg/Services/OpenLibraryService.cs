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
    // Book ids are Internet Archive identifiers; search comes from openlibrary.org,
    // metadata, covers and the embedded reader from archive.org.
    public class OpenLibraryService : LibraryServiceBase
    {
        private const string SearchFields = "key,title,author_name,first_sentence,cover_i,ia";

        private readonly OpenLibrarySettings _settings;

        public override BookSource Source => BookSource.OpenLibrary;

        public OpenLibraryService(
            IApiClient client,
            IOptions<OpenLibrarySettings> settings,
            IOptions<ELibrarySettings> apiSettings,
            ILogger<OpenLibraryService> logger)
            : base(client, logger, apiSettings.Value)
        {
            _settings = settings.Value;
            _settings.ArchiveUrl = _settings.ArchiveUrl.TrimEnd('/');
        }

        public override async Task<Response<string>> GetBookById(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return Response<string>.Fail("Book id is required");

            var summary = await GetBookSummaryById(id);
            var title = summary.Success ? summary.Data.Title : UnknownTitle;

            return Response<string>.Ok(Util.IframeViewerHtml(ReaderUrl(id), title), "Book content retrieved successfully");
        }

        public override async Task<Response<BookSummary>> GetBookSummaryById(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return Response<BookSummary>.Fail("Book id is required");

            try
            {
                var metadata = await Client.GetAsync<ArchiveMetadataResponse>($"{_settings.ArchiveUrl}/metadata/{Uri.EscapeDataString(id)}/metadata");
                var item = metadata?.Result;
                if (item == null || string.IsNullOrEmpty(item.Identifier))
                {
                    Logger.LogWarning("Archive item {Id} has no metadata", id);
                    return Response<BookSummary>.Fail($"Book with id {id} not found");
                }

                var description = Util.TruncateText(Util.StripHtml(item.DescriptionText()), SummaryLength);
                var summary = new BookSummary
                {
                    Id = id,
                    Title = item.Title ?? UnknownTitle,
                    Authors = item.Creators(),
                    Summary = string.IsNullOrEmpty(description) ? NoSummary : description,
                    ImageUrl = ImageUrlFor(id),
                    Source = Source,
                    ViewLink = ReaderUrl(id)
                };

                Logger.LogInformation("Archive summary for {Id} retrieved: '{Title}'", id, summary.Title);
                return Response<BookSummary>.Ok(summary, "Book summary retrieved successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Archive summary for {Id} could not be retrieved", id);
                return Response<BookSummary>.Fail("An error occurred getting book summary");
            }
        }

        public override Task<Response<FileContentResult>> GetImageById(string id)
            => DownloadImage($"{_settings.ArchiveUrl}/services/img/{Uri.EscapeDataString(id)}", id);

        public override Task<Response<SearchBookResponse>> SearchBooks(int? page, string searchText)
            => Search(page, searchText, "q", SearchRoute, SearchParameter);

        public override Task<Response<SearchBookResponse>> SearchBooksByTopic(int? page, string topic)
            => Search(page, topic, "subject", TopicRoute, TopicParameter);

        private async Task<Response<SearchBookResponse>> Search(int? page, string term, string queryParameter, string route, string routeParameter)
        {
            if (string.IsNullOrWhiteSpace(term))
                return Response<SearchBookResponse>.Fail("Book search text not found");

            var currentPage = NormalizePage(page);

            try
            {
                var url = $"{_settings.SearchUrl}?{queryParameter}={Uri.EscapeDataString(term)}" +
                          $"&has_fulltext=true&public_scan_b=true&page={currentPage}&limit={_settings.MaxResults}&fields={SearchFields}";

                var result = await Client.GetAsync<OpenLibrarySearchResponse>(url);
                var docs = result?.Docs ?? new List<OpenLibraryDoc>();
                var total = result?.NumFound ?? 0;

                var books = docs
                    .Where(d => !string.IsNullOrWhiteSpace(d.Ia?.FirstOrDefault()))
                    .Select(ToBook)
                    .ToList();

                Logger.LogInformation("Open Library returned {Count} of {Total} books for '{Term}' page {Page}; {Skipped} skipped without an archive id",
                    books.Count, total, term, currentPage, docs.Count - books.Count);

                return Response<SearchBookResponse>.Ok(new SearchBookResponse
                {
                    Count = total,
                    NextPageUrl = currentPage * _settings.MaxResults < total ? PageLink(route, routeParameter, term, currentPage + 1) : null,
                    PreviousPageUrl = currentPage > 1 ? PageLink(route, routeParameter, term, currentPage - 1) : null,
                    Data = books
                }, "Books retrieved successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Open Library search failed for '{Term}' page {Page}", term, currentPage);
                return Response<SearchBookResponse>.Fail("An error occurred getting books");
            }
        }

        private Data ToBook(OpenLibraryDoc doc)
        {
            var id = doc.Ia![0];
            return new Data
            {
                Id = id,
                Title = doc.Title ?? UnknownTitle,
                Authors = doc.AuthorName ?? new List<string>(),
                Summary = doc.FirstSentence?.FirstOrDefault() ?? string.Empty,
                ImageUrl = ImageUrlFor(id),
                Source = Source
            };
        }

        private string ReaderUrl(string id) => $"{_settings.ArchiveUrl}/embed/{Uri.EscapeDataString(id)}";
    }
}
