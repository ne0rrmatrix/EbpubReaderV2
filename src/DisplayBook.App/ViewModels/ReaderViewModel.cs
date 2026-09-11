using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DisplayBook.App.Models;
using DisplayBook.App.Services;
using DisplayBook.App.Services.Sync;
using DisplayBook.Viewer.Models;

namespace DisplayBook.App.ViewModels;

public partial class ReaderViewModel(
	INavigationService navigationService,
	IBookCatalogService catalogService,
	IPositionSyncService syncService) : ObservableObject
{
	static readonly TimeSpan remotePositionLookupTimeout = TimeSpan.FromSeconds(3);

	[ObservableProperty]
	public partial BookSummary? Book { get; set; }

	[ObservableProperty]
	public partial EpubLocator Locator { get; set; } = EpubLocator.Empty;

	public void SetBook(BookSummary book)
	{
		Book = book;
		Locator = string.IsNullOrWhiteSpace(book.LocatorResourceHref)
			? EpubLocator.Empty
			: new EpubLocator(book.LocatorResourceHref, book.LocatorPage, book.LocatorPageCount, book.LocatorCharOffset);
	}

	/// <summary>
	/// Checks for a newer position synced from another device and, if found, overwrites
	/// <see cref="Locator"/> before the reader page is shown. Must be awaited before the
	/// page is pushed: the WebView only reads its start locator once, at startup, so
	/// updating <see cref="Locator"/> after that point would have no effect. Bounded by
	/// a short timeout so a slow/unreachable network never delays opening a book for long.
	/// </summary>
	public async Task ApplyRemoteLocatorIfNewerAsync()
	{
		if (Book is not { ContentHash.Length: > 0 } book)
		{
			return;
		}

		using CancellationTokenSource timeoutCts = new(remotePositionLookupTimeout);
		RemoteReadingPosition? remote;
		try
		{
			remote = await syncService.TryPullPositionAsync(book.ContentHash, timeoutCts.Token);
		}
		catch (OperationCanceledException)
		{
			return;
		}

		if (remote is null || string.IsNullOrWhiteSpace(remote.ResourceHref))
		{
			return;
		}

		DateTimeOffset localUpdatedAt = book.LastOpenedAt ?? DateTimeOffset.MinValue;
		if (remote.UpdatedAt <= localUpdatedAt)
		{
			return;
		}

		Locator = new EpubLocator(remote.ResourceHref, remote.Page, remote.PageCount, remote.CharOffset);
	}

	[RelayCommand]
	async Task ExitReaderAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await navigationService.GoBackAsync();
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
			syncService.SchedulePush(Book.ContentHash, locator.ResourceHref, locator.CharOffset, locator.Page, locator.PageCount, updatedAt);
		}
	}

	public Task FlushPendingSyncAsync() => syncService.FlushPendingPushAsync();
}
