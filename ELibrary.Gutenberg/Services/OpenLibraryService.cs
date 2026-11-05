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
    // Search comes from openlibrary.org; metadata, covers and the embedded reader from archive.org.
    // Book ids are Internet Archive identifiers. Open Library work/edition keys (OL…W / OL…M),
    // which earlier releases handed out, are accepted and resolved to the work's public scan.
    public class OpenLibraryService : LibraryServiceBase
    {
        private const string ProviderName = "Open Library";
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
            try
            {
                var archiveId = await ResolveArchiveId(id);
                if (archiveId == null)
                    return Response<string>.NotFound($"Book with id {id} not found");

                var summary = await GetBookSummaryById(archiveId);
                var title = summary.Success ? summary.Data.Title : UnknownTitle;

                return Response<string>.Ok(Util.IframeViewerHtml(ReaderUrl(archiveId), title), "Book content retrieved successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Open Library book {Id} could not be retrieved", id);
                return Response<string>.FromProviderException(ex, ProviderName, $"Book with id {id} not found");
            }
        }

        public override async Task<Response<BookSummary>> GetBookSummaryById(string id)
        {
            try
            {
                var archiveId = await ResolveArchiveId(id);
                if (archiveId == null)
                    return Response<BookSummary>.NotFound($"Book with id {id} not found");

                var metadata = await Client.GetAsync<ArchiveMetadataResponse>($"{_settings.ArchiveUrl}/metadata/{Uri.EscapeDataString(archiveId)}/metadata");
                var item = metadata?.Result;
                if (item == null || string.IsNullOrEmpty(item.Identifier))
                {
                    Logger.LogWarning("Archive item {ArchiveId} has no metadata", archiveId);
                    return Response<BookSummary>.NotFound($"Book with id {id} not found");
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
                    ViewLink = ReaderUrl(archiveId)
                };

                Logger.LogInformation("Archive summary for {Id} retrieved: '{Title}'", id, summary.Title);
                return Response<BookSummary>.Ok(summary, "Book summary retrieved successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Archive summary for {Id} could not be retrieved", id);
                return Response<BookSummary>.FromProviderException(ex, ProviderName, $"Book with id {id} not found");
            }
        }

        public override async Task<Response<FileContentResult>> GetImageById(string id)
        {
            try
            {
                var archiveId = await ResolveArchiveId(id);
                if (archiveId == null)
                    return Response<FileContentResult>.NotFound($"Image not found for book {id}");

                return await DownloadImage($"{_settings.ArchiveUrl}/services/img/{Uri.EscapeDataString(archiveId)}", id);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Open Library image for {Id} could not be retrieved", id);
                return Response<FileContentResult>.FromProviderException(ex, ProviderName, $"Image not found for book {id}");
            }
        }

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
                return Response<SearchBookResponse>.FromProviderException(ex, ProviderName, "No books found");
            }
        }

        // An archive identifier is returned as-is; an OL…W/OL…M key is looked up and the work's
        // first public scan is used. Null when the key has no readable scan.
        private async Task<string?> ResolveArchiveId(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            id = id.Trim();
            if (!BookIdFormat.IsOpenLibraryKey(id)) return id;

            var keyQuery = id.EndsWith('M') ? $"edition_key:{id}" : $"key:/works/{id}";
            var url = $"{_settings.SearchUrl}?q={Uri.EscapeDataString($"{keyQuery} AND ebook_access:public")}&fields=ia&limit=1";

            var result = await Client.GetAsync<OpenLibrarySearchResponse>(url);
            var archiveId = result?.Docs?.FirstOrDefault()?.Ia?.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

            if (archiveId == null)
                Logger.LogWarning("Open Library key {Key} has no public scan", id);
            else
                Logger.LogInformation("Open Library key {Key} resolved to archive item {ArchiveId}", id, archiveId);

            return archiveId;
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

        private string ReaderUrl(string archiveId) => $"{_settings.ArchiveUrl}/embed/{Uri.EscapeDataString(archiveId)}";
    }
}
