using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DisplayBook.App.Interfaces;
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

	CancellationTokenSource? fetchCancellationSource;
	FetchedBookMetadata? pendingFetchedBook;

	public void SetBook(BookSummary book)
	{
		Book = book;
		PanelState = MetadataUpdatePanelState.Idle;
		ProposedChanges = [];
		pendingFetchedBook = null;
		_ = RefreshCanUndoAsync();
	}

	[RelayCommand(CanExecute = nameof(CanOpen))]
	Task OpenAsync()
	{
		return navigationService.ShowReaderAsync(Book ?? throw new InvalidOperationException("A book must be selected before opening the reader."));
	}

	[RelayCommand]
	Task BackAsync()
	{
		return navigationService.GoBackAsync();
	}

	bool CanOpen() => Book is not null;

	[RelayCommand(CanExecute = nameof(CanUpdateMetadata))]
	async Task UpdateMetadataAsync(CancellationToken cancellationToken)
	{
		if (Book is null)
		{
			return;
		}

		using CancellationTokenSource linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		fetchCancellationSource = linkedSource;
		PanelState = MetadataUpdatePanelState.Loading;
		StatusMessage = string.Empty;

		try
		{
			BookMetadataFetchResult result = await bookMetadataService.FetchMetadataAsync(Book, linkedSource.Token);
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
			fetchCancellationSource = null;
		}
	}

	bool CanUpdateMetadata() => Book is not null && PanelState != MetadataUpdatePanelState.Loading;

	[RelayCommand]
	void CancelMetadataFetch()
	{
		fetchCancellationSource?.Cancel();
	}

	[RelayCommand]
	void ClosePanel()
	{
		PanelState = MetadataUpdatePanelState.Idle;
		ProposedChanges = [];
		pendingFetchedBook = null;
	}

	[RelayCommand]
	async Task OpenSourceLinkAsync()
	{
		if (pendingFetchedBook?.SourceLink is not { Length: > 0 } link || !Uri.TryCreate(link, UriKind.Absolute, out Uri? uri))
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
	async Task ApplyMetadataAsync(CancellationToken cancellationToken)
	{
		if (Book is null || pendingFetchedBook is null)
		{
			return;
		}

		FetchedBookMetadata fetched = pendingFetchedBook;
		string? fetchedIsbn = fetched.Isbn13 ?? fetched.Isbn10;
		BookSummary updated = Book with
		{
			Title = GetSelectedValue("title", Book.Title),
			Author = GetSelectedValue("author", Book.Author),
			Publisher = GetSelectedValue("publisher", Book.Publisher),
			Description = GetSelectedValue("description", Book.Description),
			Isbn = string.IsNullOrWhiteSpace(Book.Isbn) && fetchedIsbn is not null ? fetchedIsbn : Book.Isbn,
		};

		string? newCoverRelativePath = null;
		MetadataFieldChange? coverChange = ProposedChanges.FirstOrDefault(change => change.IsCover);
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

		BookSummary saved = await catalogService.UpdateMetadataAsync(Book.Id, updated, newCoverRelativePath, cancellationToken);
		Book = saved;
		CanUndo = true;
		PanelState = MetadataUpdatePanelState.Applied;
		StatusMessage = $"{ProposedChanges.Count(change => change.IsSelected)} field(s) changed" + (newCoverRelativePath is not null ? ", cover replaced." : ".");
		pendingFetchedBook = null;
	}

	bool CanApplyMetadata() => PanelState == MetadataUpdatePanelState.Found && ProposedChanges.Any(change => change.IsSelected);

	[RelayCommand(CanExecute = nameof(CanUndoMetadata))]
	async Task UndoMetadataAsync(CancellationToken cancellationToken)
	{
		if (Book is null)
		{
			return;
		}

		BookSummary? restored = await catalogService.UndoMetadataAsync(Book.Id, cancellationToken);
		if (restored is not null)
		{
			Book = restored;
		}

		CanUndo = false;
		PanelState = MetadataUpdatePanelState.Idle;
	}

	bool CanUndoMetadata() => CanUndo;

	void ApplyFetchResult(FetchedBookMetadata fetched)
	{
		pendingFetchedBook = fetched;
		BookSummary book = Book!;
		List<MetadataFieldChange> changes = new();

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

		foreach (MetadataFieldChange change in changes)
		{
			change.PropertyChanged += (_, _) => ApplyMetadataCommand.NotifyCanExecuteChanged();
		}

		ProposedChanges = [with(changes)];

		string providerName = fetched.SourceProvider == BookMetadataProvider.GoogleBooks ? "Google Books" : "Open Library";
		string? identifier = fetched.Isbn13 ?? fetched.Isbn10;
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

	string GetSelectedValue(string field, string fallback)
	{
		MetadataFieldChange? change = ProposedChanges.FirstOrDefault(candidate => candidate.Field == field);
		return change is { IsSelected: true } ? change.NewValueDisplay : fallback;
	}

	async Task RefreshCanUndoAsync()
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
