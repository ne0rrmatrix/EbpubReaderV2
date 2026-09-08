using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DisplayBook.App.Models;
using DisplayBook.App.Services;
using Microsoft.Extensions.Logging;

namespace DisplayBook.App.ViewModels;

public partial class LibraryViewModel(
    IBookCatalogService catalogService,
    IBookImportService importService,
    INavigationService navigationService,
    ILogger<LibraryViewModel> logger) : ObservableObject
{
    [ObservableProperty]
    public partial ObservableCollection<BookSummary> Books { get; set; } = [];

    [ObservableProperty]
    public partial ObservableCollection<LibraryBookItem> VisibleBooks { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    [NotifyPropertyChangedFor(nameof(IsRefreshing))]
    [NotifyCanExecuteChangedFor(nameof(ImportFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnterSelectionModeCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExitSelectionModeCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleSelectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearSelectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotSelectionMode))]
    [NotifyCanExecuteChangedFor(nameof(ImportFileCommand))]
    [NotifyCanExecuteChangedFor(nameof(ImportFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(EnterSelectionModeCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExitSelectionModeCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleSelectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(SelectAllCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearSelectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    public partial bool IsSelectionMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotImporting))]
    [NotifyPropertyChangedFor(nameof(IsRefreshing))]
    [NotifyCanExecuteChangedFor(nameof(CancelImportCommand))]
    public partial bool IsImporting { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelImportCommand))]
    public partial bool IsCancelRequested { get; set; }

    [ObservableProperty]
    public partial string ImportStage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ImportCurrentItem { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportProgressPercent))]
    public partial double ImportProgress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportProgressPercent))]
    [NotifyPropertyChangedFor(nameof(ImportProgressSummary))]
    public partial int ImportCompleted { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportProgressPercent))]
    [NotifyPropertyChangedFor(nameof(ImportProgressSummary))]
    public partial int ImportTotal { get; set; }

    [ObservableProperty]
    public partial string SearchQuery { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SortOption { get; set; } = "Recently added";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorMessage { get; set; } = string.Empty;

    private readonly HashSet<string> _selectedBookIds = new(StringComparer.Ordinal);
    private CancellationTokenSource? _importCancellationSource;
    private int _importGeneration;
    private int _activeImportGeneration;

    public IReadOnlyList<string> SortOptions { get; } =
    [
        "Recently added",
        "Recently opened",
        "Title (A–Z)",
        "Author (A–Z)"
    ];

    public bool IsNotBusy => !IsBusy;

    public bool IsNotImporting => !IsImporting;

    public bool IsRefreshing => IsBusy && !IsImporting;

    public string ImportProgressPercent => ImportTotal > 0
        ? $"{Math.Round(ImportProgress * 100):0}%"
        : "Working…";

    public string ImportProgressSummary => ImportTotal > 0
        ? $"{ImportCompleted:N0} of {ImportTotal:N0} completed"
        : "Preparing…";

    public bool IsNotSelectionMode => !IsSelectionMode;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public int SelectedCount => _selectedBookIds.Count;

    public bool HasSelection => SelectedCount > 0;

    public bool IsLibraryEmpty => Books.Count == 0;

    public bool IsSearchEmpty => Books.Count > 0 && VisibleBooks.Count == 0;

    public bool AreAllVisibleBooksSelected =>
        VisibleBooks.Count > 0 && VisibleBooks.All(book => _selectedBookIds.Contains(book.Id));

    [RelayCommand]
    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            ErrorMessage = string.Empty;
            logger.LogDebug("Loading library catalog.");
            await ReloadCatalogAsync(cancellationToken);

            logger.LogDebug("Loaded {BookCount} books into the library.", Books.Count);
        }
        catch (OperationCanceledException exception)
        {
            logger.LogDebug(exception, "Library catalog load was canceled.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Library catalog load failed.");
            ErrorMessage = $"The library could not be loaded: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportFileAsync(CancellationToken cancellationToken)
    {
        await ImportAsync((progress, token) => importService.ImportFileAsync(progress, token), cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportFolderAsync(CancellationToken cancellationToken)
    {
        await ImportAsync((progress, token) => importService.ImportFolderAsync(progress, token), cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanCancelImport))]
    private void CancelImport()
    {
        if (_importCancellationSource is null)
        {
            return;
        }

        IsCancelRequested = true;
        ImportStage = "Canceling import…";
        _importCancellationSource.Cancel();
    }

    [RelayCommand]
    private Task OpenDetailsAsync(LibraryBookItem? book)
    {
        if (book is null)
        {
            return Task.CompletedTask;
        }

        if (IsSelectionMode)
        {
            ToggleSelectionCore(book);
            return Task.CompletedTask;
        }

        return navigationService.ShowBookDetailsAsync(book.Book);
    }

    [RelayCommand]
    private Task BrowseOpdsAsync() => navigationService.ShowOpdsServersAsync();

    [RelayCommand(CanExecute = nameof(CanEnterSelectionMode))]
    private void EnterSelectionMode()
    {
        ClearSelectedBooks();
        IsSelectionMode = true;
    }

    [RelayCommand(CanExecute = nameof(CanExitSelectionMode))]
    private void ExitSelectionMode()
    {
        ClearSelectedBooks();
        IsSelectionMode = false;
    }

    [RelayCommand(CanExecute = nameof(CanToggleSelection))]
    private void ToggleSelection(LibraryBookItem? book)
    {
        if (book is not null)
        {
            ToggleSelectionCore(book);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSelectAll))]
    private void SelectAll()
    {
        foreach (var book in VisibleBooks)
        {
            _selectedBookIds.Add(book.Id);
            book.IsSelected = true;
        }

        NotifySelectionChanged();
    }

    [RelayCommand(CanExecute = nameof(CanClearSelection))]
    private void ClearSelection()
    {
        ClearSelectedBooks();
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private async Task DeleteSelectedAsync(CancellationToken cancellationToken)
    {
        var selectedIds = _selectedBookIds.ToArray();
        if (selectedIds.Length == 0)
        {
            return;
        }

        var bookLabel = selectedIds.Length == 1 ? "book" : "books";
        var confirmed = await navigationService.ConfirmAsync(
            "Delete books?",
            $"Permanently delete {selectedIds.Length} {bookLabel} and its stored EPUB content?",
            "Delete",
            "Cancel");
        if (!confirmed)
        {
            return;
        }

        try
        {
            IsBusy = true;
            ErrorMessage = string.Empty;
            await catalogService.DeleteBooksAsync(selectedIds, cancellationToken);
            CompleteDeletion(selectedIds);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(exception, "Book deletion was canceled.");
        }
        catch (AggregateException exception)
        {
            logger.LogError(exception, "Book records were deleted, but some stored content could not be removed.");
            CompleteDeletion(selectedIds);
            ErrorMessage = "The selected books were removed, but some stored files could not be cleaned up.";
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Book deletion failed.");
            ErrorMessage = $"The selected books could not be deleted: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanImport() => !IsBusy && !IsSelectionMode;

    private bool CanEnterSelectionMode() => !IsBusy && !IsSelectionMode && Books.Count > 0;

    private bool CanExitSelectionMode() => !IsBusy && IsSelectionMode;

    private bool CanToggleSelection() => !IsBusy && IsSelectionMode;

    private bool CanSelectAll() => !IsBusy && IsSelectionMode && VisibleBooks.Count > 0;

    private bool CanClearSelection() => !IsBusy && IsSelectionMode && HasSelection;

    private bool CanDeleteSelected() => !IsBusy && IsSelectionMode && HasSelection;

    private bool CanCancelImport() => IsImporting && !IsCancelRequested;

    private async Task ImportAsync(
        Func<IProgress<BookImportProgress>, CancellationToken, Task<IReadOnlyList<BookSummary>>> import,
        CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        using var importCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _importCancellationSource = importCancellationSource;
        var importGeneration = Interlocked.Increment(ref _importGeneration);
        Volatile.Write(ref _activeImportGeneration, importGeneration);
        var progress = new Progress<BookImportProgress>(value => UpdateImportProgress(value, importGeneration));
        try
        {
            IsBusy = true;
            IsImporting = false;
            IsCancelRequested = false;
            ImportStage = "Starting import";
            ImportCurrentItem = string.Empty;
            ImportProgress = 0;
            ImportCompleted = 0;
            ImportTotal = 0;
            ErrorMessage = string.Empty;
            await import(progress, importCancellationSource.Token);
            IsImporting = false;
            await ReloadCatalogAsync(importCancellationSource.Token);
        }
        catch (OperationCanceledException exception) when (importCancellationSource.IsCancellationRequested)
        {
            logger.LogDebug(exception, "Book import was canceled.");
            await ReloadCatalogAfterImportCancellationAsync();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Book import failed.");
            ErrorMessage = exception.Message;
        }
        finally
        {
            Volatile.Write(ref _activeImportGeneration, 0);
            _importCancellationSource = null;
            IsImporting = false;
            IsCancelRequested = false;
            IsBusy = false;
        }
    }

    private void UpdateImportProgress(BookImportProgress progress, int importGeneration)
    {
        if (Volatile.Read(ref _activeImportGeneration) != importGeneration)
        {
            return;
        }

        IsImporting = true;
        ImportStage = progress.Stage;
        ImportCurrentItem = progress.CurrentItem;
        ImportCompleted = progress.Completed;
        ImportTotal = progress.Total;
        ImportProgress = progress.Fraction;
    }

    private async Task ReloadCatalogAfterImportCancellationAsync()
    {
        try
        {
            await ReloadCatalogAsync(CancellationToken.None);
            logger.LogInformation("Reloaded {BookCount} committed books after import cancellation.", Books.Count);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not refresh the library after import cancellation.");
        }
    }

    public async Task ReloadCatalogAsync(CancellationToken cancellationToken)
    {
        var books = await catalogService.GetBooksAsync(cancellationToken);
        Books = new ObservableCollection<BookSummary>(books);

        ClearSelectedBooks();
        IsSelectionMode = false;
        RefreshVisibleBooks();
    }

    partial void OnSearchQueryChanged(string value)
    {
        RefreshVisibleBooks();
    }

    partial void OnSortOptionChanged(string value)
    {
        RefreshVisibleBooks();
    }

    private void ToggleSelectionCore(LibraryBookItem book)
    {
        if (_selectedBookIds.Contains(book.Id))
        {
            _selectedBookIds.Remove(book.Id);
            book.IsSelected = false;
        }
        else
        {
            _selectedBookIds.Add(book.Id);
            book.IsSelected = true;
        }

        NotifySelectionChanged();
    }

    private void ClearSelectedBooks()
    {
        _selectedBookIds.Clear();
        foreach (var book in VisibleBooks)
        {
            book.IsSelected = false;
        }

        NotifySelectionChanged();
    }

    private void CompleteDeletion(IReadOnlyCollection<string> deletedIds)
    {
        var deletedIdSet = deletedIds.ToHashSet(StringComparer.Ordinal);
        for (var index = Books.Count - 1; index >= 0; index--)
        {
            if (deletedIdSet.Contains(Books[index].Id))
            {
                Books.RemoveAt(index);
            }
        }

        ClearSelectedBooks();
        IsSelectionMode = false;
        RefreshVisibleBooks();
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(AreAllVisibleBooksSelected));
        ClearSelectionCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
    }

    private void RefreshVisibleBooks()
    {
        var validIds = Books.Select(book => book.Id).ToHashSet(StringComparer.Ordinal);
        _selectedBookIds.RemoveWhere(bookId => !validIds.Contains(bookId));

        var query = SearchQuery.Trim();
        IEnumerable<BookSummary> filteredBooks = Books;
        if (query.Length > 0)
        {
            filteredBooks = filteredBooks.Where(book =>
                book.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                book.Author.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        }

        var sortedBooks = SortOption switch
        {
            "Recently opened" => filteredBooks
                .OrderByDescending(book => book.LastOpenedAt.HasValue)
                .ThenByDescending(book => book.LastOpenedAt)
                .ThenBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase),
            "Title (A–Z)" => filteredBooks
                .OrderBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(book => book.Author, StringComparer.CurrentCultureIgnoreCase),
            "Author (A–Z)" => filteredBooks
                .OrderBy(book => book.Author, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => filteredBooks
                .OrderByDescending(book => book.ImportedAt)
                .ThenBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase)
        };

        VisibleBooks = new ObservableCollection<LibraryBookItem>(
            sortedBooks.Select(book =>
            {
                var item = new LibraryBookItem(book)
                {
                    IsSelected = _selectedBookIds.Contains(book.Id)
                };
                return item;
            }));

        OnPropertyChanged(nameof(IsLibraryEmpty));
        OnPropertyChanged(nameof(IsSearchEmpty));
        OnPropertyChanged(nameof(AreAllVisibleBooksSelected));
        EnterSelectionModeCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        NotifySelectionChanged();
    }
}
