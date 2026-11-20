using ELibrary.Core.Enums;
using ELibrary.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace ELibrary.Core.Interfaces
{
    public interface ILibraryService
    {
        BookSource Source { get; }
        Task<Response<SearchBookResponse>> SearchBooks(int? page, string searchText);
        Task<Response<SearchBookResponse>> SearchBooksByTopic(int? page, string topic);
        Task<Response<BookSummary>> GetBookSummaryById(string id);
        Task<Response<string>> GetBookById(string id);
        Task<Response<FileContentResult>> GetImageById(string id);
    }
}
