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
    public class GoogleService : LibraryServiceBase
    {
        private const string FullViewability = "ALL_PAGES";

        private readonly GoogleBooksSettings _settings;

        public override BookSource Source => BookSource.Google;

        public GoogleService(
            IApiClient client,
            IOptions<GoogleBooksSettings> settings,
            IOptions<ELibrarySettings> apiSettings,
            ILogger<GoogleService> logger)
            : base(client, logger, apiSettings.Value)
        {
            _settings = settings.Value;
        }

        public override async Task<Response<string>> GetBookById(string id)
        {
            try
            {
                var volume = await GetVolume(id);
                if (volume == null)
                    return Response<string>.Fail($"Book with id {id} not found");

                if (!IsFullyViewable(volume))
                {
                    Logger.LogWarning("Google volume {Id} is not fully viewable (embeddable: {Embeddable}, viewability: {Viewability})",
                        id, volume.AccessInfo?.Embeddable, volume.AccessInfo?.Viewability);
                    return Response<string>.Fail($"Book content not found for book {id}");
                }

                return Response<string>.Ok(ViewerHtml(id, volume.VolumeInfo.Title ?? UnknownTitle), "Book content retrieved successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Google book {Id} could not be retrieved", id);
                return Response<string>.Fail("An error occurred getting book");
            }
        }

        public override async Task<Response<BookSummary>> GetBookSummaryById(string id)
        {
            try
            {
                var volume = await GetVolume(id);
                if (volume == null)
                    return Response<BookSummary>.Fail($"Book with id {id} not found");

                var summary = new BookSummary
                {
                    Id = id,
                    Title = volume.VolumeInfo.Title ?? UnknownTitle,
                    Authors = volume.VolumeInfo.Authors ?? new List<string>(),
                    Summary = SummaryOf(volume),
                    ImageUrl = ImageUrlFor(id),
                    Source = Source,
                    IsPartial = !IsFullyViewable(volume)
                };

                Logger.LogInformation("Google summary for {Id} retrieved: '{Title}' (partial: {IsPartial})", id, summary.Title, summary.IsPartial);
                return Response<BookSummary>.Ok(summary, "Book summary retrieved successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Google summary for {Id} could not be retrieved", id);
                return Response<BookSummary>.Fail("An error occurred getting book summary");
            }
        }

        public override async Task<Response<FileContentResult>> GetImageById(string id)
        {
            try
            {
                var volume = await GetVolume(id);
                var imageUrl = BestImageUrl(volume?.VolumeInfo.ImageLinks);
                if (string.IsNullOrEmpty(imageUrl))
                {
                    Logger.LogWarning("Google volume {Id} has no cover image", id);
                    return Response<FileContentResult>.Fail($"Image not found for book {id}");
                }

                return await DownloadImage(imageUrl, id);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Google image for {Id} could not be retrieved", id);
                return Response<FileContentResult>.Fail($"An error occurred getting image for book {id}");
            }
        }

        public override Task<Response<SearchBookResponse>> SearchBooks(int? page, string searchText)
            => Search(page, searchText, searchText, SearchRoute, SearchParameter);

        public override Task<Response<SearchBookResponse>> SearchBooksByTopic(int? page, string topic)
            => Search(page, topic, $"subject:{topic}", TopicRoute, TopicParameter);

        private async Task<Response<SearchBookResponse>> Search(int? page, string term, string query, string route, string parameter)
        {
            if (string.IsNullOrWhiteSpace(term))
                return Response<SearchBookResponse>.Fail("Book search text not found");

            var currentPage = NormalizePage(page);
            var startIndex = (currentPage - 1) * _settings.MaxResults;

            try
            {
                var url = WithApiKey($"{_settings.BaseUrl}/volumes?q={Uri.EscapeDataString(query)}&startIndex={startIndex}&maxResults={_settings.MaxResults}&filter=ebooks");
                var result = await Client.GetAsync<GoogleBooksResponse>(url);

                var items = result?.Items ?? new List<GoogleBookItem>();
                var viewable = items.Where(IsFullyViewable).ToList();
                var totalPages = (int)Math.Ceiling((result?.TotalItems ?? 0) / (double)_settings.MaxResults);

                Logger.LogInformation("Google returned {Total} items for '{Term}' page {Page}/{TotalPages}; {Viewable} fully viewable",
                    items.Count, term, currentPage, totalPages, viewable.Count);

                return Response<SearchBookResponse>.Ok(new SearchBookResponse
                {
                    Count = viewable.Count,
                    NextPageUrl = currentPage < totalPages ? PageLink(route, parameter, term, currentPage + 1) : null,
                    PreviousPageUrl = currentPage > 1 ? PageLink(route, parameter, term, currentPage - 1) : null,
                    Data = viewable.Select(ToBook).ToList()
                }, "Books retrieved successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Google search failed for '{Term}' page {Page}", term, currentPage);
                return Response<SearchBookResponse>.Fail("An error occurred getting books");
            }
        }

        private Task<GoogleBookItem?> GetVolume(string id)
            => Client.GetAsync<GoogleBookItem>(WithApiKey($"{_settings.BaseUrl}/volumes/{Uri.EscapeDataString(id)}"));

        private string WithApiKey(string url)
            => string.IsNullOrEmpty(_settings.ApiKey) ? url : $"{url}{(url.Contains('?') ? '&' : '?')}key={_settings.ApiKey}";

        private Data ToBook(GoogleBookItem volume) => new()
        {
            Id = volume.Id,
            Title = volume.VolumeInfo.Title ?? UnknownTitle,
            Authors = volume.VolumeInfo.Authors ?? new List<string>(),
            Summary = SummaryOf(volume),
            ImageUrl = ImageUrlFor(volume.Id),
            Source = Source,
            IsPartial = !IsFullyViewable(volume)
        };

        private static string SummaryOf(GoogleBookItem volume)
        {
            var text = Util.TruncateText(volume.VolumeInfo.Description ?? volume.VolumeInfo.Subtitle, SummaryLength);
            return string.IsNullOrEmpty(text) ? NoSummary : text;
        }

        private static bool IsFullyViewable(GoogleBookItem volume)
            => volume.AccessInfo is { Embeddable: true } access
               && string.Equals(access.Viewability, FullViewability, StringComparison.OrdinalIgnoreCase);

        private static string? BestImageUrl(GoogleImageLinks? links)
        {
            var url = links?.Large ?? links?.Medium ?? links?.Small ?? links?.Thumbnail;
            return url?.Replace("http://", "https://");
        }

        private static string ViewerHtml(string volumeId, string title)
        {
            return $@"<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"" />
    <title>{System.Net.WebUtility.HtmlEncode(title)}</title>
    <script type=""text/javascript"" src=""https://www.google.com/books/jsapi.js""></script>
    <script type=""text/javascript"">
        google.books.load();
        function initialize() {{
            var viewer = new google.books.DefaultViewer(document.getElementById('viewerCanvas'));
            viewer.load('{volumeId}', function () {{ alert('Could not embed the book.'); }});
        }}
        google.books.setOnLoadCallback(initialize);
    </script>
</head>
<body>
    <div id=""viewerCanvas"" style=""width: 100%; min-height: 100%""></div>
</body>
</html>";
        }
    }
}
