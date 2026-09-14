using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DisplayBook.App.Interfaces;
using DisplayBook.App.Models;
using DisplayBook.App.Services;
using DisplayBook.Viewer.Models;
using DisplayBook.Viewer.Services;
using Microsoft.Extensions.Logging;

namespace DisplayBook.App.ViewModels;

public partial class ReaderViewModel(
	IBookCatalogService catalogService,
	IPositionSyncService syncService,
	ILogger<ReaderViewModel> logger) : ObservableObject
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
	/// details page's prefetch -- and resolves its start locator, checking for a position
	/// synced from another device and, if it disagrees with this device's own position,
	/// prompting the user to choose which one to open at. <see cref="Locator"/>
	/// and <see cref="PublicationSource"/> are always set before <see cref="Book"/>: the
	/// EpubReaderView control only reads its bound StartLocator once, when it (re)loads a
	/// publication in response to Book/PublicationSource changing, so Book must never become
	/// non-empty until both the final locator (local, or remote if the user chose to jump to
	/// it) and the parsed publication are already in place. The remote lookup is bounded by a short timeout
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
		logger.LogInformation(
			"Reader init: book {BookId} opening at ResourceHref={ResourceHref}, Page={Page}, PageCount={PageCount}, CharOffset={CharOffset}.",
			book.Id, Locator.ResourceHref, Locator.Page, Locator.PageCount, Locator.CharOffset);
	}

	async Task<EpubLocator> ResolveRemoteLocatorAsync(BookSummary book, EpubLocator localLocator)
	{
		logger.LogInformation(
			"Local locator for {ContentHash}: ResourceHref={ResourceHref}, Page={Page}, PageCount={PageCount}, CharOffset={CharOffset}, LastOpenedAt={LastOpenedAt}.",
			book.ContentHash, localLocator.ResourceHref, localLocator.Page, localLocator.PageCount, localLocator.CharOffset, book.LastOpenedAt);

		if (string.IsNullOrEmpty(book.ContentHash))
		{
			logger.LogInformation("No ContentHash for book {BookId}; skipping remote position lookup.", book.Id);
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
			logger.LogWarning("Remote position lookup for {ContentHash} timed out.", book.ContentHash);
			return localLocator;
		}

		if (remote is null || string.IsNullOrWhiteSpace(remote.ResourceHref))
		{
			logger.LogInformation("No remote position found for {ContentHash}.", book.ContentHash);
			return localLocator;
		}

		EpubLocator remoteLocator = new(remote.ResourceHref, remote.Page, remote.PageCount, remote.CharOffset);
		logger.LogInformation(
			"Remote locator for {ContentHash}: ResourceHref={ResourceHref}, Page={Page}, PageCount={PageCount}, CharOffset={CharOffset}, UpdatedAt={UpdatedAt}.",
			book.ContentHash, remoteLocator.ResourceHref, remoteLocator.Page, remoteLocator.PageCount, remoteLocator.CharOffset, remote.UpdatedAt);

		// Nothing to choose between if this device has never read the book, or the two
		// positions already agree -- only ask when accepting the remote position would
		// actually move the reader somewhere else.
		if (string.IsNullOrWhiteSpace(localLocator.ResourceHref) || IsSamePosition(localLocator, remoteLocator))
		{
			logger.LogInformation("Adopting remote locator for {ContentHash} without prompting (no local position, or positions already match).", book.ContentHash);
			return remoteLocator;
		}

		DateTimeOffset localUpdatedAt = book.LastOpenedAt ?? DateTimeOffset.MinValue;
		bool remoteIsNewer = remote.UpdatedAt > localUpdatedAt;
		bool acceptRemote = await PromptToAdoptRemotePositionAsync(remoteIsNewer, remote.UpdatedAt);
		logger.LogInformation(
			"User {Choice} the {Direction} synced position for {ContentHash}.",
			acceptRemote ? "accepted" : "declined", remoteIsNewer ? "newer" : "older", book.ContentHash);
		return acceptRemote ? remoteLocator : localLocator;
	}

	static bool IsSamePosition(EpubLocator a, EpubLocator b) =>
		string.Equals(a.ResourceHref, b.ResourceHref, StringComparison.Ordinal) &&
		(a.CharOffset >= 0 && b.CharOffset >= 0 ? a.CharOffset == b.CharOffset : a.Page == b.Page);

	/// <summary>
	/// Asks the user before jumping to a reading position synced from another device, rather
	/// than silently adopting whichever side has the newer timestamp: date/time comparisons
	/// are an imperfect proxy for "where the reader actually wants to be" (e.g. a device that
	/// was left open, or a locator that predates a since-abandoned re-read), so the choice --
	/// framed as advancing to a later position or rewinding to an earlier one -- is left to
	/// the user instead.
	/// </summary>
	static Task<bool> PromptToAdoptRemotePositionAsync(bool remoteIsNewer, DateTimeOffset remoteUpdatedAt)
	{
		string when = remoteUpdatedAt.ToLocalTime().ToString("g");
		string title = remoteIsNewer ? "Continue from another device?" : "Different reading position found";
		string message = remoteIsNewer
			? $"You read further on another device on {when}. Advance to that position?"
			: $"A reading position from another device on {when} is behind where you are now. Rewind to that position?";
		string acceptLabel = remoteIsNewer ? "Advance" : "Rewind";
		return Shell.Current.DisplayAlertAsync(title, message, acceptLabel, "Stay Here");
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
		logger.LogInformation(
			"Reader reported locator for book {BookId}: ResourceHref={ResourceHref}, Page={Page}, PageCount={PageCount}, CharOffset={CharOffset}.",
			Book.Id, locator.ResourceHref, locator.Page, locator.PageCount, locator.CharOffset);
		await catalogService.SaveLocatorAsync(Book.Id, locator.ResourceHref, locator.Page, locator.PageCount, locator.CharOffset);
		if (!string.IsNullOrWhiteSpace(Book.ContentHash))
		{
			_ = syncService.SchedulePush(Book.ContentHash, locator.ResourceHref, locator.CharOffset, locator.Page, locator.PageCount, updatedAt);
		}
	}

	public Task FlushPendingSyncAsync() => syncService.FlushPendingPushAsync();
}