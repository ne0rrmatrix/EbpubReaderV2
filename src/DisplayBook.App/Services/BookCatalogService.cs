using DisplayBook.App.Models;

namespace DisplayBook.App.Services;

public sealed class BookCatalogService(IBookDatabase database) : IBookCatalogService
{
    public Task<IReadOnlyList<BookSummary>> GetBooksAsync(CancellationToken cancellationToken = default)
    {
        return database.GetBooksAsync(cancellationToken);
    }

    public Task<bool> ContainsContentHashAsync(string contentHash, CancellationToken cancellationToken = default)
    {
        return database.ContainsContentHashAsync(contentHash, cancellationToken);
    }

    public Task AddBookAsync(BookSummary book, string coverRelativePath, CancellationToken cancellationToken = default)
    {
        return database.AddBookAsync(book, coverRelativePath, cancellationToken);
    }

    public Task SaveLocatorAsync(string bookId, string resourceHref, int page, int pageCount, CancellationToken cancellationToken = default)
    {
        return database.SaveLocatorAsync(bookId, resourceHref, page, pageCount, cancellationToken);
    }

    public Task DeleteBooksAsync(IReadOnlyCollection<string> bookIds, CancellationToken cancellationToken = default)
    {
        return database.DeleteBooksAsync(bookIds, cancellationToken);
    }
}
