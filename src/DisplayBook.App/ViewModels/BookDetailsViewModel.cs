using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DisplayBook.App.Models;
using DisplayBook.App.Models.BookMetadata;
using DisplayBook.App.Services;
using DisplayBook.App.Services.BookMetadata;
using Microsoft.Extensions.Logging;

namespace DisplayBook.App.ViewModels;

public partial class BookDetailsViewModel(
    INavigationService navigationService,
    IBookMetadataService bookMetadataService,
    IBookCatalogService catalogService,
    ILogger<BookDetailsViewModel> logger) : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateMetadataCommand))]
    public partial BookSummary? Book { get; set; }

    // Flat, top-level properties for the XAML to bind to instead of nested "Book.Title"-style
    // paths. BookSummary is a plain record (no INotifyPropertyChanged), and a nested path
    // through a reassigned root reference is a known-tricky case for compiled-binding
    // pipelines — flattening sidesteps it entirely instead of depending on the pipeline
    // correctly re-walking the path when Book is replaced by ApplyMetadataAsync/UndoMetadataAsync.
    public string BookTitle => Book?.Title ?? string.Empty;

    public string BookAuthor => Book?.Author ?? string.Empty;

    public string BookDescription => Book?.Description ?? string.Empty;

    public string BookLanguage => Book?.Language ?? string.Empty;

    public string BookCoverPath => Book?.CoverPath ?? string.Empty;

    partial void OnBookChanged(BookSummary? value)
    {
        OnPropertyChanged(nameof(BookTitle));
        OnPropertyChanged(nameof(BookAuthor));
        OnPropertyChanged(nameof(BookDescription));
        OnPropertyChanged(nameof(BookLanguage));
        OnPropertyChanged(nameof(BookCoverPath));
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateMetadataCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyMetadataCommand))]
    public partial MetadataUpdatePanelState PanelState { get; set; } = MetadataUpdatePanelState.Idle;

    [ObservableProperty]
    public partial ObservableCollection<MetadataFieldChange> ProposedChanges { get; set; } = [];

    [ObservableProperty]
    public partial string SourceLine { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SourceAttributionText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoMetadataCommand))]
    public partial bool CanUndo { get; set; }

    private CancellationTokenSource? _fetchCancellationSource;
    private FetchedBookMetadata? _pendingFetchedBook;

    public void SetBook(BookSummary book)
    {
        Book = book;
        PanelState = MetadataUpdatePanelState.Idle;
        ProposedChanges = [];
        _pendingFetchedBook = null;
        _ = RefreshCanUndoAsync();
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private Task OpenAsync()
    {
        return navigationService.ShowReaderAsync(Book ?? throw new InvalidOperationException("A book must be selected before opening the reader."));
    }

    [RelayCommand]
    private Task BackAsync()
    {
        return navigationService.GoBackAsync();
    }

    private bool CanOpen() => Book is not null;

    [RelayCommand(CanExecute = nameof(CanUpdateMetadata))]
    private async Task UpdateMetadataAsync(CancellationToken cancellationToken)
    {
        if (Book is null)
        {
            return;
        }

        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _fetchCancellationSource = linkedSource;
        PanelState = MetadataUpdatePanelState.Loading;
        StatusMessage = string.Empty;

        try
        {
            var result = await bookMetadataService.FetchMetadataAsync(Book, linkedSource.Token);
            if (linkedSource.IsCancellationRequested)
            {
                return;
            }

            switch (result.Status)
            {
                case BookMetadataFetchStatus.Found:
                    ApplyFetchResult(result.Book ?? throw new InvalidOperationException("A Found result must carry a fetched book."));
                    break;
                case BookMetadataFetchStatus.RateLimited:
                    PanelState = MetadataUpdatePanelState.RateLimited;
                    StatusMessage = result.ErrorMessage ?? "Rate-limited by the metadata source. Wait a bit and try again.";
                    break;
                case BookMetadataFetchStatus.NotFound:
                    PanelState = MetadataUpdatePanelState.NotFound;
                    StatusMessage = result.ErrorMessage ?? "No match found on Google Books or Open Library.";
                    break;
                default:
                    PanelState = MetadataUpdatePanelState.NotFound;
                    StatusMessage = result.ErrorMessage ?? "The request failed. Check your connection and try again.";
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            PanelState = MetadataUpdatePanelState.Idle;
        }
        finally
        {
            _fetchCancellationSource = null;
        }
    }

    private bool CanUpdateMetadata() => Book is not null && PanelState != MetadataUpdatePanelState.Loading;

    [RelayCommand]
    private void CancelMetadataFetch()
    {
        _fetchCancellationSource?.Cancel();
    }

    [RelayCommand]
    private void ClosePanel()
    {
        PanelState = MetadataUpdatePanelState.Idle;
        ProposedChanges = [];
        _pendingFetchedBook = null;
    }

    [RelayCommand]
    private async Task OpenSourceLinkAsync()
    {
        if (_pendingFetchedBook?.SourceLink is not { Length: > 0 } link || !Uri.TryCreate(link, UriKind.Absolute, out var uri))
        {
            return;
        }

        try
        {
            await navigationService.OpenExternalLinkAsync(uri);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not open the metadata source link {Link}.", link);
        }
    }

    [RelayCommand(CanExecute = nameof(CanApplyMetadata))]
    private async Task ApplyMetadataAsync(CancellationToken cancellationToken)
    {
        if (Book is null || _pendingFetchedBook is null)
        {
            return;
        }

        var fetched = _pendingFetchedBook;
        var fetchedIsbn = fetched.Isbn13 ?? fetched.Isbn10;
        var updated = Book with
        {
            Title = GetSelectedValue("title", Book.Title),
            Author = GetSelectedValue("author", Book.Author),
            Publisher = GetSelectedValue("publisher", Book.Publisher),
            Description = GetSelectedValue("description", Book.Description),
            Isbn = string.IsNullOrWhiteSpace(Book.Isbn) && fetchedIsbn is not null ? fetchedIsbn : Book.Isbn,
        };

        string? newCoverRelativePath = null;
        var coverChange = ProposedChanges.FirstOrDefault(change => change.IsCover);
        if (coverChange is { IsSelected: true } && !string.IsNullOrWhiteSpace(fetched.CoverUrl))
        {
            try
            {
                newCoverRelativePath = await bookMetadataService.DownloadCoverAsync(Book.Id, fetched.CoverUrl, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Could not download the cover for {BookId}; the rest of the metadata will still apply.", Book.Id);
            }
        }

        var saved = await catalogService.UpdateMetadataAsync(Book.Id, updated, newCoverRelativePath, cancellationToken);
        Book = saved;
        CanUndo = true;
        PanelState = MetadataUpdatePanelState.Applied;
        StatusMessage = $"{ProposedChanges.Count(change => change.IsSelected)} field(s) changed" + (newCoverRelativePath is not null ? ", cover replaced." : ".");
        _pendingFetchedBook = null;
    }

    private bool CanApplyMetadata() => PanelState == MetadataUpdatePanelState.Found && ProposedChanges.Any(change => change.IsSelected);

    [RelayCommand(CanExecute = nameof(CanUndoMetadata))]
    private async Task UndoMetadataAsync(CancellationToken cancellationToken)
    {
        if (Book is null)
        {
            return;
        }

        var restored = await catalogService.UndoMetadataAsync(Book.Id, cancellationToken);
        if (restored is not null)
        {
            Book = restored;
        }

        CanUndo = false;
        PanelState = MetadataUpdatePanelState.Idle;
    }

    private bool CanUndoMetadata() => CanUndo;

    private void ApplyFetchResult(FetchedBookMetadata fetched)
    {
        _pendingFetchedBook = fetched;
        var book = Book!;
        var changes = new List<MetadataFieldChange>();

        void AddIfChanged(string field, string label, string? oldValue, string? newValue, bool preselect = true)
        {
            if (string.IsNullOrWhiteSpace(newValue) || string.Equals(oldValue?.Trim(), newValue.Trim(), StringComparison.Ordinal))
            {
                return;
            }

            changes.Add(new MetadataFieldChange
            {
                Field = field,
                Label = label,
                OldValueDisplay = string.IsNullOrWhiteSpace(oldValue) ? "(none)" : oldValue,
                NewValueDisplay = newValue,
                IsSelected = preselect,
            });
        }

        AddIfChanged("title", "Title", book.Title, fetched.Title);
        AddIfChanged("author", "Author", book.Author, fetched.Authors.Count > 0 ? string.Join(", ", fetched.Authors) : null);
        AddIfChanged("publisher", "Publisher", book.Publisher, fetched.Publisher);
        AddIfChanged("description", "Description", book.Description, fetched.Description, preselect: false);

        if (!string.IsNullOrWhiteSpace(fetched.CoverUrl))
        {
            changes.Add(new MetadataFieldChange
            {
                Field = "cover",
                Label = "Cover",
                NewValueDisplay = fetched.CoverUrl,
                IsCover = true,
                IsSelected = true,
            });
        }

        foreach (var change in changes)
        {
            change.PropertyChanged += (_, _) => ApplyMetadataCommand.NotifyCanExecuteChanged();
        }

        ProposedChanges = new ObservableCollection<MetadataFieldChange>(changes);

        var providerName = fetched.SourceProvider == BookMetadataProvider.GoogleBooks ? "Google Books" : "Open Library";
        var identifier = fetched.Isbn13 ?? fetched.Isbn10;
        SourceLine = identifier is not null ? $"{providerName} · ISBN {identifier}" : providerName;
        SourceAttributionText = $"Powered by {providerName}";

        if (changes.Count == 0)
        {
            PanelState = MetadataUpdatePanelState.NotFound;
            StatusMessage = $"{providerName} returned a match, but nothing differs from your current metadata.";
        }
        else
        {
            PanelState = MetadataUpdatePanelState.Found;
        }

        ApplyMetadataCommand.NotifyCanExecuteChanged();
    }

    private string GetSelectedValue(string field, string fallback)
    {
        var change = ProposedChanges.FirstOrDefault(candidate => candidate.Field == field);
        return change is { IsSelected: true } ? change.NewValueDisplay : fallback;
    }

    private async Task RefreshCanUndoAsync()
    {
        if (Book is null)
        {
            CanUndo = false;
            return;
        }

        try
        {
            CanUndo = await catalogService.HasPreviousMetadataAsync(Book.Id, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not determine undo availability for {BookId}.", Book.Id);
            CanUndo = false;
        }
    }
}
