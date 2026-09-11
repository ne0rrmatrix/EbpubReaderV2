using DisplayBook.App.Models;
using DisplayBook.App.Models.BookMetadata;
using DisplayBook.App.Services;
using DisplayBook.App.Services.BookMetadata;
using DisplayBook.App.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DisplayBook.Tests;

public class BookDetailsViewModelMetadataTests
{
    private static BookSummary Book(string title = "The Hobbit", string author = "J.R.R. Tolkien", string description = "Original description.") =>
        new(
            Id: "book-1",
            Title: title,
            Author: author,
            Description: description,
            Language: "en",
            Publisher: "Original Publisher",
            CoverPath: "/covers/book-1.jpg",
            PublicationRoot: "Books/book-1",
            PublicationOpfPath: "content.opf",
            OriginalFileName: "hobbit.epub",
            ImportedAt: new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            LastOpenedAt: null,
            LocatorResourceHref: string.Empty,
            LocatorPage: 0,
            LocatorPageCount: 1);

    private static FetchedBookMetadata Fetched(string? title = "The Hobbit, or There and Back Again") =>
        new(
            Title: title,
            Authors: ["J.R.R. Tolkien"],
            Publisher: "Houghton Mifflin",
            PublicationDate: "1997-09-15",
            Description: "A hobbit's unexpected journey.",
            Isbn10: "0618968634",
            Isbn13: "9780618968633",
            CoverUrl: "https://books.google.com/cover.jpg",
            SourceProvider: BookMetadataProvider.GoogleBooks,
            SourceLink: "https://books.google.com/books?id=abc123");

    private static BookDetailsViewModel CreateViewModel(
        BookSummary book,
        FakeBookMetadataService metadataService,
        FakeBookCatalogService catalog)
    {
        var viewModel = new BookDetailsViewModel(new FakeNavigationService(), metadataService, catalog, NullLogger<BookDetailsViewModel>.Instance);
        viewModel.SetBook(book);
        return viewModel;
    }

    [Fact]
    public async Task UpdateMetadata_Found_PopulatesDiffAndSetsFoundState()
    {
        var catalog = new FakeBookCatalogService(Book());
        var metadataService = new FakeBookMetadataService
        {
            NextResult = new BookMetadataFetchResult(BookMetadataFetchStatus.Found, Fetched(), TimeSpan.FromMilliseconds(10), null),
        };
        var viewModel = CreateViewModel(Book(), metadataService, catalog);

        await viewModel.UpdateMetadataCommand.ExecuteAsync(null);

        Assert.Equal(MetadataUpdatePanelState.Found, viewModel.PanelState);
        Assert.Contains(viewModel.ProposedChanges, c => c.Field == "title");
        Assert.Contains(viewModel.ProposedChanges, c => c.Field == "cover");
        Assert.Equal("Powered by Google Books", viewModel.SourceAttributionText);
        Assert.Contains("Google Books", viewModel.SourceLine);
        Assert.Contains("9780618968633", viewModel.SourceLine);
    }

    [Fact]
    public async Task UpdateMetadata_RateLimited_SetsRateLimitedStateWithMessage()
    {
        var catalog = new FakeBookCatalogService(Book());
        var metadataService = new FakeBookMetadataService
        {
            NextResult = new BookMetadataFetchResult(BookMetadataFetchStatus.RateLimited, null, TimeSpan.FromMilliseconds(5), "rate limited"),
        };
        var viewModel = CreateViewModel(Book(), metadataService, catalog);

        await viewModel.UpdateMetadataCommand.ExecuteAsync(null);

        Assert.Equal(MetadataUpdatePanelState.RateLimited, viewModel.PanelState);
        Assert.Equal("rate limited", viewModel.StatusMessage);
    }

    [Fact]
    public async Task UpdateMetadata_NotFound_SetsNotFoundState()
    {
        var catalog = new FakeBookCatalogService(Book());
        var metadataService = new FakeBookMetadataService
        {
            NextResult = new BookMetadataFetchResult(BookMetadataFetchStatus.NotFound, null, TimeSpan.FromMilliseconds(5), "no match"),
        };
        var viewModel = CreateViewModel(Book(), metadataService, catalog);

        await viewModel.UpdateMetadataCommand.ExecuteAsync(null);

        Assert.Equal(MetadataUpdatePanelState.NotFound, viewModel.PanelState);
    }

    [Fact]
    public void UpdateMetadataCommand_CannotExecute_WhileLoading()
    {
        var catalog = new FakeBookCatalogService(Book());
        var metadataService = new FakeBookMetadataService();
        var viewModel = CreateViewModel(Book(), metadataService, catalog);

        Assert.True(viewModel.UpdateMetadataCommand.CanExecute(null));
        viewModel.PanelState = MetadataUpdatePanelState.Loading;
        Assert.False(viewModel.UpdateMetadataCommand.CanExecute(null));
    }

    [Fact]
    public async Task ApplyMetadata_WritesSelectedFieldsAndDownloadsCover()
    {
        var catalog = new FakeBookCatalogService(Book());
        var metadataService = new FakeBookMetadataService
        {
            NextResult = new BookMetadataFetchResult(BookMetadataFetchStatus.Found, Fetched(), TimeSpan.FromMilliseconds(10), null),
            DownloadedCoverRelativePath = "Books/book-1/cover.metadata.jpg",
        };
        var viewModel = CreateViewModel(Book(), metadataService, catalog);
        await viewModel.UpdateMetadataCommand.ExecuteAsync(null);

        await viewModel.ApplyMetadataCommand.ExecuteAsync(null);

        Assert.Equal(MetadataUpdatePanelState.Applied, viewModel.PanelState);
        Assert.Equal("The Hobbit, or There and Back Again", viewModel.Book!.Title);
        Assert.Equal("Houghton Mifflin", viewModel.Book.Publisher);
        Assert.Equal("9780618968633", viewModel.Book.Isbn);
        Assert.True(viewModel.CanUndo);
        Assert.True(metadataService.DownloadCoverCalled);
    }

    [Fact]
    public async Task ApplyMetadata_DoesNotOverwriteExistingIsbn()
    {
        var existingBook = Book() with { Isbn = "9780132350884" };
        var catalog = new FakeBookCatalogService(existingBook);
        var metadataService = new FakeBookMetadataService
        {
            NextResult = new BookMetadataFetchResult(BookMetadataFetchStatus.Found, Fetched(), TimeSpan.FromMilliseconds(10), null),
        };
        var viewModel = CreateViewModel(existingBook, metadataService, catalog);
        await viewModel.UpdateMetadataCommand.ExecuteAsync(null);

        await viewModel.ApplyMetadataCommand.ExecuteAsync(null);

        Assert.Equal("9780132350884", viewModel.Book!.Isbn);
    }

    [Fact]
    public async Task ApplyMetadata_DescriptionNotPreselected_KeepsOriginalUnlessChecked()
    {
        var catalog = new FakeBookCatalogService(Book(description: "Original description."));
        var metadataService = new FakeBookMetadataService
        {
            NextResult = new BookMetadataFetchResult(BookMetadataFetchStatus.Found, Fetched(), TimeSpan.FromMilliseconds(10), null),
        };
        var viewModel = CreateViewModel(Book(description: "Original description."), metadataService, catalog);
        await viewModel.UpdateMetadataCommand.ExecuteAsync(null);

        var descriptionChange = Assert.Single(viewModel.ProposedChanges, c => c.Field == "description");
        Assert.False(descriptionChange.IsSelected);

        await viewModel.ApplyMetadataCommand.ExecuteAsync(null);

        Assert.Equal("Original description.", viewModel.Book!.Description);
    }

    [Fact]
    public async Task UndoMetadata_RestoresPreviousValuesAndClearsCanUndo()
    {
        var catalog = new FakeBookCatalogService(Book());
        var metadataService = new FakeBookMetadataService
        {
            NextResult = new BookMetadataFetchResult(BookMetadataFetchStatus.Found, Fetched(), TimeSpan.FromMilliseconds(10), null),
        };
        var viewModel = CreateViewModel(Book(), metadataService, catalog);
        await viewModel.UpdateMetadataCommand.ExecuteAsync(null);
        await viewModel.ApplyMetadataCommand.ExecuteAsync(null);
        Assert.Equal("The Hobbit, or There and Back Again", viewModel.Book!.Title);

        await viewModel.UndoMetadataCommand.ExecuteAsync(null);

        Assert.Equal("The Hobbit", viewModel.Book.Title);
        Assert.False(viewModel.CanUndo);
        Assert.Equal(MetadataUpdatePanelState.Idle, viewModel.PanelState);
    }

    [Fact]
    public void ClosePanel_ResetsToIdleAndClearsProposedChanges()
    {
        var catalog = new FakeBookCatalogService(Book());
        var metadataService = new FakeBookMetadataService();
        var viewModel = CreateViewModel(Book(), metadataService, catalog);
        viewModel.PanelState = MetadataUpdatePanelState.RateLimited;

        viewModel.ClosePanelCommand.Execute(null);

        Assert.Equal(MetadataUpdatePanelState.Idle, viewModel.PanelState);
        Assert.Empty(viewModel.ProposedChanges);
    }

    private sealed class FakeBookMetadataService : IBookMetadataService
    {
        public BookMetadataFetchResult NextResult { get; set; } =
            new(BookMetadataFetchStatus.NotFound, null, TimeSpan.Zero, "no result configured");

        public string DownloadedCoverRelativePath { get; set; } = "Books/book-1/cover.metadata.jpg";

        public bool DownloadCoverCalled { get; private set; }

        public Task<BookMetadataFetchResult> FetchMetadataAsync(BookSummary book, CancellationToken cancellationToken) =>
            Task.FromResult(NextResult);

        public Task<string> DownloadCoverAsync(string bookId, string coverUrl, CancellationToken cancellationToken)
        {
            DownloadCoverCalled = true;
            return Task.FromResult(DownloadedCoverRelativePath);
        }
    }

    private sealed class FakeBookCatalogService(BookSummary book) : IBookCatalogService
    {
        private BookSummary _book = book;
        private BookSummary? _previous;

        public Task<IReadOnlyList<BookSummary>> GetBooksAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BookSummary>>([_book]);

        public Task<BookSummary?> GetBookAsync(string bookId, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Equals(bookId, _book.Id, StringComparison.Ordinal) ? _book : null);

        public Task<bool> ContainsContentHashAsync(string contentHash, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task AddBookAsync(BookSummary book, string coverRelativePath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SaveLocatorAsync(string bookId, string resourceHref, int page, int pageCount, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<BookSummary> UpdateMetadataAsync(string bookId, BookSummary updated, string? newCoverRelativePath, CancellationToken cancellationToken = default)
        {
            _previous = _book;
            _book = newCoverRelativePath is null ? updated : updated with { CoverPath = newCoverRelativePath };
            return Task.FromResult(_book);
        }

        public Task<BookSummary?> UndoMetadataAsync(string bookId, CancellationToken cancellationToken = default)
        {
            if (_previous is null)
            {
                return Task.FromResult<BookSummary?>(null);
            }

            _book = _previous;
            _previous = null;
            return Task.FromResult<BookSummary?>(_book);
        }

        public Task<bool> HasPreviousMetadataAsync(string bookId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_previous is not null);

        public Task DeleteBooksAsync(IReadOnlyCollection<string> bookIds, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SaveLocatorAsync(string bookId, string resourceHref, int page, int pageCount, int charOffset = -1, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeNavigationService : INavigationService
    {
        public Task ShowBookDetailsAsync(BookSummary book) => Task.CompletedTask;

        public Task ShowReaderAsync(BookSummary book) => Task.CompletedTask;

        public Task GoBackAsync() => Task.CompletedTask;

        public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel) => Task.FromResult(true);

        public Task OpenExternalLinkAsync(Uri uri) => Task.CompletedTask;

        public Task ShowOpdsServersAsync() => Task.CompletedTask;

        public Task ShowOpdsCatalogAsync(string feedUrl, string? title = null) => Task.CompletedTask;

        public Task ShowOpdsBookAsync(string entryUrl, string? serverId = null, OpdsEntry? entry = null) => Task.CompletedTask;

        public OpdsEntry? TakePendingEntry(string entryUrl) => null;

        public Task ShowDownloadsAsync() => Task.CompletedTask;

        public Task ShowSettingsAsync() => Task.CompletedTask;

    }
}
