using DisplayBook.Viewer.Services;

namespace DisplayBook.App.Services;

/// <summary>
/// Gets a book fully ready to display as soon as its details page is shown, instead of waiting
/// until the reader page opens it -- so the work overlaps with however long the user spends
/// looking at the details page rather than adding to the reader's own loading time. "Ready" means
/// both halves of opening a book: reading the .epub into memory (see <see cref="EpubArchive"/>)
/// and parsing/assembling it into the combined reading document (see
/// <see cref="EpubPublicationLoader"/>). The second half used to run in the reader itself, where
/// it sat squarely in the path between the user tapping Open and seeing a page.
///
/// Deliberately holds at most one pending/prepared book at a time: this is a hand-off for "the
/// book the user is about to open next", not a general-purpose cache, and keeping more than one
/// fully-parsed (and therefore fully in-memory) book resident for books that are never actually
/// opened isn't worth the memory.
/// </summary>
public static class EpubArchivePrefetchCache
{
	static readonly Lock gate = new();
	static string? pendingBookId;
	static Task<EpubArchive>? pendingTask;

	public static void Prefetch(string bookId, string epubFilePath)
	{
		lock (gate)
		{
			if (pendingBookId == bookId)
			{
				return;
			}

			pendingBookId = bookId;
			Task<EpubArchive> task = OpenAndPrepareAsync(epubFilePath, CancellationToken.None);
			pendingTask = task;

			// Nothing awaits this task until TakeOrOpenAsync runs -- which may be never, if the
			// user navigates away from the details page without opening the book. Observe a
			// failure here so it doesn't surface as an UnobservedTaskException on finalization.
			task.ContinueWith(
				static faulted => _ = faulted.Exception,
				CancellationToken.None,
				TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
				TaskScheduler.Default);
		}
	}

	public static Task<EpubArchive> TakeOrOpenAsync(string bookId, string epubFilePath, CancellationToken cancellationToken = default)
	{
		lock (gate)
		{
			if (pendingBookId == bookId && pendingTask is { } task)
			{
				pendingBookId = null;
				pendingTask = null;
				return task;
			}
		}

		// No prefetch to take -- either the details page never ran one, or a different book's is
		// pending. Do the same work here so the reader's own path is identical either way; it
		// still overlaps with the remote-position lookup that runs alongside it.
		return OpenAndPrepareAsync(epubFilePath, cancellationToken);
	}

	/// <summary>
	/// Reads the .epub into memory and then parses/assembles it. The two are sequential because
	/// the second needs the first's output, but together they are everything that has to happen
	/// before the reader can hand the book to its WebView.
	/// </summary>
	static async Task<EpubArchive> OpenAndPrepareAsync(string epubFilePath, CancellationToken cancellationToken)
	{
		EpubArchive archive = await EpubArchive.OpenAsync(epubFilePath, cancellationToken);

		// Not passed the token: this result is memoized per archive and shared with whoever opens
		// the book next, so it must not be cancellable on this caller's behalf.
		await EpubPublicationLoader.PrepareAsync(archive);
		return archive;
	}
}
