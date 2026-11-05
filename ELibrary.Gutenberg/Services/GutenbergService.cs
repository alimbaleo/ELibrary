using System.Text.RegularExpressions;
using System.Xml.Linq;
using ELibrary.Core.Configuration;
using ELibrary.Core.Enums;
using ELibrary.Core.Helpers;
using ELibrary.Core.Models;
using ELibrary.Core.Services;
using ELibrary.Infrastructure.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ELibrary.Infrastructure.Services
{
    public class GutenbergService : LibraryServiceBase
    {
        private const string ProviderName = "Project Gutenberg";
        private const int OpdsPageSize = 25;
        private const string BoilerplateStart = "<section class=\"pg-boilerplate pgheader\"";
        private const string BoilerplateEnd = "</section>";

        private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
        private static readonly Regex BookIdPattern = new(@"/ebooks/(\d+)\.opds", RegexOptions.Compiled);

        private readonly GutenbergSettings _settings;

        public override BookSource Source => BookSource.Gutenberg;

        public GutenbergService(
            IApiClient client,
            IOptions<GutenbergSettings> settings,
            IOptions<ELibrarySettings> apiSettings,
            ILogger<GutenbergService> logger)
            : base(client, logger, apiSettings.Value)
        {
            _settings = settings.Value;
        }

        public override async Task<Response<string>> GetBookById(string id)
        {
            try
            {
                var html = await Client.GetStringAsync($"{_settings.ContentUrl}{id}/pg{id}-images.html");
                if (string.IsNullOrWhiteSpace(html))
                {
                    Logger.LogWarning("Gutenberg book {Id} returned no content", id);
                    return Response<string>.NotFound($"Book content not found for book {id}");
                }

                html = RemoveBoilerplate(html);
                Logger.LogInformation("Gutenberg book {Id} content retrieved ({Length} chars)", id, html.Length);
                return Response<string>.Ok(html, "Book content retrieved successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Gutenberg book {Id} could not be retrieved", id);
                return Response<string>.FromProviderException(ex, ProviderName, $"Book content not found for book {id}");
            }
        }

        public override async Task<Response<BookSummary>> GetBookSummaryById(string id)
        {
            try
            {
                var xml = await Client.GetStringAsync($"{_settings.OpdsBookUrl}{id}.opds");
                var entry = XDocument.Parse(xml).Root?.Element(Atom + "entry");
                if (entry == null)
                {
                    Logger.LogWarning("Gutenberg book {Id} has no OPDS entry", id);
                    return Response<BookSummary>.NotFound($"Book with id {id} not found");
                }

                var summary = new BookSummary
                {
                    Id = id,
                    Title = entry.Element(Atom + "title")?.Value.Trim() ?? UnknownTitle,
                    Authors = entry.Elements(Atom + "author")
                        .Select(a => a.Element(Atom + "name")?.Value.Trim())
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Select(n => n!)
                        .ToList(),
                    Summary = ExtractSummary(entry.Element(Atom + "content")) ?? NoSummary,
                    ImageUrl = ImageUrlFor(id),
                    Source = Source
                };

                Logger.LogInformation("Gutenberg summary for {Id} retrieved: '{Title}'", id, summary.Title);
                return Response<BookSummary>.Ok(summary, "Book summary retrieved successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Gutenberg summary for {Id} could not be retrieved", id);
                return Response<BookSummary>.FromProviderException(ex, ProviderName, $"Book with id {id} not found");
            }
        }

        public override Task<Response<FileContentResult>> GetImageById(string id)
            => DownloadImage($"{_settings.ContentUrl}{id}/pg{id}.cover.medium.jpg", id);

        public override Task<Response<SearchBookResponse>> SearchBooks(int? page, string searchText)
            => Search(page, searchText, SearchRoute, SearchParameter);

        public override Task<Response<SearchBookResponse>> SearchBooksByTopic(int? page, string topic)
            => Search(page, topic, TopicRoute, TopicParameter);

        private async Task<Response<SearchBookResponse>> Search(int? page, string term, string route, string parameter)
        {
            if (string.IsNullOrWhiteSpace(term))
                return Response<SearchBookResponse>.Fail("Book search text not found");

            var currentPage = NormalizePage(page);
            var startIndex = 1 + (currentPage - 1) * OpdsPageSize;

            try
            {
                var xml = await Client.GetStringAsync($"{_settings.OpdsSearchUrl}?query={Uri.EscapeDataString(term)}&start_index={startIndex}");
                var feed = XDocument.Parse(xml).Root;
                var entries = feed?.Elements(Atom + "entry") ?? Enumerable.Empty<XElement>();

                var books = entries
                    .Select(entry => (Entry: entry, Match: BookIdPattern.Match(entry.Element(Atom + "id")?.Value ?? string.Empty)))
                    .Where(e => e.Match.Success)
                    .Select(e => ToBook(e.Entry, e.Match.Groups[1].Value))
                    .ToList();

                var hasNextPage = feed?.Elements(Atom + "link").Any(l => (string?)l.Attribute("rel") == "next") ?? false;

                Logger.LogInformation("Gutenberg returned {Count} books for '{Term}' page {Page} (next page: {HasNext})",
                    books.Count, term, currentPage, hasNextPage);

                return Response<SearchBookResponse>.Ok(new SearchBookResponse
                {
                    Count = books.Count,
                    NextPageUrl = hasNextPage ? PageLink(route, parameter, term, currentPage + 1) : null,
                    PreviousPageUrl = currentPage > 1 ? PageLink(route, parameter, term, currentPage - 1) : null,
                    Data = books
                }, "Books retrieved successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Gutenberg search failed for '{Term}' page {Page}", term, currentPage);
                return Response<SearchBookResponse>.FromProviderException(ex, ProviderName, "No books found");
            }
        }

        private Data ToBook(XElement entry, string id)
        {
            var author = entry.Element(Atom + "content")?.Value.Trim();
            return new Data
            {
                Id = id,
                Title = entry.Element(Atom + "title")?.Value.Trim() ?? UnknownTitle,
                Authors = string.IsNullOrEmpty(author) ? new List<string>() : new List<string> { author },
                Summary = NoSummary,
                ImageUrl = ImageUrlFor(id),
                Source = Source
            };
        }

        private static string? ExtractSummary(XElement? content)
        {
            var paragraph = content?.Descendants()
                .Where(e => e.Name.LocalName == "p")
                .Select(e => Regex.Replace(e.Value, @"\s+", " ").Trim())
                .FirstOrDefault(t => t.StartsWith("Summary:", StringComparison.OrdinalIgnoreCase));

            var text = paragraph?["Summary:".Length..].Trim();
            return string.IsNullOrEmpty(text) ? null : Util.TruncateText(text, SummaryLength);
        }

        private static string RemoveBoilerplate(string html)
        {
            while (true)
            {
                var start = html.IndexOf(BoilerplateStart, StringComparison.OrdinalIgnoreCase);
                if (start < 0) return html;

                var end = html.IndexOf(BoilerplateEnd, start, StringComparison.OrdinalIgnoreCase);
                if (end < 0) return html;

                html = html.Remove(start, end - start + BoilerplateEnd.Length);
            }
        }
    }
}
