using DisplayBook.App.Models;

namespace DisplayBook.App.Services;

public sealed class BookCatalogService(IBookDatabase database) : IBookCatalogService
{
    public Task<IReadOnlyList<BookSummary>> GetBooksAsync(CancellationToken cancellationToken = default)
    {
        return database.GetBooksAsync(cancellationToken);
    }

    public Task<BookSummary?> GetBookAsync(string bookId, CancellationToken cancellationToken = default)
    {
        return database.GetBookAsync(bookId, cancellationToken);
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

    public Task<BookSummary> UpdateMetadataAsync(string bookId, BookSummary updated, string? newCoverRelativePath, CancellationToken cancellationToken = default)
    {
        return database.UpdateMetadataAsync(bookId, updated, newCoverRelativePath, cancellationToken);
    }

    public Task<BookSummary?> UndoMetadataAsync(string bookId, CancellationToken cancellationToken = default)
    {
        return database.UndoMetadataAsync(bookId, cancellationToken);
    }

    public Task<bool> HasPreviousMetadataAsync(string bookId, CancellationToken cancellationToken = default)
    {
        return database.HasPreviousMetadataAsync(bookId, cancellationToken);
    }

    public Task DeleteBooksAsync(IReadOnlyCollection<string> bookIds, CancellationToken cancellationToken = default)
    {
        return database.DeleteBooksAsync(bookIds, cancellationToken);
    }
}
