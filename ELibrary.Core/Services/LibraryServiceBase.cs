using ELibrary.Core.Configuration;
using ELibrary.Core.Enums;
using ELibrary.Core.Helpers;
using ELibrary.Core.Interfaces;
using ELibrary.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ELibrary.Core.Services
{
    public abstract class LibraryServiceBase : ILibraryService
    {
        protected const string UnknownTitle = "Unknown Title";
        protected const string NoSummary = "No Summary Available";
        protected const int SummaryLength = 200;

        protected const string SearchRoute = "Search";
        protected const string SearchParameter = "SearchText";
        protected const string TopicRoute = "SearchTopic";
        protected const string TopicParameter = "Topic";

        protected IApiClient Client { get; }
        protected ILogger Logger { get; }
        protected string ApiBaseUrl { get; }

        protected LibraryServiceBase(IApiClient client, ILogger logger, ELibrarySettings settings)
        {
            Client = client;
            Logger = logger;
            ApiBaseUrl = settings.BaseUrl.TrimEnd('/');
        }

        public abstract BookSource Source { get; }
        public abstract Task<Response<SearchBookResponse>> SearchBooks(int? page, string searchText);
        public abstract Task<Response<SearchBookResponse>> SearchBooksByTopic(int? page, string topic);
        public abstract Task<Response<BookSummary>> GetBookSummaryById(string id);
        public abstract Task<Response<string>> GetBookById(string id);
        public abstract Task<Response<FileContentResult>> GetImageById(string id);

        protected static int NormalizePage(int? page) => page is > 0 ? page.Value : 1;

        protected string ImageUrlFor(string id) => $"{ApiBaseUrl}/Image/{id}?source={Source}";

        protected string PageLink(string route, string parameter, string term, int page)
            => Util.PageLink(ApiBaseUrl, route, parameter, term, page);

        protected async Task<Response<FileContentResult>> DownloadImage(string url, string id)
        {
            try
            {
                var bytes = await Client.GetBytesAsync(url);
                Logger.LogInformation("{Source} image for {Id} downloaded ({Bytes} bytes)", Source, id, bytes.Length);

                var file = new FileContentResult(bytes, "image/jpeg") { FileDownloadName = $"book_{id}.jpg" };
                return Response<FileContentResult>.Ok(file, "Image retrieved successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "{Source} image for {Id} could not be downloaded from {Url}", Source, id, url);
                return Response<FileContentResult>.FromProviderException(ex, Source.ToString(), $"Image not found for book {id}");
            }
        }
    }
}
