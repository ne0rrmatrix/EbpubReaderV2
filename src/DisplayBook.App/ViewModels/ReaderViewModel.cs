using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DisplayBook.App.Interfaces;
using DisplayBook.App.Models;
using DisplayBook.Viewer.Models;

namespace DisplayBook.App.ViewModels;

public partial class ReaderViewModel(
	IBookCatalogService catalogService,
	IPositionSyncService syncService) : ObservableObject
{
	static readonly TimeSpan remotePositionLookupTimeout = TimeSpan.FromSeconds(3);

	[ObservableProperty]
	public partial BookSummary? Book { get; set; }

	[ObservableProperty]
	public partial EpubLocator Locator { get; set; } = EpubLocator.Empty;

	/// <summary>
	/// Loads the book by id and resolves its start locator, checking for a newer position
	/// synced from another device. <see cref="Locator"/> is always set before <see cref="Book"/>:
	/// the EpubReaderView control only reads its bound StartLocator once, when it (re)loads a
	/// publication in response to Book/PublicationRoot changing, so Book must never become
	/// non-empty until the final locator (local or remote, whichever is newer) is already in place.
	/// The remote lookup is bounded by a short timeout so a slow/unreachable network never delays
	/// opening a book for long.
	/// </summary>
	public async Task InitializeAsync(string bookId)
	{
		BookSummary? book = await catalogService.GetBookAsync(bookId);
		if (book is null)
		{
			return;
		}

		EpubLocator locator = string.IsNullOrWhiteSpace(book.LocatorResourceHref)
			? EpubLocator.Empty
			: new EpubLocator(book.LocatorResourceHref, book.LocatorPage, book.LocatorPageCount, book.LocatorCharOffset);

		Locator = await ResolveRemoteLocatorAsync(book, locator);
		Book = book;
	}

	async Task<EpubLocator> ResolveRemoteLocatorAsync(BookSummary book, EpubLocator localLocator)
	{
		if (string.IsNullOrEmpty(book.ContentHash))
		{
			return localLocator;
		}

		using CancellationTokenSource timeoutCts = new(remotePositionLookupTimeout);
		RemoteReadingPosition? remote;
		try
		{
			remote = await syncService.TryPullPositionAsync(book.ContentHash, timeoutCts.Token);
		}
		catch (OperationCanceledException)
		{
			return localLocator;
		}

		if (remote is null || string.IsNullOrWhiteSpace(remote.ResourceHref))
		{
			return localLocator;
		}

		DateTimeOffset localUpdatedAt = book.LastOpenedAt ?? DateTimeOffset.MinValue;
		return remote.UpdatedAt <= localUpdatedAt
			? localLocator
			: new EpubLocator(remote.ResourceHref, remote.Page, remote.PageCount, remote.CharOffset);
	}

	[RelayCommand]
	async Task ExitReaderAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await Shell.Current.GoToAsync("..");
	}

	public async Task UpdateLocatorAsync(EpubLocator locator)
	{
		Locator = locator;
		if (Book is null || string.IsNullOrWhiteSpace(locator.ResourceHref))
		{
			return;
		}

		DateTimeOffset updatedAt = DateTimeOffset.UtcNow;
		await catalogService.SaveLocatorAsync(Book.Id, locator.ResourceHref, locator.Page, locator.PageCount, locator.CharOffset);
		if (!string.IsNullOrWhiteSpace(Book.ContentHash))
		{
			_ = syncService.SchedulePush(Book.ContentHash, locator.ResourceHref, locator.CharOffset, locator.Page, locator.PageCount, updatedAt);
		}
	}

	public Task FlushPendingSyncAsync() => syncService.FlushPendingPushAsync();
}