using DisplayBook.App.Models;

namespace DisplayBook.App.Services;

public interface IBookDatabase
{
    Task<IReadOnlyList<BookSummary>> GetBooksAsync(CancellationToken cancellationToken = default);
    Task<BookSummary?> GetBookAsync(string bookId, CancellationToken cancellationToken = default);
    Task<bool> ContainsContentHashAsync(string contentHash, CancellationToken cancellationToken = default);
    Task AddBookAsync(BookSummary book, string coverRelativePath, CancellationToken cancellationToken = default);
    Task SaveLocatorAsync(string bookId, string resourceHref, int page, int pageCount, int charOffset = -1, CancellationToken cancellationToken = default);
    Task<BookSummary> UpdateMetadataAsync(string bookId, BookSummary updated, string? newCoverRelativePath, CancellationToken cancellationToken = default);
    Task<BookSummary?> UndoMetadataAsync(string bookId, CancellationToken cancellationToken = default);
    Task<bool> HasPreviousMetadataAsync(string bookId, CancellationToken cancellationToken = default);
    Task DeleteBooksAsync(IReadOnlyCollection<string> bookIds, CancellationToken cancellationToken = default);
}