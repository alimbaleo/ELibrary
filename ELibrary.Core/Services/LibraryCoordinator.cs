using System.Diagnostics;
using ELibrary.Core.Enums;
using ELibrary.Core.Helpers;
using ELibrary.Core.Interfaces;
using ELibrary.Core.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ELibrary.Core.Services
{
    public class LibraryCoordinator
    {
        private const string SourceUnavailable = "Book source is not available";

        private readonly ILibraryService[] _providers;
        private readonly ILogger<LibraryCoordinator> _logger;

        public LibraryCoordinator(IEnumerable<ILibraryService> providers, ILogger<LibraryCoordinator> logger)
        {
            _providers = providers.ToArray();
            _logger = logger;
        }

        public Task<Response<SearchBookResponse>> SearchBooks(int? page, string searchText)
            => SearchAllProviders("search", searchText, page, provider => provider.SearchBooks(page, searchText));

        public Task<Response<SearchBookResponse>> SearchBooksByTopic(int? page, string topic)
            => SearchAllProviders("topic", topic, page, provider => provider.SearchBooksByTopic(page, topic));

        public async Task<Response<BookSummary>> GetBookSummaryById(BookSource source, string id)
        {
            var provider = FindProvider(source);
            if (provider == null) return Response<BookSummary>.Fail(SourceUnavailable);

            var response = await provider.GetBookSummaryById(id);
            if (response.Success && response.Data != null) response.Data.Source = source;
            return response;
        }

        public async Task<Response<string>> GetBookById(BookSource source, string id)
        {
            var provider = FindProvider(source);
            return provider == null
                ? Response<string>.Fail(SourceUnavailable)
                : await provider.GetBookById(id);
        }

        public async Task<Response<FileContentResult>> GetImageById(BookSource source, string id)
        {
            var provider = FindProvider(source);
            return provider == null
                ? Response<FileContentResult>.Fail(SourceUnavailable)
                : await provider.GetImageById(id);
        }

        private ILibraryService? FindProvider(BookSource source)
        {
            var provider = _providers.FirstOrDefault(p => p.Source == source);
            if (provider == null)
                _logger.LogWarning("No provider registered for source {Source}", source);
            return provider;
        }

        private async Task<Response<SearchBookResponse>> SearchAllProviders(
            string kind, string term, int? page, Func<ILibraryService, Task<Response<SearchBookResponse>>> search)
        {
            var stopwatch = Stopwatch.StartNew();
            page ??= 1;
            var results = await Task.WhenAll(_providers.Select(search));

            var successful = new List<SearchBookResponse>();
            var failures = new List<string>();

            for (var i = 0; i < _providers.Length; i++)
            {
                var source = _providers[i].Source;
                var result = results[i];

                if (result.Success && result.Data != null)
                {
                    _logger.LogInformation("{Source} returned {Count} books for {Kind} '{Term}' page {Page}",
                        source, result.Data.Data.Count, kind, term, page);
                    successful.Add(result.Data);
                }
                else
                {
                    _logger.LogWarning("{Source} failed for {Kind} '{Term}' page {Page}: {Message}",
                        source, kind, term, page, result.Message);
                    failures.Add(result.Message);
                }
            }

            if (successful.Count == 0)
            {
                _logger.LogError("All {ProviderCount} sources failed for {Kind} '{Term}'", _providers.Length, kind, term);
                return Response<SearchBookResponse>.Fail(string.Join(", ", failures.Where(m => !string.IsNullOrEmpty(m))));
            }

            var books = RemoveDuplicates(successful.SelectMany(r => r.Data).OrderByDescending(b => b.Source).ToList());

            _logger.LogInformation("Combined {Count} unique books from {SourceCount}/{ProviderCount} sources for {Kind} '{Term}' in {ElapsedMs}ms",
                books.Count, successful.Count, _providers.Length, kind, term, stopwatch.ElapsedMilliseconds);

            return Response<SearchBookResponse>.Ok(new SearchBookResponse
            {
                Count = successful.Sum(r => r.Count),
                NextPageUrl = successful.Select(r => r.NextPageUrl).FirstOrDefault(u => !string.IsNullOrEmpty(u)),
                PreviousPageUrl = successful.Select(r => r.PreviousPageUrl).FirstOrDefault(u => !string.IsNullOrEmpty(u)),
                Data = books
            }, "Books retrieved successfully from multiple sources");
        }

        private static List<Data> RemoveDuplicates(List<Data> books)
        {
            var unique = new Dictionary<string, Data>(StringComparer.OrdinalIgnoreCase);
            foreach (var book in books)
            {
                var authors = string.Join(",", book.Authors.OrderBy(a => a).Select(a => a.SanitizeForComparison(true)));
                var key = $"{book.Title.SanitizeForComparison()}-{authors}";
                unique.TryAdd(key, book);
            }
            return unique.Values.ToList();
        }
    }
}
