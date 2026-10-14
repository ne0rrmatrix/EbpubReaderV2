using DisplayBook.App.Interfaces;
using DisplayBook.App.Models;
using DisplayBook.App.ViewModels;
using DisplayBook.App.Views;
using DisplayBook.DeviceTests.Support;

namespace DisplayBook.DeviceTests.Tests.Pages;

/// <summary>
/// The library page in the real app shell: importing (through the fake picker), search, sort,
/// selection, deletion (with its confirmation answered by <see cref="TestDialogs"/>) and the
/// navigation it starts.
/// </summary>
public class LibraryPageTests : AppDataTest
{
	static async Task<LibraryViewModel> OpenLibraryAsync()
	{
		await TestApp.GetShellAsync();
		await TestApp.ResetNavigationAsync();
		LibraryPage page = await TestApp.WaitForPageAsync<LibraryPage>();
		await page.ViewModel.ReloadCatalogAsync(CancellationToken.None);
		return page.ViewModel;
	}

	static async Task<LibraryViewModel> OpenLibraryWithAsync(params string[] titles)
	{
		IBookImportService import = TestApp.GetService<IBookImportService>();
		foreach (string title in titles)
		{
			await import.ImportLocalFileAsync(TestEpub.Write(title, author: $"{title} Author"));
		}

		return await OpenLibraryAsync();
	}

	static string[] VisibleTitles(LibraryViewModel library) => [.. library.VisibleBooks.Select(book => book.Title)];

	/// <summary>
	/// Each sort or search change gives the page's CollectionView a new ItemsSource. Swapping it
	/// again before WinUI has laid out the previous one has crashed the test app natively (an access
	/// violation in Microsoft.UI.Xaml.Controls.dll) under the coverage profiler, so changes are
	/// paced the way a person makes them.
	/// </summary>
	static Task SettleAsync() => Task.Delay(300);

	[UIFact]
	public async Task ImportFile_AddsThePickedBook()
	{
		LibraryViewModel library = await OpenLibraryAsync();
		FakeBookPicker.NextFile = TestEpub.Write("Alpha");

		await library.ImportFileCommand.ExecuteAsync(null);

		Assert.Equal(["Alpha"], VisibleTitles(library));
		Assert.False(library.IsLibraryEmpty);
		Assert.False(library.HasError);
		Assert.False(library.IsImporting);
		Assert.True(library.IsNotBusy);
	}

	[UIFact]
	public async Task ImportFile_PickerCancelled_ChangesNothing()
	{
		LibraryViewModel library = await OpenLibraryAsync();

		await library.ImportFileCommand.ExecuteAsync(null);

		Assert.Empty(library.VisibleBooks);
		Assert.True(library.IsLibraryEmpty);
		Assert.False(library.HasError);
	}

	[UIFact]
	public async Task ImportFile_NotAnEpub_ShowsAnError()
	{
		LibraryViewModel library = await OpenLibraryAsync();
		string path = Path.Combine(FileSystem.CacheDirectory, "notes.txt");
		await File.WriteAllTextAsync(path, "not a book");
		FakeBookPicker.NextFile = path;

		await library.ImportFileCommand.ExecuteAsync(null);

		Assert.Equal("Please choose a file with the .epub extension.", library.ErrorMessage);
		Assert.True(library.HasError);
	}

	[UIFact]
	public async Task ImportFile_SameBookTwice_IsAddedOnce()
	{
		LibraryViewModel library = await OpenLibraryAsync();
		byte[] book = TestEpub.Build("Alpha");
		string first = Path.Combine(FileSystem.CacheDirectory, "first.epub");
		string second = Path.Combine(FileSystem.CacheDirectory, "second.epub");
		await File.WriteAllBytesAsync(first, book);
		await File.WriteAllBytesAsync(second, book);

		FakeBookPicker.NextFile = first;
		await library.ImportFileCommand.ExecuteAsync(null);
		FakeBookPicker.NextFile = second;
		await library.ImportFileCommand.ExecuteAsync(null);

		Assert.Equal(["Alpha"], VisibleTitles(library));
	}

	[UIFact]
	public async Task ImportFolder_ImportsEveryEpubAndSkipsDuplicates()
	{
		LibraryViewModel library = await OpenLibraryWithAsync("Existing");
		string folder = Directory.CreateTempSubdirectory("displaybook-import-").FullName;
		File.Copy(TestEpub.Write("Bravo"), Path.Combine(folder, "bravo.epub"));
		Directory.CreateDirectory(Path.Combine(folder, "nested"));
		File.Copy(TestEpub.Write("Charlie"), Path.Combine(folder, "nested", "charlie.epub"));
		await File.WriteAllTextAsync(Path.Combine(folder, "readme.txt"), "ignored");
		FakeBookPicker.NextFolder = folder;

		await library.ImportFolderCommand.ExecuteAsync(null);

		Assert.Equal(["Bravo", "Charlie", "Existing"], VisibleTitles(library).Order());
		Assert.False(library.HasError);
	}

	[UIFact]
	public async Task ImportFolder_UnpackedEpubFolder_IsZippedAndImported()
	{
		LibraryViewModel library = await OpenLibraryAsync();
		string folder = Directory.CreateTempSubdirectory("displaybook-unpacked-").FullName;
		System.IO.Compression.ZipFile.ExtractToDirectory(TestEpub.Write("Unpacked"), folder);
		FakeBookPicker.NextFolder = folder;

		await library.ImportFolderCommand.ExecuteAsync(null);

		Assert.Equal(["Unpacked"], VisibleTitles(library));
	}

	[UIFact]
	public async Task ImportFolder_NoEpubs_ShowsAnError()
	{
		LibraryViewModel library = await OpenLibraryAsync();
		FakeBookPicker.NextFolder = Directory.CreateTempSubdirectory("displaybook-empty-").FullName;

		await library.ImportFolderCommand.ExecuteAsync(null);

		Assert.Contains("does not contain any .epub files", library.ErrorMessage);
	}

	[UIFact]
	public async Task ImportFolder_OnlyBrokenEpubs_ShowsWhyTheyWereSkipped()
	{
		LibraryViewModel library = await OpenLibraryAsync();
		string folder = Directory.CreateTempSubdirectory("displaybook-broken-").FullName;
		await File.WriteAllTextAsync(Path.Combine(folder, "broken.epub"), "this is not a zip file");
		FakeBookPicker.NextFolder = folder;

		await library.ImportFolderCommand.ExecuteAsync(null);

		Assert.StartsWith("No valid new EPUBs could be imported. broken.epub:", library.ErrorMessage);
		Assert.Empty(library.VisibleBooks);
	}

	[UIFact]
	public async Task ImportFolder_PickerCancelled_ChangesNothing()
	{
		LibraryViewModel library = await OpenLibraryAsync();

		await library.ImportFolderCommand.ExecuteAsync(null);

		Assert.Empty(library.VisibleBooks);
		Assert.False(library.HasError);
	}

	[UIFact]
	public async Task CancelImport_StopsAFolderImportPartWay()
	{
		LibraryViewModel library = await OpenLibraryAsync();
		string folder = Directory.CreateTempSubdirectory("displaybook-many-").FullName;
		for (int index = 0; index < 40; index++)
		{
			File.Copy(TestEpub.Write($"Book {index:00}", chapters: 40), Path.Combine(folder, $"book{index:00}.epub"));
		}

		FakeBookPicker.NextFolder = folder;
		Task import = library.ImportFolderCommand.ExecuteAsync(null);
		await Wait.UntilAsync(() => library.IsImporting && library.ImportCompleted > 0, "the folder import to start");
		Assert.True(library.CancelImportCommand.CanExecute(null));
		Assert.EndsWith("completed", library.ImportProgressSummary);
		Assert.EndsWith("%", library.ImportProgressPercent);

		library.CancelImportCommand.Execute(null);
		await import;

		Assert.False(library.IsImporting);
		Assert.False(library.HasError);
		Assert.InRange(library.VisibleBooks.Count, 0, 39);
	}

	[UIFact]
	public async Task Search_FiltersByTitleOrAuthor()
	{
		LibraryViewModel library = await OpenLibraryWithAsync("Dune", "Emma", "Middlemarch");

		library.SearchQuery = "emm";
		await SettleAsync();
		Assert.Equal(["Emma"], VisibleTitles(library));

		library.SearchQuery = "Dune Author";
		await SettleAsync();
		Assert.Equal(["Dune"], VisibleTitles(library));

		library.SearchQuery = "nothing matches";
		await SettleAsync();
		Assert.Empty(library.VisibleBooks);
		Assert.True(library.IsSearchEmpty);

		library.SearchQuery = string.Empty;
		await SettleAsync();
		Assert.Equal(3, library.VisibleBooks.Count);
	}

	[UIFact]
	public async Task Sort_OrdersByTheChosenOption()
	{
		LibraryViewModel library = await OpenLibraryWithAsync("Charlie", "Alpha", "Bravo");
		IBookCatalogService catalog = TestApp.GetService<IBookCatalogService>();
		BookSummary bravo = library.Books.Single(book => book.Title == "Bravo");
		await catalog.SaveLocatorAsync(bravo.Id, "Text/ch1.xhtml", 1, 10);
		await library.ReloadCatalogAsync(CancellationToken.None);

		library.SortOption = "Title (A–Z)";
		await SettleAsync();
		Assert.Equal(["Alpha", "Bravo", "Charlie"], VisibleTitles(library));

		library.SortOption = "Author (A–Z)";
		await SettleAsync();
		Assert.Equal(["Alpha", "Bravo", "Charlie"], VisibleTitles(library));

		library.SortOption = "Recently opened";
		await SettleAsync();
		Assert.Equal("Bravo", VisibleTitles(library)[0]);

		library.SortOption = "Recently added";
		await SettleAsync();
		Assert.Equal(["Bravo", "Alpha", "Charlie"], VisibleTitles(library));
		Assert.Equal(4, library.SortOptions.Count);
	}

	[UIFact]
	public async Task Selection_SelectsTogglesAndClears()
	{
		LibraryViewModel library = await OpenLibraryWithAsync("Alpha", "Bravo");
		Assert.True(library.IsNotSelectionMode);
		Assert.False(library.ExitSelectionModeCommand.CanExecute(null));

		library.EnterSelectionModeCommand.Execute(null);
		Assert.True(library.IsSelectionMode);
		Assert.False(library.ImportFileCommand.CanExecute(null));

		LibraryBookItem first = library.VisibleBooks[0];
		library.ToggleSelectionCommand.Execute(first);
		Assert.True(first.IsSelected);
		Assert.Equal(1, library.SelectedCount);
		Assert.True(library.HasSelection);
		Assert.False(library.AreAllVisibleBooksSelected);

		// Tapping a book in selection mode toggles it rather than opening it.
		await library.OpenDetailsCommand.ExecuteAsync(first);
		Assert.False(first.IsSelected);

		library.SelectAllCommand.Execute(null);
		Assert.True(library.AreAllVisibleBooksSelected);
		Assert.Equal(2, library.SelectedCount);

		library.ClearSelectionCommand.Execute(null);
		Assert.Equal(0, library.SelectedCount);

		library.ExitSelectionModeCommand.Execute(null);
		Assert.False(library.IsSelectionMode);
	}

	[UIFact]
	public async Task DeleteSelected_Confirmed_RemovesTheBooks()
	{
		LibraryViewModel library = await OpenLibraryWithAsync("Alpha", "Bravo");
		library.EnterSelectionModeCommand.Execute(null);
		library.ToggleSelectionCommand.Execute(library.VisibleBooks.Single(book => book.Title == "Alpha"));
		TestDialogs.AnswerNextAlert(accept: true);

		await library.DeleteSelectedCommand.ExecuteAsync(null);

		Assert.Equal(["Bravo"], VisibleTitles(library));
		Assert.False(library.IsSelectionMode);
		Assert.Equal("Permanently delete 1 book and its stored EPUB content?", Assert.Single(TestDialogs.Alerts).Message);
		Assert.Single(await TestApp.GetService<IBookCatalogService>().GetBooksAsync());
	}

	[UIFact]
	public async Task DeleteSelected_Declined_KeepsTheBooks()
	{
		LibraryViewModel library = await OpenLibraryWithAsync("Alpha", "Bravo");
		library.EnterSelectionModeCommand.Execute(null);
		library.SelectAllCommand.Execute(null);

		await library.DeleteSelectedCommand.ExecuteAsync(null);

		Assert.Equal(2, library.VisibleBooks.Count);
		Assert.Equal("Permanently delete 2 books and its stored EPUB content?", Assert.Single(TestDialogs.Alerts).Message);
	}

	[UIFact]
	public async Task PullToRefresh_ReloadsTheCatalog()
	{
		LibraryViewModel library = await OpenLibraryAsync();
		LibraryPage page = await TestApp.WaitForPageAsync<LibraryPage>();
		await TestApp.GetService<IBookImportService>().ImportLocalFileAsync(TestEpub.Write("Late arrival"));

		// LoadCommand belongs to the page's RefreshView, so it is driven the way a pull does it:
		// invoked directly, the busy state it sets re-triggers the RefreshView, whose second run
		// cancels the first.
		RefreshView refresh = page.GetVisualTreeDescendants().OfType<RefreshView>().Single();
		refresh.IsRefreshing = true;

		await Wait.UntilAsync(() => VisibleTitles(library).SequenceEqual(["Late arrival"]) && !library.IsBusy, "the refresh to reload the library");
		Assert.False(refresh.IsRefreshing);
	}

	[UIFact]
	public async Task OpenDetails_ShowsTheBookAndBackReturns()
	{
		LibraryViewModel library = await OpenLibraryWithAsync("Alpha");

		await library.OpenDetailsCommand.ExecuteAsync(library.VisibleBooks[0]);
		BookDetailsPage details = await TestApp.WaitForPageAsync<BookDetailsPage>();
		BookDetailsViewModel viewModel = (BookDetailsViewModel)details.BindingContext;
		await Wait.UntilAsync(() => viewModel.Book is not null, "the details page to load its book");

		Assert.Equal("Alpha", viewModel.Book!.Title);
		Assert.True(viewModel.OpenCommand.CanExecute(null));

		await viewModel.BackCommand.ExecuteAsync(null);
		await TestApp.WaitForPageAsync<LibraryPage>();
	}

	[UIFact]
	public async Task BrowseOpdsAndSettings_NavigateToTheirPages()
	{
		LibraryViewModel library = await OpenLibraryAsync();

		await library.BrowseOpdsCommand.ExecuteAsync(null);
		await TestApp.WaitForPageAsync<OpdsServersPage>();

		await TestApp.ResetNavigationAsync();
		await library.OpenSettingsCommand.ExecuteAsync(null);
		await TestApp.WaitForPageAsync<SettingsPage>();
	}
}
