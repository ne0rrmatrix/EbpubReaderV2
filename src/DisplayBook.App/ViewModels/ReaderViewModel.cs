using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DisplayBook.App.Interfaces;
using DisplayBook.App.Models;
using DisplayBook.App.Services;
using DisplayBook.Viewer.Models;
using DisplayBook.Viewer.Services;

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

	[ObservableProperty]
	public partial EpubArchive? PublicationSource { get; set; }

	/// <summary>
	/// Loads the book by id, gets its persisted .epub ready to display -- read into memory,
	/// parsed, and assembled into its combined reading document, usually already done by the
	/// details page's prefetch -- and resolves its start locator, checking for a newer position
	/// synced from another device. <see cref="Locator"/>
	/// and <see cref="PublicationSource"/> are always set before <see cref="Book"/>: the
	/// EpubReaderView control only reads its bound StartLocator once, when it (re)loads a
	/// publication in response to Book/PublicationSource changing, so Book must never become
	/// non-empty until both the final locator (local or remote, whichever is newer) and the
	/// parsed publication are already in place. The remote lookup is bounded by a short timeout
	/// so a slow/unreachable network never delays opening a book for long -- and it runs
	/// concurrently with parsing the .epub rather than before it, since the two are independent
	/// and the remote lookup (auth token refresh + a Firestore round trip) can otherwise take far
	/// longer than reading the book off local disk.
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

		Task<EpubLocator> resolvedLocatorTask = ResolveRemoteLocatorAsync(book, locator);
		Task<EpubArchive> publicationSourceTask = EpubArchivePrefetchCache.TakeOrOpenAsync(book.Id, BookStorageService.GetAbsolutePath(book.EpubRelativePath));
		await Task.WhenAll(resolvedLocatorTask, publicationSourceTask);

		Locator = resolvedLocatorTask.Result;
		PublicationSource = publicationSourceTask.Result;
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