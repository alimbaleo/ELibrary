using System.Diagnostics;
using ELibrary.Core.Enums;
using ELibrary.Core.Models;
using ELibrary.Core.Services;
using ELibrary.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ELibrary.Controllers
{
    [Authorize]
    [ApiController]
    [Route("[controller]/[action]")]
    public class LibraryController : ControllerBase
    {
        private readonly ILogger<LibraryController> _logger;
        private readonly LibraryCoordinator _coordinator;

        public LibraryController(ILogger<LibraryController> logger, LibraryCoordinator coordinator)
        {
            _logger = logger;
            _coordinator = coordinator;
        }

        [HttpGet(Name = "search")]
        [ProducesResponseType(typeof(SearchBookResponse), StatusCodes.Status200OK)]
        public Task<IActionResult> Search([FromQuery] SearchBookRequest request)
            => Execute("Search", new { request.SearchText, request.Page },
                () => _coordinator.SearchBooks(request.Page, request.SearchText));

        [HttpGet(Name = "searchTopic")]
        [ProducesResponseType(typeof(SearchBookResponse), StatusCodes.Status200OK)]
        public Task<IActionResult> SearchTopic([FromQuery] SearchBookByTopicRequest request)
            => Execute("SearchTopic", new { request.Topic, request.Page },
                () => _coordinator.SearchBooksByTopic(request.Page, request.Topic));

        [HttpGet("{id}", Name = "summary")]
        [ProducesResponseType(typeof(BookSummary), StatusCodes.Status200OK)]
        public Task<IActionResult> Summary(string id, BookSource source)
            => Execute("Summary", new { id, source }, () => _coordinator.GetBookSummaryById(source, id));

        [HttpGet("{id}", Name = "book")]
        [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
        public Task<IActionResult> Book(string id, BookSource source)
            => Execute("Book", new { id, source }, () => _coordinator.GetBookById(source, id));

        [HttpGet("{id}", Name = "image")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public Task<IActionResult> Image(string id, BookSource source)
            => Execute("Image", new { id, source }, () => _coordinator.GetImageById(source, id));

        private async Task<IActionResult> Execute<T>(string operation, object context, Func<Task<Response<T>>> action)
        {
            var stopwatch = Stopwatch.StartNew();
            _logger.LogInformation("{Operation} started {@Context}", operation, context);

            try
            {
                var response = await action();
                if (!response.Success)
                {
                    _logger.LogWarning("{Operation} failed ({ErrorType}) in {ElapsedMs}ms: {Message} {@Context}",
                        operation, response.ErrorType, stopwatch.ElapsedMilliseconds, response.Message, context);
                    return Failure(response);
                }

                _logger.LogInformation("{Operation} completed in {ElapsedMs}ms {@Context}", operation, stopwatch.ElapsedMilliseconds, context);
                return response.Data is IActionResult actionResult ? actionResult : Ok(response.Data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Operation} threw after {ElapsedMs}ms {@Context}", operation, stopwatch.ElapsedMilliseconds, context);
                return StatusCode(StatusCodes.Status500InternalServerError, "An unexpected error occurred");
            }
        }

        // 404 when the book doesn't exist, 502 when a provider is down/blocked/timed out, 400 otherwise
        private IActionResult Failure<T>(Response<T> response) => response.ErrorType switch
        {
            ErrorType.NotFound => NotFound(response.Message),
            ErrorType.Upstream => StatusCode(StatusCodes.Status502BadGateway, response.Message),
            _ => BadRequest(response.Message)
        };
    }
}
