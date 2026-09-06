using DisplayBook.App.Models;

namespace DisplayBook.App.Services;

public interface IBookCatalogService
{
    Task<IReadOnlyList<BookSummary>> GetBooksAsync(CancellationToken cancellationToken = default);
    Task<bool> ContainsContentHashAsync(string contentHash, CancellationToken cancellationToken = default);
    Task AddBookAsync(BookSummary book, string coverRelativePath, CancellationToken cancellationToken = default);
    Task SaveLocatorAsync(string bookId, string resourceHref, int page, int pageCount, CancellationToken cancellationToken = default);
    Task DeleteBooksAsync(IReadOnlyCollection<string> bookIds, CancellationToken cancellationToken = default);
}
