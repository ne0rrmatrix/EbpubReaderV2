using CommunityToolkit.Mvvm.Input;
using DisplayBook.App.Models;
using DisplayBook.App.Services;
using Microsoft.Extensions.Logging;

namespace DisplayBook.App.ViewModels;

// Split out of BookDetailsViewModel.cs because that file is compiled directly into
// DisplayBook.Tests (see the test .csproj's <Compile Include> list) to keep the tests
// MAUI-free. Everything here touches Shell/Launcher (or, below, the Viewer-project
// EpubArchive type), so it stays App-only.
public partial class BookDetailsViewModel
{
	public async Task LoadBookAsync(string bookId)
	{
		BookSummary? book = await catalogService.GetBookAsync(bookId, cancellationToken: CancellationToken.None);
		if (book is null)
		{
			return;
		}

		SetBook(book);
		OpenCommand.NotifyCanExecuteChanged();

		// The reader needs this same .epub read into memory, parsed, and assembled into its
		// combined reading document before it can show anything; starting all of that here lets
		// it happen while the user is still reading the details page instead of adding to the
		// reader's own loading time.
		EpubArchivePrefetchCache.Prefetch(book.Id, BookStorageService.GetAbsolutePath(book.EpubRelativePath));
	}

	[RelayCommand(CanExecute = nameof(CanOpen))]
	async Task OpenAsync()
	{
		BookSummary book = Book ?? throw new InvalidOperationException("A book must be selected before opening the reader.");
		await Shell.Current.GoToAsync($"reader?id={book.Id}");
	}

	bool CanOpen() => Book is not null;

	[RelayCommand]
	async Task BackAsync() => await Shell.Current.GoToAsync("..");

	[RelayCommand]
	async Task OpenSourceLinkAsync()
	{
		if (pendingFetchedBook?.SourceLink is not { Length: > 0 } link || !Uri.TryCreate(link, UriKind.Absolute, out Uri? uri))
		{
			return;
		}

		try
		{
			await Launcher.Default.OpenAsync(uri);
		}
		catch (Exception exception)
		{
			logger.LogDebug(exception, "Could not open the metadata source link {Link}.", link);
		}
	}
}
